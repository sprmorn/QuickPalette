using System;
using System.IO;
using System.Threading;
using QuickPalette.Recorder;
using Serilog;

class OddProbe
{
    [STAThread]
    static void Main()
    {
        var logger = new LoggerConfiguration().MinimumLevel.Debug().CreateLogger();
        foreach (var (w, h) in new[] { (500, 300), (501, 299), (501, 300), (500, 299) })
        {
            var out1 = Path.Combine(Path.GetTempPath(), $"qp_odd_{w}x{h}.mp4");
            File.Delete(out1);
            try
            {
                var options = new RecordingOptions(IntPtr.Zero, 400, 300, w, h, 2560, 1600, 15, 2_000_000, false, false, out1);
                using var s = new RecordingSession(options, logger);
                Thread.Sleep(800);
                s.Stop();
                Console.WriteLine($"  [✓] {w}x{h} 成功");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [✗] {w}x{h}: {ex.Message.Split('\n')[0]}");
            }
        }
    }
}
