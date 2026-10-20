using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace QuickPalette.Infrastructure.FileIndex;

/// <summary>增量读取结果状态</summary>
public enum UsnReadStatus
{
    /// <summary>读取成功（可能 0 条新记录）</summary>
    Ok,

    /// <summary>被取消（CancellationToken 或句柄关闭解除阻塞），调用方直接退出、不要重建</summary>
    Cancelled,

    /// <summary>日志失效/回绕（含 ERROR_JOURNAL_ENTRY_DELETED 等），调用方需全量重建</summary>
    NeedsRebuild,

    /// <summary>出现无法解析的 USN 记录版本，该卷应被明确排除（不再无声跳过）</summary>
    Unsupported,

    /// <summary>其他不可恢复错误，调用方需全量重建或放弃该卷</summary>
    Failed
}

/// <summary>全量枚举结果：记录 + 卷可解析性诊断</summary>
public sealed class UsnEnumerationResult
{
    /// <summary>解析出的 USN 记录（V2/V3）</summary>
    public List<UsnRecord> Records { get; init; } = new();

    /// <summary>false 表示卷内出现无法解析的记录版本/结构损坏，该卷应明确排除并在 meta 记警告</summary>
    public bool IsSupported { get; init; } = true;

    /// <summary>遇到的未知 USN_RECORD 主版本号</summary>
    public List<ushort> UnsupportedVersions { get; init; } = new();

    /// <summary>结构异常（记录越界等）次数</summary>
    public int StructuralErrors { get; init; }

    /// <summary>可直接写入日志/元数据的警告文本（IsSupported=false 时非空）</summary>
    public string? Warning { get; init; }
}

/// <summary>
/// NTFS/ReFS 卷 USN 读取器：全量枚举（MFT 快照）与增量变更读取。
/// 阻塞型 DeviceIoControl 调用不依赖任何全局锁；取消靠短超时轮询 + 句柄级 CancelIoEx。
/// </summary>
public static class UsnVolumeReader
{
    private const int BufferSize = 1 << 16; // 64KB 输出缓冲（必须 < 85KB：避免每次读取都在大对象堆(LOH)分配 1MB 造成持续抖动/碎片）

    /// <summary>100ns 单位换算：1 毫秒 = 10,000 个 100ns 单位（READ_USN_JOURNAL_DATA_V0.Timeout 官方单位）</summary>
    private const long HundredNanosecondsPerMillisecond = 10_000;

    /// <summary>
    /// 枚举所有盘符卷（C:\ D:\ ...）
    /// </summary>
    public static List<string> GetDriveRoots()
    {
        var roots = new List<string>();
        foreach (var drive in Environment.GetLogicalDrives()) // 形如 "C:\"
        {
            roots.Add(drive);
        }
        return roots;
    }

    /// <summary>
    /// 获取卷文件系统名（NTFS / ReFS / FAT32 ...），失败返回空串
    /// </summary>
    public static string GetFileSystemName(string driveRoot)
    {
        var fsName = new StringBuilder(64);
        var volumeName = new StringBuilder(128);
        if (!NativeMethods.GetVolumeInformationW(
                driveRoot, volumeName, volumeName.Capacity,
                out _, out _, out _, fsName, fsName.Capacity))
        {
            return string.Empty;
        }
        return fsName.ToString().Trim();
    }

    /// <summary>
    /// 检查卷文件系统是否支持 USN 机制（NTFS 原生支持；ReFS 支持 USN_RECORD_V3）
    /// </summary>
    public static bool IsNtfs(string driveRoot)
    {
        var fs = GetFileSystemName(driveRoot);
        return fs.Equals("NTFS", StringComparison.OrdinalIgnoreCase) ||
               fs.Equals("ReFS", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 以只读权限打开卷（需要管理员权限；Everything 亦如此）。
    /// 枚举/查询/增量读取均只需 GENERIC_READ；失败返回 null。
    /// </summary>
    public static VolumeHandle? OpenVolume(string driveRoot)
        => OpenVolume(driveRoot, writable: false);

    /// <summary>
    /// 打开卷。writable=true 时附加写权限（仅 FSCTL_CREATE_USN_JOURNAL 激活日志需要）；
    /// 只读路径一律用 writable=false，遵循最小权限。失败返回 null。
    /// </summary>
    public static VolumeHandle? OpenVolume(string driveRoot, bool writable)
    {
        var device = @"\\.\" + driveRoot.TrimEnd('\\').TrimEnd(':') + ":";
        var access = writable
            ? NativeMethods.GenericRead | NativeMethods.GenericWrite
            : NativeMethods.GenericRead;
        var raw = NativeMethods.CreateFile(
            device,
            access,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite | NativeMethods.FileShareDelete,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            NativeMethods.FileFlagBackupSemantics,
            IntPtr.Zero);
        if (raw == IntPtr.Zero || raw == new IntPtr(-1)) return null;
        return new VolumeHandle(raw, ownsHandle: true);
    }

    /// <summary>
    /// 查询 USN 日志状态
    /// </summary>
    public static bool QueryJournal(VolumeHandle handle, out UsnJournalDataV0 data)
    {
        data = default;
        var inBuf = Array.Empty<byte>();
        var outBuf = new byte[256];
        if (!NativeMethods.DeviceIoControl(
                handle, NativeMethods.FsctlQueryUsnJournal,
                inBuf, 0, outBuf, (uint)outBuf.Length,
                out var returned, IntPtr.Zero))
        {
            return false;
        }
        if (returned < (uint)Marshal.SizeOf<UsnJournalDataV0>()) return false;
        var ptr = Marshal.AllocHGlobal((int)returned);
        try
        {
            Marshal.Copy(outBuf, 0, ptr, (int)returned);
            data = Marshal.PtrToStructure<UsnJournalDataV0>(ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
        return true;
    }

    /// <summary>
    /// 激活 USN 日志（卷从未启用时调用；需要以 writable=true 打开的句柄）。
    /// 默认 256MB：高负载机器上日志回绕过快会导致"读到已删除记录→全量重建"循环，大日志显著降低回绕频率。
    /// </summary>
    public static bool CreateJournal(VolumeHandle handle, ulong maxSize = 256UL * 1024 * 1024, ulong delta = 16UL * 1024 * 1024)
    {
        var data = new CreateUsnJournalData { MaximumSize = maxSize, AllocationDelta = delta };
        var inBuf = StructToBytes(data);
        var outBuf = new byte[8];
        return NativeMethods.DeviceIoControl(
            handle, NativeMethods.FsctlCreateUsnJournal,
            inBuf, (uint)inBuf.Length, outBuf, (uint)outBuf.Length,
            out _, IntPtr.Zero);
    }

    /// <summary>
    /// 全量枚举卷上所有文件的当前记录（相当于读取 MFT 快照）。
    /// 输出格式：8 字节起始点（下一条枚举的文件引用号）+ USN_RECORD_V2/V3 数组。
    /// 出现无法解析的记录版本时 IsSupported=false（调用方应明确排除该卷并在 meta 记警告，不再无声跳过）。
    /// </summary>
    /// <param name="handle">卷句柄</param>
    /// <param name="cancellationToken">取消标记：取消后中断枚举并返回已解析部分（WasCancelled 由 Records 之外的信息体现于日志）</param>
    public static UsnEnumerationResult EnumerateAll(VolumeHandle handle, CancellationToken cancellationToken = default)
    {
        var records = new List<UsnRecord>(1 << 18);
        var stats = new ParseStats();
        var mft = new MftEnumDataV0 { StartFileReferenceNumber = 0, LowUsn = 0, HighUsn = long.MaxValue };
        var inBuf = StructToBytes(mft);
        var outBuf = new byte[BufferSize];
        ulong lastStart = 0;
        var cancelled = false;

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            uint returned;
            if (!NativeMethods.DeviceIoControl(
                    handle, NativeMethods.FsctlEnumUsnData,
                    inBuf, (uint)inBuf.Length, outBuf, (uint)outBuf.Length,
                    out returned, IntPtr.Zero))
            {
                var err = Marshal.GetLastWin32Error();
                if (err == NativeMethods.ErrorHandleEof) break; // 枚举完成
                if (err == NativeMethods.ErrorOperationAborted)
                {
                    cancelled = true;
                    break;
                }
                throw new Win32Exception(err, $"FSCTL_ENUM_USN_DATA failed (error {err})");
            }
            if (returned <= 8) break;

            // 输出第一项 = 下一条枚举的起点（文件引用号）
            var nextStart = BinaryPrimitives.ReadUInt64LittleEndian(outBuf);
            ParseRecords(outBuf, (int)returned, records, startOffset: 8, stats);

            // 起点不前进 → 枚举完成（防御死循环）
            if (nextStart <= lastStart) break;
            lastStart = nextStart;

            mft.StartFileReferenceNumber = nextStart;
            inBuf = StructToBytes(mft);
        }

        var supported = stats.UnsupportedVersions.Count == 0 && stats.StructuralErrors == 0;
        string? warning = null;
        if (!supported)
        {
            var parts = new List<string>();
            if (stats.UnsupportedVersions.Count > 0)
                parts.Add($"无法解析的 USN 记录版本: {string.Join("/", stats.UnsupportedVersions)}");
            if (stats.StructuralErrors > 0)
                parts.Add($"记录结构异常 {stats.StructuralErrors} 处");
            warning = string.Join("; ", parts);
        }

        return new UsnEnumerationResult
        {
            Records = records,
            IsSupported = supported,
            UnsupportedVersions = stats.UnsupportedVersions.ToList(),
            StructuralErrors = stats.StructuralErrors,
            Warning = warning == null
                ? (cancelled ? "枚举被取消（结果不完整）" : null)
                : (cancelled ? warning + "（且枚举被取消）" : warning)
        };
    }

    /// <summary>
    /// 增量读取 USN 日志：从 startUsn 开始读一批变更。
    /// 阻塞语义（READ_USN_JOURNAL_DATA_V0 官方定义）：
    ///  - Timeout = minimumWait 换算成 <b>100 纳秒</b>单位（1ms = 10,000）——是"等待下一条记录的最小时间"，
    ///    绝不能把毫秒数直接塞进去（传 1000 只等于 0.1ms，导致忙轮询烧 CPU）；
    ///  - BytesToWaitFor = 1：至少等到 1 字节新记录或 Timeout 用尽。
    /// 本方法不持有任何全局锁；等待可被 <paramref name="cancellationToken"/>（短超时轮询）
    /// 或 <see cref="VolumeHandle.CancelPendingIo"/>（退出时立即解除阻塞）打断。
    /// </summary>
    /// <returns>
    /// Ok=成功（可能 0 条）；Cancelled=被取消（不要重建）；
    /// NeedsRebuild=日志失效/回绕（ERROR_JOURNAL_NOT_ACTIVE / ERROR_JOURNAL_ENTRY_DELETED 等）；
    /// Unsupported=出现无法解析的记录版本（该卷应排除并记警告）；Failed=其他错误。
    /// </returns>
    public static UsnReadStatus ReadChanges(
        VolumeHandle handle,
        long startUsn,
        ulong journalId,
        TimeSpan minimumWait,
        CancellationToken cancellationToken,
        out List<UsnRecord> records,
        out long nextUsn)
    {
        records = new List<UsnRecord>();
        nextUsn = startUsn;

        if (cancellationToken.IsCancellationRequested) return UsnReadStatus.Cancelled;

        // 单位换算：100ns。TimeSpan 为负按 0（不等待，立即返回已有记录）。
        var waitMs = Math.Max(0.0, minimumWait.TotalMilliseconds);
        var timeout = (long)(waitMs * HundredNanosecondsPerMillisecond);

        var data = new ReadUsnJournalDataV0
        {
            StartUsn = startUsn,
            ReasonMask = NativeMethods.AllUsnReasons,
            ReturnOnlyOnClose = 0,
            Timeout = timeout,          // 100ns 单位：等待下一条记录的最小时间
            BytesToWaitFor = 1,         // 非零：至少积累 1 字节新记录（或 Timeout 用尽）才返回
            UsnJournalId = journalId
        };
        var inBuf = StructToBytes(data);
        var outBuf = new byte[BufferSize];

        uint returned;
        if (!NativeMethods.DeviceIoControl(
                handle, NativeMethods.FsctlReadUsnJournal,
                inBuf, (uint)inBuf.Length, outBuf, (uint)outBuf.Length,
                out returned, IntPtr.Zero))
        {
            var err = Marshal.GetLastWin32Error();

            // 超时：等待期间无新记录，正常，继续下一轮等待
            if (err == NativeMethods.ErrorTimeout)
            {
                nextUsn = startUsn;
                return UsnReadStatus.Ok;
            }

            // 取消/句柄关闭：退出流程解除阻塞，调用方直接收尾
            if (err == NativeMethods.ErrorOperationAborted && cancellationToken.IsCancellationRequested)
                return UsnReadStatus.Cancelled;

            // 日志被重建/回绕/记录被清除 → 调用方全量重建
            if (err is NativeMethods.ErrorJournalNotActive
                or NativeMethods.ErrorJournalDeleteInProgress
                or NativeMethods.ErrorJournalEntryDeleted
                or NativeMethods.ErrorAccessDenied)
                return UsnReadStatus.NeedsRebuild;

            // ERROR_INVALID_USER_BUFFER(1784) 等参数/缓冲类错误：也走重建路径（重新查询日志参数）
            return UsnReadStatus.Failed;
        }

        if (returned < 8) return UsnReadStatus.Ok; // 无新记录

        // 输出缓冲前 8 字节为下一个 USN
        nextUsn = BinaryPrimitives.ReadInt64LittleEndian(outBuf);

        var stats = new ParseStats();
        ParseRecords(outBuf, (int)returned, records, startOffset: 8, stats);

        // 无法解析的记录版本：该卷明确排除（返回 Unsupported，调用方记警告，不再无声跳过）
        if (stats.UnsupportedVersions.Count > 0)
            return UsnReadStatus.Unsupported;

        // 回绕检测：日志被截断导致序号回退 → 需要全量重建
        if (nextUsn < startUsn) return UsnReadStatus.NeedsRebuild;

        return UsnReadStatus.Ok;
    }

    /// <summary>解析统计：坏记录单条防护 + 未知版本收集</summary>
    private sealed class ParseStats
    {
        public readonly HashSet<ushort> UnsupportedVersions = new();
        public int StructuralErrors;
        public int BadTimeRecords;
    }

    /// <summary>
    /// 解析输出缓冲中的 USN_RECORD 数组（V2=NTFS 60 字节头 / V3=ReFS 80 字节头）。
    /// 单条坏记录（坏 FILETIME / 名称越界）只跳过该条，绝不丢整批；
    /// 记录头结构损坏（长度非法）停止本批解析并计数，由上层决定排除卷。
    /// </summary>
    private static void ParseRecords(byte[] buf, int length, List<UsnRecord> records, int startOffset, ParseStats stats)
    {
        var offset = startOffset;

        while (offset + 8 <= length)
        {
            var recordLength = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(offset, 4));
            var major = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(offset + 4, 2));

            // 记录长度下限 = 对应版本的头长度；越界即结构损坏，停止本批
            var minLength = major == NativeMethods.UsnRecordMajorV3
                ? NativeMethods.UsnRecordV3HeaderLength
                : NativeMethods.UsnRecordV2HeaderLength;
            if (recordLength < minLength || offset + recordLength > length)
            {
                stats.StructuralErrors++;
                break;
            }

            long frn, parent, usn, time;
            uint reason, attr;
            ushort nameLen, nameOffset;

            if (major == NativeMethods.UsnRecordMajorV2)
            {
                // USN_RECORD_V2 布局（偏移）：
                // 8: FileReferenceNumber(8) 16: ParentFileReferenceNumber(8) 24: Usn(8)
                // 32: TimeStamp(8) 40: Reason(4) 52: FileAttributes(4) 56: NameLength(2) 58: NameOffset(2)
                frn = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(offset + 8, 8));
                parent = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(offset + 16, 8));
                usn = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(offset + 24, 8));
                time = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(offset + 32, 8));
                reason = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(offset + 40, 4));
                attr = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(offset + 52, 4));
                nameLen = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(offset + 56, 2));
                nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(offset + 58, 2));
            }
            else if (major == NativeMethods.UsnRecordMajorV3)
            {
                // USN_RECORD_V3 布局（头长 80，ReFS）：
                // 8: FileReferenceNumber(FILE_ID_128，16 字节，取低 64 位)
                // 24: ParentFileReferenceNumber(FILE_ID_128，16 字节，取低 64 位)
                // 40: Usn(8) 48: TimeStamp(8) 56: Reason(4) 68: FileAttributes(4)
                // 72: NameLength(2) 74: NameOffset(2)
                frn = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(offset + 8, 8));
                parent = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(offset + 24, 8));
                usn = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(offset + 40, 8));
                time = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(offset + 48, 8));
                reason = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(offset + 56, 4));
                attr = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(offset + 68, 4));
                nameLen = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(offset + 72, 2));
                nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(offset + 74, 2));
            }
            else
            {
                // 无法解析的记录版本：记录下来，整个卷将被明确排除（不再无声跳过）
                stats.UnsupportedVersions.Add(major);
                offset += (int)recordLength;
                continue;
            }

            // 单条防护：名称越界 → 只跳过该条
            if (nameLen > 0 &&
                nameOffset >= (major == NativeMethods.UsnRecordMajorV3 ? NativeMethods.UsnRecordV3HeaderLength : NativeMethods.UsnRecordV2HeaderLength) &&
                offset + nameOffset + nameLen <= offset + recordLength)
            {
                var name = Encoding.Unicode.GetString(buf, offset + nameOffset, nameLen);

                // 单条防护：坏 FILETIME 不再丢整批（SafeFileTimeToDateTimeUtc 兜底）
                var stamp = NativeMethods.SafeFileTimeToDateTimeUtc(time);
                if (stamp == DateTime.UnixEpoch && time != 0)
                    stats.BadTimeRecords++;

                records.Add(new UsnRecord(frn, parent, usn, stamp, reason, attr, name));
            }

            offset += (int)recordLength;
        }
    }

    private static byte[] StructToBytes<T>(T value) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var arr = new byte[size];
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(value, ptr, false);
            Marshal.Copy(ptr, arr, 0, size);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
        return arr;
    }
}
