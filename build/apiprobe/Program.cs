using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using QuickPalette.App.Ocr;
using QuickPalette.Ocr;

class QrCardSmoke
{
    [STAThread]
    static void Main()
    {
        var app = new Application();
        app.Startup += (_, _) =>
        {
            try
            {
                var win = new QrPromptWindow();
                win.ShowCodes(new List<QrResult>
                {
                    new("https://github.com/sprmorn/QuickPalette", true),
                    new("123-456-789", false),
                });
                Console.WriteLine("[✓] 扫码卡片显示成功（无 XamlParse/NaN 异常）");
                var t = new Thread(() =>
                {
                    Thread.Sleep(800);
                    app.Dispatcher.Invoke(() =>
                    {
                        Console.WriteLine($"  卡片位置: {win.Left:0},{win.Top:0}  尺寸: {win.ActualWidth:0}x{win.ActualHeight:0}");
                        app.Shutdown();
                    });
                });
                t.Start();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[✗] 卡片显示失败: " + ex.GetType().Name + ": " + ex.Message);
                app.Shutdown();
            }
        };
        app.Run();
    }
}
