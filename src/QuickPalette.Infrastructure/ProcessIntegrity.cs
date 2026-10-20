using System.Runtime.InteropServices;

namespace QuickPalette.Infrastructure;

/// <summary>
/// 进程运行上下文检测。
/// - 低完整性进程（例如直接从带「低」标签的目录启动）会被强制完整性控制封禁与中完整性
///   COM 服务器/窗口的交互，系统预览处理器随之失效；
/// - 高完整性（管理员）进程的 COM 激活对 HKCU 每用户注册的解析与普通进程不同，
///   WPS 系预览器这类只注册在 HKCU/32 位视图的类会激活失败（实测 REGDB_E_CLASSNOTREG）。
/// </summary>
public static class ProcessIntegrity
{
    private const int TokenIntegrityLevel = 25;
    private const int TokenElevation = 20;
    private const uint TokenQuery = 0x0008;
    private const uint SecurityMandatoryMediumRid = 0x2000;

    /// <summary>当前进程是否运行在低完整性级别</summary>
    public static bool IsLowIntegrity()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token))
            {
                return false;
            }
            try
            {
                GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out var length);
                if (length == 0) return false;

                var buffer = Marshal.AllocHGlobal((int)length);
                try
                {
                    if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, length, out _))
                    {
                        return false;
                    }

                    // TOKEN_MANDATORY_LABEL { SID_AND_ATTRIBUTES { IntPtr Sid; uint Attributes } }
                    var sid = Marshal.ReadIntPtr(buffer);
                    if (sid == IntPtr.Zero) return false;

                    // SID 布局：{ byte Revision; byte SubAuthorityCount; byte[6] Authority; DWORD[] SubAuthority }
                    var subAuthorityCount = Marshal.ReadByte(sid, 1);
                    if (subAuthorityCount == 0) return false;
                    var rid = (uint)Marshal.ReadInt32(sid, 8 + 4 * (subAuthorityCount - 1));
                    return rid < SecurityMandatoryMediumRid;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                CloseHandle(token);
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>当前进程是否以提升令牌运行（管理员）</summary>
    public static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token))
            {
                return false;
            }
            try
            {
                var elevated = 0;
                return GetTokenInformation(token, TokenElevation, out elevated, 4, out _) && elevated != 0;
            }
            finally
            {
                CloseHandle(token);
            }
        }
        catch
        {
            return false;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle, int tokenInformationClass,
        IntPtr tokenInformation, uint tokenInformationLength, out uint returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle, int tokenInformationClass,
        out int tokenInformation, uint tokenInformationLength, out uint returnLength);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
