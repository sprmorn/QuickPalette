namespace QuickPalette.Infrastructure.Launchers;

using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using QuickPalette.Domain.Contracts;

/// <summary>
/// 启动异常分类器 - 把 Win32Exception/COMException 等映射为 <see cref="LaunchErrorKind"/>，
/// 供各启动器统一做失败分类（例如"路径失效 → NotFound → 引导重新定位"）。
/// </summary>
public static class LaunchErrorMapper
{
    // Win32 错误码（winerror.h）
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidDrive = 15;
    private const int ErrorSharingViolation = 32;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorInvalidName = 123;
    private const int ErrorBadExeFormat = 193;
    private const int ErrorDirectory = 267;
    private const int ErrorCancelled = 1223;
    private const int ErrorElevationRequired = 740;
    private const int ErrorModuleNotFound = 126;

    /// <summary>
    /// 把任意启动异常分类为 <see cref="LaunchErrorKind"/>
    /// </summary>
    public static LaunchErrorKind Classify(Exception ex)
    {
        switch (ex)
        {
            case Win32Exception w32:
                return FromWin32(w32.NativeErrorCode);
            case FileNotFoundException:
            case DirectoryNotFoundException:
                return LaunchErrorKind.NotFound;
            case UnauthorizedAccessException:
                return LaunchErrorKind.AccessDenied;
            case ArgumentException:
            case NotSupportedException:
            case PathTooLongException:
                return LaunchErrorKind.InvalidPath;
            case COMException com:
                return FromHResult(com.HResult);
            default:
                return LaunchErrorKind.Failed;
        }
    }

    /// <summary>
    /// Win32 错误码 → 失败分类
    /// </summary>
    public static LaunchErrorKind FromWin32(int nativeErrorCode)
    {
        return nativeErrorCode switch
        {
            ErrorFileNotFound => LaunchErrorKind.NotFound,
            ErrorPathNotFound => LaunchErrorKind.NotFound,
            ErrorModuleNotFound => LaunchErrorKind.NotFound,
            ErrorAccessDenied => LaunchErrorKind.AccessDenied,
            ErrorElevationRequired => LaunchErrorKind.AccessDenied,
            ErrorInvalidDrive => LaunchErrorKind.InvalidPath,
            ErrorInvalidName => LaunchErrorKind.InvalidPath,
            ErrorInvalidParameter => LaunchErrorKind.InvalidPath,
            ErrorDirectory => LaunchErrorKind.InvalidPath,
            ErrorSharingViolation => LaunchErrorKind.AccessDenied,
            ErrorBadExeFormat => LaunchErrorKind.Unsupported,
            // 用户在 UAC/安全提示处取消，视为被安全策略阻止
            ErrorCancelled => LaunchErrorKind.Blocked,
            _ => LaunchErrorKind.Failed
        };
    }

    /// <summary>
    /// COM HRESULT → 失败分类
    /// </summary>
    public static LaunchErrorKind FromHResult(int hresult)
    {
        return hresult switch
        {
            unchecked((int)0x80070002) => LaunchErrorKind.NotFound, // HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND)
            unchecked((int)0x80070003) => LaunchErrorKind.NotFound, // HRESULT_FROM_WIN32(ERROR_PATH_NOT_FOUND)
            unchecked((int)0x8007007E) => LaunchErrorKind.NotFound, // HRESULT_FROM_WIN32(ERROR_MOD_NOT_FOUND)
            unchecked((int)0x80070005) => LaunchErrorKind.AccessDenied, // E_ACCESSDENIED
            unchecked((int)0x80070020) => LaunchErrorKind.AccessDenied, // ERROR_SHARING_VIOLATION
            unchecked((int)0x80070057) => LaunchErrorKind.InvalidPath, // E_INVALIDARG
            unchecked((int)0x80040154) => LaunchErrorKind.Unsupported, // REGDB_E_CLASSNOTREG
            unchecked((int)0x800401F3) => LaunchErrorKind.InvalidPath, // CO_E_CLASSSTRING（ProgID 非法）
            unchecked((int)0x800401F0) => LaunchErrorKind.InvalidPath, // CO_E_NOTINITIALIZED
            _ => LaunchErrorKind.Failed
        };
    }
}
