namespace QuickPalette.Screenshot.Tests;

using QuickPalette.Screenshot.Capture;

/// <summary>
/// 长截图帧对齐：滚动量检测（拼接精度的核心）
/// </summary>
public class FrameAlignerTests
{
    /// <summary>构造有辨识度的行亮度序列（正弦 + 斜坡，避免全平坦导致歧义）</summary>
    private static List<double> MakeRows(int h, double phase = 0)
    {
        var rows = new List<double>(h);
        for (var y = 0; y < h; y++)
        {
            rows.Add(128 + 60 * Math.Sin((y + phase) * 0.17) + 30 * Math.Sin((y + phase) * 0.043) + y * 0.11);
        }

        return rows;
    }

    [Fact]
    public void Detects_Downward_Scroll()
    {
        // 用户向下滚 50px：内容上移 → new[y] = prev[y+50]
        var h = 300;
        var prev = MakeRows(h);
        var newRows = new List<double>(h);
        for (var y = 0; y < h; y++)
        {
            newRows.Add(y + 50 < h ? prev[y + 50] : 128 + 40 * Math.Sin(y * 0.31));
        }

        var shift = FrameAligner.DetectScroll(prev, newRows, 120);

        Assert.Equal(50, shift);
    }

    [Fact]
    public void Detects_Upward_Scroll()
    {
        var h = 300;
        var prev = MakeRows(h);
        var newRows = new List<double>(h);
        for (var y = 0; y < h; y++)
        {
            newRows.Add(y - 40 >= 0 ? prev[y - 40] : 128 + 40 * Math.Sin(y * 0.29));
        }

        var shift = FrameAligner.DetectScroll(prev, newRows, 120);

        Assert.Equal(-40, shift);
    }

    [Fact]
    public void Static_Frame_Returns_Zero()
    {
        var rows = MakeRows(300);

        Assert.Equal(0, FrameAligner.DetectScroll(rows, rows, 120));
    }

    [Fact]
    public void Tiny_Jitter_Returns_Zero()
    {
        var h = 300;
        var prev = MakeRows(h);
        var newRows = MakeRows(h, 0.02); // 亚像素级抖动 ≈ 未滚动

        var shift = FrameAligner.DetectScroll(prev, newRows, 120);

        Assert.Equal(0, shift);
    }

    [Fact]
    public void Rejects_Mismatched_Or_Too_Short()
    {
        Assert.Null(FrameAligner.DetectScroll(MakeRows(300), MakeRows(200), 100));
        Assert.Null(FrameAligner.DetectScroll(MakeRows(4), MakeRows(4), 3));
        Assert.Null(FrameAligner.DetectScroll(null!, MakeRows(10), 3));
    }

    [Fact]
    public void Large_Scroll_Within_Range()
    {
        var h = 400;
        var prev = MakeRows(h);
        var newRows = new List<double>(h);
        for (var y = 0; y < h; y++)
        {
            newRows.Add(y + 220 < h ? prev[y + 220] : 128 + 40 * Math.Sin(y * 0.37));
        }

        var shift = FrameAligner.DetectScroll(prev, newRows, 250);

        Assert.Equal(220, shift);
    }

    /// <summary>分带特征：行平均相同但左右结构不同的行（文字页常见）也能正确对齐</summary>
    [Fact]
    public void Band_Features_Distinguish_LookAlike_Rows()
    {
        var h = 300;
        // 行特征：8 列带，横向波形随行移动（模拟文本/侧栏结构）
        List<double[]> Make(int phase)
        {
            var rows = new List<double[]>(h);
            for (var y = 0; y < h; y++)
            {
                var band = new double[8];
                for (var b = 0; b < 8; b++)
                {
                    band[b] = 100 + 50 * Math.Sin((y + phase) * 0.13 + b * 0.9) + 25 * Math.Sin((y + phase) * 0.037 * (b + 1));
                }

                rows.Add(band);
            }

            return rows;
        }

        var prev = Make(0);
        var scrolled = new List<double[]>(h);
        for (var y = 0; y < h; y++)
        {
            scrolled.Add(y + 77 < h ? prev[y + 77] : Make(0)[y]);
        }

        var shift = FrameAligner.DetectScroll(prev, scrolled, 150);

        Assert.Equal(77, shift);
    }
}
