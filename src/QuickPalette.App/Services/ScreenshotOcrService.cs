namespace QuickPalette.App.Services;

using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using QuickPalette.Ocr;

/// <summary>
/// 截图 OCR / 扫码服务：选区捕获（GDI）→ 双引擎识别 / 二维码识别。
/// 引擎策略：auto=高精度优先、失败/不可用回退系统引擎。
/// </summary>
public sealed class ScreenshotOcrService
{
    private readonly Serilog.ILogger _logger;
    private readonly WindowsOcrEngine _windows = new();
    private readonly PaddleOcrEngine _paddle = new();
    private readonly object _paddleGate = new();

    public ScreenshotOcrService(Serilog.ILogger logger)
    {
        _logger = logger;
    }

    public WindowsOcrEngine WindowsEngine => _windows;
    public PaddleOcrEngine PaddleEngine => _paddle;

    /// <summary>GDI 捕获区域 → BGRA 字节 + 宽高</summary>
    public static (byte[] Bgra, int Width, int Height) CaptureRegion(Rectangle region)
    {
        using var bmp = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(region.X, region.Y, 0, 0, region.Size);
        }

        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[data.Stride * data.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            return (bytes, bmp.Width, bmp.Height);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    /// <summary>识别选区文字：auto=高精度优先、回退系统；windows/paddle=指定引擎</summary>
    public async Task<OcrTextResult> RecognizeAsync(Rectangle region, string engineMode = "auto", CancellationToken ct = default)
    {
        var (bgra, w, h) = CaptureRegion(region);
        return await RecognizeAsync(bgra, w, h, engineMode, ct);
    }

    /// <summary>识别已捕获图像（先捕获后弹窗，避免把自己弹出的面板截进识别区）</summary>
    public async Task<OcrTextResult> RecognizeAsync(byte[] bgra, int w, int h, string engineMode = "auto", CancellationToken ct = default)
    {
        // 小字放大 2x（Text-Grab 经验：提升小字号识别率）
        if (w < 480 && h < 480)
        {
            (bgra, w, h) = Upscale2x(bgra, w, h);
        }

        switch (engineMode)
        {
            case "windows":
                return await _windows.RecognizeAsync(bgra, w, h, ct);
            case "paddle":
                return await PaddleSafeAsync(bgra, w, h, ct);
            default:
                var paddle = await PaddleSafeAsync(bgra, w, h, ct);
                if (!paddle.IsEmpty)
                {
                    return paddle;
                }

                _logger.Information("PaddleOCR empty/failed, fallback to Windows OCR");
                return await _windows.RecognizeAsync(bgra, w, h, ct);
        }
    }

    /// <summary>识别选区二维码（含反色重试）</summary>
    public IReadOnlyList<QrResult> DecodeQr(Rectangle region)
    {
        var (bgra, w, h) = CaptureRegion(region);
        return QrService.Decode(bgra, w, h);
    }

    private async Task<OcrTextResult> PaddleSafeAsync(byte[] bgra, int w, int h, CancellationToken ct)
    {
        try
        {
            return await _paddle.RecognizeAsync(bgra, w, h, ct);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "PaddleOCR failed");
            return OcrTextResult.Empty(_paddle.Name, TimeSpan.Zero);
        }
    }

    /// <summary>双线性放大 2 倍（小字识别前预处理）</summary>
    private static (byte[] Bgra, int W, int H) Upscale2x(byte[] bgra, int w, int h)
    {
        var w2 = w * 2;
        var h2 = h * 2;
        var dst = new byte[w2 * h2 * 4];
        for (var y = 0; y < h2; y++)
        {
            var sy = y / 2;
            for (var x = 0; x < w2; x++)
            {
                var sx = x / 2;
                var si = (sy * w + sx) * 4;
                var di = (y * w2 + x) * 4;
                dst[di] = bgra[si];
                dst[di + 1] = bgra[si + 1];
                dst[di + 2] = bgra[si + 2];
                dst[di + 3] = bgra[si + 3];
            }
        }

        return (dst, w2, h2);
    }
}
