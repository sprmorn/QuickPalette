namespace QuickPalette.App.Screenshot;

using System.Windows;
using System.Windows.Input;
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
}
