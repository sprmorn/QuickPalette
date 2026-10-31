namespace QuickPalette.App.Screenshot;

using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuickPalette.Screenshot.Capture;
using Serilog;

/// <summary>
/// 钉图小窗（每贴图独立窗口）：钉回截图原位置、拖动移动、滚轮缩放、右键关闭、双击复制到剪贴板。
/// 大图自动缩到屏幕 45% 以内、位置钳制在所在显示器内。
/// </summary>
public partial class PinWindow : Window
{
    private readonly ILogger _logger;
    private readonly BitmapSource _image;
    private readonly System.Drawing.Rectangle _pixelOrigin;
    private readonly int _pixelWidth;
    private readonly int _pixelHeight;
    private MonitorInfo _monitor;
    private double _zoom = 1.0;

    public PinWindow(BitmapSource image, ILogger logger, System.Drawing.Rectangle pixelOrigin)
    {
        InitializeComponent();

        _image = image;
        _logger = logger;
        _pixelOrigin = pixelOrigin;
        _pixelWidth = Math.Max(1, image.PixelWidth);
        _pixelHeight = Math.Max(1, image.PixelHeight);
        PinImage.Source = image;

        // 选区所在显示器（像素坐标）
        var monitors = ScreenCaptureService.GetMonitors();
        var idx = ScreenCaptureService.MonitorIndexFromPixelPoint(
            monitors, new System.Drawing.Point(pixelOrigin.X + pixelOrigin.Width / 2, pixelOrigin.Y + pixelOrigin.Height / 2));
        _monitor = monitors[Math.Clamp(idx, 0, monitors.Count - 1)];

        // 1:1 完美覆盖优先：仅当整块屏幕放不下才缩
        _zoom = Math.Min(1.0, Math.Min(
            (double)_monitor.PixelBounds.Width / _pixelWidth,
            (double)_monitor.PixelBounds.Height / _pixelHeight));

        // 像素级设定位置与尺寸：内容 Stretch=Fill 铺满 = 精确覆盖原位，免疫 DPI 解释差异
        SourceInitialized += (_, _) => ApplyGeometry();
        Loaded += (_, _) => ApplyGeometry();
    }

    /// <summary>像素级设定窗口位置与尺寸（钉回截图原位；越界钳制在显示器内）</summary>
    private void ApplyGeometry()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            var b = _monitor.PixelBounds;
            var w = Math.Max(1, (int)Math.Round(_pixelWidth * _zoom));
            var h = Math.Max(1, (int)Math.Round(_pixelHeight * _zoom));
            var x = Math.Clamp(_pixelOrigin.X, b.Left, Math.Max(b.Left, b.Right - w));
            var y = Math.Clamp(_pixelOrigin.Y, b.Top, Math.Max(b.Top, b.Bottom - h));

            SetWindowPos(hwnd, HwndTopmost, x, y, w, h, SwpShowWindow);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Pin SetWindowPos failed");
        }
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标键已释放等竞态，忽略
        }
    }

    private void Window_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        _zoom = Math.Clamp(_zoom + (e.Delta > 0 ? 0.1 : -0.1), 0.2, 5.0);
        ApplyGeometry(); // 缩放 = 改窗口物理尺寸（内容铺满，比例始终精确）
    }

    private void Window_MouseRightButtonUp(object sender, MouseButtonEventArgs e) => Close();

    private void Window_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        try
        {
            Clipboard.SetImage(_image);
            _logger.Information("Pin copied to clipboard");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Pin copy failed");
        }
    }

    private const int HwndTopmost = -1;
    private const uint SwpShowWindow = 0x0040;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, int hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
