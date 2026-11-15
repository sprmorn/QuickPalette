namespace QuickPalette.Ocr;

/// <summary>OCR 识别结果（全文 + 分行）</summary>
public sealed record OcrTextResult(string Text, IReadOnlyList<string> Lines, string EngineName, TimeSpan Elapsed)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Text);
}

/// <summary>OCR 引擎抽象：Windows 系统 OCR / PaddleOCR(RapidOcrNet) 双实现</summary>
public interface IOcrEngine
{
    /// <summary>引擎显示名（结果面板展示）</summary>
    string Name { get; }

    /// <summary>是否可用（缺语言包/缺模型时 false）</summary>
    bool IsAvailable { get; }

    /// <summary>识别 BGRA32 图像（width*height*4 字节）</summary>
    Task<OcrTextResult> RecognizeAsync(byte[] bgra, int width, int height, CancellationToken ct = default);
}
