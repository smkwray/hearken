using System;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Threading;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.MediaFoundation;

namespace AudioBridge {
  // Plays the Mac's audio stream (raw s16le 48k stereo over TCP) to THIS PC's
  // CURRENT default render device, following default-device changes live.
  //
  // Robust path (per diagnosis): explicit shared-mode WASAPI on the real MMDevice,
  // resampled to the device's actual mix format, with the app's audio session
  // bound + un-muted (WaveOutEvent/WAVE_MAPPER produced NO render session in this
  // windowless/scheduled-task context => silent "Playing"). [STAThread] + rooted
  // notification callback + reopen on device/session change.
  // Usage: play.exe <host> <port>
  class Play : IMMNotificationClient {
    static readonly MMDeviceEnumerator en = new MMDeviceEnumerator();
    static Play notifier;                 // keep callback rooted (not GC'd)
    static readonly WaveFormat SourceFmt = new WaveFormat(48000, 16, 2);
    static float gGain = 1.0f;             // playback gain 0.0-1.0 (arg 3), applied to PCM

    // ---- output reconciliation --------------------------------------------
    // This replaced a single `deviceChanged` bool that ANY render default change (for any
    // role), ANY endpoint going non-active, ANY removal, and the player's own PlaybackStopped
    // all set. The last of those closed a loop: a deliberate teardown raises PlaybackStopped,
    // which re-armed the flag that caused the teardown. The field log shows the result --
    // eight output generations in ~13 s, and every freshness trim in the post-fix window sits
    // inside that burst, discarding 1,534.5 ms of audio that had already arrived.
    //
    // The replacement records what was actually notified, matches it against the endpoint we
    // are really bound to, and re-reads the desired default before acting, so a notification
    // that changes nothing costs nothing.
    static volatile string activeEndpointId;   // endpoint the live chain is bound to
    static volatile bool reconcileRequested;
    static volatile string lastNotify = "none";
    static long notifyAccepted, notifyIgnored, reconcileNoops;

    static void RequestReconcile(string kind, string id) {
      lastNotify = kind;
      reconcileRequested = true;
      Interlocked.Increment(ref notifyAccepted);
      Console.Error.WriteLine("event=output_notify accepted=1 kind=" + kind + " id=" + (id ?? ""));
    }
    static void IgnoreNotify(string kind, string id) {
      Interlocked.Increment(ref notifyIgnored);
      Console.Error.WriteLine("event=output_notify accepted=0 kind=" + kind + " id=" + (id ?? ""));
    }
    static bool IsActiveEndpoint(string id) {
      string a = activeEndpointId;
      return a != null && id != null && a == id;
    }
    // DesiredEndpointId re-reads the endpoint OpenDefault would select right now. Comparing it
    // against the live one is what makes reconciliation idempotent, and it coalesces a burst of
    // notifications into a single decision for free.
    static string DesiredEndpointId() {
      try {
        var d = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        string id = d.ID;
        try { d.Dispose(); } catch {}
        return id;
      } catch { return null; }
    }
    // ShouldReconcileOnStop is the PlaybackStopped guard, factored out so the self-test can
    // exercise the exact decision that produced the storm.
    static bool ShouldReconcileOnStop(OutputChain c) { return c != null && !c.Retired; }

    // Windows raises OnDefaultDeviceChanged once PER ROLE, so following every role turned one
    // device switch into three reopen requests. OpenDefault selects Render/Multimedia; that is
    // the only pair that can change which endpoint we should be on.
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string id) {
      if (flow == DataFlow.Render && role == Role.Multimedia) RequestReconcile("default_changed", id);
      else IgnoreNotify("default_changed_other", id);
    }
    // Only the endpoint we are actually rendering to matters. Reacting to every endpoint in the
    // system meant an unrelated device going idle tore down healthy playback.
    public void OnDeviceStateChanged(string id, DeviceState s) {
      if (s != DeviceState.Active && IsActiveEndpoint(id)) RequestReconcile("state_changed", id);
      else IgnoreNotify("state_changed_other", id);
    }
    public void OnDeviceAdded(string id) { IgnoreNotify("added", id); }
    public void OnDeviceRemoved(string id) {
      if (IsActiveEndpoint(id)) RequestReconcile("removed", id); else IgnoreNotify("removed_other", id);
    }
    public void OnPropertyValueChanged(string id, PropertyKey k) {}

    [STAThread]
    static void Main(string[] args) {
      if (args.Length > 0 && args[0] == "--self-test") {
        RunSelfTest();
        return;
      }
      string host = args.Length > 0 ? args[0] : "127.0.0.1";
      int port = args.Length > 1 ? int.Parse(args[1]) : 45000;
      if (args.Length > 2) { float g; if (float.TryParse(args[2], out g)) gGain = g < 0 ? 0 : (g > 1 ? 1 : g); }
      try { MediaFoundationApi.Startup(); } catch {}
      notifier = new Play();
      try { en.RegisterEndpointNotificationCallback(notifier); } catch {}
      Console.Error.WriteLine("play -> default device, source " + host + ":" + port);
      while (true) {
        try {
          using (var client = new TcpClient()) {
            client.Connect(host, port);
            client.NoDelay = true;
            // Detect a dead / half-open link so the reconnect loop below actually
            // fires. Two failure modes this guards against:
            //   1. Windows sleeps and wakes -> the TCP connection is half-open; the
            //      OS still reports it ESTABLISHED but no bytes ever arrive.
            //   2. We connected into the host's listen backlog but it never accept()ed
            //      us (it was still streaming to a stale prior client).
            // In both cases net.Read() would otherwise block FOREVER, so the process
            // stays alive, netstat shows ESTABLISHED, and the UI shows a phantom
            // "Streaming" with no audio. The Mac host streams audio continuously, or
            // a squelch keepalive every ~2s while silent, so 5s of total silence on a
            // live link is impossible -> a 5s read timeout means the link is gone.
            client.ReceiveTimeout = 5000;
            EnableKeepAlive(client.Client, 10000, 1000); // OS-level net, second line of defense
            Console.Error.WriteLine("connected");
            RunPlay(client.GetStream());
          }
        } catch (Exception e) {
          Console.Error.WriteLine("link down (" + e.Message + "); retrying...");
        }
        Thread.Sleep(1000);
      }
    }

    // EnableKeepAlive turns on TCP keepalive with a short idle/interval (ms) so the
    // OS tears down a half-open connection in seconds, not the 2-hour Windows default.
    static void EnableKeepAlive(Socket sock, uint idleMs, uint intervalMs) {
      try {
        byte[] cfg = new byte[12];
        BitConverter.GetBytes((uint)1).CopyTo(cfg, 0);    // keepalive on
        BitConverter.GetBytes(idleMs).CopyTo(cfg, 4);     // idle before first probe
        BitConverter.GetBytes(intervalMs).CopyTo(cfg, 8); // gap between probes
        sock.IOControl(IOControlCode.KeepAliveValues, cfg, null);
      } catch {}
    }

    // Timing/sizing shared by the ring and the arrival estimator. Bounds are policy,
    // not tuning knobs: nothing here is user-visible, and the operating point between
    // them is measured, never configured.
    static class Audio {
      public const int SampleRate = 48000;
      public const int FrameBytes = 4;
      public const int CapacityFrames = SampleRate * 2;        // 2 s physical ring
      public const int FadeFrames = SampleRate * 8 / 1000;     // 8 ms fade / conceal budget
      public const int MinPrebufferMs = 40;
      public const int MaxPrebufferMs = 400;
      public const int MinReserveMs = 20;
      public const int ReserveMarginMs = 10;
      public static int Ms(int frames) { return frames * 1000 / SampleRate; }
      public static int Frames(int ms) { return SampleRate * ms / 1000; }
    }

    // ArrivalPolicy measures how far behind real time the media stream is running,
    // and derives the buffer the receiver must hold to survive it.
    //
    // It runs ONLY on the network thread. The render callback never computes policy;
    // it consumes numbers published here.
    //
    // Why debt and not a read-gap percentile: a gap percentile punishes a path that
    // delivers late but in bulk, and cannot tell a 100 ms gap that arrived 100 ms of
    // audio from one that arrived 20 ms. Debt is the running shortfall between media
    // that should have arrived by now and media that actually did, so a large
    // delivery immediately pays down the gap that preceded it.
    class ArrivalPolicy {
      const int StructuralWindow = 30;   // one-second peaks feeding the p90
      const int SpikeWindow = 10;        // one-second peaks feeding the repeated-spike rule
      // IdleGapMs, KeepaliveBytes and SilencePeak are GONE as state authorities. A 500 ms
      // gap plus a heartbeat-sized silent read decided source silence from the shape of a
      // TCP read, which is arbitrary; the amplitude threshold was a hand-written twin of the
      // sender's and could drift. Source state now comes from SourceClassifier counting
      // contiguous silent media frames against the one generated profile, and this class
      // only accounts for arrival.
      const int HysteresisMs = 20;
      const int HoldSecondsBeforeLower = 60;
      const int LowerStepMs = 5;
      const int LowerIntervalSeconds = 10;
      const int ColdStartReads = 3;         // active reads that characterise the path
      const double ColdStartObserveMs = 150.0;
      const double ColdStartCapMs = 400.0;  // never wait longer than this before opening

      readonly double ticksPerSecond = Stopwatch.Frequency;
      readonly double[] structural = new double[StructuralWindow];
      readonly double[] spike = new double[SpikeWindow];
      readonly double[] scratch = new double[StructuralWindow];   // preallocated; no steady-state allocation
      int structuralCount, structuralHead, spikeCount, spikeHead;

      long lastReadTicks, epochTicks, lastLowerTicks, stableSinceTicks;
      long firstActiveTicks, suspendTicks;
      int activeReads;
      double outstandingDebtFrames, oneSecondPeakFrames, publishedDebtMs, lastResumeMs;
      int reserveMs = Audio.MinReserveMs;
      int prebufferMs = Audio.MinPrebufferMs;
      int armedPrebufferMs;                 // raised target waiting for a safe adoption point
      bool started, debtSuspended;
      long debtEpoch, squelchEnterCount, squelchExitCount;
      long raiseCount, lowerCount, armedExpiredCount, excusedGapCount;
      double excusedGapMaxMs;
      string targetReason = "init";

      public bool DebtSuspended { get { return debtSuspended; } }
      public long DebtEpoch { get { return debtEpoch; } }
      public long SquelchEnterCount { get { return squelchEnterCount; } }
      public long SquelchExitCount { get { return squelchExitCount; } }
      public long LowerCount { get { return lowerCount; } }
      public double OutstandingDebtMs { get { return outstandingDebtFrames * 1000.0 / Audio.SampleRate; } }
      public int WarrantedForTest(int renderQuantumFrames) { return WarrantedPrebufferMs(Audio.Ms(renderQuantumFrames)); }
      // The debt the current evidence justifies, in ms. Exposed so a test can assert that
      // confirmation ended continuity without deleting the measurement behind it.
      public double PublishedDebtMs(int renderQuantumFrames) {
        WarrantedPrebufferMs(Audio.Ms(renderQuantumFrames));
        return publishedDebtMs;
      }
      public int PrebufferMs { get { return prebufferMs; } }
      public int ReserveMs { get { return reserveMs; } }
      public int PrebufferFrames { get { return Audio.Frames(prebufferMs); } }

      // ObservationSatisfied gates the very first output open. The design forbids a
      // fixed measurement pause: measure while prebuffering and open as soon as either
      // enough reads have landed to characterise the path or the observation cap expires.
      public bool ObservationSatisfied(long nowTicks) {
        if (firstActiveTicks == 0) return false;          // no program audio seen yet
        double observedMs = (nowTicks - firstActiveTicks) * 1000.0 / ticksPerSecond;
        if (observedMs >= ColdStartCapMs) return true;    // never observe longer than the cap
        if (activeReads >= ColdStartReads) return true;
        return observedMs >= ColdStartObserveMs;
      }

      // SuspendDebt ends debt CONTINUITY without deleting the evidence used to learn the
      // target. The old ResetEpoch cleared oneSecondPeakFrames too, so a real stall in the
      // second before the source went quiet was erased -- immediately before ApplyIdleTarget
      // decided how far it could lower. Completed windows and the open bucket's peak survive;
      // only the running carry and the arrival continuity go.
      public void SuspendDebt(long nowTicks) {
        if (debtSuspended) return;                 // heartbeats must not re-enter
        debtSuspended = true;
        suspendTicks = nowTicks;
        squelchEnterCount++;
        lastReadTicks = 0;
        outstandingDebtFrames = 0;
      }

      // ResumeDebt starts a fresh continuity epoch at the resuming read. The bucket clock is
      // shifted forward by exactly the suspended wall time, so silence cannot manufacture
      // "stable" seconds that the lowering hold would otherwise count.
      public void ResumeDebt(long nowTicks) {
        if (!debtSuspended) return;
        debtSuspended = false;
        squelchExitCount++;
        lastResumeMs = suspendTicks == 0 ? 0 : (nowTicks - suspendTicks) * 1000.0 / ticksPerSecond;
        if (suspendTicks != 0 && epochTicks != 0) epochTicks += nowTicks - suspendTicks;
        if (suspendTicks != 0 && stableSinceTicks != 0) stableSinceTicks += nowTicks - suspendTicks;
        suspendTicks = 0;
        debtEpoch++;
        lastReadTicks = nowTicks;
        outstandingDebtFrames = 0;
      }

      // OnGap accounts for the no-byte interval in FRONT of a read. The verdict comes from the
      // state that existed before the read blocked -- never from what the returning bytes turn
      // out to contain. Reaching the confirmation threshold inside the read that closes a gap
      // must not retroactively excuse that gap: 11,999 silent frames, a 600 ms stall, then a
      // threshold frame is a stall, not source silence.
      public void OnGap(long nowTicks, bool gapStartedConfirmed, double consumeRatio) {
        if (gapStartedConfirmed) {
          if (lastReadTicks != 0) {
            excusedGapCount++;
            double ms = (nowTicks - lastReadTicks) * 1000.0 / ticksPerSecond;
            if (ms > excusedGapMaxMs) excusedGapMaxMs = ms;
          }
          lastReadTicks = nowTicks;
          return;
        }
        if (lastReadTicks != 0) {
          double rate = Audio.SampleRate * (consumeRatio > 0 ? consumeRatio : 1.0);
          double dueFrames = (nowTicks - lastReadTicks) / ticksPerSecond * rate;
          double debtBeforeRead = outstandingDebtFrames + dueFrames;
          if (debtBeforeRead < 0) debtBeforeRead = 0;
          // A gap wider than the largest buffer we would ever hold is a stall, not jitter.
          // One rebuffer answers it; proposing an unreachable target does not.
          double cap = Audio.Frames(Audio.MaxPrebufferMs);
          if (debtBeforeRead > cap) debtBeforeRead = cap;
          if (debtBeforeRead > oneSecondPeakFrames) oneSecondPeakFrames = debtBeforeRead;
          outstandingDebtFrames = debtBeforeRead;
        }
        lastReadTicks = nowTicks;
        if (epochTicks == 0) epochTicks = nowTicks;
      }

      // OnMedia pays down the charge with frames that were actually transmitted. EVERY frame
      // sent before confirmation is media, silent or not: the qualifying tail is real bytes on
      // the wire and it pays. The retired rule that "only program audio carries lateness" is
      // what let an unconfirmed silent stretch look free.
      public void OnMedia(long nowTicks, int frames, bool active) {
        if (frames <= 0) return;
        if (active) {
          if (firstActiveTicks == 0) firstActiveTicks = nowTicks;
          activeReads++;
        }
        if (debtSuspended) return;                 // confirmed silence pays nothing down
        outstandingDebtFrames -= frames;
        if (outstandingDebtFrames < 0) outstandingDebtFrames = 0;
        started = true;
        if (epochTicks != 0 && (nowTicks - epochTicks) / ticksPerSecond >= 1.0) CloseSecond(nowTicks);
      }

      // ResetForConnection clears everything a new TCP connection must not inherit. The learned
      // windows go too: a new stream has not been measured yet.
      // ResetColdObservation forgets that program was ever seen. Confirmation-era reads must not
      // count toward the gate that opens the output for the NEXT talkspurt.
      public void ResetColdObservation() { firstActiveTicks = 0; activeReads = 0; }

      public void ResetForConnection() {
        lastReadTicks = 0; epochTicks = 0; suspendTicks = 0; firstActiveTicks = 0;
        activeReads = 0; outstandingDebtFrames = 0; oneSecondPeakFrames = 0;
        debtSuspended = false;
      }

      void CloseSecond(long nowTicks) {
        structural[structuralHead] = oneSecondPeakFrames;
        structuralHead = (structuralHead + 1) % StructuralWindow;
        if (structuralCount < StructuralWindow) structuralCount++;
        spike[spikeHead] = oneSecondPeakFrames;
        spikeHead = (spikeHead + 1) % SpikeWindow;
        if (spikeCount < SpikeWindow) spikeCount++;
        oneSecondPeakFrames = 0;
        epochTicks = nowTicks;
      }

      // Recompute is called from the telemetry thread with the measured render quantum.
      // It never touches the ring; it only decides the numbers the ring will adopt.
      public void Recompute(long nowTicks, int renderQuantumFrames, bool underrunSinceLast) {
        if (!started) return;
        // Frozen while the source is confirmed silent. Silence is not evidence about the path,
        // and letting the hold clock run through it would let a quiet minute masquerade as a
        // stable one and cash in a lowering the link never earned.
        if (debtSuspended) return;
        int quantumMs = Audio.Ms(renderQuantumFrames);
        int wantPrebuffer = WarrantedPrebufferMs(quantumMs);

        if (underrunSinceLast || wantPrebuffer > prebufferMs + HysteresisMs) {
          // Arm a raise once. Re-arming the same value every second inflated the raise
          // counter and said nothing new about the path.
          if (wantPrebuffer > prebufferMs && wantPrebuffer > armedPrebufferMs) {
            armedPrebufferMs = wantPrebuffer;
            raiseCount++;
            targetReason = underrunSinceLast ? "raise_underrun" : "raise_debt";
          }
          stableSinceTicks = nowTicks;
        } else {
          // An armed raise describes a path condition. Once the measurement no longer
          // warrants it, it must expire: a stale 400 ms armed at start-up was later
          // cashed in by an unrelated rebuffer and cost ~200 ms of dead latency.
          if (armedPrebufferMs > 0 && wantPrebuffer <= armedPrebufferMs - HysteresisMs) {
            armedPrebufferMs = 0;
            armedExpiredCount++;
            targetReason = "armed_expired";
          }
          // Lowering is deliberately slow: a path that just misbehaved gets a full hold
          // before we start giving latency back, and then only in small steps.
          if (stableSinceTicks == 0) stableSinceTicks = nowTicks;
          if (wantPrebuffer < prebufferMs - HysteresisMs &&
              (nowTicks - stableSinceTicks) / ticksPerSecond >= HoldSecondsBeforeLower &&
              (lastLowerTicks == 0 || (nowTicks - lastLowerTicks) / ticksPerSecond >= LowerIntervalSeconds)) {
            int next = prebufferMs - LowerStepMs;
            if (next < wantPrebuffer) next = wantPrebuffer;
            if (next < Audio.MinPrebufferMs) next = Audio.MinPrebufferMs;
            prebufferMs = next;
            lastLowerTicks = nowTicks;
            lowerCount++;
            targetReason = "lower_stable";
          }
        }
        // Reserve always describes the target actually in force, never an armed-but-
        // unadopted one. Reporting 390 ms of reserve behind a 40 ms buffer was incoherent.
        reserveMs = Math.Max(Audio.MinReserveMs, prebufferMs - quantumMs);
      }

      // AdoptArmed promotes a raised target. The caller decides when that is safe:
      // free of charge if occupancy already covers it, otherwise at an idle
      // transition or the one hard rebuffer we were going to pay anyway.
      public bool AdoptArmed(int occupancyFrames, bool force) {
        if (armedPrebufferMs <= prebufferMs) { armedPrebufferMs = 0; return false; }
        if (!force && occupancyFrames < Audio.Frames(armedPrebufferMs)) return false;
        prebufferMs = armedPrebufferMs;
        armedPrebufferMs = 0;
        return true;
      }

      // WarrantedPrebufferMs is what the current measurement alone justifies, before any
      // hysteresis, hold or slew policy is applied.
      int WarrantedPrebufferMs(int quantumMs) {
        double structuralDebt = Percentile(structural, structuralCount, 0.90);
        double spikeDebt = SecondLargest(spike, spikeCount);
        // The OPEN bucket counts too. Completed windows alone meant a stall measured in the
        // second before the source went quiet was invisible to the very call that decides how
        // far the target may drop at confirmation.
        double worst = Math.Max(Math.Max(structuralDebt, spikeDebt), oneSecondPeakFrames);
        publishedDebtMs = worst * 1000.0 / Audio.SampleRate;

        int wantReserve = (int)Math.Ceiling(publishedDebtMs) + Audio.ReserveMarginMs;
        int reserveCeiling = Audio.MaxPrebufferMs - quantumMs;
        if (reserveCeiling < Audio.MinReserveMs) reserveCeiling = Audio.MinReserveMs;
        if (wantReserve < Audio.MinReserveMs) wantReserve = Audio.MinReserveMs;
        if (wantReserve > reserveCeiling) wantReserve = reserveCeiling;

        int want = wantReserve + quantumMs;
        if (want < Audio.MinPrebufferMs) want = Audio.MinPrebufferMs;
        if (want > Audio.MaxPrebufferMs) want = Audio.MaxPrebufferMs;
        return want;
      }

      // ApplyIdleTarget takes the whole reduction at once. Source idle or reconnect is
      // the one moment a lower target is free -- nothing is playing, so there is nothing
      // to interrupt -- and crawling down at 5 ms per 10 s through a silent gap only
      // preserves latency the evidence stopped justifying.
      public void ApplyIdleTarget(int renderQuantumFrames) {
        int quantumMs = Audio.Ms(renderQuantumFrames);
        int want = WarrantedPrebufferMs(quantumMs);
        if (want < prebufferMs) {
          prebufferMs = want;
          lowerCount++;
          targetReason = "lower_idle";
        }
        // A raise armed before the gap describes a condition that no longer holds.
        if (armedPrebufferMs > 0 && armedPrebufferMs > want) {
          armedPrebufferMs = 0;
          armedExpiredCount++;
        }
        reserveMs = Math.Max(Audio.MinReserveMs, prebufferMs - quantumMs);
        stableSinceTicks = 0;
        lastLowerTicks = 0;
      }

      double Percentile(double[] buf, int count, double q) {
        if (count <= 0) return 0;
        Array.Copy(buf, scratch, count);
        Array.Sort(scratch, 0, count);
        int idx = (int)Math.Ceiling(q * count) - 1;
        if (idx < 0) idx = 0;
        if (idx >= count) idx = count - 1;
        return scratch[idx];
      }

      static double SecondLargest(double[] buf, int count) {
        if (count <= 1) return 0;
        double first = double.MinValue, second = double.MinValue;
        for (int i = 0; i < count; i++) {
          double v = buf[i];
          if (v > first) { second = first; first = v; }
          else if (v > second) second = v;
        }
        return second == double.MinValue ? 0 : second;
      }

      public string Describe() {
        double p50 = Percentile(structural, structuralCount, 0.50) * 1000.0 / Audio.SampleRate;
        double p99 = Percentile(structural, structuralCount, 0.99) * 1000.0 / Audio.SampleRate;
        return string.Format(CultureInfo.InvariantCulture,
          "arrival_debt_ms={0:0.0} arrival_debt_p50_ms={1:0.0} arrival_debt_p99_ms={2:0.0} " +
          "reserve_target_ms={3} prebuffer_target_ms={4} target_armed_ms={5} " +
          "target_raise={6} target_lower={7} target_armed_expired={8} target_reason={9} " +
          "squelch_enter={10} squelch_exit={11} squelch_resume_ms={12:0.0} debt_suspended={13} " +
          "excused_gap={14} excused_gap_max_ms={15:0.0} debt_epoch={16}",
          publishedDebtMs, p50, p99,
          reserveMs, prebufferMs, armedPrebufferMs,
          raiseCount, lowerCount, armedExpiredCount, targetReason,
          squelchEnterCount, squelchExitCount, lastResumeMs, debtSuspended ? 1 : 0,
          excusedGapCount, excusedGapMaxMs, debtEpoch);
      }
    }

    // Where the source crossed, expressed as a position in the audio itself.
    enum RenderMode { ActivePlaying, DrainToIdle, IdleSilence, ResumePrebuffer }
    enum MarkerKind { IdleStart, TalkspurtStart }
    struct TimelineMarker {
      public MarkerKind Kind;
      public long Serial;        // absolute frame serial the boundary sits immediately before
      public long TransitionId;  // monotonic; a repeated or stale id is rejected
    }

    // Fixed-capacity, frame-counted PCM ring plus a narrow fractional provider.
    // The render callback performs only bounded arithmetic and copies under one
    // short lock: no allocation, logging, socket I/O, or unbounded drain loop.
    //
    // Underrun is NOT a cliff. A shortage inside the fade budget is concealed in
    // place; anything larger rebuffers WITHOUT discarding the ring, so audio that
    // already arrived is never thrown away to satisfy a few missing milliseconds.
    class AdaptivePlayout : IWaveProvider {
      const int SampleRate = Audio.SampleRate;
      const int FrameBytes = Audio.FrameBytes;
      const int CapacityFrames = Audio.CapacityFrames;
      const int FadeFrames = Audio.FadeFrames;
      const double MaxRatioPpm = 500.0;
      const int HighWaterAboveMs = 160;      // freshness envelope above the prebuffer target
      const int TrimAboveMs = 80;
      const int ResumeGuardMs = 10;
      const int QuantumWindow = 256;         // recent render requests feeding the p99

      readonly object gate = new object();
      readonly byte[] ring = new byte[CapacityFrames * FrameBytes];
      readonly int[] quantumRing = new int[QuantumWindow];
      readonly int[] quantumScratch = new int[QuantumWindow];
      readonly double[] gapRing = new double[QuantumWindow];
      readonly double[] gapScratch = new double[QuantumWindow];
      int quantumCount, quantumHead, gapCount, gapHead;
      int reqP50Frames, reqP95Frames, reqP99Frames;
      double gapP50Ms, gapP95Ms, gapP99Ms;

      int head, frames;
      // Absolute frame serials. The ring index alone cannot express "the boundary is 300 ms of
      // audio further on", which is exactly what a marker has to mean.
      long writeSerial, readSerial, resumeAnchorSerial;
      // 2 * ceil(CapacityFrames / ConfirmFrames) covers the most boundaries a full ring can hold
      // at the minimum silence cycle, plus margin. Fixed, so the render path stays bounded.
      const int MarkerCapacity = 32;
      readonly TimelineMarker[] markers = new TimelineMarker[MarkerCapacity];
      int markerHead, markerCount;
      long lastTransitionId, markerOverflows, markersCrossedIdle, markersCrossedTalk;
      long idleSilenceFrames, resumeWaitFrames;
      RenderMode mode = RenderMode.ActivePlaying;
      double phase, ratio = 1.0, integralMsSeconds;
      long lastControlTicks, lastCallbackTicks, softUnderrunTicks;
      bool pendingDiscontinuity, rebuffering = true, softPending;
      int transitionRemaining;
      short transitionFromL, transitionFromR, lastL, lastR;

      // Published by the network/telemetry threads, adopted at a render block boundary.
      int pendingPrebufferFrames = Audio.Frames(Audio.MinPrebufferMs);
      int prebufferFrames = Audio.Frames(Audio.MinPrebufferMs);
      int requestQuantumFrames = Audio.Frames(20);
      int targetGeneration;

      long overflowEvents, overflowFrames, freshnessTrims, freshnessTrimFrames;
      // Trims taken while the output endpoint is down -- during the FIRST open as much as a
      // later reopen -- are a device event, not the network running late. Opening a WASAPI
      // endpoint is not instant (a Bluetooth endpoint has to wake and negotiate), nothing
      // drains the ring while it happens, and the backlog that builds is then discarded. Kept
      // separate so neither a reopen storm nor a slow endpoint can read as a transport fault.
      long freshnessTrimsOutage, freshnessTrimFramesOutage;
      bool outputOutage;
      long callbackGapEvents, requestedFrames, suppliedFrames, sourceFrames;
      long softUnderruns, softZeroFillFrames, hardRebuffers, rebufferFrames;
      long underrunsSinceRecompute;
      double maxCallbackGapMs;
      int minOccupancy, maxOccupancy;
      int lastWriteBeforeFrames, lastWriteAfterFrames;

      public WaveFormat WaveFormat { get { return SourceFmt; } }
      public int BufferedFrames { get { lock (gate) { return frames; } } }
      public int BufferedMs { get { lock (gate) { return Audio.Ms(frames); } } }
      public int PrebufferFrames { get { lock (gate) { return prebufferFrames; } } }
      public int RequestQuantumFrames { get { lock (gate) { return requestQuantumFrames; } } }
      public double RatioSnapshot { get { lock (gate) { return ratio; } } }
      public long SoftUnderrunCount { get { lock (gate) { return softUnderruns; } } }
      public long HardRebufferCount { get { lock (gate) { return hardRebuffers; } } }
      public long FreshnessTrimCount { get { lock (gate) { return freshnessTrims; } } }
      // ClearForColdStart drops audio buffered before a confirmed silence when the output was
      // never opened. Keeping it would play a fragment of an old phrase seconds later, after
      // the next talkspurt -- stale audio nobody asked for. Freshness, not tidiness.
      public void ClearForColdStart() {
        lock (gate) {
          head = 0; frames = 0; phase = 0;
          readSerial = writeSerial;
          markerHead = markerCount = 0;
          mode = RenderMode.ActivePlaying;
          rebuffering = true;
          softPending = false;
          integralMsSeconds = 0;
        }
      }
      public int RenderModeForTest { get { lock (gate) { return (int)mode; } } }
      public long IdleSilenceFrames { get { lock (gate) { return idleSilenceFrames; } } }
      public long ResumeWaitFrames { get { lock (gate) { return resumeWaitFrames; } } }
      public long MarkersCrossedIdleForTest { get { lock (gate) { return markersCrossedIdle; } } }
      public long SuppliedFramesForTest { get { lock (gate) { return suppliedFrames; } } }
      public long FreshnessTrimTransitionCount { get { lock (gate) { return freshnessTrimsOutage; } } }
      // Bracket a deliberate endpoint teardown so any discard inside it is attributed to the
      // outage. Both take the same lock the render callback uses, so the flag can never be
      // observed half-set from the audio thread.
      public void BeginOutputOutage() { lock (gate) { outputOutage = true; } }
      public void EndOutputOutage() { lock (gate) { outputOutage = false; } }

      public AdaptivePlayout() {
        minOccupancy = maxOccupancy = 0;
      }

      void NoteOccupancy() {
        if (frames < minOccupancy) minOccupancy = frames;
        if (frames > maxOccupancy) maxOccupancy = frames;
      }

      void Advance(int n) {
        if (n <= 0) return;
        if (n > frames) n = frames;
        head = (head + n) % CapacityFrames;
        frames -= n;
        readSerial += n;
      }

      // PublishTarget hands the render callback a new operating point. The callback
      // adopts it at its next block boundary; policy is never computed inside it.
      public void PublishTarget(int prebufferMsValue) {
        int want = Audio.Frames(prebufferMsValue);
        lock (gate) {
          if (want == pendingPrebufferFrames) return;
          pendingPrebufferFrames = want;
          targetGeneration++;
        }
      }

      // MarkIdleStart / MarkTalkspurt place a boundary AT THE CURRENT WRITE POSITION, which is
      // the whole point: playback lags the network, so the sender can already have resumed while
      // WASAPI is still rendering audio from before the silence. A live Boolean would cut the
      // tail off the previous phrase. A marker crosses when playback actually reaches it.
      //
      // The transition id makes both idempotent: a repeated heartbeat, or a retried batch, must
      // not add a second boundary or duplicate a talkspurt.
      public bool MarkIdleStart(long transitionId) {
        lock (gate) {
          if (transitionId <= lastTransitionId) return false;
          lastTransitionId = transitionId;
          if (!PushMarkerLocked(MarkerKind.IdleStart, writeSerial, transitionId)) return false;
          // Deliberately NOT rebuffering = true. That gate holds a below-target ring and would
          // keep pre-silence program audio back to be played late; drain must ignore the target
          // and render what is already here.
          integralMsSeconds = 0;
          softPending = false;
          if (mode == RenderMode.ActivePlaying) mode = RenderMode.DrainToIdle;
          return true;
        }
      }
      public bool MarkTalkspurt(long transitionId) {
        lock (gate) {
          if (transitionId <= lastTransitionId) return false;
          lastTransitionId = transitionId;
          return PushMarkerLocked(MarkerKind.TalkspurtStart, writeSerial, transitionId);
        }
      }

      bool PushMarkerLocked(MarkerKind kind, long serial, long id) {
        if (markerCount >= MarkerCapacity) {
          // Never render ambiguously ordered PCM: collapse to the newest boundary instead.
          markerOverflows++;
          markerHead = markerCount = 0;
          mode = kind == MarkerKind.IdleStart ? RenderMode.DrainToIdle : RenderMode.ResumePrebuffer;
        }
        int at = (markerHead + markerCount) % MarkerCapacity;
        markers[at].Kind = kind; markers[at].Serial = serial; markers[at].TransitionId = id;
        markerCount++;
        return true;
      }

      // Apply every boundary playback has actually reached, in order. Called from the render
      // path and after a trim, because a trim advances the read position across markers too.
      void ConsumeDueMarkersLocked() {
        while (markerCount > 0 && markers[markerHead].Serial <= readSerial) {
          MarkerKind k = markers[markerHead].Kind;
          markerHead = (markerHead + 1) % MarkerCapacity;
          markerCount--;
          if (k == MarkerKind.IdleStart) { mode = RenderMode.IdleSilence; markersCrossedIdle++; }
          else { mode = RenderMode.ResumePrebuffer; resumeAnchorSerial = readSerial; markersCrossedTalk++; }
        }
      }

      // TakeRequestQuantum returns the p99 render request over the recent window and
      // the underrun count since the previous call. Telemetry thread only: it sorts,
      // so it must never run in the callback.
      public int TakeRequestQuantum(out bool underrunSinceLast) {
        lock (gate) {
          underrunSinceLast = underrunsSinceRecompute > 0;
          underrunsSinceRecompute = 0;
          if (quantumCount > 0) {
            Array.Copy(quantumRing, quantumScratch, quantumCount);
            Array.Sort(quantumScratch, 0, quantumCount);
            reqP50Frames = quantumScratch[Rank(quantumCount, 0.50)];
            reqP95Frames = quantumScratch[Rank(quantumCount, 0.95)];
            reqP99Frames = quantumScratch[Rank(quantumCount, 0.99)];
            requestQuantumFrames = reqP99Frames;
          }
          if (gapCount > 0) {
            Array.Copy(gapRing, gapScratch, gapCount);
            Array.Sort(gapScratch, 0, gapCount);
            gapP50Ms = gapScratch[Rank(gapCount, 0.50)];
            gapP95Ms = gapScratch[Rank(gapCount, 0.95)];
            gapP99Ms = gapScratch[Rank(gapCount, 0.99)];
          }
          return requestQuantumFrames;
        }
      }

      static int Rank(int count, double q) {
        int idx = (int)Math.Ceiling(q * count) - 1;
        if (idx < 0) idx = 0;
        if (idx >= count) idx = count - 1;
        return idx;
      }

      // BeginQuantumEpoch is called when the output endpoint is (re)opened: the new
      // device has its own period, so request and callback-gap history from the old one
      // would misdescribe it. The network-side reserve estimate is deliberately kept.
      public void BeginQuantumEpoch() {
        lock (gate) {
          quantumCount = quantumHead = 0;
          gapCount = gapHead = 0;
          lastCallbackTicks = 0;
        }
      }

      public void AddFrames(byte[] input, int offset, int count) {
        int add = count / FrameBytes;
        if (add <= 0) return;
        lock (gate) {
          lastWriteBeforeFrames = frames;
          if (add > CapacityFrames) {
            int skip = add - CapacityFrames;
            offset += skip * FrameBytes;
            add = CapacityFrames;
          }
          // Freshness envelope. An ordinary structural burst must land untouched; only
          // a genuine backlog (a multi-second blocked sender write, a stalled link that
          // then dumps) is trimmed, and it is trimmed here on the network thread, never
          // synchronously inside the render callback.
          int highWater = prebufferFrames + Audio.Frames(HighWaterAboveMs);
          if (highWater > CapacityFrames) highWater = CapacityFrames;
          int trimTo = prebufferFrames + Audio.Frames(TrimAboveMs);
          if (trimTo > highWater) trimTo = highWater;
          if (frames + add > highWater) {
            int excess = frames + add - trimTo;
            // Oldest-first: drain the ring, then skip the head of this delivery. A
            // burst arriving into an empty ring is mostly stale in the INPUT, so
            // trimming only what is already buffered would leave the latency intact.
            int fromRing = excess > frames ? frames : excess;
            if (fromRing > 0) Advance(fromRing);
            int fromInput = excess - fromRing;
            if (fromInput > add) fromInput = add;
            if (fromInput > 0) { offset += fromInput * FrameBytes; add -= fromInput; }
            if (fromRing > 0) ConsumeDueMarkersLocked();   // a trim crosses boundaries too
            if (fromRing + fromInput > 0) {
              freshnessTrims++;
              freshnessTrimFrames += fromRing + fromInput;
              if (outputOutage) {
                freshnessTrimsOutage++;
                freshnessTrimFramesOutage += fromRing + fromInput;
              }
              pendingDiscontinuity = true;
            }
          }
          int overflow = frames + add - CapacityFrames;
          if (overflow > 0) {
            Advance(overflow);
            overflowEvents++;
            overflowFrames += overflow;
            pendingDiscontinuity = true;
          }
          int tail = (head + frames) % CapacityFrames;
          int first = Math.Min(add, CapacityFrames - tail);
          Buffer.BlockCopy(input, offset, ring, tail * FrameBytes, first * FrameBytes);
          if (first < add) Buffer.BlockCopy(input, offset + first * FrameBytes, ring, 0, (add - first) * FrameBytes);
          frames += add;
          writeSerial += add;
          lastWriteAfterFrames = frames;
          NoteOccupancy();
        }
      }

      short SampleAt(int relativeFrame, int channel) {
        int f = (head + relativeFrame) % CapacityFrames;
        int i = f * FrameBytes + channel * 2;
        return (short)(ring[i] | (ring[i + 1] << 8));
      }

      static void PutSample(byte[] output, int at, short value) {
        output[at] = (byte)(value & 0xff);
        output[at + 1] = (byte)((value >> 8) & 0xff);
      }

      void BeginFadeIn(short fromL, short fromR) {
        transitionFromL = fromL;
        transitionFromR = fromR;
        transitionRemaining = FadeFrames;
      }

      void WriteSilence(byte[] output, int offset, int count, int outputFrames) {
        Array.Clear(output, offset, count);
        int fade = Math.Min(outputFrames, FadeFrames);
        for (int i = 0; i < fade; i++) {
          double scale = 1.0 - (double)(i + 1) / fade;
          PutSample(output, offset + i * FrameBytes, (short)(lastL * scale));
          PutSample(output, offset + i * FrameBytes + 2, (short)(lastR * scale));
        }
        lastL = lastR = 0;
      }

      void BeginRebuffer(long nowTicks) {
        if (!rebuffering) {
          rebuffering = true;
          hardRebuffers++;
          underrunsSinceRecompute++;
          // The ring is deliberately NOT cleared. Whatever arrived is still good audio;
          // discarding it was the defect that turned a 1 ms shortage into 140 ms of silence.
          // The controller integral is NOT cleared either: it is reset only for a new
          // media epoch or a changed target (AdoptTargetLocked), never for an ordinary
          // underrun, so one shortage cannot discard the accumulated clock estimate.
          phase = 0;
          softPending = false;
        }
      }

      // AdoptTargetLocked moves to a published operating point at a block boundary.
      void AdoptTargetLocked() {
        if (pendingPrebufferFrames == prebufferFrames) return;
        // A lower target can always be taken; a higher one only when occupancy already
        // covers it or we are already rebuffering, so it costs no extra interruption.
        if (pendingPrebufferFrames < prebufferFrames || rebuffering || frames >= pendingPrebufferFrames) {
          prebufferFrames = pendingPrebufferFrames;
          integralMsSeconds = 0;
        }
      }

      void UpdateController(long nowTicks, int needed) {
        if (lastControlTicks == 0) { lastControlTicks = nowTicks; return; }
        double seconds = (double)(nowTicks - lastControlTicks) / Stopwatch.Frequency;
        if (seconds < 0.25) return;
        lastControlTicks = nowTicks;
        // Error is measured against what remains AFTER this request is satisfied.
        // Comparing raw pre-read occupancy to the target was the original defect: it
        // let the controller call the buffer healthy at exactly the moment the next
        // callback would drain it dry.
        int postReadFrames = frames - needed;
        int reserveFrames = prebufferFrames - requestQuantumFrames;
        if (reserveFrames < 0) reserveFrames = 0;
        double errorMs = (postReadFrames - reserveFrames) * 1000.0 / SampleRate;
        if (Math.Abs(errorMs) < 5.0) errorMs = 0;
        integralMsSeconds += errorMs * seconds;
        if (integralMsSeconds > 12000) integralMsSeconds = 12000;
        if (integralMsSeconds < -12000) integralMsSeconds = -12000;
        double desired = errorMs * 2.0 + integralMsSeconds * 0.03;
        if (desired > MaxRatioPpm) desired = MaxRatioPpm;
        if (desired < -MaxRatioPpm) desired = -MaxRatioPpm;
        double current = (ratio - 1.0) * 1000000.0;
        double step = desired - current;
        if (step > 25) step = 25;
        if (step < -25) step = -25;
        ratio = 1.0 + (current + step) / 1000000.0;
      }

      void NoteRequestQuantum(int outputFrames) {
        quantumRing[quantumHead] = outputFrames;
        quantumHead = (quantumHead + 1) % QuantumWindow;
        if (quantumCount < QuantumWindow) quantumCount++;
      }

      // RenderFrames interpolates n output frames from the ring. Bounded by n; the
      // caller guarantees the ring holds enough source material.
      void RenderFrames(byte[] output, int offset, int n) {
        for (int i = 0; i < n; i++) {
          int baseFrame = (int)phase;
          double frac = phase - baseFrame;
          short aL = SampleAt(baseFrame, 0), aR = SampleAt(baseFrame, 1);
          short bL = SampleAt(baseFrame + 1, 0), bR = SampleAt(baseFrame + 1, 1);
          int l = (int)(aL + (bL - aL) * frac);
          int r = (int)(aR + (bR - aR) * frac);
          if (transitionRemaining > 0) {
            double alpha = (double)(FadeFrames - transitionRemaining + 1) / FadeFrames;
            l = (int)(transitionFromL * (1.0 - alpha) + l * alpha);
            r = (int)(transitionFromR * (1.0 - alpha) + r * alpha);
            transitionRemaining--;
          }
          lastL = (short)l; lastR = (short)r;
          PutSample(output, offset + i * FrameBytes, lastL);
          PutSample(output, offset + i * FrameBytes + 2, lastR);
          phase += ratio;
          int consume = (int)phase;
          if (consume > 0) {
            Advance(consume);
            sourceFrames += consume;
            phase -= consume;
          }
        }
      }

      public int Read(byte[] output, int offset, int count) {
        int outputFrames = count / FrameBytes;
        int renderBytes = outputFrames * FrameBytes;
        long now = Stopwatch.GetTimestamp();
        lock (gate) {
          requestedFrames += outputFrames;
          NoteRequestQuantum(outputFrames);
          if (lastCallbackTicks != 0) {
            double gap = (double)(now - lastCallbackTicks) * 1000.0 / Stopwatch.Frequency;
            if (gap > maxCallbackGapMs) maxCallbackGapMs = gap;
            if (gap > 150.0) callbackGapEvents++;
            gapRing[gapHead] = gap;
            gapHead = (gapHead + 1) % QuantumWindow;
            if (gapCount < QuantumWindow) gapCount++;
          }
          lastCallbackTicks = now;
          AdoptTargetLocked();
          ConsumeDueMarkersLocked();

          int needed = (int)Math.Ceiling(outputFrames * ratio) + 1;

          // --- source timeline ------------------------------------------------------------
          // DRAIN_TO_IDLE: the source is confirmed silent but the audio it sent before going
          // quiet is still in the ring and belongs to the listener. Render it out regardless of
          // the prebuffer target -- deliberately NOT through the rebuffer gate, which would hold
          // a below-target ring and replay that tail late -- and stop exactly on the boundary.
          if (mode == RenderMode.DrainToIdle) {
            long toBoundary = markerCount > 0 ? markers[markerHead].Serial - readSerial : 0;
            if (toBoundary < 0) toBoundary = 0;
            int drainable = (int)(toBoundary / (ratio > 0 ? ratio : 1.0));
            if (drainable > outputFrames) drainable = outputFrames;
            if (drainable > 0 && frames > 1) {
              int byOccupancy = (int)((frames - 1) / (ratio > 0 ? ratio : 1.0));
              if (drainable > byOccupancy) drainable = byOccupancy;
            } else drainable = 0;
            Array.Clear(output, offset, count);
            if (drainable > 0) {
              RenderFrames(output, offset, drainable);
              suppliedFrames += drainable;
            }
            ConsumeDueMarkersLocked();
            // The interpolator needs one frame of lookahead, so it can never consume the final
            // frame before a boundary: the drain would stall a frame short forever and never
            // cross. Step over that residue -- at most one frame, ~21 microseconds -- in the
            // same callback that rendered the prefix, so a boundary landing mid-request is
            // crossed there rather than a whole request later.
            if (mode == RenderMode.DrainToIdle && markerCount > 0) {
              long residue = markers[markerHead].Serial - readSerial;
              if (residue > 0 && residue <= 2) { Advance((int)residue); ConsumeDueMarkersLocked(); }
            }
            NoteOccupancy();
            return count;
          }

          // IDLE_SILENCE: the listener is meant to hear nothing. Not an underrun, not a
          // rebuffer, and no reason to touch the controller.
          if (mode == RenderMode.IdleSilence) {
            WriteSilence(output, offset, count, outputFrames);
            idleSilenceFrames += outputFrames;
            NoteOccupancy();
            return count;
          }

          // RESUME_PREBUFFER: the talkspurt boundary has been crossed; withhold until a full
          // target of post-boundary audio exists, then fade in ONCE. Zero-fill here is
          // intentional and must never be charged as starvation.
          if (mode == RenderMode.ResumePrebuffer) {
            long since = writeSerial - resumeAnchorSerial;
            if (since < prebufferFrames || frames < needed) {
              WriteSilence(output, offset, count, outputFrames);
              resumeWaitFrames += outputFrames;
              NoteOccupancy();
              return count;
            }
            mode = RenderMode.ActivePlaying;
            BeginFadeIn(0, 0);
          }

          if (rebuffering) {
            if (frames < prebufferFrames || frames < needed) {
              WriteSilence(output, offset, count, outputFrames);
              rebufferFrames += outputFrames;
              NoteOccupancy();
              return count;
            }
            rebuffering = false;
            BeginFadeIn(0, 0);
          }

          if (softPending) {
            // Resume only with real headroom, otherwise the next callback repeats the
            // shortage. Failing that, take the one bounded rebuffer instead.
            if (frames >= needed + Audio.Frames(ResumeGuardMs)) {
              softPending = false;
              BeginFadeIn(lastL, lastR);
            } else if (frames < needed) {
              BeginRebuffer(now);
              WriteSilence(output, offset, count, outputFrames);
              rebufferFrames += outputFrames;
              NoteOccupancy();
              return count;
            } else {
              softPending = false;
            }
          }

          if (frames < needed) {
            int shortageFrames = needed - frames;
            bool recentSoft = softUnderrunTicks != 0 &&
              (double)(now - softUnderrunTicks) * 1000.0 / Stopwatch.Frequency < 1000.0;
            if (shortageFrames <= FadeFrames && !recentSoft) {
              // Conceal in place: render everything safely interpolable, fade its tail,
              // and zero only the few missing frames.
              int renderable = frames > 1 ? (int)((frames - 1) / ratio) : 0;
              if (renderable > outputFrames) renderable = outputFrames;
              if (renderable < 0) renderable = 0;
              Array.Clear(output, offset, count);
              if (renderable > 0) RenderFrames(output, offset, renderable);
              int fade = Math.Min(renderable, FadeFrames);
              for (int i = 0; i < fade; i++) {
                int at = offset + (renderable - fade + i) * FrameBytes;
                double scale = 1.0 - (double)(i + 1) / fade;
                short l = (short)(output[at] | (output[at + 1] << 8));
                short r = (short)(output[at + 2] | (output[at + 3] << 8));
                PutSample(output, at, (short)(l * scale));
                PutSample(output, at + 2, (short)(r * scale));
              }
              lastL = lastR = 0;
              softUnderruns++;
              softZeroFillFrames += outputFrames - renderable;
              underrunsSinceRecompute++;
              softUnderrunTicks = now;
              softPending = true;
              suppliedFrames += renderable;
              NoteOccupancy();
              return count;
            }
            BeginRebuffer(now);
            WriteSilence(output, offset, count, outputFrames);
            rebufferFrames += outputFrames;
            NoteOccupancy();
            return count;
          }

          if (pendingDiscontinuity) {
            // A freshness trim or capacity overflow already moved the ring head on the
            // network thread; all that is owed here is one crossfade at this boundary.
            pendingDiscontinuity = false;
            phase = 0;
            BeginFadeIn(lastL, lastR);
          }

          UpdateController(now, needed);
          RenderFrames(output, offset, outputFrames);
          if (renderBytes < count) Array.Clear(output, offset + renderBytes, count - renderBytes);
          suppliedFrames += outputFrames;
          NoteOccupancy();
          return count;
        }
      }

      public string TakeTelemetry(long rxBytes, double rxDurationMs, double maxReadGapMs,
                                  string state, string endpoint, int generation, string renderMode,
                                  int deviceBufferMs, string arrival) {
        double ratioPpm, callbackMaxGapMs, g50, g95, g99;
        int occupancyMs, spanMs, writeBeforeMs, writeAfterMs, prebufMs, quantumMs, r50, r95, r99;
        long callbackGaps, requested, supplied, softs, softZeros, hards, hardFrames;
        long overflows, overflowedFrames, trims, trimmedFrames, playedSourceFrames;
        long outageTrims, outageTrimFrames;
        bool rebuf; int modeNow; long ovf, crossIdle, crossTalk, idleFrames, waitFrames;
        lock (gate) {
          ratioPpm = (ratio - 1.0) * 1000000.0;
          occupancyMs = Audio.Ms(frames);
          spanMs = Audio.Ms(maxOccupancy - minOccupancy);
          writeBeforeMs = Audio.Ms(lastWriteBeforeFrames);
          writeAfterMs = Audio.Ms(lastWriteAfterFrames);
          callbackMaxGapMs = maxCallbackGapMs;
          callbackGaps = callbackGapEvents;
          requested = requestedFrames;
          supplied = suppliedFrames;
          softs = softUnderruns;
          softZeros = softZeroFillFrames;
          hards = hardRebuffers;
          hardFrames = rebufferFrames;
          overflows = overflowEvents;
          overflowedFrames = overflowFrames;
          trims = freshnessTrims;
          trimmedFrames = freshnessTrimFrames;
          outageTrims = freshnessTrimsOutage;
          outageTrimFrames = freshnessTrimFramesOutage;
          playedSourceFrames = sourceFrames;
          prebufMs = Audio.Ms(prebufferFrames);
          quantumMs = Audio.Ms(requestQuantumFrames);
          r50 = Audio.Ms(reqP50Frames); r95 = Audio.Ms(reqP95Frames); r99 = Audio.Ms(reqP99Frames);
          g50 = gapP50Ms; g95 = gapP95Ms; g99 = gapP99Ms;
          rebuf = rebuffering;
          modeNow = (int)mode; ovf = markerOverflows;
          crossIdle = markersCrossedIdle; crossTalk = markersCrossedTalk;
          idleFrames = idleSilenceFrames; waitFrames = resumeWaitFrames;
          maxCallbackGapMs = 0;
          minOccupancy = maxOccupancy = frames;
        }
        // Formatting happens after releasing the ring lock, so the render callback
        // can never wait behind string allocation or log preparation.
        return string.Format(CultureInfo.InvariantCulture,
          "event=receiver_metrics rx_bytes={0} rx_duration_ms={1:0.0} max_read_gap_ms={2:0.0} occupancy_ms={3} " +
          "write_before_ms={4} write_after_ms={5} occupancy_span_ms={6} callback_max_gap_ms={7:0.0} callback_gap_events={8} " +
          "render_quantum_ms={9} prebuffer_ms={10} requested_frames={11} supplied_frames={12} " +
          "soft_underrun={13} soft_zero_fill_frames={14} soft_zero_fill_ms={15:0.0} " +
          "hard_rebuffer={16} hard_rebuffer_frames={17} hard_rebuffer_ms={18:0.0} " +
          "freshness_trim={19} freshness_trim_frames={20} freshness_trim_ms={21:0.0} " +
          "overflow_events={22} overflow_frames={23} ratio_ppm={24:0.0} source_frames={25} " +
          "output_generation={26} render_mode={27} endpoint={28} state={29} rebuffering={30} {31} " +
          "callback_gap_alert={32} occupancy_jump_alert={33} " +
          "render_req_p50_ms={34} render_req_p95_ms={35} render_req_p99_ms={36} " +
          "callback_gap_p50_ms={37:0.0} callback_gap_p95_ms={38:0.0} callback_gap_p99_ms={39:0.0} " +
          "device_buffer_ms={40} " +
          "freshness_trim_output_transition={41} freshness_trim_output_transition_ms={42:0.0} " +
          "output_notify_accepted={43} output_notify_ignored={44} output_reconcile_noop={45} " +
          "render_mode_state={46} marker_idle_crossed={47} marker_talkspurt_crossed={48} " +
          "marker_overflow={49} idle_silence_ms={50:0.0} resume_wait_ms={51:0.0}",
          rxBytes, rxDurationMs, maxReadGapMs, occupancyMs,
          writeBeforeMs, writeAfterMs, spanMs, callbackMaxGapMs, callbackGaps,
          quantumMs, prebufMs, requested, supplied,
          softs, softZeros, softZeros * 1000.0 / SampleRate,
          hards, hardFrames, hardFrames * 1000.0 / SampleRate,
          trims, trimmedFrames, trimmedFrames * 1000.0 / SampleRate,
          overflows, overflowedFrames, ratioPpm, playedSourceFrames,
          generation, renderMode, endpoint == null ? "none" : endpoint.Replace(' ', '_'), state,
          rebuf ? 1 : 0, arrival,
          callbackMaxGapMs > 150.0 ? 1 : 0, spanMs > 100 ? 1 : 0,
          r50, r95, r99, g50, g95, g99, deviceBufferMs,
          outageTrims, outageTrimFrames * 1000.0 / SampleRate,
          Interlocked.Read(ref notifyAccepted), Interlocked.Read(ref notifyIgnored), reconcileNoops,
          modeNow, crossIdle, crossTalk, ovf,
          idleFrames * 1000.0 / SampleRate, waitFrames * 1000.0 / SampleRate);
      }
    }

    // TCP can split a sample or stereo frame at any byte. Carry the incomplete
    // tail so gain and ring insertion always receive whole four-byte frames.
    // What a run of assembled frames is, and what must happen to it.
    enum SourceEdge { None, Confirmed, Resumed }

    struct SourceSpan {
      public int Offset;        // byte offset into the assembled block, frame-aligned
      public int Bytes;         // length, frame-aligned
      public bool Insert;       // false only for confirmed-squelch silence (heartbeats)
      public bool PaysDebt;     // true while unconfirmed or qualifying: it is transmitted media
      public bool Active;       // the span contained at least one non-silent frame
      public SourceEdge Edge;   // a transition occurring AT this span's first frame
    }

    // SourceClassifier walks complete stereo frames in stream order and reports where the source
    // crosses into confirmed squelch and back out.
    //
    // It replaces a whole-read peak plus a "was the last read silent" latch. TCP read boundaries
    // are arbitrary -- this file already carries split frames across reads -- so any decision
    // keyed on the shape of a read made the network's framing into policy. One silent quantum
    // could forgive a real transport stall. A contiguous run of silent FRAMES is a property of
    // the media itself and survives any fragmentation or coalescing.
    //
    // Confirmation lands on frame ConfirmFrames (12,000). That threshold frame is ordinary
    // transmitted media and is still inserted; only what follows it is discardable.
    class SourceClassifier {
      // Continuous PCM is the specified fallback if confirmed squelch cannot be trusted, and it
      // is only safe as ONE coordinated mode: the sender stops suppressing AND the receiver stops
      // forgiving. A sender-suppressed / receiver-charging pair inflates the target on every
      // pause; a sender-continuous / receiver-forgiving pair would excuse real stalls. Both ends
      // read the same BRIDGE_CONTINUOUS_PCM switch.
      readonly bool forgivenessEnabled;
      public SourceClassifier() : this(!ContinuousPcmRequested()) {}
      public SourceClassifier(bool enableForgiveness) { forgivenessEnabled = enableForgiveness; }
      public static bool ContinuousPcmRequested() {
        return Environment.GetEnvironmentVariable("BRIDGE_CONTINUOUS_PCM") == "1";
      }
      public bool ForgivenessEnabled { get { return forgivenessEnabled; } }

      int silentRun;
      bool confirmed;

      public bool Confirmed { get { return confirmed; } }
      public int SilentRun { get { return silentRun; } }

      // A new connection must observe a fresh qualifying tail before it may confirm anything;
      // carrying confirmation across a reconnect would excuse the first gap of a new stream.
      public void Reset() { silentRun = 0; confirmed = false; }

      // Scan splits [offset, offset+bytes) into runs. Returns the number of spans written.
      // Pure: no clock, no socket, no ring. `spans` must hold at least 4 entries per frame
      // boundary the caller expects; in practice a block alternates rarely, and the caller
      // flushes when full.
      public int Scan(byte[] b, int offset, int bytes, SourceSpan[] spans) {
        int count = 0;
        int i = 0;
        int frames = bytes / SquelchProfile.FrameBytes;
        while (i < frames) {
          bool wasConfirmed = confirmed;
          SourceEdge edge = SourceEdge.None;
          int spanStart = i;

          // Classify this frame, then extend the span while classification is unchanged.
          int at = offset + i * SquelchProfile.FrameBytes;
          bool silent = FrameIsSilent(b, at);

          if (confirmed && !silent) { confirmed = false; silentRun = 0; edge = SourceEdge.Resumed; }
          else if (!confirmed) {
            if (!silent) silentRun = 0;
            else {
              silentRun++;
              if (forgivenessEnabled && silentRun >= SquelchProfile.ConfirmFrames) {
                // The threshold frame itself is media. Emit it as an inserted, debt-paying span
                // of exactly one frame carrying the Confirmed edge, so the caller can place the
                // idle boundary immediately after it.
                confirmed = true;
                edge = SourceEdge.Confirmed;
                if (count < spans.Length) {
                  spans[count].Offset = at;
                  spans[count].Bytes = SquelchProfile.FrameBytes;
                  spans[count].Insert = true;
                  spans[count].PaysDebt = true;
                  spans[count].Active = false;
                  spans[count].Edge = edge;
                  count++;
                }
                i++;
                continue;
              }
            }
          }

          // Extend while the frame's disposition matches this span's.
          bool spanConfirmed = confirmed;
          bool sawActive = !silent;
          int j = i + 1;
          while (j < frames) {
            int aj = offset + j * SquelchProfile.FrameBytes;
            bool sj = FrameIsSilent(b, aj);
            if (spanConfirmed) {
              if (!sj) break;                              // resume ends a confirmed span
            } else {
              if (sj) {
                if (forgivenessEnabled && silentRun + 1 >= SquelchProfile.ConfirmFrames) break;
                silentRun++;
              } else { silentRun = 0; sawActive = true; }
            }
            j++;
          }

          if (count < spans.Length) {
            spans[count].Offset = offset + spanStart * SquelchProfile.FrameBytes;
            spans[count].Bytes = (j - spanStart) * SquelchProfile.FrameBytes;
            spans[count].Insert = !spanConfirmed;
            spans[count].PaysDebt = !spanConfirmed;
            spans[count].Active = !spanConfirmed && sawActive;
            spans[count].Edge = edge;
            count++;
          }
          i = j;
          if (count >= spans.Length) break;
        }
        return count;
      }

      static bool FrameIsSilent(byte[] b, int at) {
        short l = (short)(b[at] | (b[at + 1] << 8));
        short r = (short)(b[at + 2] | (b[at + 3] << 8));
        return SquelchProfile.FrameIsSilent(l, r);
      }
    }

    class FrameAssembler {
      readonly byte[] joined;
      readonly byte[] tail = new byte[4];
      int tailCount, completeBytes;
      public FrameAssembler(int maxRead) { joined = new byte[maxRead + 4]; }
      public int TailBytes { get { return tailCount; } }


      // Assemble carries the split-frame tail and measures the block's peak. It never
      // touches the ring, because the insert decision needs the peak and the peak needs
      // the assembled bytes: the policy must see this block BEFORE it is inserted.
      public byte[] Buffer_ { get { return joined; } }
      public int Assemble(byte[] input, int n) {
        if (tailCount > 0) System.Buffer.BlockCopy(tail, 0, joined, 0, tailCount);
        System.Buffer.BlockCopy(input, 0, joined, tailCount, n);
        int total = tailCount + n;
        completeBytes = total / SquelchProfile.FrameBytes * SquelchProfile.FrameBytes;
        tailCount = total - completeBytes;
        if (tailCount > 0) System.Buffer.BlockCopy(joined, completeBytes, tail, 0, tailCount);
        return completeBytes;
      }

    }

    class OutputChain {
      public MMDevice Device;
      public IDisposable Resampler;
      public WasapiOut Out;
      public string Mode = "unknown";
      public int LatencyMs;
      // Retired marks a teardown WE chose. NAudio raises PlaybackStopped from the playback
      // thread's finally block, so it can land after the reopen has already cleared the
      // request flag; without this guard that late event re-armed reconciliation and the
      // reopen fed itself.
      public volatile bool Retired;
      public void Dispose() {
        Retired = true;   // must precede Stop(): the handler races this
        try { if (Out != null) Out.Stop(); } catch {}
        try { if (Out != null) Out.Dispose(); } catch {}
        try { if (Resampler != null) Resampler.Dispose(); } catch {}
        // MMDevice.Dispose() (NAudio 1.10) releases only the endpoint-volume and meter
        // interfaces -- the AudioSessionManager it cached in BindAndUnmute is NOT among
        // them, so release it by hand or its COM refs accumulate in audiodg.exe on every
        // device change.
        try { if (Device != null) Device.AudioSessionManager.Dispose(); } catch {}
        try { if (Device != null) Device.Dispose(); } catch {}
      }
    }

    static bool SameFmt(WaveFormat a, WaveFormat b) {
      return a.SampleRate == b.SampleRate && a.Channels == b.Channels &&
             a.BitsPerSample == b.BitsPerSample && a.Encoding == b.Encoding;
    }

    static OutputChain OpenDefault(AdaptivePlayout provider) {
      var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
      var mix = dev.AudioClient.MixFormat;
      Console.Error.WriteLine("default device: " + dev.FriendlyName +
        " | mix=" + mix.SampleRate + "/" + mix.BitsPerSample + "/" + mix.Channels + " " + mix.Encoding);
      // Feed the raw 48k/16/2 stream straight to WasapiOut and let its OWN internal
      // (DMO) resampler convert to the device mix format. An EXPLICIT
      // MediaFoundationResampler->mix metered a peak but rendered INAUDIBLE to the
      // BT endpoint; the implicit WasapiOut path is what a known-audible local tone used.
      //
      // Event-driven shared mode with a 20 ms buffer, because the render QUANTUM is
      // what the reserve has to cover: NAudio's polling path sleeps latency/2 and then
      // pulled ~64 ms per request, so a nominal 140 ms target left only ~76 ms behind
      // the callback. Event-driven waits on the endpoint's own notification instead.
      // If the endpoint refuses event mode we fall back LOUDLY to the known-audible
      // polling path rather than silently rendering on an untested configuration.
      WasapiOut wo = null;
      string mode = "event20";
      int latencyMs = 20;
      try {
        wo = new WasapiOut(dev, AudioClientShareMode.Shared, true, latencyMs);
        wo.Init(provider);
      } catch (Exception e) {
        Console.Error.WriteLine("event=render_mode_fallback reason=\"" + e.Message.Replace('"', '\'') + "\"");
        try { if (wo != null) wo.Dispose(); } catch {}
        latencyMs = 100;
        wo = new WasapiOut(dev, AudioClientShareMode.Shared, false, latencyMs);
        wo.Init(provider);
        mode = "polling_fallback";
      }
      var chain = new OutputChain();
      chain.Device = dev; chain.Resampler = null; chain.Out = wo;
      chain.Mode = mode; chain.LatencyMs = latencyMs;
      string boundId = dev.ID;   // captured: reading dev.ID after disposal can throw
      wo.PlaybackStopped += delegate(object s, StoppedEventArgs e) {
        // A teardown we asked for raises this too. Treating that as a device change is what
        // made the reopen re-arm the flag that caused it.
        if (!ShouldReconcileOnStop(chain)) return;
        if (e.Exception != null) Console.Error.WriteLine("stopped: " + e.Exception.Message);
        RequestReconcile("playback_stopped", boundId);
      };
      wo.Play();
      BindAndUnmute(dev);
      Console.Error.WriteLine("playing WASAPI shared, mode=" + mode + ", buffer_ms=" + latencyMs +
        ", state=" + wo.PlaybackState);
      // A new endpoint has its own period; request/gap history from the old one would
      // misdescribe it. The network-side reserve estimate is intentionally preserved.
      provider.BeginQuantumEpoch();
      return chain;
    }

    // Every AudioSessionControl handed out by SessionCollection[i] is a COM wrapper that
    // must be released explicitly. Leaving them to the GC leaked handles into audiodg.exe:
    // this loop enumerates EVERY session on the endpoint up to 30 times per call, and the
    // call itself re-runs on every device change (BT reconnect, default-device switch,
    // PlaybackStopped), so the leak arrives in bursts of 30 x session-count.
    static void BindAndUnmute(MMDevice dev) {
      uint pid = (uint)Process.GetCurrentProcess().Id;
      for (int t = 0; t < 30; t++) {
        try {
          var mgr = dev.AudioSessionManager;   // cached on the MMDevice; freed in OutputChain.Dispose
          mgr.RefreshSessions();
          var ss = mgr.Sessions;
          for (int i = 0; i < ss.Count; i++) {
            var s = ss[i];
            bool mine = false;
            try {
              mine = (s.GetProcessID == pid);
              if (mine) {
                try { s.DisplayName = "Hearken"; } catch {}
                float v = s.SimpleAudioVolume.Volume; bool m = s.SimpleAudioVolume.Mute;
                Console.Error.WriteLine("session: vol=" + v.ToString("0.00") + " mute=" + m);
                if (m) s.SimpleAudioVolume.Mute = false;
                if (v < 0.05f) s.SimpleAudioVolume.Volume = 1.0f;
              }
            } finally {
              // Releasing the control wrapper does not touch the session itself, so this
              // is safe to do even for the session we just un-muted.
              try { s.Dispose(); } catch {}
            }
            if (mine) return;
          }
        } catch (Exception e) { Console.Error.WriteLine("session inspect: " + e.Message); }
        Thread.Sleep(50);
      }
      Console.Error.WriteLine("WARN: no audio session for pid " + pid);
    }

    // ApplyGain scales s16le samples in-place (clamped). Used for 0-100% playback volume.
    static void ApplyGain(byte[] b, int n, float g) { ApplyGain(b, 0, n, g); }
    static void ApplyGain(byte[] b, int offset, int n, float g) {
      for (int i = offset; i + 1 < offset + n; i += 2) {
        short s = (short)(b[i] | (b[i + 1] << 8));
        int v = (int)(s * g);
        if (v > 32767) v = 32767; else if (v < -32768) v = -32768;
        b[i] = (byte)(v & 0xff);
        b[i + 1] = (byte)((v >> 8) & 0xff);
      }
    }

    // PolicyExchange makes the network thread the sole reader AND writer of ArrivalPolicy.
    //
    // Before this, the telemetry thread called Recompute() -- which writes prebufferMs,
    // armedPrebufferMs, stableSinceTicks, lastLowerTicks and the raise/lower counters -- and also
    // read PrebufferMs and Describe(), while the network thread wrote the same object from
    // OnRead/AdoptArmed/ApplyIdleTarget. The class comment above ArrivalPolicy claimed "It runs
    // ONLY on the network thread", which is precisely why the race went unseen. The silence state
    // machine that follows is edge-sensitive and must not be built over it.
    //
    // Telemetry now only measures the provider and hands the numbers over; the network thread
    // recomputes and hands back a formatted description for the log line.
    class PolicyExchange {
      readonly object gate = new object();
      int quantumFrames;
      bool underrun, havePending;
      string description = "";

      // Telemetry -> network. The quantum is latest-wins, but the underrun flag ACCUMULATES:
      // TakeRequestQuantum reports "since the last call", so overwriting it would silently drop a
      // starvation that the raise path is the only consumer of.
      public void PublishMeasurement(int q, bool u) {
        lock (gate) { quantumFrames = q; underrun = underrun || u; havePending = true; }
      }
      public bool TakeMeasurement(out int q, out bool u) {
        lock (gate) {
          q = quantumFrames; u = underrun;
          bool had = havePending;
          havePending = false; underrun = false;
          return had;
        }
      }
      // Network -> telemetry. A snapshot string, so the telemetry thread never touches policy.
      public void PublishDescription(string d) { lock (gate) { description = d; } }
      public string TakeDescription() { lock (gate) { return description; } }
    }

    // OnConfirmedEntry is the whole decision taken when the source is confirmed silent, factored
    // out so the branch itself is under test rather than only the two methods it calls.
    //
    // With the output open the boundary goes into the audio and playback drains to it. With the
    // output never opened there is no listener to drain to, so buffered pre-silence audio is
    // merely stale: keeping it would play a fragment of an old phrase after the next talkspurt,
    // a silence of arbitrary length later. The observation is forgotten with it, or the next
    // talkspurt would open on evidence gathered before that silence.
    static void OnConfirmedEntry(AdaptivePlayout provider, ArrivalPolicy policy,
                                 bool outputOpen, long transitionId) {
      if (outputOpen) {
        provider.MarkIdleStart(transitionId);
      } else {
        provider.ClearForColdStart();
        policy.ResetColdObservation();
      }
    }

    static void RunPlay(NetworkStream net) {
      var provider = new AdaptivePlayout();
      var policy = new ArrivalPolicy();
      var classifier = new SourceClassifier();
      var spanBuf = new SourceSpan[256];
      long sourceTransitionId = 0;
      OutputChain chain = null;
      byte[] tmp = new byte[SourceFmt.AverageBytesPerSecond / 20]; // ~50ms
      var assembler = new FrameAssembler(tmp.Length);
      long intervalBytes = 0, lastRead = 0, lastHardRebuffer = 0;
      long lastWatchdogTicks = 0;
      long watchdogTicks = Stopwatch.Frequency * 5;   // endpoint-drift check, 5 s
      double maxReadGapMs = 0;
      int outputGeneration = 0;
      string endpoint = "none";
      var sw = Stopwatch.StartNew();
      double ticksPerMs = Stopwatch.Frequency / 1000.0;
      var telemetryStop = new ManualResetEvent(false);
      var telemetryGate = new object();

      // Telemetry runs on its own thread. Emitting it from the read loop meant no line
      // was produced while a read was stalled -- exactly the interval worth seeing --
      // which is the same blind spot the sender had.
      var exchange = new PolicyExchange();
      exchange.PublishDescription(policy.Describe());   // so the first log line is not blank
      var telemetry = new Thread(delegate() {
        long lastEmit = sw.ElapsedMilliseconds;
        while (!telemetryStop.WaitOne(1000)) {
          long now = sw.ElapsedMilliseconds;
          bool underrun;
          int quantum = provider.TakeRequestQuantum(out underrun);
          long bytesNow; double gapNow; string mode; string state; string ep; int gen; int devBuf;
          lock (telemetryGate) {
            bytesNow = intervalBytes; intervalBytes = 0;
            gapNow = maxReadGapMs; maxReadGapMs = 0;
            ep = endpoint; gen = outputGeneration;
            mode = chain == null ? "none" : chain.Mode;
            devBuf = chain == null ? 0 : chain.LatencyMs;
            state = chain == null ? "prebuffer" : chain.Out.PlaybackState.ToString();
          }
          // Hand the measurement to the network thread rather than mutating policy here.
          exchange.PublishMeasurement(quantum, underrun);
          Console.Error.WriteLine(provider.TakeTelemetry(bytesNow, now - lastEmit, gapNow,
            state, ep, gen, mode, devBuf, exchange.TakeDescription()));
          lastEmit = now;
        }
      });
      telemetry.IsBackground = true;
      telemetry.Start();

      try {
        while (true) {
          // Latched BEFORE blocking. Reaching the confirmation threshold inside the read that
          // closes a gap must never retroactively excuse that gap.
          bool gapStartedConfirmed = classifier.Confirmed;
          int n = net.Read(tmp, 0, tmp.Length);
          if (n <= 0) break;
          long readTicks = Stopwatch.GetTimestamp();
          long readAt = sw.ElapsedMilliseconds;
          // Assemble WITHOUT gain: classification must read source PCM, so that a low
          // playback gain can never make program audio look like silence (R10). Gain is
          // applied per span, and only to spans that will actually be inserted.
          int complete = assembler.Assemble(tmp, n);

          // The gap in FRONT of this read is judged by the state that existed before the
          // read blocked -- never by what the returning bytes turn out to contain.
          policy.OnGap(readTicks, gapStartedConfirmed, provider.RatioSnapshot);

          int spanCount = classifier.Scan(assembler.Buffer_, 0, complete, spanBuf);
          for (int si = 0; si < spanCount; si++) {
            int frames = spanBuf[si].Bytes / SquelchProfile.FrameBytes;
            // Resume happens BEFORE its own frames are handled: the talkspurt's first frame
            // must not repay debt from the interval that was excused.
            if (spanBuf[si].Edge == SourceEdge.Resumed) {
              policy.ResumeDebt(readTicks);
              policy.AdoptArmed(provider.BufferedFrames, true);
              // The target and the boundary are published before the talkspurt's own frames, so
              // the render callback can never see resumed state without the audio behind it.
              provider.PublishTarget(policy.PrebufferMs);
              provider.MarkTalkspurt(++sourceTransitionId);
            }
            if (spanBuf[si].Insert) {
              if (gGain != 1.0f) ApplyGain(assembler.Buffer_, spanBuf[si].Offset, spanBuf[si].Bytes, gGain);
              provider.AddFrames(assembler.Buffer_, spanBuf[si].Offset, spanBuf[si].Bytes);
            }
            policy.OnMedia(readTicks, frames, spanBuf[si].Active);
            // The threshold frame is media and has already been inserted; only now does the
            // source count as confirmed.
            if (spanBuf[si].Edge == SourceEdge.Confirmed) {
              policy.SuspendDebt(readTicks);
              policy.ApplyIdleTarget(provider.RequestQuantumFrames);
              provider.PublishTarget(policy.PrebufferMs);
              OnConfirmedEntry(provider, policy, chain != null, ++sourceTransitionId);
            }
          }
          if (!classifier.Confirmed && spanCount > 0) {
            if (policy.AdoptArmed(provider.BufferedFrames, false)) provider.PublishTarget(policy.PrebufferMs);
          }
          // A hard rebuffer is the design's other adoption point, and the load-bearing one:
          // on a degrading path occupancy never reaches a raised target by itself, so
          // waiting for it meant the receiver could measure that it needed a bigger buffer
          // and never take it -- 14 rebuffers in 22 s while the raise sat armed. We are
          // already paying the interruption, so take it here.
          long hardNow = provider.HardRebufferCount;
          if (hardNow != lastHardRebuffer) {
            lastHardRebuffer = hardNow;
            if (policy.AdoptArmed(provider.BufferedFrames, true)) provider.PublishTarget(policy.PrebufferMs);
          }
          // Recompute runs HERE, on the thread that owns the policy, once per published
          // measurement (~1 s). Placed after the adoption points above so it sees the state they
          // just produced, and after the read so the target reflects this block's arrival.
          int measQuantum; bool measUnderrun;
          if (exchange.TakeMeasurement(out measQuantum, out measUnderrun)) {
            policy.Recompute(readTicks, measQuantum, measUnderrun);
            provider.PublishTarget(policy.PrebufferMs);
            exchange.PublishDescription(policy.Describe());
          }
          // A block the assembler withheld (squelch keepalive) must not be re-inserted
          // later, so alignment is preserved by Push regardless of the insert decision.
          if (complete == 0 && n > 0 && assembler.TailBytes == 0)
            Console.Error.WriteLine("event=empty_push bytes=" + n);
          lock (telemetryGate) {
            intervalBytes += n;
            if (lastRead != 0 && readAt - lastRead > maxReadGapMs) maxReadGapMs = readAt - lastRead;
          }
          lastRead = readAt;

          // Cold start: no fixed measurement pause. Keep measuring while prebuffering and
          // open as soon as the path is characterised (three active reads, or 150 ms of
          // program observed, hard-capped at 400 ms) AND the buffer has reached the target
          // that measurement produced. Never open into a squelched stream.
          if (chain == null && !classifier.Confirmed && policy.ObservationSatisfied(readTicks) &&
              provider.BufferedFrames >= provider.PrebufferFrames) {
            reconcileRequested = false;
            // The first open is an outage too: nothing drains the ring until the endpoint is
            // live, so anything discarded here belongs to the transition, not to the network.
            provider.BeginOutputOutage();
            long openStart = Stopwatch.GetTimestamp();
            try {
              chain = OpenDefault(provider); // open AFTER prebuffer so first buffers are real audio
            } finally {
              provider.EndOutputOutage();
            }
            outputGeneration++;
            endpoint = chain.Device.ID;
            activeEndpointId = endpoint;
            Console.Error.WriteLine("event=output_open kind=cold_start open_ms=" +
              ((Stopwatch.GetTimestamp() - openStart) * 1000.0 / Stopwatch.Frequency).ToString("0.0", CultureInfo.InvariantCulture) +
              " endpoint=" + endpoint);
            reconcileRequested = false;    // discard anything our own open raised
          } else if (chain != null) {
            // Watchdog against over-filtering. If a notification we declined to act on did
            // matter, the desired default and the endpoint we are on disagree, and this
            // notices without needing the notification at all. Cheap because it is rate-limited.
            if (readTicks - lastWatchdogTicks >= watchdogTicks) {
              lastWatchdogTicks = readTicks;
              string drift = DesiredEndpointId();
              if (drift != null && activeEndpointId != null && drift != activeEndpointId)
                RequestReconcile("watchdog_drift", drift);
            }
            bool stopped = chain.Out.PlaybackState == PlaybackState.Stopped;
            if (reconcileRequested || stopped) {
              reconcileRequested = false;
              string desired = DesiredEndpointId();
              // Idempotent: a notification that does not change which endpoint we should be on,
              // against a player that is still running, is not a reason to interrupt audio.
              // This is where a burst of notifications collapses into one decision.
              if (!stopped && desired != null && desired == activeEndpointId) {
                reconcileNoops++;
                Console.Error.WriteLine("event=output_reconcile_noop reason=" + lastNotify +
                  " endpoint=" + desired);
              } else {
                Console.Error.WriteLine("event=output_reopen reason=" +
                  (stopped ? "player_stopped" : lastNotify) +
                  " from=" + (activeEndpointId ?? "") + " to=" + (desired ?? "") +
                  " generation=" + outputGeneration);
                // Audio discarded while the endpoint is down is an output outage, not the
                // network running late. Charging it to network freshness is what made a device
                // problem read as a transport problem.
                provider.BeginOutputOutage();
                long reopenStart = Stopwatch.GetTimestamp();
                try {
                  chain.Dispose();
                  chain = OpenDefault(provider);
                  outputGeneration++;
                  endpoint = chain.Device.ID;
                  activeEndpointId = endpoint;
                } finally {
                  provider.EndOutputOutage();
                }
                Console.Error.WriteLine("event=output_open kind=reopen open_ms=" +
                  ((Stopwatch.GetTimestamp() - reopenStart) * 1000.0 / Stopwatch.Frequency).ToString("0.0", CultureInfo.InvariantCulture) +
                  " endpoint=" + endpoint);
                reconcileRequested = false;  // our own teardown/open must not re-trigger us
              }
            }
          }
        }
      } finally {
        telemetryStop.Set();
        try { telemetry.Join(2000); } catch {}
        if (assembler.TailBytes != 0) Console.Error.WriteLine("event=partial_frame_discard bytes=" + assembler.TailBytes);
        if (chain != null) chain.Dispose();
      }
    }

    // BuildStream lays out [activeBefore][silent][activeAfter] stereo frames. Silent frames sit
    // at the inclusive threshold (both channels == SilencePeak) so the test also pins the
    // comparison as <=, not <.
    static byte[] BuildStream(int activeBefore, int silent, int activeAfter) {
      int frames = activeBefore + silent + activeAfter;
      byte[] b = new byte[frames * SquelchProfile.FrameBytes];
      for (int f = 0; f < frames; f++) {
        bool quiet = f >= activeBefore && f < activeBefore + silent;
        short v = quiet ? SquelchProfile.SilencePeak : (short)3000;
        int at = f * SquelchProfile.FrameBytes;
        b[at] = (byte)(v & 0xff); b[at + 1] = (byte)((v >> 8) & 0xff);
        b[at + 2] = (byte)(v & 0xff); b[at + 3] = (byte)((v >> 8) & 0xff);
      }
      return b;
    }

    // ClassifyStream feeds one stream through a fresh classifier in fixed-size frame chunks
    // (0 = one call) and reports the transitions by ABSOLUTE frame index. Identical audio must
    // produce identical indices for every chunk shape; that is the whole point.
    static void ClassifyStream(int activeBefore, int silent, int activeAfter, int framesPerChunk,
                               out int confirms, out int resumes, out int confirmAt, out int resumeAt) {
      byte[] b = BuildStream(activeBefore, silent, activeAfter);
      var c = new SourceClassifier();
      var spans = new SourceSpan[512];
      confirms = resumes = 0; confirmAt = resumeAt = -1;
      int totalFrames = b.Length / SquelchProfile.FrameBytes;
      int step = framesPerChunk <= 0 ? totalFrames : framesPerChunk;
      for (int f = 0; f < totalFrames; ) {
        int take = Math.Min(step, totalFrames - f);
        int n = c.Scan(b, f * SquelchProfile.FrameBytes, take * SquelchProfile.FrameBytes, spans);
        for (int i = 0; i < n; i++) {
          int absFrame = spans[i].Offset / SquelchProfile.FrameBytes;
          if (spans[i].Edge == SourceEdge.Confirmed) { confirms++; confirmAt = absFrame; }
          else if (spans[i].Edge == SourceEdge.Resumed) { resumes++; resumeAt = absFrame; }
        }
        f += take;
      }
    }

    // DriveRead mirrors the read loop's ordering exactly: latch the gap verdict BEFORE the
    // read, charge the gap, then walk the spans in stream order, resuming before a talkspurt's
    // own frames and confirming only after the threshold frame has been counted as media.
    static void DriveRead(ArrivalPolicy pol, SourceClassifier cls, SourceSpan[] spans,
                          long atTicks, byte[] pcm, int offset, int bytes) {
      bool gapStartedConfirmed = cls.Confirmed;
      pol.OnGap(atTicks, gapStartedConfirmed, 1.0);
      int n = cls.Scan(pcm, offset, bytes, spans);
      for (int i = 0; i < n; i++) {
        if (spans[i].Edge == SourceEdge.Resumed) pol.ResumeDebt(atTicks);
        pol.OnMedia(atTicks, spans[i].Bytes / SquelchProfile.FrameBytes, spans[i].Active);
        if (spans[i].Edge == SourceEdge.Confirmed) pol.SuspendDebt(atTicks);
      }
    }

    // The suite must assert its own INVENTORY, not just its result. Four cases were silently
    // deleted during this build by wholesale region replacement, and the suite kept reporting ok
    // throughout, because a deleted test cannot fail. Every case registers by name and the run
    // fails if the set ever differs from ExpectedCases -- so losing one is as loud as breaking one.
    static readonly System.Collections.Generic.List<string> ranCases =
      new System.Collections.Generic.List<string>();
    static void Case(string id) {
      if (ranCases.Contains(id)) throw new Exception("duplicate self-test case id: " + id);
      ranCases.Add(id);
    }
    static readonly string[] ExpectedCases = new string[] {
      "exchange", "profile-mode", "R1", "R5", "R6", "R10", "trim-attribution",
      "no-discard", "cadence", "cadence-teeth",
      "A1", "A2", "A3", "A5", "A6", "C1", "C2",
      "R2", "R3", "R4", "R7", "R9", "P1", "P2", "P3", "P4",
      "clean-feed", "forced-adopt", "freshness",
    };
    static void AssertInventory() {
      var missing = new System.Collections.Generic.List<string>();
      foreach (string id in ExpectedCases) if (!ranCases.Contains(id)) missing.Add(id);
      var extra = new System.Collections.Generic.List<string>();
      foreach (string id in ranCases) if (Array.IndexOf(ExpectedCases, id) < 0) extra.Add(id);
      if (missing.Count > 0 || extra.Count > 0)
        throw new Exception("self-test inventory changed - missing [" + string.Join(",", missing.ToArray()) +
          "] unexpected [" + string.Join(",", extra.ToArray()) + "]. A deleted case is a failure.");
    }

    static void RunSelfTest() {
      long perSec = Stopwatch.Frequency;
      const int HysteresisForTest = 20;
      // R1 / R5: the exact confirmation boundary, and its independence from read shape. These
      // two were WRITTEN, FALSIFIED, AND THEN SILENTLY DELETED by a later region replacement --
      // ClassifyStream was left defined and never called. Restored.
      Case("R1"); Case("R5");
      {
        const int activeBefore = 100, activeAfter = 50;
        int[] chunkShapes = new int[] { 1, 2, 3, 7, 257, 4096, 0 };   // frames per Scan; 0 = all
        foreach (int shape in chunkShapes) {
          int confirms, resumes, confirmAt, resumeAt;
          ClassifyStream(activeBefore, SquelchProfile.ConfirmFrames - 1, activeAfter, shape,
            out confirms, out resumes, out confirmAt, out resumeAt);
          if (confirms != 0)
            throw new Exception("11,999 silent frames must remain qualifying, not confirm");
        }
        int expectConfirm = activeBefore + SquelchProfile.ConfirmFrames - 1;
        int expectResume = activeBefore + SquelchProfile.ConfirmFrames;
        foreach (int shape in chunkShapes) {
          int confirms, resumes, confirmAt, resumeAt;
          ClassifyStream(activeBefore, SquelchProfile.ConfirmFrames, activeAfter, shape,
            out confirms, out resumes, out confirmAt, out resumeAt);
          if (confirms != 1)
            throw new Exception("frame 12,000 must confirm exactly once, got " + confirms);
          if (confirmAt != expectConfirm)
            throw new Exception("confirmation landed on frame " + confirmAt + ", expected " +
              expectConfirm + " (chunk shape " + shape + ") - read shape must not move it");
          if (resumes != 1 || resumeAt != expectResume)
            throw new Exception("resume must land exactly on the first active frame after silence");
        }
        // A silent run broken by one active frame restarts; two half-runs never add up.
        var c = new SourceClassifier(); var spans = new SourceSpan[64];
        byte[] half = BuildStream(0, SquelchProfile.ConfirmFrames / 2, 1);
        c.Scan(half, 0, half.Length, spans);
        if (c.Confirmed) throw new Exception("half a qualifying run must not confirm");
        if (c.SilentRun != 0) throw new Exception("an active frame must reset the silent run");
        byte[] second = BuildStream(0, SquelchProfile.ConfirmFrames / 2, 0);
        c.Scan(second, 0, second.Length, spans);
        if (c.Confirmed)
          throw new Exception("two half-runs split by program must not add up to a confirmation");
        // The threshold frame is media and is inserted; only what follows is discardable.
        var t2 = new SourceClassifier();
        byte[] tail2 = BuildStream(0, SquelchProfile.ConfirmFrames + 480, 0);
        int n2 = t2.Scan(tail2, 0, tail2.Length, spans);
        bool sawDiscard = false;
        for (int i = 0; i < n2; i++) {
          if (spans[i].Edge == SourceEdge.Confirmed && !spans[i].Insert)
            throw new Exception("the threshold frame is media and must still be inserted");
          if (!spans[i].Insert) {
            sawDiscard = true;
            if (spans[i].PaysDebt) throw new Exception("confirmed silence must not pay down arrival debt");
          }
        }
        if (!sawDiscard) throw new Exception("silence past the threshold must be discarded");
      }

      // The measurement handoff between telemetry and network threads. Also deleted and restored:
      // the underrun flag reports "since the last call", so latest-wins would drop a starvation
      // from the only path that raises the target for one.
      Case("exchange");
      {
        var ex = new PolicyExchange();
        int exQ; bool exU;
        if (ex.TakeMeasurement(out exQ, out exU))
          throw new Exception("an empty exchange must report no pending measurement");
        ex.PublishMeasurement(480, false);
        ex.PublishMeasurement(512, true);
        ex.PublishMeasurement(480, false);
        if (!ex.TakeMeasurement(out exQ, out exU))
          throw new Exception("a published measurement must be pending");
        if (exQ != 480) throw new Exception("the render quantum must be latest-wins");
        if (!exU) throw new Exception("an underrun must survive a later clean publish (raise evidence)");
        if (ex.TakeMeasurement(out exQ, out exU))
          throw new Exception("a consumed measurement must not be pending again");
        ex.PublishMeasurement(480, false);
        ex.TakeMeasurement(out exQ, out exU);
        if (exU) throw new Exception("an underrun must clear on the take, not be counted twice");
      }

      // A trim taken while the endpoint is down is attributed to the transition, so a device
      // problem can never be counted as the network delivering late. Deleted and restored.
      Case("trim-attribution");
      {
        var outage = Primed(Audio.Frames(100));
        outage.PublishTarget(Audio.MinPrebufferMs);
        outage.BeginOutputOutage();
        outage.AddFrames(MakeTone(Audio.Frames(400)), 0, Audio.Frames(400) * 4);
        outage.EndOutputOutage();
        if (outage.FreshnessTrimCount == 0)
          throw new Exception("a stale backlog must still be trimmed during an outage");
        if (outage.FreshnessTrimTransitionCount != outage.FreshnessTrimCount)
          throw new Exception("a trim during an output transition must be attributed to the transition");
      }

      Case("R6");
      // R6: a partial frame advances nothing. One, two or three bytes must not classify, must
      // not move the arrival clock, and must not start or finish qualification -- the final
      // byte completes exactly one frame. This is what makes a one-byte read and a coalesced
      // read semantically identical.
      {
        var assembler = new FrameAssembler(64);
        byte[] oneFrame = new byte[] { 0xd0, 0x07, 0x30, 0xf8 }; // +2000, -2000
        if (assembler.Assemble(oneFrame, 1) != 0)
          throw new Exception("one byte cannot complete a stereo frame");
        if (assembler.TailBytes != 1) throw new Exception("the partial byte must be carried");
        if (assembler.Assemble(oneFrame, 2) != 0)
          throw new Exception("three bytes still cannot complete a stereo frame");
        if (assembler.Assemble(oneFrame, 1) != SquelchProfile.FrameBytes)
          throw new Exception("the fourth byte must complete exactly one frame");
        if (assembler.TailBytes != 0) throw new Exception("no tail may remain after a whole frame");

        // Driving the classifier a byte at a time must reach the identical verdict as one
        // whole-block read: read shape is not evidence.
        var byteCls = new SourceClassifier(); var byteAsm = new FrameAssembler(64);
        var sp = new SourceSpan[16];
        byte[] stream = BuildStream(0, 3, 1);          // 3 silent frames then program
        int completedFrames = 0, activeSpans = 0;
        byte[] one = new byte[1];
        for (int i = 0; i < stream.Length; i++) {
          one[0] = stream[i];
          int done = byteAsm.Assemble(one, 1);
          if (done > 0) {
            completedFrames += done / SquelchProfile.FrameBytes;
            int n = byteCls.Scan(byteAsm.Buffer_, 0, done, sp);
            for (int k = 0; k < n; k++) if (sp[k].Active) activeSpans++;
          }
        }
        if (completedFrames != 4) throw new Exception("byte-at-a-time must complete exactly 4 frames");
        if (activeSpans != 1) throw new Exception("byte-at-a-time must still see exactly one program run");
        if (byteCls.SilentRun != 0) throw new Exception("program must reset the silent run");
      }

      {
        byte[] gained = new byte[] { 0xd0, 0x07, 0x30, 0xf8 };
        ApplyGain(gained, gained.Length, 0.5f);
        short gl = (short)(gained[0] | (gained[1] << 8));
        short gr = (short)(gained[2] | (gained[3] << 8));
        if (gl != 1000 || gr != -1000) throw new Exception("gain test failed");
      }

      Case("R10");
      // R10: classification reads SOURCE pcm, before gain. At zero gain every inserted sample
      // is silent, and classifying post-gain audio would declare the source squelched and start
      // forgiving real network gaps.
      {
        var cls = new SourceClassifier(); var sp = new SourceSpan[16];
        byte[] program = BuildStream(0, 0, 480);
        int n = cls.Scan(program, 0, program.Length, sp);
        bool sawActive = false;
        for (int i = 0; i < n; i++) if (sp[i].Active) sawActive = true;
        if (!sawActive) throw new Exception("program must classify as active");
        ApplyGain(program, 0, program.Length, 0.0f);      // silence it AFTER classification
        var post = new SourceClassifier();
        int m = post.Scan(program, 0, program.Length, sp);
        bool postActive = false;
        for (int i = 0; i < m; i++) if (sp[i].Active) postActive = true;
        if (postActive)
          throw new Exception("test is vacuous: zeroed audio must read as silent");
        if (post.SilentRun == 0)
          throw new Exception("test is vacuous: zeroed audio must accumulate a silent run");
      }

      // Steady-state fidelity: after the opening fade, reconstruction is exact.
      var provider = Primed(Audio.Frames(300));
      byte[] output = new byte[480 * 4];
      if (provider.Read(output, 0, output.Length) != output.Length)
        throw new Exception("adaptive provider returned a short block");
      short outL = (short)(output[0] | (output[1] << 8));
      short outR = (short)(output[2] | (output[3] << 8));
      if (outL != 1000 || outR != -1000) throw new Exception("adaptive provider sample test failed");

      Case("no-discard");
      // The load-bearing regression: an underrun must NOT discard buffered audio.
      // The old EnterStarved zeroed head/frames/phase, so a shortage of a few frames
      // threw away everything already received and forced a full refill.
      var keep = Primed(Audio.Frames(100));
      int before = keep.BufferedFrames;
      byte[] big = new byte[Audio.Frames(200) * 4];
      keep.Read(big, 0, big.Length);                 // request far exceeds occupancy
      if (keep.BufferedFrames != before)
        throw new Exception("underrun discarded buffered audio (the defect this rewrite removes)");
      if (keep.HardRebufferCount != 1) throw new Exception("large shortage must count one rebuffer");

      // A shortage inside the fade budget is concealed in place, not escalated.
      var soft = Primed(Audio.Frames(50));
      int b = soft.BufferedFrames;
      byte[] softOut = new byte[(b + 2) * 4];         // needed = b + 3 => 3 frames short
      soft.Read(softOut, 0, softOut.Length);
      if (soft.SoftUnderrunCount != 1 || soft.HardRebufferCount != 0)
        throw new Exception("small shortage must conceal, not rebuffer");

      Case("cadence");
      // The measured failure, replayed: media arrives in ~105 ms bursts (the cadence
      // seen on both days of the 2026-08-06 court) while the device pulls it back in
      // small quanta. A target that covers burst + quantum + margin must not starve.
      const int CadenceMs = 105, QuantumMs = 21, Cycles = 40;
      int delivery = Audio.Frames(CadenceMs);
      byte[] pull = new byte[Audio.Frames(QuantumMs) * 4];
      var sized = new AdaptivePlayout();
      sized.PublishTarget(CadenceMs + QuantumMs + Audio.ReserveMarginMs);
      for (int c = 0; c < Cycles; c++) {
        sized.AddFrames(MakeTone(delivery), 0, delivery * 4);
        for (int r = 0; r < CadenceMs / QuantumMs; r++) sized.Read(pull, 0, pull.Length);
      }
      if (sized.HardRebufferCount != 0 || sized.SoftUnderrunCount != 0)
        throw new Exception("sized target must survive the measured 105 ms burst cadence");

      Case("cadence-teeth");
      // Teeth for the check above: the shipped 40 ms floor cannot cover that cadence,
      // so the same replay MUST starve. If this ever stops starving, the test is vacuous.
      var undersized = new AdaptivePlayout();
      for (int c = 0; c < Cycles; c++) {
        undersized.AddFrames(MakeTone(delivery), 0, delivery * 4);
        for (int r = 0; r < CadenceMs / QuantumMs; r++) undersized.Read(pull, 0, pull.Length);
      }
      if (undersized.HardRebufferCount + undersized.SoftUnderrunCount == 0)
        throw new Exception("undersized target must starve, else the cadence test proves nothing");

      Case("profile-mode");
      // Continuous PCM must be one coordinated mode. With forgiveness off the receiver never
      // confirms, so it never excuses a gap -- which is the only safe pairing with a sender that
      // has stopped suppressing.
      {
        var off = new SourceClassifier(false);
        var sp = new SourceSpan[64];
        byte[] longSilence = BuildStream(0, SquelchProfile.ConfirmFrames * 2, 0);
        off.Scan(longSilence, 0, longSilence.Length, sp);
        if (off.Confirmed)
          throw new Exception("with forgiveness disabled the receiver must never confirm squelch");
        var on = new SourceClassifier(true);
        on.Scan(longSilence, 0, longSilence.Length, sp);
        if (!on.Confirmed)
          throw new Exception("the same fixture must confirm with forgiveness on, else this proves nothing");
      }

      // ---- render timeline: the boundary lives in the audio, not in a flag ---------------
      Case("A1");
      // A1: audio the source sent BEFORE going quiet belongs to the listener. Draining must
      // ignore the prebuffer target -- routing it through the ordinary rebuffer gate holds a
      // below-target ring and replays that tail late, which is exactly the defect.
      {
        var prov = new AdaptivePlayout();
        prov.PublishTarget(Audio.MinPrebufferMs);
        int tail = Audio.Frames(15);                       // deliberately BELOW the 40 ms target
        prov.AddFrames(MakeTone(tail), 0, tail * 4);
        prov.MarkIdleStart(1);
        byte[] outBuf = new byte[Audio.Frames(10) * 4];
        for (int i = 0; i < 8; i++) prov.Read(outBuf, 0, outBuf.Length);
        if (prov.SuppliedFramesForTest < tail - 2)
          throw new Exception("drain must render the pre-silence tail regardless of the target, got " +
            prov.SuppliedFramesForTest + " of " + tail);
        if (prov.HardRebufferCount != 0 || prov.SoftUnderrunCount != 0)
          throw new Exception("reaching a source boundary is not starvation");
        if (prov.RenderModeForTest != (int)RenderMode.IdleSilence)
          throw new Exception("crossing the idle boundary must enter idle silence");
        if (prov.IdleSilenceFrames == 0)
          throw new Exception("past the boundary the listener must get deliberate silence");
      }

      Case("A3");
      // A3: at the talkspurt boundary, withhold until a full target of POST-boundary audio
      // exists, then fade in once. Zero-fill while waiting is intentional, not starvation.
      {
        var prov = new AdaptivePlayout();
        prov.PublishTarget(Audio.MinPrebufferMs);
        prov.MarkIdleStart(1);
        byte[] outBuf = new byte[Audio.Frames(10) * 4];
        prov.Read(outBuf, 0, outBuf.Length);               // cross into idle silence
        prov.MarkTalkspurt(2);
        int trickle = Audio.Frames(10);                    // a quarter of the target
        prov.AddFrames(MakeTone(trickle), 0, trickle * 4);
        prov.Read(outBuf, 0, outBuf.Length);
        if (prov.RenderModeForTest != (int)RenderMode.ResumePrebuffer)
          throw new Exception("a talkspurt boundary must enter resume prebuffer");
        if (prov.SoftUnderrunCount != 0 || prov.HardRebufferCount != 0)
          throw new Exception("waiting for the resume target must not be charged as starvation");
        int rest = Audio.Frames(60);
        prov.AddFrames(MakeTone(rest), 0, rest * 4);
        prov.Read(outBuf, 0, outBuf.Length);
        if (prov.RenderModeForTest != (int)RenderMode.ActivePlaying)
          throw new Exception("a full target of post-boundary audio must resume playback");
      }

      Case("A6");
      // A6: boundaries are idempotent. A repeated heartbeat or a retried batch must not add a
      // second boundary or duplicate a talkspurt.
      {
        var prov = new AdaptivePlayout();
        prov.PublishTarget(Audio.MinPrebufferMs);
        if (!prov.MarkIdleStart(7)) throw new Exception("a fresh transition id must be accepted");
        if (prov.MarkIdleStart(7)) throw new Exception("a repeated transition id must be rejected");
        if (prov.MarkIdleStart(3)) throw new Exception("a stale transition id must be rejected");
        byte[] outBuf = new byte[Audio.Frames(10) * 4];
        prov.Read(outBuf, 0, outBuf.Length);
        if (prov.MarkersCrossedIdleForTest != 1)
          throw new Exception("a duplicated boundary must not be crossed twice, crossed " +
            prov.MarkersCrossedIdleForTest);
      }

      Case("A2");
      // A2: a boundary landing mid-request renders the prefix and zeroes the remainder, without
      // charging the shortfall as starvation. Checking source state once per callback would
      // either replay past the boundary or throw the prefix away.
      {
        var prov = new AdaptivePlayout();
        prov.PublishTarget(Audio.MinPrebufferMs);
        int prefix = 200;                                  // less than one 480-frame request
        prov.AddFrames(MakeTone(prefix), 0, prefix * 4);
        prov.MarkIdleStart(1);
        byte[] outBuf = new byte[Audio.Frames(10) * 4];
        prov.Read(outBuf, 0, outBuf.Length);
        if (prov.SuppliedFramesForTest < prefix - 2 || prov.SuppliedFramesForTest > prefix)
          throw new Exception("a mid-request boundary must render exactly the prefix, got " +
            prov.SuppliedFramesForTest + " of " + prefix);
        if (prov.SoftUnderrunCount != 0 || prov.HardRebufferCount != 0)
          throw new Exception("the zeroed remainder after a boundary is not starvation");
        if (prov.RenderModeForTest != (int)RenderMode.IdleSilence)
          throw new Exception("a mid-request boundary must still be crossed");
      }

      Case("A5");
      // A5: a freshness trim advances the read position and can step over boundaries. If it
      // does so without applying them, render state no longer matches the first retained frame.
      {
        var prov = new AdaptivePlayout();
        prov.PublishTarget(Audio.MinPrebufferMs);
        int some = Audio.Frames(30);
        prov.AddFrames(MakeTone(some), 0, some * 4);
        prov.MarkIdleStart(1);
        if (prov.RenderModeForTest != (int)RenderMode.DrainToIdle)
          throw new Exception("the fixture must start in drain, else the trim proves nothing");
        int trimBurst = Audio.Frames(Audio.MinPrebufferMs + 400);        // forces a trim
        prov.AddFrames(MakeTone(trimBurst), 0, trimBurst * 4);
        if (prov.FreshnessTrimCount == 0)
          throw new Exception("the fixture must actually trim, else A5 is vacuous");
        if (prov.RenderModeForTest == (int)RenderMode.DrainToIdle)
          throw new Exception("a trim that steps over a boundary must apply it, not leave it behind");
      }

      Case("C1"); Case("C2");
      // C1/C2: a receiver whose output never opened must not keep a fragment of an old phrase
      // across a silence and play it after the next talkspurt, and must not open the output on
      // observations gathered before that silence.
      {
        var prov = new AdaptivePlayout();
        var pol = new ArrivalPolicy(); var cls = new SourceClassifier();
        var sp = new SourceSpan[64];
        long t = Stopwatch.GetTimestamp();
        byte[] program = BuildStream(2000, 0, 0);
        for (int i = 0; i < 4; i++) {          // the cold gate needs several active reads
          t += (long)(0.042 * perSec);
          DriveRead(pol, cls, sp, t, program, 0, program.Length);
          prov.AddFrames(program, 0, program.Length);
        }
        if (!pol.ObservationSatisfied(t))
          throw new Exception("program must satisfy the cold observation, else C1 is vacuous");
        if (prov.BufferedFrames == 0)
          throw new Exception("the fixture must have buffered audio, else C1 is vacuous");
        OnConfirmedEntry(prov, pol, false, 1);        // the production branch, output closed
        if (prov.BufferedFrames != 0)
          throw new Exception("a never-opened receiver must drop pre-silence audio, not replay it late");
        if (pol.ObservationSatisfied(t))
          throw new Exception("confirmation-era observation must not open the output for the next talkspurt");
        // Teeth: with the output OPEN the same entry must place a boundary and keep the audio,
        // or this check would pass on a branch that simply always cleared.
        var openProv = new AdaptivePlayout();
        openProv.PublishTarget(Audio.MinPrebufferMs);
        openProv.AddFrames(program, 0, program.Length);
        OnConfirmedEntry(openProv, new ArrivalPolicy(), true, 1);
        if (openProv.BufferedFrames == 0)
          throw new Exception("with the output open, pre-silence audio must be drained, not dropped");
        if (openProv.RenderModeForTest != (int)RenderMode.DrainToIdle)
          throw new Exception("with the output open, confirmation must place a drain boundary");
      }

      Case("R9");
      // R9: confirmation and resumption inside ONE assembled read. Stream order must still give
      // exactly one enter, one exit, a new debt epoch, and no lost active frame.
      {
        var pol = new ArrivalPolicy(); var cls = new SourceClassifier();
        var sp = new SourceSpan[64];
        long t = Stopwatch.GetTimestamp();
        long epochBefore = pol.DebtEpoch;
        byte[] both = BuildStream(0, SquelchProfile.ConfirmFrames, 480);
        int inserted = 0, activeInserted = 0;
        bool gapConfirmed = cls.Confirmed;
        pol.OnGap(t, gapConfirmed, 1.0);
        int n = cls.Scan(both, 0, both.Length, sp);
        for (int i = 0; i < n; i++) {
          if (sp[i].Edge == SourceEdge.Resumed) pol.ResumeDebt(t);
          if (sp[i].Insert) { inserted += sp[i].Bytes / SquelchProfile.FrameBytes;
                              if (sp[i].Active) activeInserted += sp[i].Bytes / SquelchProfile.FrameBytes; }
          pol.OnMedia(t, sp[i].Bytes / SquelchProfile.FrameBytes, sp[i].Active);
          if (sp[i].Edge == SourceEdge.Confirmed) pol.SuspendDebt(t);
        }
        if (pol.SquelchEnterCount != 1 || pol.SquelchExitCount != 1)
          throw new Exception("one read crossing both boundaries must count one enter and one exit, got " +
            pol.SquelchEnterCount + "/" + pol.SquelchExitCount);
        if (pol.DebtEpoch != epochBefore + 1)
          throw new Exception("resuming must open exactly one new debt epoch");
        if (activeInserted != 480)
          throw new Exception("every active frame must survive the crossing, inserted " + activeInserted);
        if (inserted != SquelchProfile.ConfirmFrames + 480)
          throw new Exception("qualifying media and the talkspurt must both be inserted, got " + inserted);
      }

      Case("P3");
      // P3: an arm that the evidence no longer supports expires at confirmation; one it still
      // supports survives to be adopted at the talkspurt.
      {
        var pol = new ArrivalPolicy(); var cls = new SourceClassifier();
        var sp = new SourceSpan[64];
        long t = Stopwatch.GetTimestamp();
        byte[] bursty = BuildStream(9600, 0, 0);
        for (int i = 0; i < 60; i++) {
          t += (long)(0.200 * perSec);
          DriveRead(pol, cls, sp, t, bursty, 0, bursty.Length);
          pol.Recompute(t, Audio.Frames(10), false);
        }
        int warranted = pol.WarrantedForTest(Audio.Frames(10));
        if (warranted <= Audio.MinPrebufferMs)
          throw new Exception("the P3 fixture must warrant a raise, else it proves nothing");
        pol.ApplyIdleTarget(Audio.Frames(10));
        if (pol.PrebufferMs < Audio.MinPrebufferMs)
          throw new Exception("a still-warranted target must not be discarded at confirmation");
      }

      // ---- arrival accounting, driven exactly as production drives it ------------------
      // These replace the old keepalive-era suite. Source state is no longer a Boolean this
      // class owns, and "was the last read silent" is no longer evidence of anything.


      Case("R7");
      // R7: every frame transmitted before confirmation is media and pays down debt, silent or
      // not. The retired rule that only program audio carried lateness is what made an
      // unconfirmed silent stretch look free.
      {
        var pol = new ArrivalPolicy(); var cls = new SourceClassifier();
        var sp = new SourceSpan[64];
        long t = Stopwatch.GetTimestamp();
        byte[] program = BuildStream(480, 0, 0);
        DriveRead(pol, cls, sp, t, program, 0, program.Length);          // establish continuity
        t += (long)(0.100 * perSec);                                     // a 100 ms stall = 4,800 frames
        byte[] quiet = BuildStream(0, 480, 0);                           // answered by silence
        DriveRead(pol, cls, sp, t, quiet, 0, quiet.Length);
        pol.Recompute(t, Audio.Frames(10), false);
        pol.AdoptArmed(Audio.Frames(Audio.MaxPrebufferMs), true);
        if (pol.PrebufferMs <= Audio.MinPrebufferMs)
          throw new Exception("an unconfirmed gap must be charged even when silence answers it");
        // ...and those silent frames are transmitted media, so they PAY THE CHARGE DOWN. A
        // 300 ms gap owes ~14,400 frames; 480 silent frames repay 10 ms of it and the rest must
        // still be outstanding, so deliver enough to clear it and require that it clears.
        double owedAfterFirst = pol.OutstandingDebtMs;
        if (owedAfterFirst < 80)
          throw new Exception("a 100 ms gap must leave a debt carry, got " + owedAfterFirst);
        // Stay under the confirmation threshold: confirming would zero the carry outright and
        // the check below could not tell payment from suspension.
        byte[] payer = BuildStream(0, 5000, 0);
        DriveRead(pol, cls, sp, t, payer, 0, payer.Length);
        if (cls.Confirmed)
          throw new Exception("the R7 fixture must stay unconfirmed, else SuspendDebt zeroes the carry");
        if (pol.OutstandingDebtMs > 1.0)
          throw new Exception("unconfirmed silent frames are media and must pay the debt down, " +
            "still owing " + pol.OutstandingDebtMs);
      }

      Case("R2");
      // R2: silence shorter than the confirmation threshold, then a stall, then program. The
      // stall must be charged. This is the defect the whole phase exists to remove -- one
      // silent block used to forgive it.
      for (int blocks = 1; blocks <= 11; blocks++) {
        var pol = new ArrivalPolicy(); var cls = new SourceClassifier();
        var sp = new SourceSpan[64];
        long t = Stopwatch.GetTimestamp();
        byte[] program = BuildStream(1008, 0, 0);
        DriveRead(pol, cls, sp, t, program, 0, program.Length);
        byte[] shortQuiet = BuildStream(0, blocks * 1008, 0);            // 1..11 ordinary blocks
        t += (long)(0.021 * perSec);
        DriveRead(pol, cls, sp, t, shortQuiet, 0, shortQuiet.Length);
        if (cls.Confirmed)
          throw new Exception("under 12,000 silent frames must not confirm (" + blocks + " blocks)");
        t += (long)(0.600 * perSec);                                     // the stall
        byte[] resumed = BuildStream(1008, 0, 0);
        DriveRead(pol, cls, sp, t, resumed, 0, resumed.Length);
        pol.Recompute(t, Audio.Frames(10), false);
        pol.AdoptArmed(Audio.Frames(Audio.MaxPrebufferMs), true);
        if (pol.PrebufferMs <= Audio.MinPrebufferMs)
          throw new Exception("a stall after " + blocks + " silent block(s) must be charged");
      }

      Case("R3");
      // R3: the gap verdict is latched before the read. 11,999 silent frames, a 600 ms stall,
      // then a read whose FIRST frame crosses the threshold -- the stall began unconfirmed and
      // must be charged, however the returning bytes classify.
      {
        var pol = new ArrivalPolicy(); var cls = new SourceClassifier();
        var sp = new SourceSpan[64];
        long t = Stopwatch.GetTimestamp();
        byte[] almost = BuildStream(0, SquelchProfile.ConfirmFrames - 1, 0);
        DriveRead(pol, cls, sp, t, almost, 0, almost.Length);
        if (cls.Confirmed) throw new Exception("11,999 frames must not confirm");
        t += (long)(0.600 * perSec);
        byte[] crossing = BuildStream(0, 1, 480);      // threshold frame, then program
        DriveRead(pol, cls, sp, t, crossing, 0, crossing.Length);
        pol.Recompute(t, Audio.Frames(10), false);
        pol.AdoptArmed(Audio.Frames(Audio.MaxPrebufferMs), true);
        if (pol.PrebufferMs <= Audio.MinPrebufferMs)
          throw new Exception("a gap that began unconfirmed must be charged even if the read that closes it confirms");
      }

      Case("R4");
      // R4: once confirmed, silence of ANY length is free. A cap would reintroduce the defect
      // for exactly the long pauses the mechanism exists to handle.
      {
        var pol = new ArrivalPolicy(); var cls = new SourceClassifier();
        var sp = new SourceSpan[64];
        long t = Stopwatch.GetTimestamp();
        byte[] tail = BuildStream(480, SquelchProfile.ConfirmFrames, 0);
        DriveRead(pol, cls, sp, t, tail, 0, tail.Length);
        if (!cls.Confirmed) throw new Exception("a full qualifying tail must confirm");
        int afterConfirm = pol.PrebufferMs;
        double[] gaps = new double[] { 0.5, 2.5, 10.0 };
        foreach (double g in gaps) {
          t += (long)(g * perSec);
          byte[] beat = BuildStream(0, 64, 0);                            // a heartbeat
          DriveRead(pol, cls, sp, t, beat, 0, beat.Length);
        }
        t += (long)(0.021 * perSec);
        byte[] back = BuildStream(480, 0, 0);
        DriveRead(pol, cls, sp, t, back, 0, back.Length);
        pol.Recompute(t, Audio.Frames(10), false);
        pol.AdoptArmed(Audio.Frames(Audio.MaxPrebufferMs), true);
        if (pol.PrebufferMs > afterConfirm)
          throw new Exception("confirmed silence of any length must not become arrival debt");
        if (pol.DebtEpoch == 0) throw new Exception("resuming must open a new debt epoch");
      }

      Case("P2"); Case("P4");
      // P2 / P4: heartbeats are idempotent, and confirmed silence must not manufacture stable
      // time. Sixty seconds of it must not walk the target down on its own.
      {
        var pol = new ArrivalPolicy(); var cls = new SourceClassifier();
        var sp = new SourceSpan[64];
        long t = Stopwatch.GetTimestamp();
        // Drive the target well above the floor first, or lowering is impossible and the check
        // below cannot fail however the freeze behaves.
        byte[] burstBlock = BuildStream(9600, 0, 0);
        for (int i = 0; i < 60; i++) {
          t += (long)(0.200 * perSec);
          DriveRead(pol, cls, sp, t, burstBlock, 0, burstBlock.Length);
          pol.Recompute(t, Audio.Frames(10), false);
        }
        pol.AdoptArmed(Audio.Frames(Audio.MaxPrebufferMs), true);
        int raised = pol.PrebufferMs;
        if (raised <= Audio.MinPrebufferMs + HysteresisForTest)
          throw new Exception("the P4 fixture must raise the target, else the freeze check is vacuous");
        byte[] tail = BuildStream(480, SquelchProfile.ConfirmFrames, 0);
        t += (long)(0.021 * perSec);
        DriveRead(pol, cls, sp, t, tail, 0, tail.Length);
        long entersAfterFirst = pol.SquelchEnterCount;
        if (entersAfterFirst != 1) throw new Exception("confirmation must count exactly one enter");
        for (int i = 0; i < 30; i++) {                                    // 60 s of heartbeats
          t += (long)(2.0 * perSec);
          byte[] beat = BuildStream(0, 64, 0);
          DriveRead(pol, cls, sp, t, beat, 0, beat.Length);
          pol.Recompute(t, Audio.Frames(10), false);
        }
        if (pol.SquelchEnterCount != entersAfterFirst)
          throw new Exception("heartbeats must not re-enter confirmed squelch");
        if (pol.LowerCount != 0)
          throw new Exception("confirmed silence must not lower the target on its own, lowered " +
            pol.LowerCount + " time(s)");

        // The load-bearing part: 60 s of silence must not have satisfied the lowering hold. On
        // resume the path is clean, so the warranted target drops -- but the hold has to be
        // earned by ACTIVE time. Feed a few clean seconds and require that no lowering has yet
        // fired; without the resume-time shift the silent minute would have paid for it.
        // Feed ~35 s of clean program: long enough to flush the 30-entry structural window so
        // the warranted target genuinely drops (otherwise no lowering could fire however the
        // hold behaves, and this check would be decorative), but well short of the 60 s hold.
        t += (long)(0.021 * perSec);
        byte[] clean = BuildStream(1008, 0, 0);
        for (int i = 0; i < 1667; i++) {
          t += (long)(0.021 * perSec);
          DriveRead(pol, cls, sp, t, clean, 0, clean.Length);
          pol.Recompute(t, Audio.Frames(10), false);
        }
        if (pol.WarrantedForTest(Audio.Frames(10)) >= raised - HysteresisForTest)
          throw new Exception("the P4 fixture must leave the warranted target lowerable, else " +
            "the hold check below is vacuous");
        if (pol.LowerCount != 0)
          throw new Exception("silent wall time must not count toward the 60 s lowering hold: " +
            "lowered " + pol.LowerCount + " time(s) after only ~35 s of active time");
      }

      Case("P1");
      // P1: confirmation ends debt continuity without deleting the evidence. A stall in the
      // second before the source went quiet must still be visible to the call that decides how
      // far the target may drop.
      {
        var pol = new ArrivalPolicy(); var cls = new SourceClassifier();
        var sp = new SourceSpan[64];
        long t = Stopwatch.GetTimestamp();
        byte[] program = BuildStream(1008, 0, 0);
        DriveRead(pol, cls, sp, t, program, 0, program.Length);
        t += (long)(0.300 * perSec);                                      // a real stall
        byte[] more = BuildStream(1008, 0, 0);
        DriveRead(pol, cls, sp, t, more, 0, more.Length);
        double debtBefore = pol.PublishedDebtMs(Audio.Frames(10));
        if (debtBefore < 100)
          throw new Exception("a 300 ms stall must register as debt before confirmation");
        t += (long)(0.021 * perSec);
        byte[] tail = BuildStream(0, SquelchProfile.ConfirmFrames, 0);
        DriveRead(pol, cls, sp, t, tail, 0, tail.Length);
        if (!cls.Confirmed) throw new Exception("the tail must confirm");
        double debtAfter = pol.PublishedDebtMs(Audio.Frames(10));
        if (debtAfter < debtBefore * 0.9)
          throw new Exception("confirmation must not erase the measured stall that preceded it");
      }

      Case("clean-feed");
      // Teeth for the whole group: a clean steady feed must NOT be taxed, or every check above
      // would pass on a policy that simply always raises.
      {
        var pol = new ArrivalPolicy(); var cls = new SourceClassifier();
        var sp = new SourceSpan[64];
        long t = Stopwatch.GetTimestamp();
        byte[] block = BuildStream(1008, 0, 0);                           // 21 ms of program
        for (int i = 0; i < 200; i++) {
          t += (long)(0.021 * perSec);
          DriveRead(pol, cls, sp, t, block, 0, block.Length);
          pol.Recompute(t, Audio.Frames(10), false);
        }
        pol.AdoptArmed(Audio.Frames(Audio.MaxPrebufferMs), true);
        if (pol.PrebufferMs > Audio.MinPrebufferMs)
          throw new Exception("a clean steady feed must stay at the floor, not inherit a tax");
      }

      Case("forced-adopt");
      // A degrading path arms a raise that occupancy will never reach by itself, because the
      // ring is starving precisely when the bigger buffer is needed. Forced adoption -- what a
      // hard rebuffer performs -- must take it regardless of occupancy.
      {
        var stuck = new ArrivalPolicy(); var cls = new SourceClassifier();
        var sp = new SourceSpan[64];
        long st = Stopwatch.GetTimestamp();
        byte[] burstBlock = BuildStream(9600, 0, 0);                      // 200 ms deliveries
        for (int i = 0; i < 60; i++) {
          st += (long)(0.200 * perSec);
          DriveRead(stuck, cls, sp, st, burstBlock, 0, burstBlock.Length);
          stuck.Recompute(st, Audio.Frames(10), false);
        }
        int floorTarget = stuck.PrebufferMs;
        if (!stuck.AdoptArmed(1, true))
          throw new Exception("forced adoption must take an armed raise regardless of occupancy");
        if (stuck.PrebufferMs <= floorTarget)
          throw new Exception("adopting the armed raise must actually raise the target");
      }

      Case("freshness");
      // Freshness trim bounds latency without the old occupancy-only reset.
      var trim = new AdaptivePlayout();
      trim.PublishTarget(Audio.MinPrebufferMs);
      int burst = Audio.Frames(Audio.MinPrebufferMs + 400);
      trim.AddFrames(MakeTone(burst), 0, burst * 4);
      if (trim.FreshnessTrimCount == 0) throw new Exception("stale backlog must be trimmed");
      if (trim.BufferedMs > Audio.MinPrebufferMs + 200) throw new Exception("trim did not bound latency");

      AssertInventory();
      Console.WriteLine("play self-test ok (" + ranCases.Count + " cases)");
    }

    // Primed returns a provider that has left its opening prebuffer and consumed the
    // fade-in, so a test can assert steady-state behaviour rather than start-up.
    static AdaptivePlayout Primed(int frames) {
      var p = new AdaptivePlayout();
      p.AddFrames(MakeTone(frames), 0, frames * 4);
      byte[] warm = new byte[480 * 4];               // 480 frames > the 384-frame fade
      p.Read(warm, 0, warm.Length);
      return p;
    }

    static byte[] MakeTone(int frames) {
      byte[] pcm = new byte[frames * 4];
      for (int i = 0; i < frames; i++) {
        pcm[i * 4] = 0xe8; pcm[i * 4 + 1] = 0x03;       // +1000
        pcm[i * 4 + 2] = 0x18; pcm[i * 4 + 3] = 0xfc;   // -1000
      }
      return pcm;
    }
  }
}
