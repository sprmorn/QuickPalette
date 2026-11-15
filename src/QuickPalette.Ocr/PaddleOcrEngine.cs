namespace QuickPalette.Ocr;

using System.Runtime.InteropServices;
using SkiaSharp;

/// <summary>
/// PaddleOCR 高精度引擎（RapidOcrNet + PP-OCRv5 ONNX 模型，纯托管本地推理）。
/// 中英混排强；识别模型 ch_PP-OCRv5_rec（多语言合一）+ 中文词典。
/// 模型位于 [baseDir]/models/v5/（det/cls 由 RapidOcrNet 包内置，rec/dict 随轻启分发）。
/// </summary>
public sealed class PaddleOcrEngine : IOcrEngine
{
    private readonly string _baseDir;
    private RapidOcrNet.RapidOcr? _ocr;
    private bool _initFailed;

    public PaddleOcrEngine(string? baseDir = null)
    {
        _baseDir = baseDir ?? AppContext.BaseDirectory;
    }

    public string Name => "高精度引擎";

    public bool IsAvailable => EnsureReady();

    public string UnavailableReason => "本地识别模型缺失（models/v5/ch_PP-OCRv5_rec_mobile_infer.onnx）";

    private bool EnsureReady()
    {
        if (_ocr != null)
        {
            return true;
        }

        if (_initFailed)
        {
            return false;
        }

        try
        {
            var v5 = Path.Combine(_baseDir, "models", "v5");
            var det = Path.Combine(v5, "ch_PP-OCRv5_mobile_det.onnx");
            var cls = Path.Combine(v5, "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx");
            var rec = Path.Combine(v5, "ch_PP-OCRv5_rec_mobile_infer.onnx");
            var dict = Path.Combine(v5, "ppocrv5_dict.txt");

            if (!File.Exists(det) || !File.Exists(cls) || !File.Exists(rec) || !File.Exists(dict))
            {
                _initFailed = true;
                return false;
            }

            var ocr = new RapidOcrNet.RapidOcr();
            ocr.InitModels(det, cls, rec, dict);
            _ocr = ocr;
            return true;
        }
        catch
        {
            _initFailed = true;
            return false;
        }
    }

    public async Task<OcrTextResult> RecognizeAsync(byte[] bgra, int width, int height, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (!IsAvailable)
        {
            return new OcrTextResult(string.Empty, Array.Empty<string>(), Name, sw.Elapsed);
        }

        using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        Marshal.Copy(bgra, 0, bitmap.GetPixels(), bgra.Length);

        var result = await _ocr!.DetectAsync(bitmap, RapidOcrNet.RapidOcrOptions.Default, null, ct)
            .ConfigureAwait(false);

        var lines = result.TextBlocks?
                .Select(b => b.Text)
                .Where(t => !string.IsNullOrWhiteSpace(t)).ToList()
            ?? new List<string>();
        var text = string.Join(Environment.NewLine, lines);
        return new OcrTextResult(text, lines, Name, sw.Elapsed);
    }
}
