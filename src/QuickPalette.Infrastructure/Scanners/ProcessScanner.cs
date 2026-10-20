namespace QuickPalette.Infrastructure.Scanners;

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using QuickPalette.Domain.Contracts;
using QuickPalette.Domain.Models;
using Serilog;

/// <summary>
/// 进程扫描器（运行中捕获）- 捕获前台窗口所属进程为 RawApp
/// </summary>
public class ProcessScanner : IAppSource
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>
    /// 系统壳/系统宿主进程（通用判断，不依赖具体盘符路径）
    /// </summary>
    private static readonly HashSet<string> ShellProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "shellexperiencehost", "startmenuexperiencehost", "searchhost", "searchui",
        "textinputhost", "searchapp", "dwm", "csrss", "winlogon", "sihost", "taskhostw",
        "runtimebroker", "applicationframehost", "systemsettings", "lockapp", "fontdrvhost"
    };

    private readonly ILogger _logger;

    public string SourceId => "Process";

    public ProcessScanner(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 同步扫描（IAppSource 契约）：捕获当前前台窗口进程
    /// </summary>
    public Task<IEnumerable<RawApp>> ScanAsync(CancellationToken ct = default)
    {
        var apps = new List<RawApp>();

        try
        {
            ct.ThrowIfCancellationRequested();
            var rawApp = CaptureForeground();
            if (rawApp != null)
            {
                apps.Add(rawApp);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Process scan failed");
        }

        _logger.Information("Process scan found {Count} apps", apps.Count);
        return Task.FromResult<IEnumerable<RawApp>>(apps);
    }

    /// <summary>
    /// 捕获前台窗口进程（失败返回 null 并记日志，绝不抛异常）
    /// </summary>
    public RawApp? CaptureForeground()
    {
        return CaptureWindowCore(GetForegroundWindow(), "CaptureForeground");
    }

    /// <summary>
    /// 捕获指定顶层窗口的进程。托盘菜单/任务栏右键会让目标窗口失焦，
    /// 此时必须传入菜单弹出前记录的句柄，而不是捕获"当前前台"（那会是菜单/桌面）。
    /// 失败返回 null 并记日志，绝不抛异常。
    /// </summary>
    public RawApp? CaptureWindow(IntPtr hwnd)
    {
        return CaptureWindowCore(hwnd, "CaptureWindow");
    }

    private RawApp? CaptureWindowCore(IntPtr hwnd, string logTag)
    {
        try
        {
            if (hwnd == IntPtr.Zero)
            {
                _logger.Debug("{Tag}: no target window", logTag);
                return null;
            }

            // 任务栏/开始菜单/桌面/通知区域等壳窗口绝不该被捕获（窗口类判断，进程名判断兜底）
            if (IsShellWindowClass(hwnd))
            {
                _logger.Debug("{Tag}: skip shell window class", logTag);
                return null;
            }

            GetWindowThreadProcessId(hwnd, out var processId);
            if (processId == 0)
            {
                _logger.Debug("{Tag}: target window has no process", logTag);
                return null;
            }

            using var process = Process.GetProcessById((int)processId);

            // 跳过自身
            if (process.Id == Environment.ProcessId)
            {
                _logger.Debug("{Tag}: skip self", logTag);
                return null;
            }

            var path = TryGetProcessPath(process);
            if (string.IsNullOrEmpty(path))
            {
                _logger.Debug("{Tag}: cannot resolve path for pid {Pid}", logTag, processId);
                return null;
            }

            // 跳过系统壳进程与系统目录下的程序（通用系统目录判断，不硬编码盘符）
            if (IsSystemShellOrSystemLocation(process.ProcessName, path))
            {
                _logger.Debug("{Tag}: skip system shell/location: {Path}", logTag, path);
                return null;
            }

            return new RawApp
            {
                Name = process.ProcessName,
                Path = path,
                Type = AppType.Exe,
                SourceId = SourceId
            };
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "{Tag} failed", logTag);
            return null;
        }
    }

    /// <summary>
    /// 异步捕获前台窗口进程
    /// </summary>
    public Task<RawApp?> CaptureForegroundAsync(CancellationToken ct = default)
    {
        return Task.Run(() => CaptureForeground(), ct);
    }

    /// <summary>
    /// 取进程可执行文件路径：优先 QueryFullProcessImageName（跨位数可靠），失败回退 MainModule
    /// </summary>
    private string? TryGetProcessPath(Process process)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)process.Id);
        if (handle != IntPtr.Zero)
        {
            try
            {
                var sb = new StringBuilder(1024);
                var size = (uint)sb.Capacity;
                if (QueryFullProcessImageName(handle, 0, sb, ref size))
                {
                    return sb.ToString();
                }

                _logger.Debug("QueryFullProcessImageName failed for pid {Pid}, fallback to MainModule", process.Id);
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception ex)
        {
            // 位数不一致/权限不足时 MainModule 会抛异常
            _logger.Debug(ex, "MainModule access failed for pid {Pid}", process.Id);
            return null;
        }
    }

    /// <summary>
    /// 通用判断：系统壳进程（按进程名）或位于系统目录（%WINDIR% / System 目录）的程序
    /// </summary>
    private static bool IsSystemShellOrSystemLocation(string processName, string path)
    {
        if (ShellProcessNames.Contains(processName))
            return true;

        var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(windowsDir) &&
            path.StartsWith(windowsDir, StringComparison.OrdinalIgnoreCase))
            return true;

        var systemDir = Environment.SystemDirectory;
        if (!string.IsNullOrEmpty(systemDir) &&
            path.StartsWith(systemDir, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    /// <summary>
    /// 系统壳窗口类：任务栏/开始菜单/桌面/通知区域/系统弹出菜单等，一律不作为捕获目标
    /// </summary>
    private static readonly HashSet<string> ShellWindowClasses = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Shell_Flyout",
        "Progman", "WorkerW", "SHELLDLL_DefView",
        "#32768", // 标准 Win32 弹出菜单（含任务栏右键菜单）
        "NotifyIconOverflowWindow", "TaskListThumbnailWnd", "TaskListOverlayWnd",
        "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow",
    };

    /// <summary>按窗口类判断是否为系统壳窗口（判断失败按非壳处理，交给后续进程名兜底）</summary>
    public static bool IsShellWindowClass(IntPtr hwnd)
    {
        try
        {
            var sb = new StringBuilder(256);
            if (GetClassName(hwnd, sb, sb.Capacity) <= 0)
                return false;
            return ShellWindowClasses.Contains(sb.ToString());
        }
        catch
        {
            return false;
        }
    }

    #region Win32 API

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    #endregion
}
