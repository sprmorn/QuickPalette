using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.Wave;
using QuickPalette.Recorder;
using Serilog;

class PcmDumpCheck
{
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(long point, uint flags);

    static void Main()
    {
        System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.ConsoleTraceListener());
        var logger = new LoggerConfiguration().MinimumLevel.Debug().CreateLogger();

        var dumpPath = Path.Combine(Path.GetTempPath(), "qp_pcm.pcm");
        Environment.SetEnvironmentVariable("QP_PCM_DUMP", dumpPath);
        if (File.Exists(dumpPath)) File.Delete(dumpPath);

        var sig = Sweep(48000, 5.5);
        var stream = new RawSourceWaveStream(new MemoryStream(sig), new WaveFormat(48000, 16, 2));
        var player = new WaveOutEvent();
        player.Init(stream);
        player.Play();

        var out1 = Path.Combine(Path.GetTempPath(), "qp_pd.mp4");
        File.Delete(out1);
        // 单路（仅内录）对照
        var options = new RecordingOptions(MonitorFromPoint(0, 0), 400, 300, 500, 300, 2560, 1600, 30, 2_000_000, true, true, out1);
        using (var session = new RecordingSession(options, logger))
        {
            Thread.Sleep(5200);
            session.Stop();
        }
        player.Stop();

        var pcm = File.ReadAllBytes(dumpPath);
        var frames = pcm.Length / 4;
        int glitches = 0;
        double worst = 0;
        short prevL = 0;
        for (var i = 1; i < frames; i++)
        {
            var l = (short)(pcm[i * 4] | (pcm[i * 4 + 1] << 8));
            var d = Math.Abs(l - prevL) / 32768.0;
            if (d > worst) worst = d;
            if (d > 0.45) glitches++;
            prevL = l;
        }

        Console.WriteLine($"[单路] PCM {frames / 48000.0:0.00}s：最大跳变 {worst:0.000}，毛刺 {glitches}");
        Console.WriteLine(glitches == 0 ? "[✓] 单路干净 → 真凶=双路丢帧" : "[✗] 单路也有 → 采集层");
    }

    static byte[] Sweep(int rate, double sec)
    {
        var frames = (int)(rate * sec);
        var buf = new byte[frames * 4];
        double phase = 0;
        for (var i = 0; i < frames; i++)
        {
            var t = i / (double)rate;
            var f = 200 + (2000 - 200) * (t / sec);
            phase += 2 * Math.PI * f / rate;
            var amp = 0.9 * (0.55 + 0.45 * Math.Sin(2 * Math.PI * 2.5 * t));
            var v = (short)(Math.Sin(phase) * amp * 32767);
            buf[i * 4] = (byte)(v & 0xFF);
            buf[i * 4 + 1] = (byte)((v >> 8) & 0xFF);
            buf[i * 4 + 2] = buf[i * 4];
            buf[i * 4 + 3] = buf[i * 4 + 1];
        }

        return buf;
    }
}
