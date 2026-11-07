namespace QuickPalette.App.Services;

using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using QuickPalette.App.Screenshot;
using Serilog;

/// <summary>
/// U盘小助手提示窗：显示"打开文件夹 / 弹出"快捷操作。
/// 右下角弹出、可拖动、20 秒无操作自动关闭。
/// </summary>
public partial class UsbPromptWindow : Window
{
    /// <summary>同屏多弹窗错层偏移（多盘同时插入时不互相遮挡）</summary>
    private static int _openCount;

    private readonly ILogger _logger;
    private readonly string _drive;
    private readonly DispatcherTimer _autoClose;

    public UsbPromptWindow(ILogger logger, string drive)
    {
        InitializeComponent();

        _logger = logger;
        _drive = drive;

        // 大容量可移动磁盘用移动硬盘图标，普通 U盘用 U盘图标
        var bigDisk = IsBigDisk(drive);
        DriveIconHost.Content = ToolbarIcons.Create(bigDisk ? "usb-disk" : "usb", 34);
        OpenIconHost.Content = ToolbarIcons.Create("usb-folder", 15);
        EjectIconHost.Content = ToolbarIcons.Create("usb-eject", 15);

        DriveText.Text = DescribeDrive(drive);

        Loaded += (_, _) => PositionBottomRight();
        SourceInitialized += (_, _) =>
        {
            // 对截屏隐形（与长截图预览窗一致，避免被拍进截图）
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

    private static string DescribeDrive(string drive)
    {
        try
        {
            var info = new DriveInfo(drive);
            var label = string.IsNullOrWhiteSpace(info.VolumeLabel) ? "可移动磁盘" : info.VolumeLabel;
            var freeGb = Math.Round(info.AvailableFreeSpace / 1024.0 / 1024 / 1024, 1);
            return $"{drive.TrimEnd('\\')}：{label}（可用 {freeGb} GB）";
        }
        catch
        {
            return drive;
        }
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

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
}
