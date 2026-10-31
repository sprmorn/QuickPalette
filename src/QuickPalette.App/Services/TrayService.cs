namespace QuickPalette.App.Services;

using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Serilog;

/// <summary>
/// 系统托盘服务：图标（按主题）、菜单（显示面板 / 添加应用 / 捕获当前窗口 / 设置 / 退出）。
/// 图标句柄（HICON）与旧 Icon 实例均显式释放，避免主题切换泄漏 GDI 句柄。
/// </summary>
public class TrayService : IDisposable
{
    private readonly ILogger _logger;
    private readonly Action _showPalette;
    private readonly Action _showAddApp;
    private readonly Action _captureCurrentWindow;
    private readonly Action _takeScreenshot;
    private readonly Action _showSettings;
    private readonly Action _exitApp;

    private readonly NotifyIcon _notifyIcon;
    private bool _disposed;

    public TrayService(
        ILogger logger,
        Action showPalette,
        Action showAddApp,
        Action captureCurrentWindow,
        Action takeScreenshot,
        Action showSettings,
        Action exitApp)
    {
        _logger = logger;
        _showPalette = showPalette;
        _showAddApp = showAddApp;
        _captureCurrentWindow = captureCurrentWindow;
        _takeScreenshot = takeScreenshot;
        _showSettings = showSettings;
        _exitApp = exitApp;

        _notifyIcon = new NotifyIcon
        {
            Icon = CreateIcon("dark"),
            Visible = true,
            Text = UiText.T("tray.tooltip")
        };

        _notifyIcon.MouseClick += NotifyIcon_MouseClick;
        _notifyIcon.ContextMenuStrip = CreateContextMenu();
    }

    /// <summary>按主题更新托盘图标（theme=system 时按实际生效主题选择）</summary>
    public void SetTheme(string theme)
    {
        try
        {
            var resolved = ThemeResolver.Resolve(theme);
            var oldIcon = _notifyIcon.Icon;
            _notifyIcon.Icon = CreateIcon(resolved);
            oldIcon?.Dispose(); // 旧图标显式释放，防止每次主题切换泄漏 GDI 句柄
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to apply tray icon theme: {Theme}", theme);
        }
    }

    /// <summary>从嵌入资源构建 16px 托盘图标（HICON 用后销毁，返回自有句柄的 Icon）</summary>
    private static Icon CreateIcon(string theme)
    {
        var resolved = ThemeResolver.Resolve(theme);
        var uri = new Uri($"Assets/icon-{resolved}.png", UriKind.Relative);
        using var stream = System.Windows.Application.GetResourceStream(uri)?.Stream
            ?? throw new InvalidOperationException($"Icon resource not found: {uri}");
        using var bitmap = new Bitmap(stream);
        using var small = new Bitmap(bitmap, new Size(16, 16));

        var hIcon = small.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone(); // Clone 出的 Icon 自带句柄，可安全 Dispose
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private ContextMenuStrip CreateContextMenu()
    {
        var menu = new ContextMenuStrip();

        var showItem = new ToolStripMenuItem(UiText.T("tray.show"));
        showItem.Click += (s, e) => _showPalette();
        menu.Items.Add(showItem);

        var addItem = new ToolStripMenuItem(UiText.T("tray.add"));
        addItem.Click += (s, e) => _showAddApp();
        menu.Items.Add(addItem);

        // 运行中捕获：把当前前台窗口的进程加入轻启（无快捷方式软件的入库通道）
        var captureItem = new ToolStripMenuItem(UiText.T("tray.capture"));
        captureItem.Click += (s, e) => _captureCurrentWindow();
        menu.Items.Add(captureItem);

        // 截图（Alt+D 全局热键的兜底入口）
        var screenshotItem = new ToolStripMenuItem(UiText.T("tray.screenshot"));
        screenshotItem.Click += (s, e) => _takeScreenshot();
        menu.Items.Add(screenshotItem);

        menu.Items.Add(new ToolStripSeparator());

        var settingsItem = new ToolStripMenuItem(UiText.T("tray.settings"));
        settingsItem.Click += (s, e) => _showSettings();
        menu.Items.Add(settingsItem);

        menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem(UiText.T("tray.exit"));
        exitItem.Click += Exit_Click;
        menu.Items.Add(exitItem);

        return menu;
    }

    private void NotifyIcon_MouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _showPalette();
        }
    }

    private void Exit_Click(object? sender, EventArgs e)
    {
        var result = System.Windows.MessageBox.Show(
            UiText.T("tray.exit.confirm"),
            UiText.T("tray.exit.title"),
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);

        if (result == System.Windows.MessageBoxResult.Yes)
        {
            _logger.Information("User requested exit");
            _exitApp(); // 由 App 统一执行：关主窗口（绕过 Closing 拦截）+ Shutdown + 日志收尾
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _notifyIcon.MouseClick -= NotifyIcon_MouseClick;
        _notifyIcon.Visible = false;
        _notifyIcon.Icon?.Dispose();
        _notifyIcon.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
