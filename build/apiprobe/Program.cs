using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using QuickPalette.Recorder;
using Serilog;

class Repro
{
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(long point, uint flags);

    static void Main()
    {
        System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.ConsoleTraceListener());
        var logger = new LoggerConfiguration().MinimumLevel.Debug().CreateLogger();
        var out1 = Path.Combine(Path.GetTempPath(), "qp_repro.mp4");
        File.Delete(out1);

        // 用户场景：1368x528 @30fps 含音频，录 ~5 秒
        var hmon = MonitorFromPoint(0, 0);
        var options = new RecordingOptions(hmon, 432, 714, 1368, 528, 2560, 1600, 30, 8_000_000, true, true, out1);
        using (var session = new RecordingSession(options, logger))
        {
            Thread.Sleep(5000);
            session.Stop();
        }
        Console.WriteLine($"[1] 录制 ✓ {new FileInfo(out1).Length} 字节");

        // 用户场景：裁剪 + 倍速导出
        var out2 = Path.Combine(Path.GetTempPath(), "qp_repro_export.mp4");
        File.Delete(out2);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var t = Task.Run(() => RecordingExporter.Export(out1, out2, 0.5, 0, 2.0));
        if (t.Wait(30000))
        {
            Console.WriteLine($"[2] 导出 ✓ {new FileInfo(out2).Length} 字节，耗时 {sw.ElapsedMilliseconds}ms");
        }
        else
        {
            Console.WriteLine("[2] ✗✗ 导出 30 秒未完成 = 挂死复现！");
        }
    }
}
