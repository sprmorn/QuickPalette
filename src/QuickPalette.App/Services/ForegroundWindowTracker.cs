using System.Runtime.InteropServices;
using QuickPalette.Infrastructure.Scanners;
using Serilog;

namespace QuickPalette.App.Services;

/// <summary>
/// 前台窗口跟踪器：WinEventHook（EVENT_SYSTEM_FOREGROUND，事件驱动、零轮询、近零 CPU）
/// 持续记住"最后一个非轻启自身、非系统壳的顶层窗口"。
///
/// 用途：托盘菜单/任务栏右键菜单弹出后，GetForegroundWindow 已经不是用户想捕获的窗口
/// （而是菜单/任务栏自己），此时"捕获窗口"必须使用菜单弹出前处于前台的那个窗口——
/// 本跟踪器就是那个"弹出前"的可靠来源。
/// </summary>
public sealed class ForegroundWindowTracker : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutOfContext = 0x0000;

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    private readonly ILogger _logger;
    private readonly WinEventDelegate _callback; // 必须持有引用防 GC 回收
    private readonly object _sync = new();
    private IntPtr _hook = IntPtr.Zero;
    private IntPtr _lastForeground = IntPtr.Zero;

    public ForegroundWindowTracker(ILogger logger)
    {
        _logger = logger;
        _callback = OnWinEvent;
    }

    /// <summary>
    /// 最近一个可用目标窗口（已验证 IsWindow）；IntPtr.Zero = 尚无可信目标。
    /// 可从任意线程读取。
    /// </summary>
    public IntPtr LastForeground
    {
        get
        {
            lock (_sync)
            {
                if (_lastForeground != IntPtr.Zero && !IsWindow(_lastForeground))
                {
                    _lastForeground = IntPtr.Zero;
                }
                return _lastForeground;
            }
        }
    }

    /// <summary>开始跟踪（失败只记警告，绝不抛异常——捕获功能退化为前台窗口直取）</summary>
    public void Start()
    {
        try
        {
            _hook = SetWinEventHook(
                EventSystemForeground, EventSystemForeground,
                IntPtr.Zero, _callback, 0, 0, WineventOutOfContext);
            if (_hook == IntPtr.Zero)
            {
                _logger.Warning("SetWinEventHook(EVENT_SYSTEM_FOREGROUND) failed: {Error}", Marshal.GetLastWin32Error());
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "ForegroundWindowTracker start failed");
        }
    }

    private void OnWinEvent(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        try
        {
            if (hwnd == IntPtr.Zero || idObject != 0 /* OBJID_WINDOW */)
            {
                return;
            }

            // 只记真实目标窗口：轻启自身（面板/设置/向导/托盘菜单）与系统壳窗口一律忽略
            if (IsSelfOrShellWindow(hwnd))
            {
                return;
            }

            lock (_sync)
            {
                _lastForeground = hwnd;
            }
        }
        catch
        {
            // 跟踪失败绝不影响主流程
        }
    }

    private static bool IsSelfOrShellWindow(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == (uint)Environment.ProcessId)
        {
            return true;
        }
        return ProcessScanner.IsShellWindowClass(hwnd);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }

    #region Win32 API

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    #endregion
}
