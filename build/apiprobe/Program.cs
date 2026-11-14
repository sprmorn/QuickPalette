using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.Wave;
using QuickPalette.Recorder;
using Serilog;

class QualityCheck
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

        var out1 = Path.Combine(Path.GetTempPath(), "qp_q.mp4");
        File.Delete(out1);
        var options = new RecordingOptions(MonitorFromPoint(0, 0), 400, 300, 500, 300, 2560, 1600, 30, 2_000_000, true, true, out1);
        using (var session = new RecordingSession(options, logger))
        {
            Thread.Sleep(4200);
            session.Stop();
        }
        player.Stop();

        var dumpLen = new FileInfo(dumpPath).Length;
        var decoded = DecodeAudio(out1, out var decSec);
        Console.WriteLine($"[对比] 录 4.2s → 编码前 PCM {dumpLen / 4.0 / 48000:0.00}s → 解码 {decSec:0.00}s");

        var dump = File.ReadAllBytes(dumpPath);
        var srcMono = Mono(sig);
        var dumpMono = Mono(dump);
        var decMono = Mono(decoded);
        Console.WriteLine($"[相关] PCM层 {Correlate(srcMono, dumpMono, out var lag1):0.000} @lag={lag1 / 48000.0:0.000}s；解码层 {Correlate(srcMono, decMono, out var lag2):0.000} @lag={lag2 / 48000.0:0.000}s");

        int g1 = Glitches(dump), g2 = Glitches(decoded);
        Console.WriteLine($"[毛刺] PCM层 {g1}；解码层 {g2}");
    }

    static int Glitches(byte[] pcm)
    {
        int glitches = 0;
        short prevL = 0;
        for (var i = 1; i < pcm.Length / 4; i++)
        {
            var l = (short)(pcm[i * 4] | (pcm[i * 4 + 1] << 8));
            if (Math.Abs(l - prevL) / 32768.0 > 0.45) glitches++;
            prevL = l;
        }

        return glitches;
    }

    static double[] Mono(byte[] pcm)
    {
        var n = pcm.Length / 4;
        var r = new double[n];
        for (var i = 0; i < n; i++)
        {
            r[i] = (short)(pcm[i * 4] | (pcm[i * 4 + 1] << 8)) / 32768.0;
        }

        return r;
    }

    static double Correlate(double[] a, double[] b, out int bestLag)
    {
        bestLag = 0;
        var n = Math.Min(a.Length, b.Length);
        if (n < 1000) return 0;
        double best = -2;
        for (var lag = -24000; lag <= 24000; lag += 8)
        {
            double dot = 0, ea = 0, eb = 0;
            for (var i = 0; i < n - 4800; i += 16)
            {
                var j = i + lag;
                if (j < 0 || j >= b.Length) continue;
                var x = a[i];
                var y = b[j];
                dot += x * y;
                ea += x * x;
                eb += y * y;
            }

            var c = dot / (Math.Sqrt(ea * eb) + 1e-12);
            if (c > best) { best = c; bestLag = lag; }
        }

        return best;
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

    static byte[] DecodeAudio(string path, out double sec)
    {
        MfNative.EnsureStarted();
        MfNative.MFCreateSourceReaderFromURL(path, IntPtr.Zero, out var reader);
        var audioIdx = -1;
        for (var i = 0; i < 4; i++)
        {
            try
            {
                MfNative.ReaderGetNativeMediaType(reader, i, 0, out var mt);
                MfNative.GetGuid(mt, MfGuids.MajorType, out var major);
                if (major == MfGuids.MediaTypeAudio && audioIdx < 0) audioIdx = i;
            }
            catch { }
        }

        sec = 0;
        if (audioIdx < 0) return Array.Empty<byte>();

        MfNative.MFCreateMediaType(out var pcm);
        MfNative.SetGuid(pcm, MfGuids.MajorType, MfGuids.MediaTypeAudio);
        MfNative.SetGuid(pcm, MfGuids.Subtype, MfGuids.AudioFormatPcm);
        MfNative.SetUInt32(pcm, MfGuids.AudioBitsPerSample, 16);
        MfNative.SetUInt32(pcm, MfGuids.AudioSamplesPerSecond, 48000);
        MfNative.SetUInt32(pcm, MfGuids.AudioNumChannels, 2);
        MfNative.SetUInt32(pcm, MfGuids.AudioBlockAlignment, 4);
        MfNative.SetUInt32(pcm, MfGuids.AudioAvgBytesPerSecond, 192000);
        MfNative.ReaderSetCurrentMediaType(reader, audioIdx, pcm);

        var all = new MemoryStream();
        while (true)
        {
            MfNative.ReaderReadSample(reader, audioIdx, out _, out var flags, out _, out var sample);
            if ((flags & 2) != 0 || sample == IntPtr.Zero) break;
            MfNative.SampleToContiguousBuffer(sample, out var buffer);
            MfNative.SampleGetTotalLength(sample, out var totalLength);
            MfNative.BufferLock(buffer, out var ptr, out _, out var length);
            try
            {
                var n = (int)Math.Min(length, totalLength);
                var tmp = new byte[n];
                Marshal.Copy(ptr, tmp, 0, n);
                all.Write(tmp, 0, n);
            }
            finally { MfNative.BufferUnlock(buffer); }
        }

        sec = all.Length / 4.0 / 48000;
        return all.ToArray();
    }
}
