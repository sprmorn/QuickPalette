using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using QuickPalette.Recorder;
using Serilog;

class Smoke
{
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(long point, uint flags);

    static void Main()
    {
        System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.ConsoleTraceListener());
        var logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Console().CreateLogger();
        var out1 = Path.Combine(Path.GetTempPath(), "qp_rec_smoke.mp4");
        var out2 = Path.Combine(Path.GetTempPath(), "qp_rec_export.mp4");
        File.Delete(out1); File.Delete(out2);

        var hmon = MonitorFromPoint(0, 0);
        Console.WriteLine("[1] 录制 3 秒（区域 500x300 @15fps，含暂停）");
        var options = new RecordingOptions(hmon, 400, 300, 500, 300, 2560, 1600, 15, 2_000_000, false, false, out1);
        using (var session = new RecordingSession(options, logger))
        {
            Thread.Sleep(1200);
            session.Pause();
            Thread.Sleep(600);
            session.Resume();
            Thread.Sleep(1800);
            var path = session.Stop();
            var fi = new FileInfo(path);
            Console.WriteLine($"  录制完成: {fi.Length} 字节，时长 {session.Elapsed.TotalSeconds:0.0}s");
        }

        Console.WriteLine("[2] 检查录制文件的原生流类型");
        NAudio.MediaFoundation.MediaFoundationApi.Startup();
        NAudio.MediaFoundation.MediaFoundationInterop.MFCreateSourceReaderFromURL(out1, null, out var rdr);
        for (var i = 0; i < 3; i++)
        {
            try
            {
                rdr.GetNativeMediaType(i, 0, out var mt);
                var k = QuickPalette.Recorder.MfGuids.MajorType;
                mt.GetGUID(k, out var major);
                var k2 = QuickPalette.Recorder.MfGuids.Subtype;
                mt.GetGUID(k2, out var sub);
                var k3 = QuickPalette.Recorder.MfGuids.FrameSize;
                long fs = 0;
                try { mt.GetUINT64(k3, out fs); } catch { }
                Console.WriteLine($"  流{i}: major={major} sub={sub} framesize={fs >> 32}x{fs & 0xFFFFFFFF}");
            }
            catch { Console.WriteLine($"  流{i}: 无"); }
        }

        Console.WriteLine("[3] 导出（裁掉前 0.5 秒 + 2 倍速）");
        RecordingExporter.Export(out1, out2, 0.5, 0, 2.0);
        var fi2 = new FileInfo(out2);
        Console.WriteLine($"  导出完成: {fi2.Length} 字节");
        Console.WriteLine(fi2.Length > 10_000 ? "[✓] 录制+导出管线打通" : "[△] 输出偏小：" + fi2.Length);
    }
}
