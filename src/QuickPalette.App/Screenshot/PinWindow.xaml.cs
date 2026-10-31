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
    private double _zoom = 1.0;

    public PinWindow(BitmapSource image, ILogger logger, System.Drawing.Rectangle pixelOrigin)
    {
        InitializeComponent();

        _image = image;
        _logger = logger;
        _pixelOrigin = pixelOrigin;
        PinImage.Source = image;

        // 选区所在显示器（像素坐标）
        var monitors = ScreenCaptureService.GetMonitors();
        var idx = ScreenCaptureService.MonitorIndexFromPixelPoint(
            monitors, new System.Drawing.Point(pixelOrigin.X + pixelOrigin.Width / 2, pixelOrigin.Y + pixelOrigin.Height / 2));
        var monitor = monitors[Math.Clamp(idx, 0, monitors.Count - 1)];
        var scale = monitor.ScaleX;

        // 大图缩放到屏幕 45% 以内（RTB 的 DPI 已含 scale，DIP 尺寸 = 像素 × 96 / dpi）
        var imageDipWidth = Math.Max(1, image.PixelWidth * 96.0 / image.DpiX);
        var imageDipHeight = Math.Max(1, image.PixelHeight * 96.0 / image.DpiY);
        var maxW = monitor.PixelBounds.Width / scale * 0.45;
        var maxH = monitor.PixelBounds.Height / scale * 0.45;
        _zoom = Math.Min(1.0, Math.Min(maxW / imageDipWidth, maxH / imageDipHeight));
        PinImage.LayoutTransform = new ScaleTransform(_zoom, _zoom);

        // 钉回截图原位置（像素级置位，绕过跨 DPI 的 DIP 换算误差）
        SourceInitialized += (_, _) => PositionPixelPerfect(monitor);
        Loaded += (_, _) => PositionPixelPerfect(monitor);
    }

    /// <summary>像素级置位：钉在选区原位，越界钳制在显示器内</summary>
    private void PositionPixelPerfect(MonitorInfo monitor)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            var b = monitor.PixelBounds;
            var wPx = Math.Max(1, (int)Math.Round(ActualWidth * monitor.ScaleX));
            var hPx = Math.Max(1, (int)Math.Round(ActualHeight * monitor.ScaleY));
            var x = Math.Clamp(_pixelOrigin.X, b.Left, Math.Max(b.Left, b.Right - wPx));
            var y = Math.Clamp(_pixelOrigin.Y, b.Top, Math.Max(b.Top, b.Bottom - hPx));

            SetWindowPos(hwnd, HwndTopmost, x, y, 0, 0, SwpNoSize);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Pin SetWindowPos failed, fallback to WPF placement");
            Left = _pixelOrigin.X / monitor.ScaleX;
            Top = _pixelOrigin.Y / monitor.ScaleY;
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
        PinImage.LayoutTransform = new ScaleTransform(_zoom, _zoom);
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
    private const uint SwpNoSize = 0x0001;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, int hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
