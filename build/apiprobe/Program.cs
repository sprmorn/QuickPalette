using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuickPalette.App.Ocr;
using QuickPalette.Ocr;

class TableUiSmoke
{
    [STAThread]
    static void Main()
    {
        var app = new Application();

        // 模拟主题资源（真实 App 在 App.xaml 主题字典里）
        var dict = new ResourceDictionary();
        dict["BgWindow"] = new SolidColorBrush(Color.FromRgb(0x2B, 0x34, 0x46));
        dict["BgWindowBorder"] = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
        dict["BgTitle"] = new SolidColorBrush(Color.FromRgb(0x22, 0x2A, 0x3A));
        dict["TextPrimary"] = new SolidColorBrush(Colors.White);
        dict["TextSecondary"] = new SolidColorBrush(Color.FromRgb(0xAA, 0xB6, 0xCC));
        dict["InputBrush"] = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
        dict["InputBorderBrush"] = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        dict["HoverBrush"] = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        dict["AccentBrush"] = new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF));
        dict["AccentHoverBrush"] = new SolidColorBrush(Color.FromRgb(0x3B, 0x7B, 0xE8));
        dict["CloseButtonBrush"] = new SolidColorBrush(Colors.White);
        app.Resources.MergedDictionaries.Add(dict);

        app.Startup += (_, _) =>
        {
            try
            {
                var rows = new List<string[]>
                {
                    new[] { "方案", "语言", "解码能力", "体积", "结论" },
                    new[] { "ZXing.Net", "C# 原生", "QR/条码全家桶，容错（模糊/倾斜/反色）良好", "~500KB", "选" },
                    new[] { "ZBar (P/Invoke)", "C", "快", "原生 dll 分发", "绑定麻烦" },
                    new[] { "QRCoder", "C#", "只生成", "-", "不适用" },
                    new[] { "OpenCV QRCodeDetector", "C++/绑定", "强", "+30MB", "过重" },
                };
                var lines = new List<string>();
                var boxes = new List<OcrLineBox>();
                double y = 10;
                foreach (var r in rows)
                {
                    double x = 50;
                    foreach (var cell in r)
                    {
                        boxes.Add(new OcrLineBox(cell, x, y, Math.Max(30, cell.Length * 12), 20));
                        x += 190;
                    }

                    lines.Add(string.Join(" ", r));
                    y += 40;
                }

                var result = new OcrTextResult(string.Join(Environment.NewLine, lines), lines, boxes, "高精度引擎", TimeSpan.FromMilliseconds(300));
                var win = new OcrResultWindow();
                win.ShowResult(result);
                win.Show();

                // 反射勾选“表格”页
                var tab = (RadioButton)typeof(OcrResultWindow)
                    .GetField("TabTable", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(win)!;
                tab.IsChecked = true;

                var t = new Thread(() =>
                {
                    Thread.Sleep(1000);
                    app.Dispatcher.Invoke(() =>
                    {
                        var rtb = new RenderTargetBitmap((int)win.ActualWidth, (int)win.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                        rtb.Render(win);
                        var path = Path.Combine(Path.GetTempPath(), "qp_table_ui2.png");
                        using var fs = File.Create(path);
                        new PngBitmapEncoder { Frames = { BitmapFrame.Create(rtb) } }.Save(fs);
                        Console.WriteLine("[✓] 表格页渲染: " + path);
                        app.Shutdown();
                    });
                });
                t.Start();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[✗] " + ex);
                app.Shutdown();
            }
        };
        app.Run();
    }
}
