namespace QuickPalette.App.Services;

using System.Diagnostics;
using System.Runtime.InteropServices;
using Serilog;

/// <summary>
/// 窗口置顶工具：把当前前台窗口在"置顶 / 取消置顶"间切换（快捷键触发）。
/// 跳过本进程窗口与桌面/任务栏等系统壳窗口。
/// </summary>
public static class WindowTopmostTool
{
    private const int GwlExstyle = -20;
    private const int WsExTopmost = 0x8;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotopmost = new(-2);
    private const uint SwpNomove = 0x0002;
    private const uint SwpNosize = 0x0001;
    private const uint SwpNoactivate = 0x0010;

    /// <summary>切换前台窗口的置顶状态；返回切换后的置顶状态（null=未操作）</summary>
    public static bool? ToggleForeground(ILogger logger)
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                return null;
            }

            // 自己的窗口（搜索面板/设置/截图等）不参与
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == (uint)Environment.ProcessId)
            {
                logger.Information("Topmost toggle skipped (own process window)");
                return null;
            }

            return Toggle(hwnd, logger);
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Toggle topmost failed");
            return null;
        }
    }

    /// <summary>切换指定窗口的置顶状态</summary>
    public static bool? Toggle(IntPtr hwnd, ILogger logger)
    {
        var ex = GetWindowLong(hwnd, GwlExstyle);
        var isTopmost = (ex & WsExTopmost) != 0;
        var target = isTopmost ? HwndNotopmost : HwndTopmost;
        SetWindowPos(hwnd, target, 0, 0, 0, 0, SwpNomove | SwpNosize | SwpNoactivate);
        var now = !isTopmost;
        logger.Information("Topmost toggled: hwnd={Hwnd} topmost={Topmost} ({Proc})",
            hwnd, now, SafeProcessName(hwnd));
        return now;
    }

    private static string SafeProcessName(IntPtr hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            return Process.GetProcessById((int)pid).ProcessName;
        }
        catch
        {
            return "?";
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
