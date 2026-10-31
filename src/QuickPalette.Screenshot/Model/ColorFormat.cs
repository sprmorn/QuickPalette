namespace QuickPalette.Screenshot.Model;

/// <summary>取色器输出格式</summary>
public enum ColorFormatKind
{
    Hex,
    Rgb,
    Hsl
}

/// <summary>
/// 颜色格式化 / 解析：HEX、RGB、HSL 互转。
/// 取色器展示与颜色输入框共用（纯函数，可单测）。
/// </summary>
public static class ColorFormat
{
    /// <summary>格式化为展示串（HEX → #RRGGBB；RGB → rgb(r, g, b)；HSL → hsl(h, s%, l%)）</summary>
    public static string Format(RgbaColor color, ColorFormatKind kind) => kind switch
    {
        ColorFormatKind.Rgb => $"rgb({color.R}, {color.G}, {color.B})",
        ColorFormatKind.Hsl => ToHslString(color),
        _ => ToHex(color)
    };

    /// <summary>#RRGGBB（透明度非 255 时输出 #AARRGGBB）</summary>
    public static string ToHex(RgbaColor color)
        => color.A == 255
            ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
            : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>解析 #RGB / #RRGGBB / #AARRGGBB（带或不带 # 均可）</summary>
    public static bool TryParseHex(string? text, out RgbaColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var s = text.Trim().TrimStart('#');
        try
        {
            switch (s.Length)
            {
                case 3: // RGB → RRGGBB
                    s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);
                    goto case 6;
                case 6:
                    color = new RgbaColor(
                        Convert.ToByte(s[..2], 16),
                        Convert.ToByte(s[2..4], 16),
                        Convert.ToByte(s[4..6], 16));
                    return true;
                case 8: // AARRGGBB
                    color = new RgbaColor(
                        Convert.ToByte(s[2..4], 16),
                        Convert.ToByte(s[4..6], 16),
                        Convert.ToByte(s[6..8], 16),
                        Convert.ToByte(s[..2], 16));
                    return true;
                default:
                    return false;
            }
        }
        catch (FormatException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>hsl(h, s%, l%)：h∈[0,360)，s/l∈[0,100] 取整展示</summary>
    public static string ToHslString(RgbaColor color)
    {
        var (h, s, l) = ToHsl(color);
        return $"hsl({(int)Math.Round(h)}, {(int)Math.Round(s * 100)}%, {(int)Math.Round(l * 100)}%)";
    }

    /// <summary>RGB → HSL（h∈[0,360)，s/l∈[0,1]）</summary>
    public static (double H, double S, double L) ToHsl(RgbaColor color)
    {
        double r = color.R / 255.0;
        double g = color.G / 255.0;
        double b = color.B / 255.0;

        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2.0;

        if (Math.Abs(max - min) < 1e-9)
        {
            return (0, 0, l); // 无饱和度：色相无意义
        }

        var d = max - min;
        var s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);

        double h;
        if (Math.Abs(max - r) < 1e-9)
        {
            h = (g - b) / d + (g < b ? 6 : 0);
        }
        else if (Math.Abs(max - g) < 1e-9)
        {
            h = (b - r) / d + 2;
        }
        else
        {
            h = (r - g) / d + 4;
        }

        h *= 60;
        return (h, s, l);
    }

    /// <summary>HSL → RGB（h∈[0,360)，s/l∈[0,1]；越界钳制）</summary>
    public static RgbaColor HslToRgb(double h, double s, double l, byte alpha = 255)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        l = Math.Clamp(l, 0, 1);

        if (s < 1e-9)
        {
            var v = (byte)Math.Round(l * 255);
            return new RgbaColor(v, v, v, alpha);
        }

        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;

        static double Hue(double p, double q, double t)
        {
            t = ((t % 1) + 1) % 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 1.0 / 2) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }

        var hNorm = h / 360.0;
        return new RgbaColor(
            (byte)Math.Round(Hue(p, q, hNorm + 1.0 / 3) * 255),
            (byte)Math.Round(Hue(p, q, hNorm) * 255),
            (byte)Math.Round(Hue(p, q, hNorm - 1.0 / 3) * 255),
            alpha);
    }
}
