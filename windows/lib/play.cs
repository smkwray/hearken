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
    static volatile bool deviceChanged;
    static readonly WaveFormat SourceFmt = new WaveFormat(48000, 16, 2);
    static float gGain = 1.0f;             // playback gain 0.0-1.0 (arg 3), applied to PCM

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string id) {
      if (flow == DataFlow.Render) deviceChanged = true;
    }
    public void OnDeviceStateChanged(string id, DeviceState s) { if (s != DeviceState.Active) deviceChanged = true; }
    public void OnDeviceAdded(string id) {}
    public void OnDeviceRemoved(string id) { deviceChanged = true; }
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
      const double IdleGapMs = 500.0;    // no active-rate media for this long => suspect squelch
      const int KeepaliveBytes = 4032;   // one 21 ms AudioQueue buffer (the squelch heartbeat)
      // Must be >= the sender's squelchThreshold (hear-capture.swift, default 16), or a
      // keepalive reads as program and idle never fires. v1 has no way to negotiate it;
      // protocol v2's SILENCE_START replaces this inference with a stated fact.
      const short SilencePeak = 16;
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

      long lastReadTicks, epochTicks, lastActiveTicks, lastLowerTicks, stableSinceTicks;
      long firstActiveTicks, idleEnterTicks;
      int activeReads;
      double outstandingDebtFrames, oneSecondPeakFrames, publishedDebtMs, lastIdleResumeMs;
      int reserveMs = Audio.MinReserveMs;
      int prebufferMs = Audio.MinPrebufferMs;
      int armedPrebufferMs;                 // raised target waiting for a safe adoption point
      bool idle, started;
      long idleEnterCount, idleExitCount, raiseCount, lowerCount, armedExpiredCount;
      string targetReason = "init";

      public bool Idle { get { return idle; } }
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

      public void ResetEpoch(long nowTicks) {
        // A reconnect, explicit discontinuity or idle transition invalidates the debt
        // history: the stream did not fall behind, it stopped. Keep the learned target.
        lastReadTicks = 0;
        epochTicks = nowTicks;
        outstandingDebtFrames = 0;
        oneSecondPeakFrames = 0;
      }

      // OnRead is called once per completed socket read, BEFORE the bytes reach the ring.
      public void OnRead(long nowTicks, int bytes, short peak, double consumeRatio) {
        bool silent = peak <= SilencePeak;
        bool keepalive = silent && bytes <= KeepaliveBytes;

        // Squelch detection. A keepalive-sized silent block arriving after a gap means
        // the source stopped sending program, not that the link fell behind.
        // It must also fire when no program has EVER been seen: a receiver that starts
        // while the Mac is already silent would otherwise treat every keepalive gap as
        // lateness, accrue debt without bound, and pin the target at its ceiling.
        if (keepalive && !idle) {
          bool longGap = lastActiveTicks == 0 ||
                         (nowTicks - lastActiveTicks) * 1000.0 / ticksPerSecond >= IdleGapMs;
          if (longGap) {
            idle = true;
            idleEnterCount++;
            idleEnterTicks = nowTicks;
            ResetEpoch(nowTicks);
          }
        }

        if (idle) {
          if (silent) { lastReadTicks = nowTicks; return; }   // still squelched: nothing to measure
          idle = false;
          idleExitCount++;
          lastIdleResumeMs = idleEnterTicks == 0 ? 0
            : (nowTicks - idleEnterTicks) * 1000.0 / ticksPerSecond;
          ResetEpoch(nowTicks);                                // resumption is not lateness
        }

        if (!silent) lastActiveTicks = nowTicks;

        // Only program audio carries lateness. Accruing debt across a silent stretch
        // would measure how long the source was quiet, not how late the link is.
        if (silent) { lastReadTicks = nowTicks; return; }

        if (firstActiveTicks == 0) firstActiveTicks = nowTicks;
        activeReads++;
        int receivedFrames = bytes / Audio.FrameBytes;
        if (lastReadTicks != 0) {
          // Expected arrival rate is the rate the ring is actually drained at, which
          // is the nominal rate scaled by the clock-correction ratio.
          double rate = Audio.SampleRate * (consumeRatio > 0 ? consumeRatio : 1.0);
          double dueFrames = (nowTicks - lastReadTicks) / ticksPerSecond * rate;
          double debtBeforeRead = outstandingDebtFrames + dueFrames;
          if (debtBeforeRead < 0) debtBeforeRead = 0;
          // A gap wider than the largest buffer we would ever hold is a stall, not
          // jitter. One rebuffer answers it; proposing an unreachable target does not.
          double cap = Audio.Frames(Audio.MaxPrebufferMs);
          if (debtBeforeRead > cap) debtBeforeRead = cap;
          if (debtBeforeRead > oneSecondPeakFrames) oneSecondPeakFrames = debtBeforeRead;
          outstandingDebtFrames = debtBeforeRead - receivedFrames;
          if (outstandingDebtFrames < 0) outstandingDebtFrames = 0;
        }
        lastReadTicks = nowTicks;
        if (epochTicks == 0) epochTicks = nowTicks;

        if ((nowTicks - epochTicks) / ticksPerSecond >= 1.0) {
          CloseSecond(nowTicks);
        }
        started = true;
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
        publishedDebtMs = Math.Max(structuralDebt, spikeDebt) * 1000.0 / Audio.SampleRate;

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
          "idle_enter={10} idle_exit={11} idle_resume_ms={12:0.0} idle={13}",
          publishedDebtMs, p50, p99,
          reserveMs, prebufferMs, armedPrebufferMs,
          raiseCount, lowerCount, armedExpiredCount, targetReason,
          idleEnterCount, idleExitCount, lastIdleResumeMs, idle ? 1 : 0);
      }
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
      double phase, ratio = 1.0, integralMsSeconds;
      long lastControlTicks, lastCallbackTicks, softUnderrunTicks;
      bool pendingDiscontinuity, rebuffering = true, idle, softPending;
      int transitionRemaining;
      short transitionFromL, transitionFromR, lastL, lastR;

      // Published by the network/telemetry threads, adopted at a render block boundary.
      int pendingPrebufferFrames = Audio.Frames(Audio.MinPrebufferMs);
      int prebufferFrames = Audio.Frames(Audio.MinPrebufferMs);
      int requestQuantumFrames = Audio.Frames(20);
      int targetGeneration;

      long overflowEvents, overflowFrames, freshnessTrims, freshnessTrimFrames;
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

      public void SetIdle(bool value) {
        lock (gate) {
          if (idle == value) return;
          idle = value;
          if (idle) {
            // Squelch is not an underrun. Stop the controller, keep the ring, and be
            // ready to prebuffer cleanly when program audio returns.
            integralMsSeconds = 0;
            rebuffering = true;
            softPending = false;
          }
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
            if (fromRing + fromInput > 0) {
              freshnessTrims++;
              freshnessTrimFrames += fromRing + fromInput;
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

          int needed = (int)Math.Ceiling(outputFrames * ratio) + 1;

          // Source squelched: render silence without charging an underrun. The listener
          // is meant to hear nothing, so nothing here is a fault to count or recover from.
          if (idle && frames < needed) {
            WriteSilence(output, offset, count, outputFrames);
            NoteOccupancy();
            return count;
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
        bool rebuf, idleNow;
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
          playedSourceFrames = sourceFrames;
          prebufMs = Audio.Ms(prebufferFrames);
          quantumMs = Audio.Ms(requestQuantumFrames);
          r50 = Audio.Ms(reqP50Frames); r95 = Audio.Ms(reqP95Frames); r99 = Audio.Ms(reqP99Frames);
          g50 = gapP50Ms; g95 = gapP95Ms; g99 = gapP99Ms;
          rebuf = rebuffering;
          idleNow = idle;
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
          "device_buffer_ms={40}",
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
          r50, r95, r99, g50, g95, g99, deviceBufferMs);
      }
    }

    // TCP can split a sample or stereo frame at any byte. Carry the incomplete
    // tail so gain and ring insertion always receive whole four-byte frames.
    class FrameAssembler {
      readonly byte[] joined;
      readonly byte[] tail = new byte[4];
      int tailCount, completeBytes;
      short lastPeak;
      public FrameAssembler(int maxRead) { joined = new byte[maxRead + 4]; }
      public int TailBytes { get { return tailCount; } }
      public short LastPeak { get { return lastPeak; } }
      public int CompleteBytes { get { return completeBytes; } }

      // Assemble carries the split-frame tail and measures the block's peak. It never
      // touches the ring, because the insert decision needs the peak and the peak needs
      // the assembled bytes: the policy must see this block BEFORE it is inserted.
      public int Assemble(byte[] input, int n, float gain) {
        if (tailCount > 0) Buffer.BlockCopy(tail, 0, joined, 0, tailCount);
        Buffer.BlockCopy(input, 0, joined, tailCount, n);
        int total = tailCount + n;
        completeBytes = total / 4 * 4;
        tailCount = total - completeBytes;
        if (tailCount > 0) Buffer.BlockCopy(joined, completeBytes, tail, 0, tailCount);
        lastPeak = Peak(joined, completeBytes);
        if (completeBytes > 0 && gain != 1.0f) ApplyGain(joined, completeBytes, gain);
        return completeBytes;
      }

      // Flush hands the assembled frames to the ring. The caller skips it only while the
      // source is squelched, so the keepalive heartbeat never becomes program material.
      // Alignment survives either way because Assemble already consumed the bytes.
      public void Flush(AdaptivePlayout dest) {
        if (completeBytes > 0) dest.AddFrames(joined, 0, completeBytes);
      }

      // Peak is measured on the network thread, before insertion, so the render
      // callback never inspects program content to decide anything.
      static short Peak(byte[] b, int n) {
        int peak = 0;
        for (int i = 0; i + 1 < n; i += 2) {
          int s = (short)(b[i] | (b[i + 1] << 8));
          if (s < 0) s = -s;
          if (s > peak) peak = s;
        }
        return peak > short.MaxValue ? short.MaxValue : (short)peak;
      }
    }

    class OutputChain {
      public MMDevice Device;
      public IDisposable Resampler;
      public WasapiOut Out;
      public string Mode = "unknown";
      public int LatencyMs;
      public void Dispose() {
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
      wo.PlaybackStopped += delegate(object s, StoppedEventArgs e) {
        if (e.Exception != null) Console.Error.WriteLine("stopped: " + e.Exception.Message);
        deviceChanged = true;
      };
      wo.Play();
      BindAndUnmute(dev);
      Console.Error.WriteLine("playing WASAPI shared, mode=" + mode + ", buffer_ms=" + latencyMs +
        ", state=" + wo.PlaybackState);
      var chain = new OutputChain();
      chain.Device = dev; chain.Resampler = null; chain.Out = wo;
      chain.Mode = mode; chain.LatencyMs = latencyMs;
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
    static void ApplyGain(byte[] b, int n, float g) {
      for (int i = 0; i + 1 < n; i += 2) {
        short s = (short)(b[i] | (b[i + 1] << 8));
        int v = (int)(s * g);
        if (v > 32767) v = 32767; else if (v < -32768) v = -32768;
        b[i] = (byte)(v & 0xff);
        b[i + 1] = (byte)((v >> 8) & 0xff);
      }
    }

    static void RunPlay(NetworkStream net) {
      var provider = new AdaptivePlayout();
      var policy = new ArrivalPolicy();
      OutputChain chain = null;
      byte[] tmp = new byte[SourceFmt.AverageBytesPerSecond / 20]; // ~50ms
      var assembler = new FrameAssembler(tmp.Length);
      long intervalBytes = 0, lastRead = 0, lastHardRebuffer = 0;
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
          policy.Recompute(Stopwatch.GetTimestamp(), quantum, underrun);
          provider.PublishTarget(policy.PrebufferMs);
          Console.Error.WriteLine(provider.TakeTelemetry(bytesNow, now - lastEmit, gapNow,
            state, ep, gen, mode, devBuf, policy.Describe()));
          lastEmit = now;
        }
      });
      telemetry.IsBackground = true;
      telemetry.Start();

      try {
        while (true) {
          int n = net.Read(tmp, 0, tmp.Length);
          if (n <= 0) break;
          long readTicks = Stopwatch.GetTimestamp();
          long readAt = sw.ElapsedMilliseconds;
          bool wasIdle = policy.Idle;
          // Assemble -> classify -> insert. The policy must judge this block before it
          // reaches the ring; deciding from the PREVIOUS idle state discarded the first
          // block of program audio after every squelch gap.
          int complete = assembler.Assemble(tmp, n, gGain);
          policy.OnRead(readTicks, n, assembler.LastPeak, provider.RatioSnapshot);
          if (!policy.Idle) assembler.Flush(provider);
          if (policy.Idle != wasIdle) {
            provider.SetIdle(policy.Idle);
            // An idle boundary is the free moment to move the target: nothing is playing,
            // so a change interrupts nothing. Entering idle takes the whole reduction the
            // measurement warrants; leaving it adopts a raise only if the measurement
            // still warrants one, so a stale armed value cannot be cashed in here.
            if (policy.Idle) policy.ApplyIdleTarget(provider.RequestQuantumFrames);
            else policy.AdoptArmed(provider.BufferedFrames, true);
            provider.PublishTarget(policy.PrebufferMs);
          } else if (!policy.Idle) {
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
          if (chain == null && !policy.Idle && policy.ObservationSatisfied(readTicks) &&
              provider.BufferedFrames >= provider.PrebufferFrames) {
            deviceChanged = false;
            chain = OpenDefault(provider); // open AFTER prebuffer so first buffers are real audio
            outputGeneration++;
            endpoint = chain.Device.ID;
          } else if (chain != null && (deviceChanged || chain.Out.PlaybackState == PlaybackState.Stopped)) {
            Console.Error.WriteLine("reopening output (device/session change)");
            chain.Dispose();
            deviceChanged = false;
            chain = OpenDefault(provider);
            outputGeneration++;
            endpoint = chain.Device.ID;
          }
        }
      } finally {
        telemetryStop.Set();
        try { telemetry.Join(2000); } catch {}
        if (assembler.TailBytes != 0) Console.Error.WriteLine("event=partial_frame_discard bytes=" + assembler.TailBytes);
        if (chain != null) chain.Dispose();
      }
    }

    static void RunSelfTest() {
      var splitProvider = new AdaptivePlayout();
      var assembler = new FrameAssembler(8);
      byte[] oneFrame = new byte[] { 0xd0, 0x07, 0x30, 0xf8 }; // +2000, -2000
      assembler.Assemble(oneFrame, 1, 1.0f); assembler.Flush(splitProvider);
      byte[] remainder = new byte[] { 0x07, 0x30, 0xf8 };
      assembler.Assemble(remainder, remainder.Length, 1.0f); assembler.Flush(splitProvider);
      if (assembler.TailBytes != 0 || splitProvider.BufferedFrames != 1)
        throw new Exception("frame assembler failed split-frame carry");

      // A withheld block still consumes its bytes and keeps frame alignment.
      var idleProvider = new AdaptivePlayout();
      var idleAssembler = new FrameAssembler(8);
      idleAssembler.Assemble(oneFrame, oneFrame.Length, 1.0f);   // no Flush: squelched
      if (idleAssembler.TailBytes != 0 || idleProvider.BufferedFrames != 0)
        throw new Exception("a withheld block must consume bytes without inserting");
      // ...and the NEXT block, once program resumes, must still reach the ring. Deciding
      // insertion from the previous idle state dropped exactly this block.
      idleAssembler.Assemble(oneFrame, oneFrame.Length, 1.0f);
      idleAssembler.Flush(idleProvider);
      if (idleProvider.BufferedFrames != 1)
        throw new Exception("the first block after squelch must not be discarded");

      byte[] gained = new byte[] { 0xd0, 0x07, 0x30, 0xf8 };
      ApplyGain(gained, gained.Length, 0.5f);
      short gl = (short)(gained[0] | (gained[1] << 8));
      short gr = (short)(gained[2] | (gained[3] << 8));
      if (gl != 1000 || gr != -1000) throw new Exception("gain test failed");

      // Peak observation drives squelch detection, so it must see program content.
      var peakAssembler = new FrameAssembler(8);
      var peakSink = new AdaptivePlayout();
      peakAssembler.Assemble(oneFrame, oneFrame.Length, 1.0f); peakAssembler.Flush(peakSink);
      if (peakAssembler.LastPeak != 2000) throw new Exception("peak observation failed");
      byte[] quiet = new byte[] { 0x01, 0x00, 0x00, 0x00 };
      peakAssembler.Assemble(quiet, quiet.Length, 1.0f); peakAssembler.Flush(peakSink);
      if (peakAssembler.LastPeak > 16) throw new Exception("silent block must read as silent");

      // Steady-state fidelity: after the opening fade, reconstruction is exact.
      var provider = Primed(Audio.Frames(300));
      byte[] output = new byte[480 * 4];
      if (provider.Read(output, 0, output.Length) != output.Length)
        throw new Exception("adaptive provider returned a short block");
      short outL = (short)(output[0] | (output[1] << 8));
      short outR = (short)(output[2] | (output[3] << 8));
      if (outL != 1000 || outR != -1000) throw new Exception("adaptive provider sample test failed");

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

      // Teeth for the check above: the shipped 40 ms floor cannot cover that cadence,
      // so the same replay MUST starve. If this ever stops starving, the test is vacuous.
      var undersized = new AdaptivePlayout();
      for (int c = 0; c < Cycles; c++) {
        undersized.AddFrames(MakeTone(delivery), 0, delivery * 4);
        for (int r = 0; r < CadenceMs / QuantumMs; r++) undersized.Read(pull, 0, pull.Length);
      }
      if (undersized.HardRebufferCount + undersized.SoftUnderrunCount == 0)
        throw new Exception("undersized target must starve, else the cadence test proves nothing");

      // Squelch seen from a cold start. This shipped broken: idle only fired if program
      // audio had already been seen, so a receiver that started while the Mac was
      // silent treated every 2 s keepalive gap as lateness, accrued 14 s of debt and
      // pinned the target at its 400 ms ceiling.
      var pol = new ArrivalPolicy();
      long perSec = Stopwatch.Frequency, t0 = Stopwatch.GetTimestamp();
      for (int i = 0; i < 6; i++) pol.OnRead(t0 + (long)(i * 2.0 * perSec), 4032, 0, 1.0);
      if (!pol.Idle) throw new Exception("silent keepalives must enter idle with no prior program");
      pol.Recompute(t0 + (long)(12 * perSec), Audio.Frames(10), false);
      if (pol.PrebufferMs > Audio.MinPrebufferMs + 20)
        throw new Exception("squelch must not inflate the target");
      pol.OnRead(t0 + (long)(14 * perSec), 9600, 3000, 1.0);
      if (pol.Idle) throw new Exception("program audio must exit idle");

      // Wired proxy: a steady low-jitter feed must not be charged a jitter tax. One 21 ms
      // sender block every 21 ms is what a clean Ethernet path looks like from here.
      var clean = new ArrivalPolicy();
      long c0 = Stopwatch.GetTimestamp();
      for (int i = 0; i < 200; i++) {
        long t = c0 + (long)(i * 0.021 * perSec);
        clean.OnRead(t, 4032, 3000, 1.0);
        clean.Recompute(t, Audio.Frames(10), false);
      }
      if (clean.PrebufferMs > Audio.MinPrebufferMs)
        throw new Exception("a clean link must stay at the floor, not inherit a bursty path's target");

      // A silent gap must hand back latency the measurement no longer justifies, at once.
      // Crawling down at 5 ms per 10 s through a gap where nothing is playing kept ~200 ms
      // of dead latency in the field.
      var drop = new ArrivalPolicy();
      long d0 = Stopwatch.GetTimestamp(), tt = d0;
      for (int i = 0; i < 150; i++) {                    // bursty: 100 ms deliveries
        tt = d0 + (long)(i * 0.100 * perSec);
        drop.OnRead(tt, 19200, 3000, 1.0);
        drop.Recompute(tt, Audio.Frames(10), false);
      }
      drop.AdoptArmed(Audio.Frames(Audio.MaxPrebufferMs), true);
      int raised = drop.PrebufferMs;
      if (raised <= Audio.MinPrebufferMs) throw new Exception("a bursty path must raise the target");
      long baseT = tt;
      for (int i = 1; i <= 2000; i++) {                  // 42 s clean: flushes the 30 s window,
        tt = baseT + (long)(i * 0.021 * perSec);         // but is short of the 60 s lowering hold
        drop.OnRead(tt, 4032, 3000, 1.0);
        drop.Recompute(tt, Audio.Frames(10), false);
      }
      if (drop.PrebufferMs != raised)
        throw new Exception("the slow lowering hold must not have fired yet - test would prove nothing");
      for (int i = 1; i <= 4; i++) drop.OnRead(tt + (long)(i * 2.0 * perSec), 4032, 0, 1.0);
      if (!drop.Idle) throw new Exception("keepalives must enter idle");
      drop.ApplyIdleTarget(Audio.Frames(10));
      if (drop.PrebufferMs >= raised)
        throw new Exception("an idle gap must return the latency the measurement no longer justifies");

      // A degrading path arms a raise that occupancy will never reach by itself, because
      // the ring is starving precisely when the bigger buffer is needed. Forced adoption
      // -- what a hard rebuffer performs -- must take it regardless of occupancy. Omitting
      // that adoption point left the receiver measuring a need it could not act on, and it
      // thrashed through 14 rebuffers in 22 s with the raise sitting armed.
      var stuck = new ArrivalPolicy();
      long s0 = Stopwatch.GetTimestamp(), st = s0;
      for (int i = 0; i < 60; i++) {
        st = s0 + (long)(i * 0.200 * perSec);        // 200 ms deliveries: badly bursty
        stuck.OnRead(st, 38400, 3000, 1.0);
        stuck.Recompute(st, Audio.Frames(10), false);
      }
      int floorTarget = stuck.PrebufferMs;
      if (!stuck.AdoptArmed(1, true))
        throw new Exception("forced adoption must take an armed raise regardless of occupancy");
      if (stuck.PrebufferMs <= floorTarget)
        throw new Exception("adopting the armed raise must actually raise the target");

      // Freshness trim bounds latency without the old occupancy-only reset.
      var trim = new AdaptivePlayout();
      trim.PublishTarget(Audio.MinPrebufferMs);
      int burst = Audio.Frames(Audio.MinPrebufferMs + 400);
      trim.AddFrames(MakeTone(burst), 0, burst * 4);
      if (trim.FreshnessTrimCount == 0) throw new Exception("stale backlog must be trimmed");
      if (trim.BufferedMs > Audio.MinPrebufferMs + 200) throw new Exception("trim did not bound latency");

      Console.WriteLine("play self-test ok");
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
