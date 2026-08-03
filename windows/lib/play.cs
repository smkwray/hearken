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

    static void RunSelfTest() {
      var splitProvider = new AdaptivePlayout();
      var assembler = new FrameAssembler(8);
      byte[] oneFrame = new byte[] { 0xd0, 0x07, 0x30, 0xf8 }; // +2000, -2000
      assembler.Push(oneFrame, 1, 1.0f, splitProvider);
      byte[] remainder = new byte[] { 0x07, 0x30, 0xf8 };
      assembler.Push(remainder, remainder.Length, 1.0f, splitProvider);
      if (assembler.TailBytes != 0 || splitProvider.BufferedFrames != 1)
        throw new Exception("frame assembler failed split-frame carry");

      byte[] gained = new byte[] { 0xd0, 0x07, 0x30, 0xf8 };
      ApplyGain(gained, gained.Length, 0.5f);
      short gl = (short)(gained[0] | (gained[1] << 8));
      short gr = (short)(gained[2] | (gained[3] << 8));
      if (gl != 1000 || gr != -1000) throw new Exception("gain test failed");

      var provider = new AdaptivePlayout();
      int frames = 48000 * 200 / 1000;
      byte[] pcm = new byte[frames * 4];
      for (int i = 0; i < frames; i++) {
        pcm[i * 4] = 0xe8; pcm[i * 4 + 1] = 0x03;
        pcm[i * 4 + 2] = 0x18; pcm[i * 4 + 3] = 0xfc;
      }
      provider.AddFrames(pcm, 0, pcm.Length);
      byte[] output = new byte[480 * 4];
      if (provider.Read(output, 0, output.Length) != output.Length)
        throw new Exception("adaptive provider returned a short block");
      short outL = (short)(output[0] | (output[1] << 8));
      short outR = (short)(output[2] | (output[3] << 8));
      if (outL != 1000 || outR != -1000) throw new Exception("adaptive provider sample test failed");
      Console.WriteLine("play self-test ok");
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

    // Fixed-capacity, frame-counted PCM ring plus a narrow fractional provider.
    // The render callback performs only bounded arithmetic and copies under one
    // short lock: no allocation, logging, socket I/O, or unbounded drain loop.
    class AdaptivePlayout : IWaveProvider {
      public const int TargetMs = 140;
      const int SampleRate = 48000;
      const int FrameBytes = 4;
      const int TargetFrames = SampleRate * TargetMs / 1000;
      const int ResetThresholdFrames = SampleRate * 120 / 1000;
      const int FadeFrames = SampleRate * 8 / 1000;
      const int CapacityFrames = SampleRate * 2;
      const double MaxRatioPpm = 500.0;
      readonly object gate = new object();
      readonly byte[] ring = new byte[CapacityFrames * FrameBytes];
      int head, frames;
      double phase, ratio = 1.0, integralMsSeconds;
      long lastControlTicks, lastCallbackTicks, starvedSinceTicks;
      bool starved, pendingDiscontinuity;
      int transitionRemaining;
      short transitionFromL, transitionFromR, lastL, lastR;

      long starvedEnterCount, starvedExitCount, starvedTicks;
      long overflowEvents, overflowFrames, resetEvents, resetFrames;
      long callbackGapEvents, requestedFrames, suppliedFrames, sourceFrames;
      double maxCallbackGapMs;
      int minOccupancy = TargetFrames, maxOccupancy = TargetFrames;
      int lastWriteBeforeFrames, lastWriteAfterFrames;

      public WaveFormat WaveFormat { get { return SourceFmt; } }
      public int BufferedFrames { get { lock (gate) { return frames; } } }
      public int BufferedMs { get { lock (gate) { return frames * 1000 / SampleRate; } } }

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
          int drop = frames + add - CapacityFrames;
          if (drop > 0) {
            Advance(drop);
            overflowEvents++;
            overflowFrames += drop;
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

      void BeginTransition(short fromL, short fromR) {
        transitionFromL = fromL;
        transitionFromR = fromR;
        transitionRemaining = FadeFrames;
      }

      void EnterStarved(long nowTicks) {
        if (starved) return;
        starved = true;
        starvedSinceTicks = nowTicks;
        starvedEnterCount++;
        // Residual frames cannot bridge an underrun safely. Start the refill from
        // a fresh boundary and fade the last rendered sample to silence.
        head = 0;
        frames = 0;
        phase = 0;
        integralMsSeconds = 0;
      }

      void WriteFadeToSilence(byte[] output, int offset, int outputFrames) {
        Array.Clear(output, offset, outputFrames * FrameBytes);
        int fade = Math.Min(outputFrames, FadeFrames);
        for (int i = 0; i < fade; i++) {
          double scale = 1.0 - (double)(i + 1) / fade;
          PutSample(output, offset + i * FrameBytes, (short)(lastL * scale));
          PutSample(output, offset + i * FrameBytes + 2, (short)(lastR * scale));
        }
        lastL = lastR = 0;
      }

      void UpdateController(long nowTicks) {
        if (lastControlTicks == 0) { lastControlTicks = nowTicks; return; }
        double seconds = (double)(nowTicks - lastControlTicks) / Stopwatch.Frequency;
        if (seconds < 0.25) return;
        lastControlTicks = nowTicks;
        double errorMs = (frames - TargetFrames) * 1000.0 / SampleRate;
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

      public int Read(byte[] output, int offset, int count) {
        int outputFrames = count / FrameBytes;
        int renderBytes = outputFrames * FrameBytes;
        long now = Stopwatch.GetTimestamp();
        lock (gate) {
          requestedFrames += outputFrames;
          if (lastCallbackTicks != 0) {
            double gap = (double)(now - lastCallbackTicks) * 1000.0 / Stopwatch.Frequency;
            if (gap > maxCallbackGapMs) maxCallbackGapMs = gap;
            if (gap > 150.0) callbackGapEvents++;
          }
          lastCallbackTicks = now;

          if (starved) {
            if (frames < TargetFrames + 2) {
              WriteFadeToSilence(output, offset, outputFrames);
              if (renderBytes < count) Array.Clear(output, offset + renderBytes, count - renderBytes);
              NoteOccupancy();
              return count;
            }
            starved = false;
            starvedExitCount++;
            starvedTicks += now - starvedSinceTicks;
            BeginTransition(0, 0);
          }

          int needed = (int)Math.Ceiling(outputFrames * ratio) + 1;
          if (frames < needed || TargetFrames - frames > ResetThresholdFrames) {
            EnterStarved(now);
            WriteFadeToSilence(output, offset, outputFrames);
            if (renderBytes < count) Array.Clear(output, offset + renderBytes, count - renderBytes);
            NoteOccupancy();
            return count;
          }

          if (frames - TargetFrames > ResetThresholdFrames || pendingDiscontinuity) {
            int drop = Math.Max(0, frames - TargetFrames);
            Advance(drop);
            resetEvents++;
            resetFrames += drop;
            phase = 0;
            integralMsSeconds = 0;
            pendingDiscontinuity = false;
            BeginTransition(lastL, lastR);
          }

          UpdateController(now);
          for (int i = 0; i < outputFrames; i++) {
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
          if (renderBytes < count) Array.Clear(output, offset + renderBytes, count - renderBytes);
          suppliedFrames += outputFrames;
          NoteOccupancy();
          return count;
        }
      }

      public string TakeTelemetry(long rxBytes, double rxDurationMs, double maxReadGapMs, string state, string endpoint, int generation) {
        double starvedMs, ratioPpm, callbackMaxGapMs;
        int occupancyMs, spanMs, writeBeforeMs, writeAfterMs;
        long callbackGaps, requested, supplied, starvedEnters, starvedExits;
        long overflows, overflowedFrames, resets, resetDroppedFrames, playedSourceFrames;
        lock (gate) {
          starvedMs = (double)starvedTicks * 1000.0 / Stopwatch.Frequency;
          if (starved) starvedMs += (double)(Stopwatch.GetTimestamp() - starvedSinceTicks) * 1000.0 / Stopwatch.Frequency;
          ratioPpm = (ratio - 1.0) * 1000000.0;
          occupancyMs = frames * 1000 / SampleRate;
          spanMs = (maxOccupancy - minOccupancy) * 1000 / SampleRate;
          writeBeforeMs = lastWriteBeforeFrames * 1000 / SampleRate;
          writeAfterMs = lastWriteAfterFrames * 1000 / SampleRate;
          callbackMaxGapMs = maxCallbackGapMs;
          callbackGaps = callbackGapEvents;
          requested = requestedFrames;
          supplied = suppliedFrames;
          starvedEnters = starvedEnterCount;
          starvedExits = starvedExitCount;
          overflows = overflowEvents;
          overflowedFrames = overflowFrames;
          resets = resetEvents;
          resetDroppedFrames = resetFrames;
          playedSourceFrames = sourceFrames;
          maxCallbackGapMs = 0;
          minOccupancy = maxOccupancy = frames;
        }
        // Formatting happens after releasing the ring lock, so the render callback
        // can never wait behind string allocation or log preparation.
        return string.Format(CultureInfo.InvariantCulture,
          "event=receiver_metrics rx_bytes={0} rx_duration_ms={1:0.0} max_read_gap_ms={2:0.0} occupancy_ms={3} write_before_ms={4} write_after_ms={5} occupancy_span_ms={6} callback_max_gap_ms={7:0.0} callback_gap_events={8} requested_frames={9} supplied_frames={10} starved_enter={11} starved_exit={12} starved_ms={13:0.0} overflow_events={14} overflow_frames={15} overflow_ms={16:0.0} reset_events={17} reset_frames={18} reset_ms={19:0.0} ratio_ppm={20:0.0} source_frames={21} output_generation={22} endpoint={23} state={24} callback_gap_alert={25} occupancy_jump_alert={26}",
          rxBytes, rxDurationMs, maxReadGapMs, occupancyMs, writeBeforeMs, writeAfterMs, spanMs, callbackMaxGapMs,
          callbackGaps, requested, supplied, starvedEnters, starvedExits, starvedMs,
          overflows, overflowedFrames, overflowedFrames * 1000.0 / SampleRate,
          resets, resetDroppedFrames, resetDroppedFrames * 1000.0 / SampleRate, ratioPpm, playedSourceFrames,
          generation, endpoint == null ? "none" : endpoint.Replace(' ', '_'), state,
          callbackMaxGapMs > 150.0 ? 1 : 0, spanMs > 100 ? 1 : 0);
      }
    }

    // TCP can split a sample or stereo frame at any byte. Carry the incomplete
    // tail so gain and ring insertion always receive whole four-byte frames.
    class FrameAssembler {
      readonly byte[] joined;
      readonly byte[] tail = new byte[4];
      int tailCount;
      public FrameAssembler(int maxRead) { joined = new byte[maxRead + 4]; }
      public int TailBytes { get { return tailCount; } }
      public int Push(byte[] input, int n, float gain, AdaptivePlayout dest) {
        if (tailCount > 0) Buffer.BlockCopy(tail, 0, joined, 0, tailCount);
        Buffer.BlockCopy(input, 0, joined, tailCount, n);
        int total = tailCount + n;
        int complete = total / 4 * 4;
        tailCount = total - complete;
        if (tailCount > 0) Buffer.BlockCopy(joined, complete, tail, 0, tailCount);
        if (complete > 0) {
          if (gain != 1.0f) ApplyGain(joined, complete, gain);
          dest.AddFrames(joined, 0, complete);
        }
        return complete;
      }
    }

    class OutputChain {
      public MMDevice Device;
      public IDisposable Resampler;
      public WasapiOut Out;
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
      // 100ms shared-mode buffer: fixed latency on top of the playout buffer. Event-
      // driven shared mode is stable well below this; 150 was headroom we don't need.
      var wo = new WasapiOut(dev, AudioClientShareMode.Shared, false, 100);
      wo.PlaybackStopped += delegate(object s, StoppedEventArgs e) {
        if (e.Exception != null) Console.Error.WriteLine("stopped: " + e.Exception.Message);
        deviceChanged = true;
      };
      wo.Init(provider);
      wo.Play();
      BindAndUnmute(dev);
      Console.Error.WriteLine("playing WASAPI shared, state=" + wo.PlaybackState);
      var chain = new OutputChain();
      chain.Device = dev; chain.Resampler = null; chain.Out = wo;
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
      OutputChain chain = null;
      byte[] tmp = new byte[SourceFmt.AverageBytesPerSecond / 20]; // ~50ms
      var assembler = new FrameAssembler(tmp.Length);
      long intervalBytes = 0, lastLog = 0, lastRead = 0;
      double maxReadGapMs = 0;
      int outputGeneration = 0;
      string endpoint = "none";
      var sw = Stopwatch.StartNew();
      try {
        while (true) {
          int n = net.Read(tmp, 0, tmp.Length);
          if (n <= 0) break;
          long readAt = sw.ElapsedMilliseconds;
          if (lastRead != 0 && readAt - lastRead > maxReadGapMs) maxReadGapMs = readAt - lastRead;
          lastRead = readAt;
          assembler.Push(tmp, n, gGain, provider);
          intervalBytes += n;
          if (chain == null && provider.BufferedMs >= AdaptivePlayout.TargetMs) {
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
          if (sw.ElapsedMilliseconds - lastLog > 1000) {
            long now = sw.ElapsedMilliseconds;
            string st = chain == null ? "prebuffer" : chain.Out.PlaybackState.ToString();
            Console.Error.WriteLine(provider.TakeTelemetry(intervalBytes, now - lastLog, maxReadGapMs, st, endpoint, outputGeneration));
            lastLog = now;
            intervalBytes = 0;
            maxReadGapMs = 0;
          }
        }
      } finally {
        if (assembler.TailBytes != 0) Console.Error.WriteLine("event=partial_frame_discard bytes=" + assembler.TailBytes);
        if (chain != null) chain.Dispose();
      }
    }
  }
}
