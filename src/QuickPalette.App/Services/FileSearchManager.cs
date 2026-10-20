using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using QuickPalette.Domain.Models;
using QuickPalette.Domain.Services;
using QuickPalette.Infrastructure.FileIndex;
using Serilog;

namespace QuickPalette.App.Services;

/// <summary>文件索引状态</summary>
public enum FileIndexStatus
{
    /// <summary>未启用</summary>
    Disabled,
    /// <summary>Worker 启动中/索引构建中</summary>
    Starting,
    /// <summary>索引就绪</summary>
    Ready,
    /// <summary>需要管理员权限（UAC 被拒绝）</summary>
    NeedsElevation,
    /// <summary>尚未建立索引</summary>
    NoIndex,
    /// <summary>出错</summary>
    Error
}

/// <summary>
/// 本地文件搜索管理器（Everything 式）：
/// 以提权方式拉起 FileIndexWorker 建立/维护全盘索引，主进程只读快照 + 版本号增量重载。
/// 职责边界：
///  1. 所有重活（快照装载 / 进程枚举 / UAC 拉起）都在后台线程，UI 只读状态；
///  2. 索引重载为双缓冲——旁路构建新索引，完成后一次性替换，期间查询不受影响；
///  3. Worker 生命周期受管：启动时校验已有 worker 的 --parent（孤儿另行拉起），
///     死亡后按指数退避重启；协议（--parent / index.bin / index.meta.json / rebuild.request）不变。
/// </summary>
public class FileSearchManager : IDisposable
{
    private const uint SeeMaskNoCloseProcess = 0x40;
    private const int SwHide = 0;
    private const string WorkerProcessName = "QuickPalette.FileIndexWorker";

    private readonly ILogger _logger;
    private readonly FileIndexStore _store;
    private readonly string _workerExe;

    /// <summary>双缓冲索引：重载时整体替换引用，查询线程永远看到完整快照</summary>
    private volatile FileSearchEngine _engine = new();

    private System.Threading.Timer? _pollTimer;
    private volatile bool _enabled;
    private int _lastVersion = -1;
    private volatile FileIndexStatus _status = FileIndexStatus.Disabled;

    // Worker 生命周期
    private int _workerPid;                 // 本实例拉起的 worker（0 = 无）
    private int _restartAttempts;           // 死亡重启次数（退避用）
    private DateTime _nextRestartUtc = DateTime.MinValue;
    private bool _elevationDeclined;        // 用户拒绝过 UAC：不再自动重复弹窗（显式重建除外）
    private int _workerCheckBusy;           // 防重入
    private int _pollBusy;                  // 防重入

    public FileSearchManager(ILogger logger, bool enabled)
    {
        _logger = logger;
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "QuickPalette", "FileIndex");
        _store = new FileIndexStore(dataDir);
        _workerExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "QuickPalette.FileIndexWorker.exe");

        Configure(enabled);
    }

    public FileSearchEngine Engine => _engine;
    public FileIndexStatus Status => _status;
    public int IndexedCount => _engine.Count;
    public DateTime? IndexLoadedAt => _engine.Count > 0 ? _engine.LoadedAt : null;

    /// <summary>启用/停用文件搜索（设置保存后调用）。立即返回，重活在后台执行。</summary>
    public void Configure(bool enabled)
    {
        _enabled = enabled;
        if (!enabled)
        {
            Stop();
            _status = FileIndexStatus.Disabled;
            return;
        }

        // 快照约 200 万条、加载数秒——放到后台线程，避免阻塞 App 启动 / UI
        Task.Run(EnsureRunningCore);
    }

    /// <summary>后台初始化：装载已有快照 → 校验/拉起 Worker → 启动版本轮询</summary>
    private void EnsureRunningCore()
    {
        if (!_enabled)
            return;

        try
        {
            // 1. 先加载已有快照（即使 worker 还没就绪也能立即提供搜索）
            var meta = _store.LoadMeta();
            if (meta is { Status: "ready" } && meta.Count > 0)
            {
                if (_engine.Count == 0)
                    _status = FileIndexStatus.Starting;

                if (Volatile.Read(ref _lastVersion) != meta.Version || _engine.Count == 0)
                {
                    Volatile.Write(ref _lastVersion, meta.Version); // 标记已加载该版本，防止轮询重复加载
                    ReloadIndex();
                }

                if (_enabled && _engine.Count > 0)
                    _status = FileIndexStatus.Ready;

                TrimAfterLoad();
            }
            else if (_engine.Count == 0)
            {
                _status = FileIndexStatus.NoIndex;
            }

            // 2. 确保 Worker 在运行（校验 --parent，孤儿另行拉起）
            EnsureWorkerRunning();

            // 3. 轮询快照版本 + worker 存活监控
            _pollTimer ??= new System.Threading.Timer(
                _ => PollTick(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20));
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to initialize file search");
            _status = FileIndexStatus.Error;
        }
    }

    /// <summary>
    /// 确保提权 Worker 在运行：
    /// 启动时校验已有 worker 的命令行 --parent（本进程 PID 或 --parent auto 均视为受管）；
    /// 拉起首选计划任务（安装时注册，静默提权无 UAC），无任务/失败才走 UAC，
    /// 用户拒绝则降级为已有快照且不重复弹窗。
    /// </summary>
    private void EnsureWorkerRunning()
    {
        if (!_enabled)
            return;
        if (Interlocked.CompareExchange(ref _workerCheckBusy, 1, 0) != 0)
            return;

        try
        {
            var myPid = Environment.ProcessId;
            var foundMine = false;
            var orphans = 0;

            foreach (var process in Process.GetProcessesByName(WorkerProcessName))
            {
                using (process)
                {
                    if (process.Id == _workerPid)
                    {
                        foundMine = true;
                        continue;
                    }

                    // 读取命令行校验 --parent（提权 worker 也可跨完整性级别查询）
                    var commandLine = ProcessCommandLineReader.TryGetCommandLine(process.Id);
                    var parent = ParseParentPid(commandLine);
                    if (parent == myPid || parent == TaskManagedParent)
                    {
                        // --parent auto 的 worker 自绑最新主进程；若它绑到的是刚退出的旧实例，
                        // 会随父进程死亡自行退出，MonitorWorker 的存活监控负责重启（最终一致）
                        foundMine = true;
                        _workerPid = process.Id;
                    }
                    else
                    {
                        orphans++;
                    }
                }
            }

            if (foundMine)
            {
                _restartAttempts = 0;
                _nextRestartUtc = DateTime.MinValue;
                return;
            }

            // 孤儿 worker（--parent 不是本进程）通常几秒内随其父进程死亡自行退出，
            // 等其退出后再拉起，避免两个 worker 并发写同一份快照
            if (orphans > 0)
            {
                _nextRestartUtc = DateTime.UtcNow.AddSeconds(3);
                _logger.Information("Orphan FileIndexWorker detected ({Count}), waiting for exit", orphans);
                return;
            }

            if (DateTime.UtcNow < _nextRestartUtc)
                return;

            if (!File.Exists(_workerExe))
            {
                _logger.Warning("FileIndexWorker.exe not found at {Path}", _workerExe);
                _status = FileIndexStatus.Error;
                return;
            }

            // 首选计划任务静默提权（无 UAC）；便携版/任务缺失回退 runas（UAC）
            if (TryLaunchViaScheduledTask(out var taskPid))
            {
                _workerPid = taskPid;
                _restartAttempts = 0;
                _elevationDeclined = false;
                _logger.Information("FileIndexWorker launched via scheduled task (pid={Pid})", taskPid);
                if (_status != FileIndexStatus.Ready) _status = FileIndexStatus.Starting;
            }
            else if (LaunchElevated($"--parent {myPid}", out var pid))
            {
                _workerPid = pid;
                _restartAttempts = 0;
                _elevationDeclined = false;
                _logger.Information("FileIndexWorker launched elevated (pid={Pid})", pid);
                if (_status != FileIndexStatus.Ready) _status = FileIndexStatus.Starting;
            }
            else
            {
                // UAC 被拒绝/启动失败：降级为已有快照，不再自动重复弹 UAC
                _elevationDeclined = true;
                _logger.Warning("FileIndexWorker elevation declined by user");
                if (_status != FileIndexStatus.Ready) _status = FileIndexStatus.NeedsElevation;
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to start FileIndexWorker");
        }
        finally
        {
            Interlocked.Exchange(ref _workerCheckBusy, 0);
        }
    }

    /// <summary>从 worker 命令行解析 --parent 值（不存在/解析失败返回 0；--parent auto 返回 TaskManagedParent）</summary>
    private const int TaskManagedParent = -2; // 计划任务静默启动的 worker（--parent auto）

    private static int ParseParentPid(string? commandLine)
    {
        if (string.IsNullOrEmpty(commandLine))
            return 0;

        var idx = commandLine.IndexOf("--parent", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return 0;

        var rest = commandLine[(idx + "--parent".Length)..].TrimStart();
        if (rest.StartsWith("auto", StringComparison.OrdinalIgnoreCase))
            return TaskManagedParent;

        var end = 0;
        while (end < rest.Length && char.IsDigit(rest[end]))
            end++;

        return end > 0 && int.TryParse(rest[..end], out var pid) ? pid : 0;
    }

    /// <summary>请求全量重建索引：写入标记文件，由提权 Worker 检测后重建（普通权限无法终止提权进程）</summary>
    public void RequestRebuild()
    {
        try
        {
            var flag = Path.Combine(_store.DirectoryPath, "rebuild.request");
            File.WriteAllText(flag, DateTime.Now.ToString("O"));
            Volatile.Write(ref _lastVersion, -1);
            _status = FileIndexStatus.Starting;
            _logger.Information("Rebuild request written: {Flag}", flag);

            // 显式重建允许再次弹 UAC（用户此前拒绝过也重试一次），后台拉起 worker
            _elevationDeclined = false;
            _nextRestartUtc = DateTime.MinValue;
            Task.Run(EnsureWorkerRunning);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to write rebuild request");
        }
    }

    /// <summary>计划任务名：安装时由 Setup 注册（动作 = worker --parent auto，/RL HIGHEST，按需运行）</summary>
    private const string WorkerTaskName = @"QuickPalette\FileIndexWorker";

    /// <summary>
    /// 经计划任务静默提权启动 worker（无 UAC）：schtasks /Run 由任务计划服务代为提权。
    /// 任务不存在/启动失败/超时未见 worker → 返回 false，调用方回退 runas（UAC）。
    /// </summary>
    private bool TryLaunchViaScheduledTask(out int pid)
    {
        pid = 0;
        try
        {
            using (var schtasks = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Run /TN \"{WorkerTaskName}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }))
            {
                if (schtasks == null) return false;
                if (!schtasks.WaitForExit(5000) || schtasks.ExitCode != 0)
                {
                    _logger.Information("schtasks /Run failed (exit={Code}), fallback to UAC launch",
                        schtasks.HasExited ? schtasks.ExitCode : -1);
                    return false;
                }
            }

            // 计划任务异步拉起 worker：短轮询等待 --parent auto 的 worker 出现
            for (var i = 0; i < 20; i++) // 最多约 5 秒
            {
                Thread.Sleep(250);
                foreach (var process in Process.GetProcessesByName(WorkerProcessName))
                {
                    using (process)
                    {
                        var commandLine = ProcessCommandLineReader.TryGetCommandLine(process.Id);
                        if (ParseParentPid(commandLine) == TaskManagedParent)
                        {
                            pid = process.Id;
                            return true;
                        }
                    }
                }
            }

            _logger.Information("scheduled task ran but no --parent auto worker appeared, fallback to UAC launch");
            return false;
        }
        catch (Exception ex)
        {
            _logger.Information("scheduled task launch unavailable: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>ShellExecuteEx(runas) 提权启动 worker；hProcess 用后立即 CloseHandle</summary>
    private bool LaunchElevated(string args, out int pid)
    {
        pid = 0;
        var info = new ShellExecuteInfo
        {
            cbSize = Marshal.SizeOf<ShellExecuteInfo>(),
            fMask = SeeMaskNoCloseProcess,
            lpVerb = "runas",
            lpFile = _workerExe,
            lpParameters = args,
            nShow = SwHide
        };

        if (!ShellExecuteExW(ref info))
        {
            var error = Marshal.GetLastWin32Error();
            _logger.Warning("ShellExecuteEx failed: {Error}", error);
            return false;
        }

        // SEE_MASK_NOCLOSEPROCESS 返回的 hProcess 必须显式关闭，否则句柄持续泄漏
        if (info.hProcess != IntPtr.Zero)
        {
            try
            {
                pid = (int)GetProcessId(info.hProcess);
            }
            finally
            {
                CloseHandle(info.hProcess);
            }
        }

        return true;
    }

    /// <summary>轮询节拍（防重入）：快照版本 + worker 存活</summary>
    private void PollTick()
    {
        if (!_enabled)
            return;
        if (Interlocked.CompareExchange(ref _pollBusy, 1, 0) != 0)
            return;

        try
        {
            PollVersion();
            MonitorWorker();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Poll file index tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _pollBusy, 0);
        }
    }

    /// <summary>worker 存活监控：死亡则按指数退避重启（用户拒绝过 UAC 时不自动重复弹窗）</summary>
    private void MonitorWorker()
    {
        if (!_enabled)
            return;
        if (_elevationDeclined)
            return;

        var alive = false;
        if (_workerPid != 0)
        {
            try
            {
                using var process = Process.GetProcessById(_workerPid);
                alive = !process.HasExited;
            }
            catch
            {
                alive = false;
            }
        }

        if (!alive && _workerPid != 0)
        {
            // 确认死亡：清掉 pid，指数退避后重启（2s → 4s → ... 上限 60s）
            _logger.Warning("FileIndexWorker (pid={Pid}) is gone, scheduling restart", _workerPid);
            _workerPid = 0;
            _restartAttempts = Math.Min(_restartAttempts + 1, 6);
            var backoffSeconds = Math.Min(60, 2 << (_restartAttempts - 1));
            _nextRestartUtc = DateTime.UtcNow.AddSeconds(backoffSeconds);
        }

        EnsureWorkerRunning();
    }

    private void PollVersion()
    {
        try
        {
            var meta = _store.LoadMeta();
            if (meta == null)
            {
                if (_status == FileIndexStatus.Ready) _status = FileIndexStatus.NoIndex;
                return;
            }

            if (meta.Version == Volatile.Read(ref _lastVersion)) return;
            Volatile.Write(ref _lastVersion, meta.Version);

            switch (meta.Status)
            {
                case "ready":
                    ReloadIndex();
                    if (_enabled && _engine.Count > 0)
                        _status = FileIndexStatus.Ready;
                    TrimAfterLoad();
                    break;
                case "no_permission":
                    _status = FileIndexStatus.NeedsElevation;
                    break;
                case "error":
                    _status = FileIndexStatus.Error;
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Poll file index version failed");
        }
    }

    /// <summary>
    /// 双缓冲重载：在旁路构建完整新索引，构建成功后一次性替换引用；
    /// 不再"先清空再装载"，查询期间始终有完整索引可用。
    /// </summary>
    private void ReloadIndex()
    {
        try
        {
            var meta = _store.LoadMeta();
            var roots = meta?.Volumes?.Select(v => v.RootPath).ToList() ?? new List<string>();

            var fresh = new FileSearchEngine();
            fresh.LoadRaw(_store.ReadSnapshotRaw() ?? Enumerable.Empty<RawEntry>(), roots);

            if (!_enabled)
                return; // 刚被停用：丢弃新索引

            _engine = fresh;
            _logger.Information("File index reloaded: {Count} entries", fresh.Count);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Reload file index failed");
        }
    }

    /// <summary>重载后压缩回收 + 归还工作集</summary>
    private static void TrimAfterLoad()
    {
        // 压缩式回收：合并堆碎片并归还 GC 提交（workstation GC 默认不收缩提交）
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        TrimWorkingSet();
    }

    private void Stop()
    {
        _pollTimer?.Dispose();
        _pollTimer = null;
        _engine = new FileSearchEngine(); // 双缓冲式清空：直接换空索引
        Volatile.Write(ref _lastVersion, -1);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWorkingSetSize(
        IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetProcessId(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>让 OS 回收进程工作集（EmptyWorkingSet 惯用法，SIZE_T 参数 -1 以 IntPtr 传递）</summary>
    private static void TrimWorkingSet()
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            SetProcessWorkingSetSize(p.Handle, (IntPtr)(-1), (IntPtr)(-1));
        }
        catch
        {
            // 失败忽略（非关键）
        }
    }

    public void Dispose()
    {
        _enabled = false;
        _pollTimer?.Dispose();
        _pollTimer = null;
    }

    #region ShellExecuteEx（runas 提权启动）

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShellExecuteExW(ref ShellExecuteInfo lpExecInfo);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string? lpVerb;
        public string lpFile;
        public string? lpParameters;
        public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    #endregion
}
