using System;
using System.IO;
using System.Threading.Tasks;
using QuickPalette.Ocr;
using SkiaSharp;

class OcrSmoke
{
    static void Main()
    {
        Run().GetAwaiter().GetResult();
    }

    static async Task Run()
    {
        var img = @"D:\Desktop\轻启\website\assets\img\shots\search.png";
        var bmp = SKBitmap.Decode(img);
        Console.WriteLine($"测试图: {Path.GetFileName(img)}  {bmp.Width}x{bmp.Height}");

        var bgra = new byte[bmp.Width * bmp.Height * 4];
        System.Runtime.InteropServices.Marshal.Copy(bmp.GetPixels(), bgra, 0, bgra.Length);

        // ① Windows 系统 OCR
        var win = new WindowsOcrEngine();
        Console.WriteLine($"[系统引擎] 可用={win.IsAvailable}");
        if (win.IsAvailable)
        {
            var r1 = await win.RecognizeAsync(bgra, bmp.Width, bmp.Height);
            Console.WriteLine($"  耗时 {r1.Elapsed.TotalMilliseconds:0}ms，{r1.Lines.Count} 行");
            var p1 = r1.Text.Replace(Environment.NewLine, " / ");
            Console.WriteLine("  预览: " + p1.Substring(0, Math.Min(120, p1.Length)));
        }
        else
        {
            Console.WriteLine("  不可用: " + win.UnavailableReason);
        }

        // ② PaddleOCR 高精度（模型在 App 输出目录 models\v5）
        var paddle = new PaddleOcrEngine(@"D:\Desktop\轻启\src\QuickPalette.App\bin\Debug\net9.0-windows10.0.22621.0");
        Console.WriteLine($"[高精度引擎] 可用={paddle.IsAvailable}");
        if (paddle.IsAvailable)
        {
            var r2 = await paddle.RecognizeAsync(bgra, bmp.Width, bmp.Height);
            Console.WriteLine($"  耗时 {r2.Elapsed.TotalMilliseconds:0}ms，{r2.Lines.Count} 行");
            var p2 = r2.Text.Replace(Environment.NewLine, " / ");
            Console.WriteLine("  预览: " + p2.Substring(0, Math.Min(120, p2.Length)));
        }
        else
        {
            Console.WriteLine("  不可用: " + paddle.UnavailableReason);
        }

        // ③ 二维码自产自销
        Console.WriteLine("[二维码] 生成→识别 自检");
        var qrBmp = GenerateQr("https://github.com/sprmorn/QuickPalette", 300);
        var qrBytes = new byte[qrBmp.Width * qrBmp.Height * 4];
        System.Runtime.InteropServices.Marshal.Copy(qrBmp.GetPixels(), qrBytes, 0, qrBytes.Length);
        var codes = QrService.Decode(qrBytes, qrBmp.Width, qrBmp.Height);
        foreach (var c in codes)
        {
            Console.WriteLine($"  识别: {c.Text}  IsUrl={c.IsUrl}");
        }

        Console.WriteLine(codes.Count > 0 ? "[✓] 二维码通过" : "[✗] 二维码失败");
    }

    static SKBitmap GenerateQr(string content, int size)
    {
        var writer = new ZXing.BarcodeWriter<SKBitmap>
        {
            Format = ZXing.BarcodeFormat.QR_CODE,
            Options = new ZXing.QrCode.QrCodeEncodingOptions
            {
                Width = size,
                Height = size,
                Margin = 1,
            },
            Renderer = new ZXing.SkiaSharp.Rendering.SKBitmapRenderer(),
        };
        return writer.Write(content);
    }
}
