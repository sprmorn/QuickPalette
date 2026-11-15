namespace QuickPalette.Ocr;

using System.Runtime.InteropServices;
using SkiaSharp;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

/// <summary>
/// Windows 系统 OCR（Windows.Media.Ocr）——零体积、离线、印刷体强。
/// 中文支持取决于系统 OCR 语言包（中文系统默认带）。
/// 字节流经 PNG 中转喂给 SoftwareBitmap（规避 WinRT 缓冲互操作的 RCW 坑）。
/// </summary>
public sealed class WindowsOcrEngine : IOcrEngine
{
    private static readonly Lazy<(bool Ok, string Reason)> Availability = new(() =>
    {
        try
        {
            var langs = OcrEngine.AvailableRecognizerLanguages;
            if (langs == null || langs.Count == 0)
            {
                return (false, "系统未安装 OCR 语言包（设置 → 语言 → 添加中文可选功能）");
            }

            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    });

    public string Name => "系统引擎";

    public bool IsAvailable => Availability.Value.Ok;

    public string UnavailableReason => Availability.Value.Reason;

    public async Task<OcrTextResult> RecognizeAsync(byte[] bgra, int width, int height, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (!IsAvailable)
        {
            return new OcrTextResult(string.Empty, Array.Empty<string>(), Name, sw.Elapsed);
        }

        // 语言优先级：简体中文 > 用户配置 > 第一可用
        OcrEngine? engine = null;
        var langs = OcrEngine.AvailableRecognizerLanguages;
        var zh = langs.FirstOrDefault(l =>
            l.LanguageTag.StartsWith("zh", StringComparison.OrdinalIgnoreCase));
        if (zh != null)
        {
            engine = OcrEngine.TryCreateFromLanguage(zh);
        }

        engine ??= OcrEngine.TryCreateFromUserProfileLanguages();

        if (engine == null)
        {
            return new OcrTextResult(string.Empty, Array.Empty<string>(), Name, sw.Elapsed);
        }

        using var sb = await EncodeToSoftwareBitmapAsync(bgra, width, height, ct);
        var result = await engine.RecognizeAsync(sb).AsTask(ct);

        var lines = result.Lines?.Select(l => l.Text)
                .Where(t => !string.IsNullOrWhiteSpace(t)).ToList()
            ?? new List<string>();
        var text = string.Join(Environment.NewLine, lines);
        return new OcrTextResult(text, lines, Name, sw.Elapsed);
    }

    /// <summary>byte[] BGRA → PNG 内存流 → SoftwareBitmap（可靠且无互操作坑）</summary>
    private static async Task<SoftwareBitmap> EncodeToSoftwareBitmapAsync(byte[] bgra, int width, int height, CancellationToken ct)
    {
        byte[] png;
        using (var sk = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul))
        {
            Marshal.Copy(bgra, 0, sk.GetPixels(), bgra.Length);
            using var ms = new MemoryStream();
            sk.Encode(ms, SKEncodedImageFormat.Png, 100);
            png = ms.ToArray();
        }

        var stream = new InMemoryRandomAccessStream();
        var writer = new DataWriter(stream);
        writer.WriteBytes(png);
        await writer.StoreAsync().AsTask(ct);
        writer.DetachStream();
        writer.Dispose();
        stream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct);
        var sb = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied)
            .AsTask(ct);
        stream.Dispose();
        return sb;
    }
}
