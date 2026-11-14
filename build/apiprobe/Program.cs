using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.Wave;
using QuickPalette.Recorder;
using Serilog;

class DriftDiag
{
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(long point, uint flags);

    static void Main()
    {
        System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.ConsoleTraceListener());
        var logger = new LoggerConfiguration().MinimumLevel.Debug().CreateLogger();

        // 节拍声 + 录 6 秒
        var beeps = Beeps(48000, 12, 0.5, 0.08);
        var stream = new RawSourceWaveStream(new MemoryStream(beeps), new WaveFormat(48000, 16, 2));
        var player = new WaveOutEvent();
        player.Init(stream);
        player.Play();

        var out1 = Path.Combine(Path.GetTempPath(), "qp_drift.mp4");
        File.Delete(out1);
        var hmon = MonitorFromPoint(0, 0);
        var options = new RecordingOptions(hmon, 400, 300, 500, 300, 2560, 1600, 30, 2_000_000, true, true, out1);
        using (var session = new RecordingSession(options, logger))
        {
            Thread.Sleep(6200);
            session.Stop();
        }
        player.Stop();
        Console.WriteLine($"[1] 录制 ✓");

        Analyze("源文件", out1);

        var out2 = Path.Combine(Path.GetTempPath(), "qp_drift_2x.mp4");
        File.Delete(out2);
        RecordingExporter.Export(out1, out2, 0.3, 0, 2.0);
        Analyze("2x导出", out2);
    }

    static void Analyze(string tag, string path)
    {
        MfNative.EnsureStarted();
        MfNative.MFCreateSourceReaderFromURL(path, IntPtr.Zero, out var reader);
        for (var i = 0; i < 4; i++)
        {
            long first = -1, last = -1, count = 0, lastDur = 0;
            string kind = "?";
            try
            {
                MfNative.ReaderGetNativeMediaType(reader, i, 0, out var mt);
                MfNative.GetGuid(mt, MfGuids.MajorType, out var major);
                kind = major == MfGuids.MediaTypeVideo ? "video" : major == MfGuids.MediaTypeAudio ? "audio" : "?";
                if (kind == "?") continue;
            }
            catch { continue; }

            while (true)
            {
                MfNative.ReaderReadSample(reader, i, out _, out var flags, out var ts, out var sample);
                if ((flags & 2) != 0 || sample == IntPtr.Zero) break;
                if (first < 0) first = ts;
                MfNative.SampleGetDuration(sample, out var d);
                if (d > 0) lastDur = d;
                last = ts;
                count++;
            }

            var spanSec = first >= 0 ? (last - first) / 1e7 + lastDur / 1e7 : 0;
            Console.WriteLine($"[{tag}] {kind}: {count} 样本, 首ts={first / 1e7:0.000}s, 时长跨度={spanSec:0.000}s");
        }
    }

    static byte[] Beeps(int rate, int count, double intervalSec, double beepSec)
    {
        var total = (int)(rate * (count * intervalSec + 0.5)) * 4;
        var buf = new byte[total];
        for (var b = 0; b < count; b++)
        {
            var start = (int)(b * intervalSec * rate);
            var len = (int)(beepSec * rate);
            for (var i = 0; i < len; i++)
            {
                var v = (short)(Math.Sin(2 * Math.PI * 1000 * i / rate) * 12000 * Math.Min(1, i / 200.0));
                var off = (start + i) * 4;
                if (off + 3 < buf.Length)
                {
                    buf[off] = (byte)(v & 0xFF);
                    buf[off + 1] = (byte)((v >> 8) & 0xFF);
                    buf[off + 2] = buf[off];
                    buf[off + 3] = buf[off + 1];
                }
            }
        }

        return buf;
    }
}
