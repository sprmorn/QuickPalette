namespace QuickPalette.Screenshot.Capture;

using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

/// <summary>
/// 长截图帧对齐：检测两次捕获之间的垂直滚动量（像素）。
/// 特征 = 每行分横向多段的平均亮度（行 × 列带），比纯行平均更能区分文字/图像行，
/// 大幅降低文字页"对偏几行造成接缝重复"的概率。
/// </summary>
public static class FrameAligner
{
    /// <summary>未滚动判定阈值（像素）：位移小于该值视为同一画面</summary>
    public const int MinScrollThreshold = 4;

    /// <summary>行特征的横向分带数</summary>
    private const int Bands = 8;

    /// <summary>
    /// 计算 newFrame 相对 prevFrame 的垂直滚动量（正数 = 内容上移 = 用户向下滚动）。
    /// 无法可靠对齐时返回 null（调用方跳过该帧）。
    /// </summary>
    public static int? DetectScroll(Bitmap prev, Bitmap @new, int maxShift)
    {
        if (prev == null || @new == null)
        {
            return null;
        }

        if (prev.Width != @new.Width || prev.Height != @new.Height)
        {
            return null;
        }

        var prevRows = RowProfiles(prev);
        var newRows = RowProfiles(@new);
        return DetectScroll(prevRows, newRows, maxShift);
    }

    /// <summary>行特征版本（纯逻辑，供单测）：profiles = 每行的亮度特征（多列带）</summary>
    public static int? DetectScroll(IReadOnlyList<double[]> prevRows, IReadOnlyList<double[]> newRows, int maxShift)
    {
        if (prevRows == null || newRows == null || prevRows.Count != newRows.Count || prevRows.Count < 8)
        {
            return null;
        }

        var h = prevRows.Count;
        maxShift = Math.Clamp(maxShift, 1, h - 4);

        // 对每个候选位移 s：比较 prev[s..h) 与 new[0..h-s)（滚动 s 后两帧的重叠区）
        var minOverlap = Math.Max(8, h / 4);
        var bestShift = 0;
        var bestScore = double.MaxValue;

        for (var s = -maxShift; s <= maxShift; s++)
        {
            var overlap = h - Math.Abs(s);
            if (overlap < minOverlap)
            {
                continue;
            }

            double diff = 0;
            if (s >= 0)
            {
                for (var y = 0; y < overlap; y++)
                {
                    diff += RowDistance(prevRows[y + s], newRows[y]);
                }
            }
            else
            {
                for (var y = 0; y < overlap; y++)
                {
                    diff += RowDistance(prevRows[y], newRows[y - s]);
                }
            }

            var score = diff / overlap;
            if (score < bestScore)
            {
                bestScore = score;
                bestShift = s;
            }
        }

        if (bestScore == double.MaxValue)
        {
            return null;
        }

        return Math.Abs(bestShift) < MinScrollThreshold ? 0 : bestShift;
    }

    /// <summary>单行特征距离（各列带平均绝对差之和）</summary>
    private static double RowDistance(double[] a, double[] b)
    {
        double d = 0;
        for (var i = 0; i < a.Length && i < b.Length; i++)
        {
            d += Math.Abs(a[i] - b[i]);
        }

        return d;
    }

    /// <summary>兼容入口：单值行特征（等价于单列带）</summary>
    public static int? DetectScroll(IReadOnlyList<double> prevRows, IReadOnlyList<double> newRows, int maxShift)
    {
        if (prevRows == null || newRows == null)
        {
            return null;
        }

        return DetectScroll(
            prevRows.Select(v => new[] { v }).ToList(),
            newRows.Select(v => new[] { v }).ToList(),
            maxShift);
    }

    /// <summary>位图 → 每行的横向分带平均亮度（灰度）</summary>
    private static double[][] RowProfiles(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[data.Stride * data.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);

            var rows = new double[data.Height][];
            var bandWidth = Math.Max(1, data.Width / Bands);
            for (var y = 0; y < data.Height; y++)
            {
                var row = y * data.Stride;
                var bandSums = new double[Bands];
                var bandCounts = new double[Bands];
                for (var x = 0; x < data.Width; x++)
                {
                    var i = row + x * 4;
                    var b = Math.Min(Bands - 1, x / bandWidth);
                    bandSums[b] += (bytes[i] * 0.114) + (bytes[i + 1] * 0.587) + (bytes[i + 2] * 0.299);
                    bandCounts[b]++;
                }

                for (var b = 0; b < Bands; b++)
                {
                    bandSums[b] = bandCounts[b] > 0 ? bandSums[b] / bandCounts[b] : 0;
                }

                rows[y] = bandSums;
            }

            return rows;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
