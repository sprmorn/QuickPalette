namespace QuickPalette.Ocr;

using System.Runtime.InteropServices;
using SkiaSharp;
using ZXing;
using ZXing.Common;
using ZXing.SkiaSharp;

/// <summary>二维码识别（ZXing.Net，支持同图多码、TryHarder、反色重试）</summary>
public sealed record QrResult(string Text, bool IsUrl);

public static class QrService
{
    /// <summary>识别 BGRA 图像中的全部二维码（含反色重试）</summary>
    public static IReadOnlyList<QrResult> Decode(byte[] bgra, int width, int height)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        Marshal.Copy(bgra, 0, bitmap.GetPixels(), bgra.Length);

        var found = DecodeBitmap(bitmap);
        if (found.Count == 0)
        {
            using var inverted = Invert(bitmap);
            found = DecodeBitmap(inverted);
        }

        return found;
    }

    private static List<QrResult> DecodeBitmap(SKBitmap bitmap)
    {
        var results = new List<QrResult>();
        try
        {
            var reader = new BarcodeReaderGeneric
            {
                AutoRotate = true,
                Options = new DecodingOptions
                {
                    TryHarder = true,
                    PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.QR_CODE },
                },
            };

            var source = new SKBitmapLuminanceSource(bitmap);
            var multi = reader.DecodeMultiple(source);
            if (multi != null)
            {
                foreach (var r in multi.Where(r => !string.IsNullOrWhiteSpace(r.Text)))
                {
                    results.Add(new QrResult(r.Text, IsUrl(r.Text)));
                }
            }
            else
            {
                var single = reader.Decode(source);
                if (single != null && !string.IsNullOrWhiteSpace(single.Text))
                {
                    results.Add(new QrResult(single.Text, IsUrl(single.Text)));
                }
            }
        }
        catch
        {
            // 识别失败按“无码”处理
        }

        return results;
    }

    private static bool IsUrl(string text)
        => Uri.TryCreate(text.Trim(), UriKind.Absolute, out var u) && u.Scheme.Length > 1;

    private static SKBitmap Invert(SKBitmap src)
    {
        var dst = new SKBitmap(src.Width, src.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var srcSpan = src.GetPixelSpan();
        var dstSpan = dst.GetPixelSpan();
        var n = Math.Min(srcSpan.Length, dstSpan.Length);
        for (var i = 0; i < n; i++)
        {
            dstSpan[i] = (i % 4 == 3) ? srcSpan[i] : (byte)(255 - srcSpan[i]);
        }

        return dst;
    }
}
