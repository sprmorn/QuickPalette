namespace QuickPalette.Screenshot.Capture;

using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

/// <summary>
/// 长截图帧对齐：检测两次捕获之间的垂直滚动量（像素）。
/// 做法：把两帧降采样为灰度行特征，按"行平均亮度序列"做一维互相关，
/// 找使重叠区差异最小的位移。滚动量 &lt; 阈值视为未滚动（跳过该帧，避免重复拼接）。
/// </summary>
public static class FrameAligner
{
    /// <summary>未滚动判定阈值（像素）：位移小于该值视为同一画面</summary>
    public const int MinScrollThreshold = 4;

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

    /// <summary>行特征版本（纯逻辑，供单测）：profiles = 每行的平均亮度</summary>
    public static int? DetectScroll(IReadOnlyList<double> prevRows, IReadOnlyList<double> newRows, int maxShift)
    {
        if (prevRows == null || newRows == null || prevRows.Count != newRows.Count || prevRows.Count < 8)
        {
            return null;
        }

        var h = prevRows.Count;
        maxShift = Math.Clamp(maxShift, 1, h - 4);

        // 对每个候选位移 s：比较 prev[s..h) 与 new[0..h-s)（滚动 s 后两帧的重叠区）
        // 差异 = 平均绝对差；要求重叠区足够大（≥ 1/4 帧高）
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
                    diff += Math.Abs(prevRows[y + s] - newRows[y]);
                }
            }
            else
            {
                for (var y = 0; y < overlap; y++)
                {
                    diff += Math.Abs(prevRows[y] - newRows[y - s]);
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

        // 静止画面的行特征几乎一致（任意位移差异都小）：用"零位移是否最优"结合绝对差异兜底
        return Math.Abs(bestShift) < MinScrollThreshold ? 0 : bestShift;
    }

    /// <summary>位图 → 每行平均亮度（灰度）</summary>
    private static double[] RowProfiles(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[data.Stride * data.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);

            var rows = new double[data.Height];
            for (var y = 0; y < data.Height; y++)
            {
                double sum = 0;
                var row = y * data.Stride;
                // 逐 4 像素采样足够表征滚动（求速度）
                for (var x = 0; x < data.Width; x += 4)
                {
                    var i = row + x * 4;
                    sum += (bytes[i] * 0.114) + (bytes[i + 1] * 0.587) + (bytes[i + 2] * 0.299);
                }

                rows[y] = sum / Math.Ceiling(data.Width / 4.0);
            }

            return rows;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
