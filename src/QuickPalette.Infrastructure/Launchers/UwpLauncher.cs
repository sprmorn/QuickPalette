namespace QuickPalette.Infrastructure.Launchers;

using System.Diagnostics;
using System.Runtime.InteropServices;
using QuickPalette.Domain.Contracts;
using QuickPalette.Domain.Models;
using Serilog;

/// <summary>
/// UWP 启动器 - 优先 IApplicationActivationManager（COM）激活，失败降级 explorer shell:AppsFolder
/// </summary>
public class UwpLauncher : ILauncher
{
    /// <summary>CLSID_ApplicationActivationManager</summary>
    private static readonly Guid ClsidApplicationActivationManager = new("45BA127D-10A8-46EA-8AB7-56EA9078943C");

    private readonly ILogger _logger;

    public UwpLauncher(ILogger logger)
    {
        _logger = logger;
    }

    public bool CanLaunch(AppItem item)
    {
        return item.Type == AppType.Uwp;
    }

    public Task<LaunchResult> LaunchAsync(AppItem item, LaunchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        // "打开所在目录"：商店应用没有可浏览的本地安装目录，明确告知而不是误把应用启动起来
        if (options?.OpenDirectory == true)
        {
            _logger.Information("OpenDirectory requested for UWP app (no local directory): {Name}", item.Name);
            return Task.FromResult(LaunchResult.Fail("商店应用没有本地安装目录，无法打开文件位置", LaunchErrorKind.InvalidPath));
        }
        return Task.FromResult(LaunchCore(item));
    }

    private LaunchResult LaunchCore(AppItem item)
    {
        var aumid = item.Aumid?.Trim();

        // 1. AUMID 校验（包名!应用ID 简单校验）
        if (string.IsNullOrEmpty(aumid))
        {
            _logger.Warning("UWP launch rejected: {Name} has empty AUMID", item.Name);
            return LaunchResult.Fail("UWP 应用缺少 AUMID", LaunchErrorKind.InvalidPath);
        }

        if (!IsValidAumid(aumid))
        {
            _logger.Warning("UWP launch rejected: {Name} invalid AUMID: {Aumid}", item.Name, aumid);
            return LaunchResult.Fail($"AUMID 格式非法: {aumid}", LaunchErrorKind.InvalidPath);
        }

        // 2. 优先 COM 激活（IApplicationActivationManager.ActivateApplication）
        var activated = TryActivateViaCom(item, aumid, out var comResult);
        if (activated != null)
            return activated;

        // 3. 降级 explorer shell:AppsFolder（如实返回 Success，但记日志注明降级）
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"shell:AppsFolder\\{aumid}",
                UseShellExecute = true
            });

            _logger.Warning("UWP 激活降级为 explorer shell:AppsFolder: {Name} ({Aumid})，COM 激活结果: {HResult}",
                item.Name, aumid, comResult);
            return LaunchResult.Ok(process?.Id);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            var kind = LaunchErrorMapper.FromWin32(ex.NativeErrorCode);
            _logger.Error(ex, "Win32 failure launching UWP via explorer: {Name} ({Aumid}), code={Code}", item.Name, aumid, ex.NativeErrorCode);
            return LaunchResult.Fail($"启动 UWP 应用失败 (0x{ex.NativeErrorCode:X}): {ex.Message}", kind);
        }
        catch (Exception ex)
        {
            var kind = LaunchErrorMapper.Classify(ex);
            _logger.Error(ex, "Failed to launch UWP: {Name} ({Aumid})", item.Name, aumid);
            return LaunchResult.Fail(ex.Message, kind);
        }
    }

    /// <summary>
    /// 通过 IApplicationActivationManager 激活；成功返回结果，失败返回 null（由调用方降级）。
    /// </summary>
    private LaunchResult? TryActivateViaCom(AppItem item, string aumid, out string comResult)
    {
        comResult = "未执行";
        IApplicationActivationManager? manager = null;
        try
        {
            var type = Type.GetTypeFromCLSID(ClsidApplicationActivationManager, throwOnError: false);
            if (type == null)
            {
                comResult = "CLSID 未注册";
                return null;
            }

            manager = (IApplicationActivationManager?)Activator.CreateInstance(type);
            if (manager == null)
            {
                comResult = "创建激活管理器失败";
                return null;
            }

            var hr = manager.ActivateApplication(aumid, string.IsNullOrEmpty(item.Args) ? null : item.Args,
                ActivateOptions.None, out var processId);
            comResult = $"0x{hr:X8}";

            if (hr >= 0)
            {
                _logger.Information("Launched UWP via IApplicationActivationManager: {Name} ({Aumid}) pid={Pid}",
                    item.Name, aumid, processId);
                return LaunchResult.Ok(unchecked((int)processId));
            }

            _logger.Warning("IApplicationActivationManager.ActivateApplication failed: {Name} ({Aumid}) hr={HResult}",
                item.Name, aumid, comResult);
            return null;
        }
        catch (Exception ex)
        {
            // COMException/InvalidCastException 等统一记日志后降级
            comResult = $"{ex.GetType().Name}: {ex.Message}";
            _logger.Warning(ex, "COM activation failed for: {Name} ({Aumid})", item.Name, aumid);
            return null;
        }
        finally
        {
            if (manager != null && Marshal.IsComObject(manager))
            {
                try { Marshal.FinalReleaseComObject(manager); }
                catch (Exception ex) { _logger.Debug(ex, "Release IApplicationActivationManager failed"); }
            }
        }
    }

    /// <summary>
    /// AUMID 简单校验：形如 "包名!应用ID"，恰好一个 '!'，两段均非空且不含空白/路径分隔符/引号等危险字符。
    /// 纯函数，可独立测试。
    /// </summary>
    public static bool IsValidAumid(string? aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid))
            return false;

        var s = aumid.Trim();

        var bangIndex = s.IndexOf('!');
        if (bangIndex <= 0 || bangIndex != s.LastIndexOf('!'))
            return false; // 必须恰好一个 '!' 且不在首尾

        var package = s[..bangIndex];
        var application = s[(bangIndex + 1)..];
        if (package.Length == 0 || application.Length == 0)
            return false;

        return IsSafeAumidPart(package) && IsSafeAumidPart(application);
    }

    /// <summary>
    /// AUMID 片段合法字符：字母/数字/ . _ - { } ~（同时排除 shell:AppsFolder 注入所需的路径分隔符、引号、分号等）
    /// </summary>
    private static bool IsSafeAumidPart(string part)
    {
        foreach (var ch in part)
        {
            var ok = char.IsLetterOrDigit(ch)
                     || ch is '.' or '_' or '-' or '{' or '}' or '~';
            if (!ok)
                return false;
        }

        return true;
    }

    #region COM Interop - IApplicationActivationManager

    /// <summary>激活选项（ACTIVATEOPTIONS）</summary>
    private enum ActivateOptions
    {
        None = 0,
        DesignMode = 1,
        NoErrorUI = 2,
        NoSplashScreen = 4
    }

    /// <summary>
    /// IApplicationActivationManager（shobjidl_core.h），IID = 2E941141-7F97-4756-BA1D-9DECDE894A3D。
    /// 仅 ActivateApplication 被使用，其余方法占位以保持 vtable 布局。
    /// </summary>
    [ComImport]
    [Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        /// <summary>按 AUMID 激活应用，返回 HRESULT，成功时输出进程 PID</summary>
        [PreserveSig]
        int ActivateApplication(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string? arguments,
            ActivateOptions options,
            out uint processId);

        /// <summary>占位（保持 vtable 布局，不使用）</summary>
        [PreserveSig]
        int ActivateForFile(IntPtr itemArray, IntPtr verb, out uint processId);

        /// <summary>占位（保持 vtable 布局，不使用）</summary>
        [PreserveSig]
        int ActivateForProtocol(
            [MarshalAs(UnmanagedType.LPWStr)] string itemArray,
            [MarshalAs(UnmanagedType.LPWStr)] string? arguments,
            out uint processId);
    }

    #endregion
}
