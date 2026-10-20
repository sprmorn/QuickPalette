namespace QuickPalette.App.Services;

using System.Runtime.InteropServices;

/// <summary>
/// 读取其他进程的命令行（用于校验 FileIndexWorker 的 --parent 参数，识别孤儿 worker）。
/// 使用 NtQueryInformationProcess(ProcessCommandLineInformation)：
/// 只要求 PROCESS_QUERY_LIMITED_INFORMATION，可跨 UAC 完整性级别读取提权 Worker 的命令行；
/// 传统 PEB + ReadProcessMemory 方案对提权进程会 ACCESS_DENIED，故不采用。
/// </summary>
public static class ProcessCommandLineReader
{
    /// <summary>ProcessCommandLineInformation（Win10 1803+ 支持）</summary>
    private const int ProcessCommandLineInformation = 60;

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        IntPtr processInformation,
        int processInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>ntdll 返回的 UNICODE_STRING（Buffer 指向同一块返回缓冲区内）</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeStringHeader
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    /// <summary>
    /// 尝试读取指定进程的命令行；权限不足 / 进程已退出 / 系统不支持时返回 null。
    /// </summary>
    public static string? TryGetCommandLine(int processId)
    {
        if (processId <= 0)
            return null;

        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero)
            return null;

        try
        {
            // 第一次调用只为拿所需缓冲区长度（STATUS_INFO_LENGTH_MISMATCH）
            NtQueryInformationProcess(handle, ProcessCommandLineInformation, IntPtr.Zero, 0, out var length);
            if (length <= 0 || length > 1024 * 1024)
                return null;

            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                var status = NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, length, out _);
                if (status != 0)
                    return null;

                var header = Marshal.PtrToStructure<UnicodeStringHeader>(buffer);
                if (header.Buffer == IntPtr.Zero || header.Length == 0)
                    return string.Empty;

                return Marshal.PtrToStringUni(header.Buffer, header.Length / sizeof(char));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}
