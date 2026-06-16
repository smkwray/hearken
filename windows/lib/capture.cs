using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Diagnostics;
using NAudio.Wave;
using NAudio.MediaFoundation;

namespace AudioBridge {
  // Passive WASAPI loopback of the DEFAULT render device, resampled in-process to
  // 48 kHz / 16-bit / stereo and streamed to the Mac over TCP (auto-reconnecting).
  //
  // This is a READ-ONLY copy of the audio stream. It does NOT reroute audio, change
  // the default device, or add latency to local playback. Anything that ever went
  // wrong here could only affect the copy sent to the Mac -- never what you hear.
  //
  // Usage: capture.exe <host> <port>
  class Capture {
    static void Main(string[] args) {
      string host = args.Length > 0 ? args[0] : "127.0.0.1";
      int port = args.Length > 1 ? int.Parse(args[1]) : 45001;
      MediaFoundationApi.Startup();
      var outFmt = new WaveFormat(48000, 16, 2);
      Console.Error.WriteLine("target " + host + ":" + port + " -> 48000/16/2");
      while (true) {
        try {
          using (var client = new TcpClient()) {
            client.Connect(host, port);
            client.NoDelay = true;
            Console.Error.WriteLine("connected");
            RunCapture(client.GetStream(), outFmt);
          }
        } catch (Exception e) {
          Console.Error.WriteLine("link down (" + e.Message + "); retrying...");
        }
        Thread.Sleep(1000);
      }
    }

    static void RunCapture(Stream outStream, WaveFormat outFmt) {
      var cap = new WasapiLoopbackCapture();
      var srcFmt = WaveFormat.CreateIeeeFloatWaveFormat(cap.WaveFormat.SampleRate, cap.WaveFormat.Channels);
      Console.Error.WriteLine("capturing " + srcFmt.SampleRate + "Hz/" + srcFmt.Channels + "ch float");
      var buf = new BufferedWaveProvider(srcFmt) {
        BufferDuration = TimeSpan.FromSeconds(5),
        DiscardOnBufferOverflow = true,
        ReadFully = true
      };
      cap.DataAvailable += (s, a) => { try { buf.AddSamples(a.Buffer, 0, a.BytesRecorded); } catch {} };
      var stop = new ManualResetEventSlim(false);
      cap.RecordingStopped += (s, a) => stop.Set();
      using (var resampler = new MediaFoundationResampler(buf, outFmt) { ResamplerQuality = 60 }) {
        cap.StartRecording();
        int bps = outFmt.AverageBytesPerSecond;
        int interval = 20;
        int chunk = bps * interval / 1000; chunk -= chunk % (outFmt.Channels * 2);
        byte[] tmp = new byte[chunk];
        var sw = Stopwatch.StartNew();
        long written = 0;
        try {
          while (!stop.IsSet) {
            long target = (long)(sw.Elapsed.TotalSeconds * bps);
            while (written < target) {
              int n = resampler.Read(tmp, 0, tmp.Length);
              if (n <= 0) break;
              outStream.Write(tmp, 0, n);
              written += n;
            }
            outStream.Flush();
            Thread.Sleep(interval);
          }
        } finally {
          try { cap.StopRecording(); } catch {}
          try { cap.Dispose(); } catch {}
        }
      }
    }
  }
}
