using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace QuickPalette.Infrastructure.FileIndex;

/// <summary>
/// NTFS USN Journal / MFT 直读所需的 Win32 原生互操作
/// （Everything 的核心机制：FSCTL_ENUM_USN_DATA 全量枚举 + FSCTL_READ_USN_JOURNAL 增量）
/// </summary>
internal static class NativeMethods
{
    // CreateFile 访问掩码
    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;
    public const uint FileShareRead = 0x1;
    public const uint FileShareWrite = 0x2;
    public const uint FileShareDelete = 0x4;
    public const uint OpenExisting = 0x3;
    public const uint FileFlagBackupSemantics = 0x02000000;

    // FSCTL（来自 winioctl.h）
    public const uint FsctlQueryUsnJournal = 0x000900f4;
    public const uint FsctlCreateUsnJournal = 0x000900e7;
    public const uint FsctlEnumUsnData = 0x000900b3;
    public const uint FsctlReadUsnJournal = 0x000900bb;

    // Win32 错误码
    public const int ErrorAccessDenied = 5;
    public const int ErrorHandleEof = 38;
    public const int ErrorOperationAborted = 995;          // ERROR_OPERATION_ABORTED：CancelIoEx/关句柄解除阻塞
    public const int ErrorJournalDeleteInProgress = 1178;  // ERROR_JOURNAL_DELETE_IN_PROGRESS
    public const int ErrorJournalNotActive = 1179;         // ERROR_JOURNAL_NOT_ACTIVE：日志未启用/被删除
    public const int ErrorJournalEntryDeleted = 1181;      // ERROR_JOURNAL_ENTRY_DELETED：请求的 USN 记录已被日志回绕清除
    public const int ErrorTimeout = 1460;                  // ERROR_TIMEOUT：等待超时、暂无新记录
    public const int ErrorInvalidUserBuffer = 1784;        // ERROR_INVALID_USER_BUFFER：缓冲区参数非法

    // USN Reason（winioctl.h）
    public const uint UsnReasonDataOverwrite = 0x00000001;
    public const uint UsnReasonDataExtend = 0x00000002;
    public const uint UsnReasonDataTruncation = 0x00000004;
    public const uint UsnReasonNamedDataOverwrite = 0x00000010;
    public const uint UsnReasonNamedDataExtend = 0x00000020;
    public const uint UsnReasonNamedDataTruncation = 0x00000040;
    public const uint UsnReasonFileCreate = 0x00000100;
    public const uint UsnReasonFileDelete = 0x00000200;
    public const uint UsnReasonEaChange = 0x00000400;
    // 注意：USN_REASON_BASIC_INFO_CHANGE 是 0x00008000（winioctl.h），
    // 此前误写为 0x00000800（该位实际是 USN_REASON_EA_CHANGE 的邻居、未定义用途），已修正。
    public const uint UsnReasonBasicInfoChange = 0x00008000;
    public const uint UsnReasonHardLinkChange = 0x00010000;
    public const uint UsnReasonCompressionChange = 0x00020000;
    public const uint UsnReasonEncryptionChange = 0x00040000;
    public const uint UsnReasonObjectIdChange = 0x00080000;
    public const uint UsnReasonReparsePointChange = 0x00100000;
    public const uint UsnReasonStreamChange = 0x00200000;
    public const uint UsnReasonTransactedChange = 0x00400000;
    public const uint UsnReasonIntegrityChange = 0x00800000;
    public const uint UsnReasonDesiredStorageClassChange = 0x01000000;
    public const uint UsnReasonClose = 0x80000000;
    public const uint UsnReasonRenameOldName = 0x00001000;
    public const uint UsnReasonRenameNewName = 0x00002000;
    public const uint AllUsnReasons = 0xFFFFFFFF;

    // FileAttributes（部分）
    public const uint FileAttributeDirectory = 0x10;
    public const uint FileAttributeSystem = 0x4;
    public const uint FileAttributeHidden = 0x2;

    // USN_RECORD 版本与头长度（winioctl.h）
    public const ushort UsnRecordMajorV2 = 2;              // NTFS：头 60 字节
    public const ushort UsnRecordMajorV3 = 3;              // ReFS：头 80 字节（FILE_ID_128 引用）
    public const int UsnRecordV2HeaderLength = 60;
    public const int UsnRecordV3HeaderLength = 80;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    /// <summary>DeviceIoControl：hDevice 传 <see cref="VolumeHandle"/>，由 SafeHandle 保证句柄生命周期</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeviceIoControl(
        VolumeHandle hDevice,
        uint dwIoControlCode,
        byte[] lpInBuffer,
        uint nInBufferSize,
        byte[] lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    /// <summary>取消句柄上挂起的 I/O（含其他线程阻塞中的 DeviceIoControl），用于退出时解除阻塞</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CancelIoEx(IntPtr hFile, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVolumeInformationW(
        string lpRootPathName,
        StringBuilder lpVolumeNameBuffer,
        int nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        StringBuilder lpFileSystemNameBuffer,
        int nFileSystemNameSize);

    /// <summary>
    /// 坏 FILETIME 单条防护：单条记录时间非法时返回兜底时间（Unix 纪元，UTC），
    /// 绝不让一条坏记录抛异常丢掉整批解析结果。
    /// </summary>
    public static DateTime SafeFileTimeToDateTimeUtc(long fileTime)
    {
        try
        {
            // FILETIME 合法范围约 [0, 2.6e18]；越界值 FromFileTimeUtc 会抛 ArgumentOutOfRangeException
            if (fileTime < 0 || fileTime > 2_650_467_744_000_000_000L)
                return DateTime.UnixEpoch;
            return DateTime.FromFileTimeUtc(fileTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTime.UnixEpoch;
        }
        catch (ArgumentException)
        {
            return DateTime.UnixEpoch;
        }
    }

    /// <summary>
    /// 坏时间单条防护（反向）：DateTime → FILETIME，非法（MinValue/越界）返回 0，不抛异常。
    /// </summary>
    public static long SafeDateTimeToFileTimeUtc(DateTime value)
    {
        try
        {
            return value.ToFileTimeUtc();
        }
        catch (ArgumentOutOfRangeException)
        {
            return 0;
        }
        catch (ArgumentException)
        {
            return 0;
        }
    }
}

/// <summary>
/// 卷句柄（SafeHandle 包装）：
/// 释放时先 CancelIoEx 解除本句柄上仍阻塞的 DeviceIoControl（如 FSCTL_READ_USN_JOURNAL 的等待），
/// 再 CloseHandle —— 保证退出流程不会卡死在内核等待上。
/// </summary>
public sealed class VolumeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private VolumeHandle() : base(ownsHandle: true)
    {
    }

    /// <summary>包装一个已打开的裸句柄（接管所有权）</summary>
    public VolumeHandle(IntPtr preexistingHandle, bool ownsHandle) : base(ownsHandle)
    {
        SetHandle(preexistingHandle);
    }

    /// <summary>解除句柄上所有挂起 I/O（其他线程阻塞中的读随之返回 ERROR_OPERATION_ABORTED）</summary>
    public void CancelPendingIo()
    {
        if (!IsInvalid && !IsClosed)
            NativeMethods.CancelIoEx(handle, IntPtr.Zero);
    }

    protected override bool ReleaseHandle()
    {
        // 先取消挂起 I/O 再关句柄：退出时打破阻塞等待，防止线程死锁/悬挂
        NativeMethods.CancelIoEx(handle, IntPtr.Zero);
        return NativeMethods.CloseHandle(handle);
    }
}

/// <summary>FSCTL_ENUM_USN_DATA 输入结构</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MftEnumDataV0
{
    public ulong StartFileReferenceNumber;
    public long LowUsn;
    public long HighUsn;
}

/// <summary>
/// FSCTL_READ_USN_JOURNAL 输入结构（READ_USN_JOURNAL_DATA_V0）。
/// 注意字段单位（winioctl.h / MSDN 官方语义）：
///  - Timeout：等待下一条记录的最小时间，单位是 <b>100 纳秒</b>（1ms = 10,000 个单位），
///    不是毫秒！传 5000 只等于 0.5 毫秒，会造成忙轮询；
///  - BytesToWaitFor：返回前至少积累的记录字节数（>0 时配合 Timeout 阻塞等待）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ReadUsnJournalDataV0
{
    public long StartUsn;       // 8
    public uint ReasonMask;     // 4
    public uint ReturnOnlyOnClose; // 4
    public long Timeout;        // 8 (DWORDLONG)：100ns 单位的最小等待时间
    public long BytesToWaitFor; // 8 (DWORDLONG)
    public ulong UsnJournalId;  // 8
}

/// <summary>FSCTL_CREATE_USN_JOURNAL 输入结构</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CreateUsnJournalData
{
    public ulong MaximumSize;
    public ulong AllocationDelta;
}

/// <summary>FSCTL_QUERY_USN_JOURNAL 输出结构</summary>
public struct UsnJournalDataV0
{
    public ulong UsnJournalId;
    public long FirstUsn;
    public long NextUsn;
    public long LowestValidUsn;
    public long MaxUsn;
    public ulong MaximumSize;
    public ulong AllocationDelta;
}

/// <summary>
/// 一条 USN 记录（V2/V3 统一视图）——手工解析自原始字节。
/// V3（ReFS）的 FileReferenceNumber/ParentFileReferenceNumber 是 16 字节 FILE_ID_128，
/// 取低 64 位（本产品仅需记录号 + 序列号，位于低 48 位/高 16 位）。
/// </summary>
public readonly record struct UsnRecord(
    long FileReference,
    long ParentReference,
    long Usn,
    DateTime TimeStamp,
    uint Reason,
    uint FileAttributes,
    string FileName);
