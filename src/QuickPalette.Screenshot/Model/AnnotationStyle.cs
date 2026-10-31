namespace QuickPalette.Screenshot.Model;

/// <summary>标注类型</summary>
public enum AnnotationKind
{
    /// <summary>矩形</summary>
    Rectangle,
    /// <summary>椭圆</summary>
    Ellipse,
    /// <summary>箭头（Frame 左上 → 右下）</summary>
    Arrow,
    /// <summary>任意画笔</summary>
    Brush,
    /// <summary>荧光笔（低透明度粗笔刷，multiply 叠加）</summary>
    Highlight,
    /// <summary>马赛克（对底图的裁剪滤镜对象，不破坏底图）</summary>
    Mosaic,
    /// <summary>文字</summary>
    Text
}

/// <summary>
/// 标注样式（不可变 record：改样式 = 换对象，天然进入命令历史）。
/// </summary>
public sealed record AnnotationStyle
{
    public RgbaColor Color { get; init; } = RgbaColor.Default;

    /// <summary>描边/笔画粗细（DIP）</summary>
    public double StrokeWidth { get; init; } = 2;

    /// <summary>整体不透明度 [0,1]</summary>
    public double Opacity { get; init; } = 1;

    /// <summary>文字字号（DIP）</summary>
    public int FontSize { get; init; } = 16;

    /// <summary>马赛克粒度（像素块大小）</summary>
    public int MosaicGranularity { get; init; } = 12;

    public static AnnotationStyle Default { get; } = new();

    /// <summary>荧光笔默认样式：半透明、粗、圆头</summary>
    public static AnnotationStyle HighlighterDefault { get; } = new()
    {
        Color = new RgbaColor(250, 204, 21),
        StrokeWidth = 18,
        Opacity = 0.4
    };
}
