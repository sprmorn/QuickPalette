namespace QuickPalette.Screenshot.Capture;

using System.Drawing;

/// <summary>显示器信息（像素边界 + 缩放比例）</summary>
public sealed record MonitorInfo(int Index, Rectangle PixelBounds, double ScaleX, double ScaleY)
{
    /// <summary>像素 → DIP</summary>
    public double ToDip(double pixel) => pixel / ScaleX;

    /// <summary>DIP → 像素</summary>
    public double ToPixel(double dip) => dip * ScaleX;
}

/// <summary>捕获到的一屏画面（持有 Bitmap，调用方负责 Dispose）</summary>
public sealed class CapturedScreen : IDisposable
{
    public MonitorInfo Monitor { get; }
    public Bitmap Bitmap { get; }

    public CapturedScreen(MonitorInfo monitor, Bitmap bitmap)
    {
        Monitor = monitor;
        Bitmap = bitmap;
    }

    public void Dispose() => Bitmap.Dispose();
}

/// <summary>
/// 显示器枚举与整屏捕获（GDI CopyFromScreen，物理像素）。
/// DPI 经 GetDpiForMonitor(MDT_EFFECTIVE_DPI) 获取，多屏混合 DPI 各自独立。
/// </summary>
public static class ScreenCaptureService
{
    /// <summary>枚举全部显示器（像素坐标，含任务栏所在工作区外的边界）</summary>
    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr hMonitor, IntPtr hdc, ref NativeMethods.Rect rect, IntPtr dwData) =>
            {
                var info = new NativeMethods.MonitorInfoEx();
                info.Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfoEx>();
                if (NativeMethods.GetMonitorInfo(hMonitor, ref info))
                {
                    var bounds = Rectangle.FromLTRB(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom);
                    var (dpiX, _) = GetMonitorDpi(hMonitor);
                    var scale = dpiX / 96.0;
                    monitors.Add(new MonitorInfo(monitors.Count, bounds, scale, scale));
                }

                return true;
            }, IntPtr.Zero);

        return monitors;
    }

    /// <summary>取包含指定像素点的显示器下标（找不到取主屏/第一块）</summary>
    public static int MonitorIndexFromPixelPoint(IReadOnlyList<MonitorInfo> monitors, Point pixelPoint)
    {
        for (var i = 0; i < monitors.Count; i++)
        {
            if (monitors[i].PixelBounds.Contains(pixelPoint))
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>捕获单屏（物理像素）</summary>
    public static Bitmap Capture(MonitorInfo monitor)
    {
        var bounds = monitor.PixelBounds;
        var bitmap = new Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        return bitmap;
    }

    /// <summary>捕获全部显示器</summary>
    public static IReadOnlyList<CapturedScreen> CaptureAll()
    {
        var result = new List<CapturedScreen>();
        foreach (var monitor in GetMonitors())
        {
            result.Add(new CapturedScreen(monitor, Capture(monitor)));
        }

        return result;
    }

    private static (uint X, uint Y) GetMonitorDpi(IntPtr hMonitor)
    {
        try
        {
            // MDT_EFFECTIVE_DPI = 0
            if (NativeMethods.GetDpiForMonitor(hMonitor, 0, out var x, out var y) == 0)
            {
                return (x, y);
            }
        }
        catch (EntryPointNotFoundException)
        {
            // Windows 8.1 以下无此 API
        }

        return (96, 96);
    }
}
