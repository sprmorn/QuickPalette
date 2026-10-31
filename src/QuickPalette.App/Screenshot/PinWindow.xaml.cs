namespace QuickPalette.App.Screenshot;

using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuickPalette.Screenshot.Capture;
using Serilog;

/// <summary>
/// 钉图小窗（每贴图独立窗口）：拖动移动、滚轮缩放、右键关闭、双击复制到剪贴板。
/// 大图自动缩到屏幕 45% 以内、初始位置钳制在光标所在屏内（防止钉图超屏"看不见"）。
/// </summary>
public partial class PinWindow : Window
{
    private readonly ILogger _logger;
    private readonly BitmapSource _image;
    private double _zoom = 1.0;

    public PinWindow(BitmapSource image, ILogger logger)
    {
        InitializeComponent();

        _image = image;
        _logger = logger;
        PinImage.Source = image;

        // 光标（物理像素）→ 所在显示器
        var cursor = GetCursorPos(out var p) ? new Point(p.X, p.Y) : new Point(200, 200);
        var monitors = ScreenCaptureService.GetMonitors();
        var idx = ScreenCaptureService.MonitorIndexFromPixelPoint(
            monitors, new System.Drawing.Point((int)cursor.X, (int)cursor.Y));
        var monitor = monitors[Math.Clamp(idx, 0, monitors.Count - 1)];
        var scale = monitor.ScaleX;

        // 大图缩放到屏幕 45% 以内（RTB 的 DPI 已含 scale，DIP 尺寸 = 像素 × 96 / dpi）
        var imageDipWidth = Math.Max(1, image.PixelWidth * 96.0 / image.DpiX);
        var imageDipHeight = Math.Max(1, image.PixelHeight * 96.0 / image.DpiY);
        var maxW = monitor.PixelBounds.Width / scale * 0.45;
        var maxH = monitor.PixelBounds.Height / scale * 0.45;
        _zoom = Math.Min(1.0, Math.Min(maxW / imageDipWidth, maxH / imageDipHeight));
        PinImage.LayoutTransform = new ScaleTransform(_zoom, _zoom);

        Loaded += (_, _) =>
        {
            Left = cursor.X / scale + 24;
            Top = cursor.Y / scale + 24;
            ClampToMonitor(monitor, scale);
        };
    }

    /// <summary>窗口整体钳制在光标所在显示器内</summary>
    private void ClampToMonitor(MonitorInfo monitor, double scale)
    {
        var minX = monitor.PixelBounds.Left / scale;
        var minY = monitor.PixelBounds.Top / scale;
        var maxX = monitor.PixelBounds.Right / scale - ActualWidth;
        var maxY = monitor.PixelBounds.Bottom / scale - ActualHeight;
        Left = Math.Max(minX, Math.Min(Left, Math.Max(minX, maxX)));
        Top = Math.Max(minY, Math.Min(Top, Math.Max(minY, maxY)));
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

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint lpPoint);
}
