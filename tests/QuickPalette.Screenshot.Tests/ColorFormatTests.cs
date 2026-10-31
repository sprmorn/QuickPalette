namespace QuickPalette.Screenshot.Tests;

using QuickPalette.Screenshot.Model;

/// <summary>
/// ColorFormat HEX/RGB/HSL 互转测试（取色器格式化与解析）
/// </summary>
public class ColorFormatTests
{
    [Fact]
    public void ToHex_Opaque_Uses_RRGGBB()
    {
        Assert.Equal("#FF8800", ColorFormat.ToHex(new RgbaColor(255, 136, 0)));
    }

    [Fact]
    public void ToHex_Transparent_Uses_AARRGGBB()
    {
        Assert.Equal("#80FF8800", ColorFormat.ToHex(new RgbaColor(255, 136, 0, 128)));
    }

    [Theory]
    [InlineData("#FF8800", 255, 136, 0, 255)]
    [InlineData("FF8800", 255, 136, 0, 255)]
    [InlineData("#F80", 255, 136, 0, 255)]
    [InlineData("#80FF8800", 255, 136, 0, 128)]
    public void TryParseHex_Supports_Common_Forms(string text, byte r, byte g, byte b, byte a)
    {
        Assert.True(ColorFormat.TryParseHex(text, out var color));
        Assert.Equal(new RgbaColor(r, g, b, a), color);
    }

    [Theory]
    [InlineData("")]
    [InlineData("#GGG")]
    [InlineData("#12345")]
    [InlineData("not-a-color")]
    public void TryParseHex_Rejects_Invalid(string text)
    {
        Assert.False(ColorFormat.TryParseHex(text, out _));
    }

    [Fact]
    public void Hex_RoundTrip()
    {
        var original = new RgbaColor(12, 34, 56);
        Assert.True(ColorFormat.TryParseHex(ColorFormat.ToHex(original), out var parsed));
        Assert.Equal(original, parsed);
    }

    [Fact]
    public void ToHsl_Primary_Colors()
    {
        var (h, s, l) = ColorFormat.ToHsl(new RgbaColor(255, 0, 0));
        Assert.Equal(0, h, 0);
        Assert.Equal(1, s, 5);
        Assert.Equal(0.5, l, 5);

        (h, s, l) = ColorFormat.ToHsl(new RgbaColor(0, 0, 255));
        Assert.Equal(240, h, 0);
    }

    [Fact]
    public void HslToRgb_RoundTrip_Within_One_Byte()
    {
        var original = new RgbaColor(200, 90, 45);
        var (h, s, l) = ColorFormat.ToHsl(original);
        var back = ColorFormat.HslToRgb(h, s, l, original.A);

        Assert.InRange(back.R, original.R - 1, original.R + 1);
        Assert.InRange(back.G, original.G - 1, original.G + 1);
        Assert.InRange(back.B, original.B - 1, original.B + 1);
    }

    [Fact]
    public void HslToRgb_Gray_And_Clamping()
    {
        // 无饱和度 → 灰
        var gray = ColorFormat.HslToRgb(0, 0, 0.5);
        Assert.Equal(gray.R, gray.G);
        Assert.Equal(gray.G, gray.B);

        // 越界输入钳制不抛异常
        var clamped = ColorFormat.HslToRgb(-370, 2, -1);
        Assert.Equal(new RgbaColor(0, 0, 0), clamped);
    }

    [Fact]
    public void Format_Output_Kinds()
    {
        var color = new RgbaColor(255, 136, 0);
        Assert.Equal("#FF8800", ColorFormat.Format(color, ColorFormatKind.Hex));
        Assert.Equal("rgb(255, 136, 0)", ColorFormat.Format(color, ColorFormatKind.Rgb));
        Assert.StartsWith("hsl(", ColorFormat.Format(color, ColorFormatKind.Hsl));
    }
}
