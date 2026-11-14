using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuickPalette.Recorder;
using Serilog;

class Verify2
{
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(long point, uint flags);

    [STAThread]
    static void Main()
    {
        var logger = new LoggerConfiguration().MinimumLevel.Debug().CreateLogger();
        var region = new System.Drawing.Rectangle(400, 300, 500, 300);

        var out1 = Path.Combine(Path.GetTempPath(), "qp_verify.mp4");
        File.Delete(out1);
        var hmon = MonitorFromPoint(0, 0);
        var options = new RecordingOptions(hmon, region.X, region.Y, region.Width, region.Height, 2560, 1600, 15, 2_000_000, false, false, out1);
        using (var session = new RecordingSession(options, logger))
        {
            Thread.Sleep(2000);
            session.Stop();
        }
        Console.WriteLine($"[1] 录制 ✓ {new FileInfo(out1).Length} 字节");

        // 二次录制回归
        try
        {
            using var s2 = new RecordingSession(options with { OutputPath = out1 + "2.mp4" }, logger);
            Thread.Sleep(1000);
            s2.Stop();
            Console.WriteLine("[2] 二次录制 ✓（无 0xC00D36B4）");
        }
        catch (Exception ex) { Console.WriteLine("[2] ✗ " + ex.Message); }

        // WPF MediaElement 渲染首帧（用户所见视角）
        var app = new Application();
        app.Startup += (_, _) =>
        {
            var win = new Window
            {
                Width = region.Width,
                Height = region.Height,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                Topmost = true,
                Left = 50,
                Top = 50
            };
            var me = new MediaElement
            {
                Source = new Uri(out1),
                LoadedBehavior = MediaState.Manual,
                UnloadedBehavior = MediaState.Stop,
                Stretch = Stretch.Fill
            };
            win.Content = me;
            win.Show();
            me.Play();
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                me.Pause();
                var rtb = new RenderTargetBitmap(region.Width, region.Height, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(me);
                var png = Path.Combine(Path.GetTempPath(), "qp_verify_frame.png");
                using (var fs = File.Create(png))
                    new PngBitmapEncoder { Frames = { BitmapFrame.Create(rtb) } }.Save(fs);

                // 亮度方向比对
                var px = new byte[region.Width * region.Height * 4];
                rtb.CopyPixels(px, region.Width * 4, 0);
                double Top() { double s = 0; for (int y = 0; y < 10; y++) for (int x = 0; x < region.Width; x += 4) { int i = (y * region.Width + x) * 4; s += (px[i] + px[i + 1] + px[i + 2]) / 3.0; } return s / (10 * (region.Width / 4)); }
                double Bottom() { double s = 0; for (int y = region.Height - 10; y < region.Height; y++) for (int x = 0; x < region.Width; x += 4) { int i = (y * region.Width + x) * 4; s += (px[i] + px[i + 1] + px[i + 2]) / 3.0; } return s / (10 * (region.Width / 4)); }
                Console.WriteLine($"[3] 渲染帧: 上10行={Top():0.0} 下10行={Bottom():0.0}（屏幕参照请人工核对 {png}）");
                Console.WriteLine("[3] 帧已存: " + png);
                app.Shutdown();
            };
            t.Start();
        };
        app.Run();

        // 导出回归（2x + 裁剪）
        var out3 = Path.Combine(Path.GetTempPath(), "qp_verify_export.mp4");
        File.Delete(out3);
        RecordingExporter.Export(out1, out3, 0.3, 0, 2.0);
        Console.WriteLine($"[4] 导出(0.3s起+2x) ✓ {new FileInfo(out3).Length} 字节");
    }
}
