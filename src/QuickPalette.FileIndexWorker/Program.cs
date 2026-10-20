using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using QuickPalette.Infrastructure.FileIndex;

namespace QuickPalette.FileIndexWorker;

/// <summary>
/// 轻启文件索引 Worker（以管理员权限运行）
/// 职责：全量枚举 NTFS/ReFS MFT（FSCTL_ENUM_USN_DATA）建索引 → 常驻监听 USN Journal 增量更新 → 持久化快照
/// 主进程（普通权限）通过快照 + 版本号消费索引。
///
/// 通信协议（与主进程约定，勿改）：
///  - 启动参数 --parent &lt;pid&gt;（主进程 PID）；
///  - 快照 %APPDATA%\QuickPalette\FileIndex\index.bin + index.meta.json（Version/Count/Status/Volumes）；
///  - 全量重建标记 rebuild.request（主进程写、Worker 消费后删除）。
///
/// 退出码语义：
///  0 = 正常退出（--once 完成 / 收到退出请求并已保存最终快照）
///  2 = 参数错误（--parent 缺失/非法，含 --parent 0、int.MinValue）
///  3 = 父进程丢失（启动时无法验证父进程，或运行中父进程死亡/启动时间不符）
///  4 = 已有实例在运行（单实例命名互斥量冲突）
///  5 = 权限不足（非管理员）
///  6 = 初始化失败（无可用卷 / 索引初始化失败）
///  7 = 未处理异常
/// </summary>
internal static class Program
{
    // ── 退出码 ──
    private const int ExitOk = 0;
    private const int ExitBadArgs = 2;
    private const int ExitParentLost = 3;
    private const int ExitAlreadyRunning = 4;
    private const int ExitNoPermission = 5;
    private const int ExitInitFailed = 6;
    private const int ExitUnhandled = 7;

    private const int ParentPollIntervalMs = 10000;         // 父进程存活轮询间隔（10s：降低空转唤醒，父退出检测延迟 ≤10s 可接受）
    private static readonly TimeSpan IncrementWait = TimeSpan.FromMilliseconds(3000); // 增量读取等待下一条记录的最小时间（3s：减少空唤醒，变更延迟 ≤3s 可接受）
    private const int SaveJoinTimeoutMs = 5000;             // 退出时等待各卷循环收尾的限时

    private static readonly object SaveSync = new();        // 只串行化"落盘"动作（秒级），绝不包住阻塞 I/O
    private static readonly object WarningSync = new();
    private static readonly Dictionary<string, string> VolumeWarnings = new(); // 卷 → 警告文本（无法解析被排除等）
    private static readonly List<VolumeJob> Volumes = new(); // 初始化后不再增删，各线程只读遍历
    private static readonly Stopwatch SaveTimer = Stopwatch.StartNew();
    private static int PendingChanges;                       // 跨卷累积的未落盘变化数（节流保存用）
    private static int _rebuildGate;                         // 0=空闲 1=重建中（Interlocked 防并发触发）
    private static int _finalSaveDone;                       // 最终快照只保存一次

    private static FileIndexStore _store = null!;
    private static CancellationTokenSource _cts = null!;
    private static Mutex? _singleInstance;
    private static int _parentPid = -1;
    private static bool _parentAuto; // --parent auto：计划任务静默提权启动，自行解析父进程
    private static DateTime _parentStartUtc = DateTime.MinValue;
    private static bool _rebuild;
    private static bool _once;

    private static int Main(string[] args)
    {
        var workerStartUtc = DateTime.UtcNow;

        try
        {
            var dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "QuickPalette");
            _store = new FileIndexStore(Path.Combine(dataDir, "FileIndex"));
            _cts = new CancellationTokenSource();

            // ── 1. 参数解析（--parent <pid> 必须合法：0/MinValue/负数/无法解析一律拒绝；
            //        --parent auto = 计划任务静默提权启动用，由本进程自行解析为最新主进程）──
            if (!TryParseArgs(args, out var parseError))
            {
                _store.Log($"参数错误，退出（exit={ExitBadArgs}）: {parseError}");
                return ExitBadArgs;
            }
            if (_parentAuto && !TryResolveParentAuto(out var autoError))
            {
                _store.Log($"--parent auto 解析失败，退出（exit={ExitBadArgs}）: {autoError}");
                return ExitBadArgs;
            }
            _store.Log($"=== worker start (rebuild={_rebuild}, once={_once}, parent={_parentPid}{(_parentAuto ? " auto" : "")}) ===");

            // ── 2. 校验父进程：PID + 进程 StartTime 都对得上才运行（防 PID 复用）──
            if (!TryValidateParent(_parentPid, workerStartUtc, out _parentStartUtc, out var parentError))
            {
                _store.Log($"父进程校验失败，退出（exit={ExitParentLost}）: {parentError}");
                return ExitParentLost;
            }

            // ── 3. 收集支持 USN 的卷 ──
            var drives = UsnVolumeReader.GetDriveRoots()
                .Where(UsnVolumeReader.IsNtfs)
                .ToList();
            if (drives.Count == 0)
            {
                _store.Log($"No NTFS/ReFS volume found, exit={ExitInitFailed}");
                WriteMetaStatus("error", "未找到 NTFS/ReFS 卷");
                return ExitInitFailed;
            }
            _store.Log($"USN-capable volumes: {string.Join(", ", drives)}");

            // ── 4. 单实例：Local 命名互斥量（名字含用户 SID + 卷集合，避免多用户/多卷集合互相挤掉）──
            if (!TryAcquireSingleInstance(drives))
            {
                _store.Log($"已有实例在运行，退出（exit={ExitAlreadyRunning}）");
                return ExitAlreadyRunning;
            }

            // ── 5. 权限检查（读卷需要管理员）──
            if (!IsAdministrator())
            {
                _store.Log($"Not running as administrator, exit={ExitNoPermission}");
                WriteMetaStatus("no_permission", "需要管理员权限");
                return ExitNoPermission;
            }

            // ── 6. 打开卷并确保 USN Journal 可用（只读优先；建日志才申请写权限）──
            foreach (var drive in drives)
            {
                var handle = UsnVolumeReader.OpenVolume(drive); // 只读：枚举/查询/增量均只需 GENERIC_READ
                if (handle == null)
                {
                    _store.Log($"Cannot open volume {drive} (access denied?)");
                    lock (WarningSync) VolumeWarnings[drive] = "无法打开卷（权限不足）";
                    continue;
                }

                if (!UsnVolumeReader.QueryJournal(handle, out var journal))
                {
                    // 日志未启用：FSCTL_CREATE_USN_JOURNAL 需要写权限，单独以可写句柄激活后仍用只读句柄工作
                    var writable = UsnVolumeReader.OpenVolume(drive, writable: true);
                    var created = writable != null && UsnVolumeReader.CreateJournal(writable);
                    writable?.Dispose();
                    if (!created || !UsnVolumeReader.QueryJournal(handle, out journal))
                    {
                        _store.Log($"Cannot create USN journal on {drive}");
                        handle.Dispose();
                        lock (WarningSync) VolumeWarnings[drive] = "USN 日志不可用";
                        continue;
                    }
                }

                Volumes.Add(new VolumeJob
                {
                    RootPath = drive,
                    Handle = handle,
                    Index = new InMemoryFileIndex(drive),
                    JournalId = journal.UsnJournalId,
                    LastUsn = journal.NextUsn
                });
            }

            if (Volumes.Count == 0)
            {
                _store.Log($"No usable volume, exit={ExitInitFailed}");
                WriteMetaStatus("warning", "无法打开任何 NTFS/ReFS 卷（详见 worker.log）");
                return ExitInitFailed;
            }

            // ── 7. 恢复/重建每卷索引（流式恢复快照或全量枚举；全程不持全局锁）──
            var oldStates = _store.LoadMeta()?.Volumes ?? new List<VolumeState>();
            var needInitialSave = false; // 任一卷全量重建时需要落盘；恢复自快照时磁盘已是最新

            foreach (var v in Volumes)
            {
                var old = oldStates.FirstOrDefault(o =>
                    o.RootPath.Equals(v.RootPath, StringComparison.OrdinalIgnoreCase));

                var restored = false;
                if (!_rebuild && old != null && old.JournalId == v.JournalId)
                {
                    try
                    {
                        var stream = _store.ReadSnapshotRaw(); // 快照缺失/旧格式(v5 及更早)/损坏 → null
                        if (stream != null)
                        {
                            // 卷下标 = 本卷在快照卷序（meta.Volumes）中的位置；
                            // 条目区位翻转类损坏在此抛 InvalidDataException → 捕获后走全量重建
                            var volIdx = oldStates.IndexOf(old);
                            v.Index.LoadRaw(stream.Where(e => e.VolumeIndex == volIdx));
                            v.LastUsn = old.LastUsn;
                            _store.Log($"Volume {v.RootPath}: restored {v.Index.Count} entries from snapshot (streaming), usn={v.LastUsn}");
                            restored = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        _store.Log($"Volume {v.RootPath}: snapshot restore failed ({ex.Message}), falling back to full rebuild");
                        v.Index = new InMemoryFileIndex(v.RootPath);
                    }
                }

                if (!restored)
                {
                    needInitialSave = true;
                    if (!TryRebuildVolume(v, _cts.Token))
                    {
                        _store.Log($"Volume {v.RootPath}: initial enumeration failed or volume excluded");
                    }
                }
            }

            if (Volumes.All(v => !v.IsUsable))
            {
                _store.Log($"索引初始化失败（所有卷不可用），exit={ExitInitFailed}");
                WriteMetaStatus("warning", "索引初始化失败：所有卷均不可用/无法解析（详见 worker.log）");
                return ExitInitFailed;
            }

            // ── 8. 保存初始快照（仅当有卷全量重建时；恢复自快照时磁盘已是最新，
            //       不重写——避免每次启动都 version++ 触发主进程全量重载）──
            if (needInitialSave) SaveSnapshot();

            // 装载/重建期的瞬态大对象已失去引用：压缩式回收 + 修剪工作集，
            // 避免常驻工作集长期停在装载峰值（实测可回收数百 MB）
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            TrimWorkingSet();

            // --once：枚举完保存即退出（诊断/一次性重建用）
            if (_once)
            {
                _store.Log($"once mode: snapshot saved, exiting (exit={ExitOk})");
                FinalSave("once");
                ShutdownTasks();
                return ExitOk;
            }

            // ── 9. 启动各卷增量监听（每卷独立线程；阻塞读不在任何全局锁内）──
            var tasks = Volumes
                .Where(v => v.IsUsable)
                .Select(v => Task.Run(() => VolumeLoop(v, _cts.Token)))
                .ToArray();

            // ── 10. 监控父进程 / 外部退出请求；等待循环可被 CancellationToken 打断 ──
            var exitCode = ExitOk;
            var exitReason = "收到退出请求";
            using var shutdownHook = new ShutdownHook(_cts);
            while (true)
            {
                if (_cts.Token.WaitHandle.WaitOne(ParentPollIntervalMs))
                {
                    exitReason = "收到退出请求";
                    break;
                }
                if (!IsParentAlive(_parentPid, _parentStartUtc))
                {
                    exitReason = "父进程丢失（进程不存在或启动时间不符）";
                    exitCode = ExitParentLost;
                    break;
                }
            }

            // ── 11. 退出流程：cancel → 解除阻塞 → join（限时）→ 保存最终快照（保证落盘）──
            _cts.Cancel();
            foreach (var v in Volumes) v.Handle.CancelPendingIo(); // 打断阻塞中的 FSCTL_READ_USN_JOURNAL
            try { Task.WaitAll(tasks, SaveJoinTimeoutMs); }
            catch { /* 超时/聚合异常忽略：最终快照仍会落盘 */ }

            FinalSave(exitReason);
            ShutdownTasks();
            _store.Log($"=== worker exit: {exitReason} (exit={exitCode}) ===");
            return exitCode;
        }
        catch (Exception ex)
        {
            try { _store?.Log($"未处理异常，退出（exit={ExitUnhandled}）: {ex}"); } catch { }
            try { FinalSave("未处理异常"); } catch { }
            return ExitUnhandled;
        }
    }

    /// <summary>单卷增量监听循环</summary>
    private static void VolumeLoop(VolumeJob v, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // 1. 主进程的重建请求（rebuild.request 标记文件）：原子消费 + 互斥重建
            if (CheckRebuildRequest())
            {
                try
                {
                    _store.Log("Rebuild requested by main process");
                    RebuildAllVolumes(ct); // 逐卷重建、逐卷落盘（分批保存，全程不持全局锁）
                }
                finally
                {
                    Interlocked.Exchange(ref _rebuildGate, 0);
                }
                SaveTimer.Restart();
                Interlocked.Exchange(ref PendingChanges, 0);
                continue;
            }

            // 2. 增量读取：DeviceIoControl 的阻塞等待不持任何锁
            //    （短超时轮询 + CancellationToken 检查；退出时 CancelIoEx 立即解除阻塞）
            var status = UsnVolumeReader.ReadChanges(
                v.Handle, v.LastUsn, v.JournalId, IncrementWait, ct, out var records, out var nextUsn);

            switch (status)
            {
                case UsnReadStatus.Ok:
                    if (records.Count > 0)
                    {
                        v.Index.ApplyDelta(records); // 索引内部加锁（线程安全）
                        v.LastUsn = nextUsn;
                        Interlocked.Add(ref PendingChanges, records.Count);
                        _store.Log($"Volume {v.RootPath}: applied {records.Count} changes, now {v.Index.Count} entries");
                    }
                    break;

                case UsnReadStatus.Cancelled:
                    return; // 退出中，不要重建

                case UsnReadStatus.Unsupported:
                    // 不能解析的卷：明确排除并记警告（不再无声跳过）
                    ExcludeVolume(v, "出现无法解析的 USN 记录版本");
                    return;

                default: // NeedsRebuild / Failed：日志失效、回绕、参数错误等 → 全量重建该卷
                    // 重建退避：高负载机器上日志回绕可能反复触发"重建→又回绕→再重建"风暴
                    // （磁盘 30-45MB/s + 内存尖峰 + CPU 常占），每卷两次重建至少间隔 10 分钟；
                    // 用户显式重建（rebuild.request）不受退避限制。退避期内降速等待，避免错误轮询空转。
                    if (DateTime.UtcNow.Ticks < v.NextRebuildTicksUtc)
                    {
                        _store.Log($"Volume {v.RootPath}: journal invalidated ({status}), rebuild suppressed (backoff)");
                        ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
                        break;
                    }

                    _store.Log($"Volume {v.RootPath}: journal invalidated ({status}), rebuilding...");
                    v.NextRebuildTicksUtc = DateTime.UtcNow.AddMinutes(10).Ticks;
                    if (TryEnterRebuild())
                    {
                        try
                        {
                            if (TryRebuildVolume(v, ct)) SaveSnapshot();
                        }
                        finally
                        {
                            Interlocked.Exchange(ref _rebuildGate, 0);
                        }
                    }
                    break;
            }

            // 3. 节流落盘：有变化时最多每 5 分钟一次。
            //    未落盘的变更由 meta.LastUsn 之后的 USN 重放补齐（落盘只是加速冷启动的缓存），
            //    频繁落盘反而造成磁盘风暴与内存抖动（≈100MB/次）并推进 version 触发主进程全量重载
            if (PendingChanges > 0 && SaveTimer.Elapsed >= TimeSpan.FromMinutes(5))
            {
                SaveSnapshot();
                SaveTimer.Restart();
                Interlocked.Exchange(ref PendingChanges, 0);
            }
        }
    }

    /// <summary>
    /// 检测并消费主进程的重建请求（原子化）：
    /// 多卷线程并发看到标记时，仅一个线程通过 Interlocked 占位并消费（删除）标记，其余返回 false。
    /// </summary>
    private static bool CheckRebuildRequest()
    {
        var flag = Path.Combine(_store.DirectoryPath, "rebuild.request");
        bool exists;
        try
        {
            exists = File.Exists(flag);
        }
        catch
        {
            return false;
        }
        if (!exists) return false;

        // 原子占位：同一时刻只允许一个线程触发重建
        if (Interlocked.CompareExchange(ref _rebuildGate, 1, 0) != 0) return false;

        try
        {
            // 占位成功后消费标记（删除失败也视为已消费，避免反复触发）
            try { File.Delete(flag); } catch { /* 并发删除等 */ }
            return true;
        }
        catch
        {
            Interlocked.Exchange(ref _rebuildGate, 0);
            return false;
        }
    }

    private static bool TryEnterRebuild() => Interlocked.CompareExchange(ref _rebuildGate, 1, 0) == 0;

    /// <summary>全量重建所有可用卷：逐卷重建、逐卷落盘（分批保存；不长时间持锁）</summary>
    private static void RebuildAllVolumes(CancellationToken ct)
    {
        foreach (var v in Volumes)
        {
            if (ct.IsCancellationRequested) return;
            if (!v.IsUsable) continue;
            try
            {
                if (TryRebuildVolume(v, ct))
                    SaveSnapshot(); // 每卷完成立即落盘一次（分批保存）
            }
            catch (Exception ex)
            {
                _store.Log($"Volume {v.RootPath} rebuild failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 全量重建单个卷：枚举（阻塞 I/O，不持锁）→ 构建 → 装载（索引内部短写锁）。
    /// 无法解析的卷明确排除并记警告。返回 false 表示该卷重建失败/被排除。
    /// </summary>
    private static bool TryRebuildVolume(VolumeJob v, CancellationToken ct)
    {
        _store.Log($"Volume {v.RootPath}: full enumeration...");
        try
        {
            if (!UsnVolumeReader.QueryJournal(v.Handle, out var j))
            {
                _store.Log($"Volume {v.RootPath}: QueryJournal failed before rebuild");
                return false;
            }

            var result = UsnVolumeReader.EnumerateAll(v.Handle, ct);
            if (!result.IsSupported)
            {
                ExcludeVolume(v, result.Warning ?? "USN 记录无法解析");
                return false;
            }
            if (ct.IsCancellationRequested)
            {
                _store.Log($"Volume {v.RootPath}: enumeration cancelled");
                return false;
            }

            var built = FileIndexBuilder.BuildRawFromEnumeration(result.Records);
            v.Index.LoadRaw(built);
            var excluded = v.Index.FilterExcluded(); // 沿父链剔除排除规则命中项（系统目录等不驻留）
            v.LastUsn = j.NextUsn;
            v.JournalId = j.UsnJournalId;
            _store.Log($"Volume {v.RootPath}: indexed {v.Index.Count} nodes (excluded {excluded}), usn={v.LastUsn}");
            return true;
        }
        catch (Exception ex)
        {
            _store.Log($"Volume {v.RootPath} enumeration failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>明确排除一个无法使用的卷，并把警告写入 meta</summary>
    private static void ExcludeVolume(VolumeJob v, string reason)
    {
        v.IsUsable = false;
        lock (WarningSync)
        {
            VolumeWarnings[v.RootPath] = reason;
        }
        _store.Log($"Volume {v.RootPath} excluded: {reason}");
        WriteMetaWarnings();
    }

    /// <summary>
    /// 合并所有可用卷的条目并流式写入快照 + 版本号。
    /// 不持全局锁：各卷索引内部自行加锁（读锁覆盖本卷枚举区间）；
    /// 先取断点（保守方向：条目可能比 LastUsn 新，重放旧记录是收敛的、不会丢更新）。
    /// </summary>
    private static void SaveSnapshot()
    {
        lock (SaveSync) // 仅串行化落盘动作，绝不包住阻塞 I/O
        {
            try
            {
                var states = new List<VolumeState>();
                var usable = new List<VolumeJob>();
                foreach (var v in Volumes)
                {
                    if (!v.IsUsable) continue;
                    usable.Add(v);
                    states.Add(new VolumeState
                    {
                        RootPath = v.RootPath,
                        LastUsn = v.LastUsn,
                        JournalId = v.JournalId
                    });
                }

                // 流式落盘（v6）：节点级直写，零路径字符串构造、零 FileEntry 物化
                // （旧 GetEntries() 路径缓存 ≈ 0.4GB 瞬时尖峰 + 250MB 快照是内存/磁盘元凶）
                var count = _store.SaveSnapshotStreaming(writer =>
                {
                    var n = 0;
                    for (var i = 0; i < usable.Count; i++)
                    {
                        n += usable[i].Index.WriteSnapshotEntries(writer, i);
                    }
                    return n;
                }, states);

                WriteMetaWarnings();
                var meta = _store.LoadMeta();
                _store.Log($"Saved snapshot: {count} entries (version {meta?.Version})");
            }
            catch (Exception ex)
            {
                _store.Log($"Save snapshot failed: {ex.Message}");
            }
            finally
            {
                // 落盘的临时对象已失去引用：强制【压缩式】回收 + 修剪工作集，
                // 让 OS 真正归还内存（非压缩 GC 只回收不搬移，工作集会长期停在峰值）。
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                TrimWorkingSet();
            }
        }
    }

    /// <summary>
    /// 把排除卷警告写入 meta：
    ///  - 有可用索引时 Status 保持 "ready"（主进程以 Status=="ready" 判定可装载，警告写入 Error 字段）；
    ///  - 无任何可用卷时 Status="warning"，警告直接体现在 meta.Status。
    /// </summary>
    private static void WriteMetaWarnings()
    {
        try
        {
            string warning;
            lock (WarningSync)
            {
                warning = VolumeWarnings.Count == 0
                    ? string.Empty
                    : "警告: " + string.Join("; ", VolumeWarnings.Select(kv => $"{kv.Key}: {kv.Value}"));
            }
            if (warning.Length == 0) return;

            var meta = _store.LoadMeta() ?? new IndexMeta();
            meta.Error = warning;
            if (Volumes.All(v => !v.IsUsable)) meta.Status = "warning";
            _store.SaveMeta(meta);
        }
        catch (Exception ex)
        {
            _store.Log($"Write meta warnings failed: {ex.Message}");
        }
    }

    private static void WriteMetaStatus(string status, string error)
    {
        try
        {
            var meta = _store.LoadMeta() ?? new IndexMeta();
            meta.Status = status;
            meta.Error = error;
            meta.UpdatedAt = DateTime.Now;
            _store.SaveMeta(meta);
        }
        catch
        {
            // meta 写失败不阻断退出流程
        }
    }

    /// <summary>最终快照：全局只保存一次（主流程与 ProcessExit 钩子互斥）</summary>
    private static void FinalSave(string reason)
    {
        if (Interlocked.CompareExchange(ref _finalSaveDone, 1, 0) != 0) return;
        _store.Log($"Final snapshot save ({reason})");
        SaveSnapshot();
    }

    /// <summary>释放各卷句柄（SafeHandle 释放时 CancelIoEx + CloseHandle）</summary>
    private static void ShutdownTasks()
    {
        foreach (var v in Volumes)
        {
            try { v.Handle.CancelPendingIo(); } catch { }
            try { v.Handle.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// 解析启动参数。协议要求 --parent &lt;pid&gt;；缺失、无法解析、0、负数（含 int.MinValue）一律拒绝。
    /// </summary>
    private static bool TryParseArgs(string[] args, out string error)
    {
        error = string.Empty;
        var parentSeen = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--parent":
                    if (i + 1 >= args.Length)
                    {
                        error = "--parent 缺少 PID 参数";
                        return false;
                    }
                    var raw = args[++i];
                    if (string.Equals(raw, "auto", StringComparison.OrdinalIgnoreCase))
                    {
                        _parentAuto = true;
                        parentSeen = true;
                        break;
                    }
                    if (!int.TryParse(raw, out var pid))
                    {
                        error = $"--parent 值无法解析为 PID: '{raw}'";
                        return false;
                    }
                    if (pid <= 0) // --parent 0 / 负数（含 int.MinValue）不放行
                    {
                        error = $"--parent 必须是正整数 PID（收到 {pid}）";
                        return false;
                    }
                    _parentPid = pid;
                    parentSeen = true;
                    break;

                case "--rebuild":
                    _rebuild = true;
                    break;

                case "--once":
                    _once = true;
                    break;

                default:
                    error = $"未知参数: {args[i]}";
                    return false;
            }
        }

        if (!parentSeen)
        {
            error = "缺少 --parent <pid>（主进程 PID）";
            return false;
        }
        return true;
    }

    /// <summary>
    /// 自动解析父进程（--parent auto，计划任务静默提权启动用）：
    /// 取本会话内最新的 QuickPalette.App 进程（单实例锁保证至多一个）。
    /// 主进程先于本 worker 存在是常态；短重试覆盖极端时序，仍无则失败退出。
    /// </summary>
    private static bool TryResolveParentAuto(out string error)
    {
        error = string.Empty;
        for (var attempt = 0; attempt < 10; attempt++) // 最多等 5 秒
        {
            try
            {
                Process? best = null;
                foreach (var p in Process.GetProcessesByName("QuickPalette.App"))
                {
                    try
                    {
                        if (best == null || p.StartTime > best.StartTime)
                        {
                            best?.Dispose();
                            best = p;
                        }
                        else
                        {
                            p.Dispose();
                        }
                    }
                    catch
                    {
                        p.Dispose();
                    }
                }

                if (best != null)
                {
                    using (best)
                    {
                        _parentPid = best.Id;
                        return true;
                    }
                }
            }
            catch
            {
                // 枚举失败按未找到处理
            }

            Thread.Sleep(500);
        }

        error = "未找到 QuickPalette.App 进程（--parent auto 解析超时）";
        return false;
    }

    /// <summary>
    /// 校验父进程：PID 存在 + StartTime 可读且合理才放行（防 PID 复用）。
    /// 本 worker 由父进程拉起，父进程 StartTime 必然早于本进程；晚于本进程的"同名 PID"必是复用出的新进程。
    /// StartTime 读不到（= DateTime.MinValue）视为不可验证，不放行。
    /// </summary>
    private static bool TryValidateParent(int pid, DateTime workerStartUtc, out DateTime parentStartUtc, out string error)
    {
        parentStartUtc = DateTime.MinValue;
        error = string.Empty;

        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited)
            {
                error = $"父进程 (pid={pid}) 已退出";
                return false;
            }
            parentStartUtc = p.StartTime;
        }
        catch (Exception ex)
        {
            error = $"父进程 (pid={pid}) 不存在或不可访问: {ex.Message}";
            return false;
        }

        if (parentStartUtc == DateTime.MinValue)
        {
            error = $"父进程 (pid={pid}) StartTime 不可读（=MinValue），无法验证，拒绝运行";
            parentStartUtc = DateTime.MinValue;
            return false;
        }

        // PID 复用防护：父进程启动时间必须不晚于本 worker（留 2s 时钟容差）
        if (parentStartUtc.ToUniversalTime() > workerStartUtc.AddSeconds(2))
        {
            error = $"父进程 (pid={pid}) 启动时间 {parentStartUtc:O} 晚于本进程（疑似 PID 复用），拒绝运行";
            parentStartUtc = DateTime.MinValue;
            return false;
        }

        return true;
    }

    /// <summary>父进程存活判定：进程存在且 StartTime 与启动时捕获的一致（PID 复用视为丢失）</summary>
    private static bool IsParentAlive(int pid, DateTime startTime)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return false;
            return Math.Abs((p.StartTime - startTime).TotalSeconds) < 1;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 单实例：Local 命名互斥量。名字含用户 SID + 卷集合，
    /// 避免同机多用户 / 不同卷集合的 worker 互相挤掉。已被占用 → 已有实例在运行。
    /// </summary>
    private static bool TryAcquireSingleInstance(IReadOnlyList<string> drives)
    {
        string user;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            user = identity.User?.Value ?? Environment.UserName;
        }
        catch
        {
            user = Environment.UserName;
        }
        var volumeTag = string.Concat(drives.Select(d => d.TrimEnd('\\', ':')));
        var name = $@"Local\QuickPalette.FileIndexWorker.{user}.{volumeTag}";

        _singleInstance = new Mutex(initiallyOwned: true, name, out var createdNew);
        if (!createdNew)
        {
            _singleInstance.Dispose();
            _singleInstance = null;
            return false;
        }
        GC.KeepAlive(_singleInstance); // 保持句柄到进程结束
        _store.Log($"Single-instance mutex acquired: {name}");
        return true;
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(
        IntPtr hProcess, int dwMinimumWorkingSetSize, int dwMaximumWorkingSetSize);

    /// <summary>让 OS 回收进程工作集（EmptyWorkingSet 惯用法，参数 -1）</summary>
    private static void TrimWorkingSet()
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            SetProcessWorkingSetSize(p.Handle, -1, -1);
        }
        catch
        {
            // 失败忽略（非关键）
        }
    }

    /// <summary>
    /// 进程退出钩子：Ctrl+C / 关机 / 外部终止时取消等待循环、解除阻塞读，并保证最终快照落盘。
    /// 与主流程的 FinalSave 通过 Interlocked 互斥，只落盘一次。
    /// </summary>
    private sealed class ShutdownHook : IDisposable
    {
        private readonly CancellationTokenSource _cts;
        private volatile bool _disposed;

        public ShutdownHook(CancellationTokenSource cts)
        {
            _cts = cts;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            try { Console.CancelKeyPress += OnCancelKeyPress; } catch { /* WinExe 无控制台 */ }
        }

        private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true; // 不直接杀进程：走 cancel → join → save 的正常退出流程
            RequestShutdown();
        }

        private void OnProcessExit(object? sender, EventArgs e)
        {
            // 最后兜底：正常退出路径已保存过则跳过；被外部终止时抢一次落盘
            RequestShutdown();
            try { FinalSave("process exit"); } catch { }
            try { ShutdownTasks(); } catch { }
        }

        private void RequestShutdown()
        {
            try { _cts.Cancel(); } catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
            try { Console.CancelKeyPress -= OnCancelKeyPress; } catch { }
        }
    }
}

/// <summary>单卷工作单元（LastUsn/JournalId 跨线程可见，用 Interlocked 读写）</summary>
internal sealed class VolumeJob
{
    private long _lastUsn;
    private long _journalId;

    public required string RootPath { get; init; }
    public required VolumeHandle Handle { get; init; }
    public required InMemoryFileIndex Index { get; set; }

    /// <summary>该卷是否可用（无法解析/打不开时置 false，明确排除）</summary>
    public volatile bool IsUsable = true;

    /// <summary>重建退避截止时刻（UTC Ticks）：两次自动重建至少间隔 10 分钟，防重建风暴</summary>
    private long _nextRebuildTicksUtc;
    public long NextRebuildTicksUtc
    {
        get => Interlocked.Read(ref _nextRebuildTicksUtc);
        set => Interlocked.Exchange(ref _nextRebuildTicksUtc, value);
    }

    /// <summary>增量续读断点（最新已应用 USN）</summary>
    public long LastUsn
    {
        get => Interlocked.Read(ref _lastUsn);
        set => Interlocked.Exchange(ref _lastUsn, value);
    }

    /// <summary>USN 日志 ID（日志重建后变化 → 需全量重建）</summary>
    public ulong JournalId
    {
        get => unchecked((ulong)Interlocked.Read(ref _journalId));
        set => Interlocked.Exchange(ref _journalId, unchecked((long)value));
    }
}
