namespace QuickPalette.Screenshot.Model;

/// <summary>
/// 模块内部的 RGBA 颜色（不依赖 WPF/Drawing，便于单测）。
/// UI 层负责与 System.Windows.Media.Color 互转。
/// </summary>
public readonly record struct RgbaColor(byte R, byte G, byte B, byte A = 255)
{
    /// <summary>常用预设（工具栏色板）</summary>
    public static readonly RgbaColor Red = new(239, 68, 68);
    public static readonly RgbaColor Orange = new(245, 158, 11);
    public static readonly RgbaColor Yellow = new(250, 204, 21);
    public static readonly RgbaColor Green = new(34, 197, 94);
    public static readonly RgbaColor Blue = new(59, 130, 246);
    public static readonly RgbaColor Purple = new(168, 85, 247);
    public static readonly RgbaColor Black = new(17, 17, 17);
    public static readonly RgbaColor White = new(255, 255, 255);

    /// <summary>默认标注色</summary>
    public static RgbaColor Default => Red;
}
