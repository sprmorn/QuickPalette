namespace QuickPalette.App.Recorder;

using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

/// <summary>
/// 录屏倒数 + 区域高亮遮罩（一窗双用）：
/// 全屏灰罩挖洞突出选区 + 选区亮色边框 + 3/2/1 倒数数字（防止“直接开录太冲”，
/// 同时让用户确信录的就是框选的区域）。对录屏/截屏隐形（WDA），不拦截鼠标。
///
/// 坐标系：入参为物理像素；窗口定位用 SetWindowPos（物理矩形，免受 WPF DIP
/// 换算影响——多屏缩放下 Left/Top 会偏），内部布局按窗口 DPI 换算为 DIP。
/// </summary>
public partial class CountdownOverlayWindow : Window
{
    private readonly DispatcherTimer _timer;
    private int _count = 3;
    private readonly System.Drawing.Rectangle _screen;
    private readonly System.Drawing.Rectangle _region;
    private readonly int _seconds;

    /// <summary>倒数结束（应开始录制）</summary>
    public event Action? CountdownCompleted;

    /// <summary>screenBounds/region：物理像素（相对整屏）。内部统一换算 DIP（物理÷目标屏缩放），
    /// 由 WPF 自行换算回物理——不与 per-monitor DPI 缩放对抗（对抗会被打 1/Scale 折扣）。</summary>
    public CountdownOverlayWindow(System.Drawing.Rectangle screenBounds, System.Drawing.Rectangle region, int seconds = 3)
    {
        InitializeComponent();

        _screen = screenBounds;
        _region = region;
        _seconds = Math.Max(1, seconds);
        _count = _seconds;

        // 目标屏 DPI（选区中心所在屏）
        var cx = screenBounds.X + screenBounds.Width / 2;
        var cy = screenBounds.Y + screenBounds.Height / 2;
        var hmon = MonitorFromPoint(((long)cy << 32) | (uint)cx, 2);
        double scale = 1.0;
        if (hmon != IntPtr.Zero && GetDpiForMonitor(hmon, 0, out var dpiX, out _) == 0 && dpiX > 0)
        {
            scale = dpiX / 96.0;
        }

        // 窗口覆盖目标屏（WPF DIP 坐标）
        Left = screenBounds.X / scale;
        Top = screenBounds.Y / scale;
        Width = screenBounds.Width / scale;
        Height = screenBounds.Height / scale;

        ApplyLayout(scale);
        CountText.Text = _count.ToString();
        AnimatePulse();

        SourceInitialized += (_, _) =>
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
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
        Loaded += (_, _) =>
        {
            // 多屏/混合 DPI 保险：PMv2 下 Win32 物理坐标最权威——再钉一次物理矩形，
            // 并按窗口实际 DPI 重算内部布局（跨屏时 WPF 的 Left/Top 换算规则复杂，直接绕开）
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                SetWindowPos(hwnd, new IntPtr(-1 /* HWND_TOPMOST */),
                    _screen.X, _screen.Y, _screen.Width, _screen.Height,
                    0x0010 | 0x0040);
                var dpi = GetDpiForWindow(hwnd);
                var s = dpi > 0 ? dpi / 96.0 : scale;
                ApplyLayout(s);
            }
            catch
            {
                // 忽略
            }

            _timer.Start();
        };
    }

    /// <summary>DIP 布局：灰罩挖洞 + 选区边框 + 倒数数字定位（scale=目标屏 DPI/96）</summary>
    private void ApplyLayout(double scale)
    {
        var sw = _screen.Width / scale;
        var sh = _screen.Height / scale;
        var rx = (_region.X - _screen.X) / scale;
        var ry = (_region.Y - _screen.Y) / scale;
        var rw = _region.Width / scale;
        var rh = _region.Height / scale;

        var outer = new RectangleGeometry(new Rect(0, 0, sw, sh));
        var inner = new RectangleGeometry(new Rect(rx, ry, rw, rh));
        MaskPath.Data = new CombinedGeometry(GeometryCombineMode.Exclude, outer, inner);

        // 边框与洞共用同一矩形几何（结构性保证不会错位）
        RegionPath.Data = inner;

        // 倒数数字放选区中央（选区太矮则放屏幕中上部）
        if (rh >= 120)
        {
            CountText.HorizontalAlignment = HorizontalAlignment.Left;
            CountText.VerticalAlignment = VerticalAlignment.Top;
            CountText.Margin = new Thickness(rx, ry + rh / 2.0 - 80, 0, 0);
            CountText.Width = rw;
            CountText.Height = 160;
            CountText.FontSize = Math.Min(160, Math.Max(64, rh * 0.55));
        }
        else
        {
            CountText.VerticalAlignment = VerticalAlignment.Top;
            CountText.Margin = new Thickness(0, sh * 0.18, 0, 0);
            CountText.Width = sw;
            CountText.FontSize = 160;
        }
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

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(long point, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmon, int type, out uint dpiX, out uint dpiY);
}
