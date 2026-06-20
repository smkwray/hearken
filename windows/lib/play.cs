using System;
using System.Diagnostics;
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
    static int gPlayoutMs = 250;           // playout jitter-buffer cap in ms (arg 4)
    const int BytesPerMs = 192;            // 48000 * 2ch * 2bytes / 1000

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string id) {
      if (flow == DataFlow.Render) deviceChanged = true;
    }
    public void OnDeviceStateChanged(string id, DeviceState s) { if (s != DeviceState.Active) deviceChanged = true; }
    public void OnDeviceAdded(string id) {}
    public void OnDeviceRemoved(string id) { deviceChanged = true; }
    public void OnPropertyValueChanged(string id, PropertyKey k) {}

    [STAThread]
    static void Main(string[] args) {
      string host = args.Length > 0 ? args[0] : "127.0.0.1";
      int port = args.Length > 1 ? int.Parse(args[1]) : 45000;
      if (args.Length > 2) { float g; if (float.TryParse(args[2], out g)) gGain = g < 0 ? 0 : (g > 1 ? 1 : g); }
      if (args.Length > 3) { int p; if (int.TryParse(args[3], out p)) gPlayoutMs = p < 80 ? 80 : (p > 800 ? 800 : p); }
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

    // LatencyCap wraps the jitter buffer and drops the oldest excess on each read,
    // so playout latency stays bounded (a network burst / clock drift can otherwise
    // bloat BufferedWaveProvider to seconds and it never drains back down).
    class LatencyCap : NAudio.Wave.IWaveProvider {
      readonly BufferedWaveProvider src;
      readonly int maxBytes;
      public LatencyCap(BufferedWaveProvider s, int max) { src = s; maxBytes = max; }
      public WaveFormat WaveFormat { get { return src.WaveFormat; } }
      public int Read(byte[] buffer, int offset, int count) {
        int over = src.BufferedBytes - maxBytes;
        if (over > 0) {
          byte[] skip = new byte[Math.Min(over, 65536)];
          int dropped = 0;
          while (dropped < over) {
            int r = src.Read(skip, 0, Math.Min(skip.Length, over - dropped));
            if (r <= 0) break;
            dropped += r;
          }
        }
        return src.Read(buffer, offset, count);
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
        try { if (Device != null) Device.Dispose(); } catch {}
      }
    }

    static bool SameFmt(WaveFormat a, WaveFormat b) {
      return a.SampleRate == b.SampleRate && a.Channels == b.Channels &&
             a.BitsPerSample == b.BitsPerSample && a.Encoding == b.Encoding;
    }

    static OutputChain OpenDefault(BufferedWaveProvider netBuf) {
      var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
      var mix = dev.AudioClient.MixFormat;
      Console.Error.WriteLine("default device: " + dev.FriendlyName +
        " | mix=" + mix.SampleRate + "/" + mix.BitsPerSample + "/" + mix.Channels + " " + mix.Encoding);
      // Feed the raw 48k/16/2 stream straight to WasapiOut and let its OWN internal
      // (DMO) resampler convert to the device mix format. An EXPLICIT
      // MediaFoundationResampler->mix metered a peak but rendered INAUDIBLE to the
      // BT endpoint; the implicit WasapiOut path is what a known-audible local tone used.
      var wo = new WasapiOut(dev, AudioClientShareMode.Shared, false, 150);
      wo.PlaybackStopped += delegate(object s, StoppedEventArgs e) {
        if (e.Exception != null) Console.Error.WriteLine("stopped: " + e.Exception.Message);
        deviceChanged = true;
      };
      wo.Init(new LatencyCap(netBuf, gPlayoutMs * BytesPerMs)); // cap playout latency (drop oldest on bloat)
      wo.Play();
      BindAndUnmute(dev);
      Console.Error.WriteLine("playing WASAPI shared, state=" + wo.PlaybackState);
      var chain = new OutputChain();
      chain.Device = dev; chain.Resampler = null; chain.Out = wo;
      return chain;
    }

    static void BindAndUnmute(MMDevice dev) {
      uint pid = (uint)Process.GetCurrentProcess().Id;
      for (int t = 0; t < 30; t++) {
        try {
          dev.AudioSessionManager.RefreshSessions();
          var ss = dev.AudioSessionManager.Sessions;
          for (int i = 0; i < ss.Count; i++) {
            var s = ss[i];
            if (s.GetProcessID == pid) {
              try { s.DisplayName = "Hearken"; } catch {}
              float v = s.SimpleAudioVolume.Volume; bool m = s.SimpleAudioVolume.Mute;
              Console.Error.WriteLine("session: vol=" + v.ToString("0.00") + " mute=" + m);
              if (m) s.SimpleAudioVolume.Mute = false;
              if (v < 0.05f) s.SimpleAudioVolume.Volume = 1.0f;
              return;
            }
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
      var buf = new BufferedWaveProvider(SourceFmt);
      buf.BufferDuration = TimeSpan.FromSeconds(2);
      buf.DiscardOnBufferOverflow = true;
      buf.ReadFully = true;
      OutputChain chain = null;
      byte[] tmp = new byte[SourceFmt.AverageBytesPerSecond / 20]; // ~50ms
      long total = 0, lastLog = 0;
      var sw = Stopwatch.StartNew();
      try {
        while (true) {
          int n = net.Read(tmp, 0, tmp.Length);
          if (n <= 0) break;
          if (gGain != 1.0f) ApplyGain(tmp, n, gGain);
          buf.AddSamples(tmp, 0, n);
          total += n;
          if (chain == null && buf.BufferedDuration >= TimeSpan.FromMilliseconds(gPlayoutMs * 0.6)) {
            deviceChanged = false;
            chain = OpenDefault(buf); // open AFTER prebuffer so first buffers are real audio
          } else if (chain != null && (deviceChanged || chain.Out.PlaybackState == PlaybackState.Stopped)) {
            Console.Error.WriteLine("reopening output (device/session change)");
            chain.Dispose();
            deviceChanged = false;
            chain = OpenDefault(buf);
          }
          if (sw.ElapsedMilliseconds - lastLog > 1000) {
            lastLog = sw.ElapsedMilliseconds;
            string st = chain == null ? "prebuffer" : chain.Out.PlaybackState.ToString();
            Console.Error.WriteLine("rx=" + total + "B buffered=" + buf.BufferedBytes + " state=" + st);
          }
        }
      } finally {
        if (chain != null) chain.Dispose();
      }
    }
  }
}
