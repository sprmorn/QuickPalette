namespace QuickPalette.App.Recorder;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using QuickPalette.App.Screenshot;
using QuickPalette.Recorder;

/// <summary>
/// 录制中细条条：计时 + 麦克风/系统声音开关 + 暂停/继续 + 停止。
/// 深色圆角、可拖动、置顶、对截屏隐形。
/// </summary>
public partial class RecordingBarWindow : Window
{
    private readonly RecordingSession _session;
    private readonly DispatcherTimer _timer;

    /// <summary>用户点停止</summary>
    public event Action? StopRequested;

    public RecordingBarWindow(RecordingSession session, bool micEnabled, bool volumeEnabled)
    {
        InitializeComponent();

        _session = session;

        MicIconHost.Content = ToolbarIcons.Create("rec-mic", 17, Brushes.White);
        VolumeIconHost.Content = ToolbarIcons.Create("rec-volume", 17, Brushes.White);
        PauseIconHost.Content = ToolbarIcons.Create("rec-pause", 17, Brushes.White);
        StopIconHost.Content = ToolbarIcons.Create("rec-stop", 16, Brushes.White);

        MicButton.IsChecked = micEnabled;
        VolumeButton.IsChecked = volumeEnabled;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => TimerText.Text = FormatTime(_session.Elapsed);
        _timer.Start();

        Loaded += (_, _) => PositionNearSelection();
        SourceInitialized += (_, _) =>
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    SetWindowDisplayAffinity(hwnd, 0x11);
                }
            }
            catch
            {
                // 忽略
            }
        };

        Closed += (_, _) => _timer.Stop();
    }

    private static string FormatTime(TimeSpan t)
        => $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";

    /// <summary>置于屏幕下方中间（选区外的空位）</summary>
    private void PositionNearSelection()
    {
        var area = SystemParameters.WorkArea;
        var w = double.IsNaN(Width) || Width <= 0 ? ActualWidth : Width;
        Left = Math.Max(0, (area.Width - w) / 2);
        Top = Math.Max(0, area.Bottom - ActualHeight - 24);
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 竞态忽略
        }
    }

    private void Mic_Toggled(object sender, RoutedEventArgs e) => _session.SetMicEnabled(MicButton.IsChecked == true);

    private void Volume_Toggled(object sender, RoutedEventArgs e) => _session.SetLoopbackEnabled(VolumeButton.IsChecked == true);

    private void Pause_Toggled(object sender, RoutedEventArgs e)
    {
        if (PauseButton.IsChecked == true)
        {
            _session.Pause();
            PauseIconHost.Content = ToolbarIcons.Create("rec-dot", 15, Brushes.White); // 显示"继续"圆点
            ToolTipService.SetToolTip(PauseButton, "继续");
        }
        else
        {
            _session.Resume();
            PauseIconHost.Content = ToolbarIcons.Create("rec-pause", 17, Brushes.White);
            ToolTipService.SetToolTip(PauseButton, "暂停");
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => StopRequested?.Invoke();

    /// <summary>程序触发停止（测试钩子用，与点击停止等价）</summary>
    public void TriggerStop() => StopRequested?.Invoke();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
}
