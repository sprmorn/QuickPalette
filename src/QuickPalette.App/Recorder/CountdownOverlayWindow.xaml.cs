namespace QuickPalette.App.Recorder;

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

/// <summary>
/// 录屏倒数 + 区域高亮遮罩（一窗双用）：
/// 全屏灰罩挖洞突出选区 + 选区亮色边框 + 3/2/1 倒数数字（防止“直接开录太冲”，
/// 同时让用户确信录的就是框选的区域）。对录屏/截屏隐形（WDA），不拦截鼠标。
/// </summary>
public partial class CountdownOverlayWindow : Window
{
    private readonly DispatcherTimer _timer;
    private int _count = 3;

    /// <summary>倒数结束（应开始录制）</summary>
    public event Action? CountdownCompleted;

    /// <summary>screenBounds/region：物理像素（相对整屏）</summary>
    public CountdownOverlayWindow(System.Drawing.Rectangle screenBounds, System.Drawing.Rectangle region, int seconds = 3)
    {
        InitializeComponent();

        _count = Math.Max(1, seconds);

        // 窗口覆盖录制屏
        Left = screenBounds.X;
        Top = screenBounds.Y;
        Width = screenBounds.Width;
        Height = screenBounds.Height;

        // 灰罩挖洞
        var outer = new RectangleGeometry(new Rect(0, 0, screenBounds.Width, screenBounds.Height));
        var inner = new RectangleGeometry(new Rect(
            region.X - screenBounds.X, region.Y - screenBounds.Y, region.Width, region.Height));
        MaskPath.Data = new CombinedGeometry(GeometryCombineMode.Exclude, outer, inner);

        // 选区边框
        RegionBorder.Margin = new Thickness(region.X - screenBounds.X, region.Y - screenBounds.Y, 0, 0);
        RegionBorder.Width = region.Width;
        RegionBorder.Height = region.Height;

        // 倒数数字放选区中央（选区太矮则放屏幕中上部）
        if (region.Height >= 160)
        {
            CountText.Margin = new Thickness(
                region.X - screenBounds.X, region.Y - screenBounds.Y + region.Height / 2.0 - 110,
                0, 0);
            CountText.HorizontalAlignment = HorizontalAlignment.Left;
            CountText.VerticalAlignment = VerticalAlignment.Top;
            CountText.Width = region.Width;
            CountText.Height = 220;
        }
        else
        {
            CountText.VerticalAlignment = VerticalAlignment.Top;
            CountText.Margin = new Thickness(0, screenBounds.Height * 0.18, 0, 0);
            CountText.Width = screenBounds.Width;
        }

        CountText.Text = _count.ToString();
        AnimatePulse();

        SourceInitialized += (_, _) =>
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    // 对录屏/截屏隐形——遮罩绝不能被录进去
                    SetWindowDisplayAffinity(hwnd, 0x11);
                }
            }
            catch
            {
                // 忽略
            }
        };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            _count--;
            if (_count > 0)
            {
                CountText.Text = _count.ToString();
                AnimatePulse();
            }
            else
            {
                _timer.Stop();
                CountText.Text = string.Empty;
                // 遮罩保留（持续提示录制区域），仅通知开始录制
                CountdownCompleted?.Invoke();
            }
        };
        Loaded += (_, _) => _timer.Start();
    }

    /// <summary>数字换页的缩放脉冲</summary>
    private void AnimatePulse()
    {
        var scale = new ScaleTransform(1.25, 1.25);
        CountText.RenderTransform = scale;
        CountText.RenderTransformOrigin = new Point(0.5, 0.5);
        var anim = new DoubleAnimation(1.25, 1.0, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
}
