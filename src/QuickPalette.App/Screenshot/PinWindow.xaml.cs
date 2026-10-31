namespace QuickPalette.App.Screenshot;

using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Serilog;

/// <summary>
/// 钉图小窗（每贴图独立窗口）：拖动移动、滚轮缩放、右键关闭、双击复制到剪贴板。
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

        // 初始位置：光标附近偏移，避免全部叠在左上角
        var cursor = GetCursorPos(out var p) ? new Point(p.X, p.Y) : new Point(200, 200);
        Left = cursor.X + 24;
        Top = cursor.Y + 24;
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
