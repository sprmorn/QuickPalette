using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuickPalette.App.Ocr;
using QuickPalette.Ocr;

class IconThemeSmoke
{
    [STAThread]
    static void Main()
    {
        var app = new Application();
        app.Startup += async (_, _) =>
        {
            try
            {
                await RenderAsync(app, dark: true);
                await RenderAsync(app, dark: false);
                app.Shutdown();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[✗] " + ex);
                app.Shutdown();
            }
        };
        app.Run();
    }

    static Task RenderAsync(Application app, bool dark)
    {
        var tcs = new TaskCompletionSource();
        var dict = new ResourceDictionary();
        if (dark)
        {
            dict["BgWindow"] = new SolidColorBrush(Color.FromRgb(0x2B, 0x34, 0x46));
            dict["BgTitle"] = new SolidColorBrush(Color.FromRgb(0x22, 0x2A, 0x3A));
            dict["TextPrimary"] = new SolidColorBrush(Colors.White);
            dict["TextSecondary"] = new SolidColorBrush(Color.FromRgb(0xAA, 0xB6, 0xCC));
            dict["InputBrush"] = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
            dict["CloseButtonBrush"] = new SolidColorBrush(Colors.White);
        }
        else
        {
            dict["BgWindow"] = new SolidColorBrush(Colors.White);
            dict["BgTitle"] = new SolidColorBrush(Color.FromRgb(0xF2, 0xF5, 0xFA));
            dict["TextPrimary"] = new SolidColorBrush(Color.FromRgb(0x0F, 0x15, 0x31));
            dict["TextSecondary"] = new SolidColorBrush(Color.FromRgb(0x60, 0x6B, 0x82));
            dict["InputBrush"] = new SolidColorBrush(Color.FromRgb(0xF4, 0xF6, 0xFA));
            dict["CloseButtonBrush"] = new SolidColorBrush(Color.FromRgb(0x44, 0x4C, 0x5E));
        }

        dict["BgWindowBorder"] = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
        dict["InputBorderBrush"] = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0x00, 0x00));
        dict["HoverBrush"] = new SolidColorBrush(Color.FromArgb(0x18, 0x00, 0x00, 0x00));
        dict["AccentBrush"] = new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF));
        dict["AccentHoverBrush"] = new SolidColorBrush(Color.FromRgb(0x3B, 0x7B, 0xE8));

        app.Resources.MergedDictionaries.Clear();
        app.Resources.MergedDictionaries.Add(dict);

        var result = new OcrTextResult("示例文字", new List<string> { "示例文字" },
            new List<OcrLineBox> { new("示例文字", 10, 10, 80, 20) }, "高精度引擎", TimeSpan.Zero);
        var win = new OcrResultWindow();
        win.ShowResult(result);
        win.Show();

        var t = new Thread(() =>
        {
            Thread.Sleep(700);
            app.Dispatcher.Invoke(() =>
            {
                var rtb = new RenderTargetBitmap((int)win.ActualWidth, (int)win.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(win);
                var path = Path.Combine(Path.GetTempPath(), dark ? "qp_icon_dark.png" : "qp_icon_light.png");
                using var fs = File.Create(path);
                new PngBitmapEncoder { Frames = { BitmapFrame.Create(rtb) } }.Save(fs);
                Console.WriteLine($"[✓] {(dark ? "深色" : "浅色")}渲染: {path}");
                win.Close();
                tcs.SetResult();
            });
        });
        t.Start();
        return tcs.Task;
    }
}
