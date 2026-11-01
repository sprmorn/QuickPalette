namespace QuickPalette.App.Screenshot;

using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using QuickPalette.App.Services;
using Serilog;

/// <summary>
/// 长截图控制浮窗：实时预览拼接中的长图 + 状态 + 完成/取消。
/// 采集期间主覆盖层隐藏（露出真实内容供滚动），本窗置顶悬浮、可拖动。
/// </summary>
public partial class LongCaptureWindow : Window
{
    private readonly LongCaptureSession _session;
    private readonly ILogger _logger;

    public LongCaptureWindow(LongCaptureSession session, ILogger logger, Point position)
    {
        InitializeComponent();

        _session = session;
        _logger = logger;
        Left = position.X;
        Top = position.Y;

        session.Updated += img => Dispatcher.BeginInvoke(() => ShowPreview(img));
        StatusText.Text = "正在采集…… 滚动页面即可自动拼接";
        Loaded += (_, _) => Keyboard.Focus(this);
        SourceInitialized += (_, _) => SetExcludedFromCapture();

        Closed += (_, _) => _logger.Information("Long capture window closed");
    }

    private void ShowPreview(BitmapSource img)
    {
        PreviewImage.Source = img;
        StatusText.Text = $"已拼接 {_session.Frames} 帧 · 高 {_session.HeightPixels}px（继续滚动…）";
    }

    /// <summary>标题栏区域拖动浮窗</summary>
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

    private void Finish_Click(object sender, RoutedEventArgs e) => _session.Complete();

    private void Cancel_Click(object sender, RoutedEventArgs e) => _session.Cancel();

    /// <summary>Enter=完成，Esc=取消（控制窗获得焦点时）</summary>
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                _session.Complete();
                e.Handled = true;
                break;
            case Key.Escape:
                _session.Cancel();
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// 窗口对截屏 API 隐形（WDA_EXCLUDEFROMCAPTURE）：长截图实时捕获时不把预览窗拍进长图；
    /// GDI 截取下该窗口区域透出后面的真实内容（已实测 0 差异）。
    /// </summary>
    private void SetExcludedFromCapture()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture);
            }
        }
        catch
        {
            // 排除失败不阻塞（最坏情况预览窗偶尔入镜）
        }
    }

    private const uint WdaExcludeFromCapture = 0x11;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
}
