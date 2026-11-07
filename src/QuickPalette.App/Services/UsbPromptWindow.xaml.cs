namespace QuickPalette.App.Services;

using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using QuickPalette.App.Screenshot;
using Serilog;

/// <summary>
/// U盘小助手提示卡：盘符图标 + 名称 + 容量数字与用量条 + 「弹出 / 打开」。
/// 右下角弹出（多卡错层）、可拖动、20 秒无操作自动关闭；对截屏隐形（--usb-check 除外，便于验证）。
/// </summary>
public partial class UsbPromptWindow : Window
{
    /// <summary>同屏多弹窗错层偏移（多盘同时插入时不互相遮挡）</summary>
    private static int _openCount;

    private readonly ILogger _logger;
    private readonly string _drive;
    private readonly DispatcherTimer _autoClose;
    private double _usedRatio;

    public UsbPromptWindow(ILogger logger, string drive)
    {
        InitializeComponent();

        _logger = logger;
        _drive = drive;

        // 盘符图标（大容量 = 移动硬盘造型）；按钮之外必须显式着色
        var bigDisk = IsBigDisk(drive);
        DriveIconHost.Content = ToolbarIcons.Create(bigDisk ? "usb-disk" : "usb", 34, Brushes.White);
        OpenIconHost.Content = ToolbarIcons.Create("usb-folder", 14);
        EjectIconHost.Content = ToolbarIcons.Create("usb-eject", 14);

        LoadDriveInfo();

        Loaded += (_, _) =>
        {
            PositionBottomRight();

            // --usb-shot：把自身渲染为 PNG（视觉验证用；分层窗口 GDI 截屏看不到，WPF 渲染管线可以）
            if (Environment.GetCommandLineArgs().Any(a => string.Equals(a, "--usb-shot", StringComparison.OrdinalIgnoreCase)))
            {
                Dispatcher.BeginInvoke(RenderSelfForTest, System.Windows.Threading.DispatcherPriority.Loaded);
            }
        };
        SizeChanged += (_, _) => UpdateUsageBar();
        SourceInitialized += (_, _) =>
        {
            // 对截屏隐形（避免工具窗被拍进截图）；测试入口 --usb-check 时跳过以便视觉验证
            if (Environment.GetCommandLineArgs().Any(a => string.Equals(a, "--usb-check", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

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

        _autoClose = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _autoClose.Tick += (_, _) => Close();
        _autoClose.Start();

        _openCount++;
        Closed += (_, _) => _openCount = Math.Max(0, _openCount - 1);
    }

    /// <summary>名称 / 容量数字 / 已用比例</summary>
    private void LoadDriveInfo()
    {
        try
        {
            var info = new DriveInfo(_drive);
            var label = string.IsNullOrWhiteSpace(info.VolumeLabel) ? "可移动磁盘" : info.VolumeLabel;
            var letter = _drive.TrimEnd('\\');
            DriveNameText.Text = $"{label} ({letter})";

            var totalGb = info.TotalSize / 1024.0 / 1024 / 1024;
            var freeGb = info.AvailableFreeSpace / 1024.0 / 1024 / 1024;
            CapacityText.Text = $"可用 {freeGb:0.#} GB / 共 {totalGb:0.#} GB";
            _usedRatio = info.TotalSize <= 0 ? 0 : 1 - (info.AvailableFreeSpace / (double)info.TotalSize);
        }
        catch
        {
            DriveNameText.Text = _drive;
            CapacityText.Text = string.Empty;
            _usedRatio = 0;
        }
    }

    /// <summary>容量条：全长 = 总容量，蓝条 = 已用</summary>
    private void UpdateUsageBar()
    {
        var width = UsageTrack.ActualWidth;
        UsageBar.Width = Math.Max(0, Math.Min(width, width * _usedRatio));
    }

    /// <summary>屏幕右下角弹出（通知位，多窗错层）</summary>
    private void PositionBottomRight()
    {
        // SizeToContent 下 Width/Height 为 NaN，必须用实际尺寸（否则坐标算成 NaN 窗口跑飞）
        var w = double.IsNaN(Width) || Width <= 0 ? ActualWidth : Width;
        var h = double.IsNaN(Height) || Height <= 0 ? ActualHeight : Height;
        var area = SystemParameters.WorkArea;
        Left = Math.Max(0, area.Right - w - 16);
        Top = Math.Max(0, area.Bottom - h - 16 - _openCount * (h + 8));
    }

    /// <summary>标题区拖动</summary>
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

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            UsbDriveMonitor.OpenFolder(_drive);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "USB open folder failed");
        }

        Close();
    }

    private void Eject_Click(object sender, RoutedEventArgs e)
    {
        UsbDriveMonitor.TryEject(_drive, _logger);
        Close();
    }

    /// <summary>测试钩子：RenderTargetBitmap 渲染自身到 PNG（验证 UI 外观）</summary>
    private void RenderSelfForTest()
    {
        try
        {
            UpdateLayout();
            var w = Math.Max(1, (int)Math.Ceiling(ActualWidth));
            var h = Math.Max(1, (int)Math.Ceiling(ActualHeight));
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w * 2, h * 2, 192, 192, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(this);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, $"_usb_prompt_render_{DateTime.Now:HHmmss}.png");
            using var fs = File.Create(path);
            encoder.Save(fs);
            _logger.Information("USB prompt rendered for test: {Path}", path);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "USB prompt test render failed");
        }
    }

    private static bool IsBigDisk(string drive)
    {
        try
        {
            var info = new DriveInfo(drive);
            return info.TotalSize > 128L * 1024 * 1024 * 1024; // >128GB 视为移动硬盘
        }
        catch
        {
            return false;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
}
