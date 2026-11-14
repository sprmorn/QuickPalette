using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using QuickPalette.Recorder;
using Serilog;

class MemCheck
{
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(long point, uint flags);

    static void Main()
    {
        var logger = new LoggerConfiguration().MinimumLevel.Debug().CreateLogger();
        var out1 = Path.Combine(Path.GetTempPath(), "qp_mem.mp4");
        File.Delete(out1);

        var proc = Process.GetCurrentProcess();
        var options = new RecordingOptions(MonitorFromPoint(0, 0), 400, 300, 800, 500, 2560, 1600, 30, 4_000_000, true, true, out1);
        using (var session = new RecordingSession(options, logger))
        {
            for (var i = 0; i < 6; i++)
            {
                Thread.Sleep(10000);
                proc.Refresh();
                var priv = proc.PrivateMemorySize64 / 1024.0 / 1024.0;
                var managed = GC.GetTotalMemory(false) / 1024.0 / 1024.0;
                Console.WriteLine($"  {(i + 1) * 10}s: 私有内存 {priv:0}MB  托管堆 {managed:0}MB");
            }

            session.Stop();
        }

        proc.Refresh();
        Console.WriteLine($"[结束] 私有内存 {proc.PrivateMemorySize64 / 1024.0 / 1024.0:0}MB");
        Console.WriteLine("(私有内存 60 秒增长 <300MB 为健康；>1.5GB 即泄漏复现)");
    }
}
