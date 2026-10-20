using System.Collections.Concurrent;
using Microsoft.Win32;

namespace QuickPalette.Infrastructure.Preview;

/// <summary>预览处理器（Preview Handler）的 COM 激活方式</summary>
public enum PreviewHandlerActivationKind
{
    /// <summary>未识别的注册方式（无 InprocServer32 / LocalServer32）</summary>
    Unknown = 0,

    /// <summary>进程内 DLL：必须与宿主进程位数一致才能加载</summary>
    InProcServer,

    /// <summary>本地 COM 服务器（独立进程）：任意位数的宿主均可激活，由 COM 跨进程封送</summary>
    LocalServer
}

/// <summary>承载该处理器所需的宿主位数</summary>
public enum PreviewHandlerHostArch
{
    /// <summary>任意宿主均可（LocalServer 跨进程封送；探测不出架构时两种宿主都试）</summary>
    Any = 0,

    /// <summary>仅 x64 宿主（64 位进程内 DLL）</summary>
    X64Only,

    /// <summary>仅 x86 宿主（32 位进程内 DLL：WPS 系插件、Foxit 等）</summary>
    X86Only
}

/// <summary>一条预览处理器注册信息（解析自 HKCR，对应资源管理器预览窗格的同一套关联）</summary>
/// <param name="Clsid">处理器 CLSID</param>
/// <param name="DisplayName">注册表中的显示名（如 "Microsoft Word previewer"，可能为空）</param>
/// <param name="Kind">COM 激活方式</param>
/// <param name="InProcServerPath">进程内 DLL 路径（仅 <see cref="PreviewHandlerActivationKind.InProcServer"/>）</param>
/// <param name="InProcServerArch">进程内 DLL 的 PE 架构（x64/x86/arm64；探测失败为 null）</param>
public sealed record PreviewHandlerRegistration(
    Guid Clsid,
    string? DisplayName,
    PreviewHandlerActivationKind Kind,
    string? InProcServerPath,
    string? InProcServerArch)
{
    /// <summary>
    /// 承载所需宿主位数：LocalServer 由 COM 跨进程封送（32 位视图注册的 WPS 预览器
    /// 实测可由 x64 宿主激活）；进程内处理器必须与宿主同位数。
    /// </summary>
    public PreviewHandlerHostArch HostArch => Kind switch
    {
        PreviewHandlerActivationKind.LocalServer => PreviewHandlerHostArch.Any,
        PreviewHandlerActivationKind.InProcServer => InProcServerArch switch
        {
            "x86" => PreviewHandlerHostArch.X86Only,
            "x64" => PreviewHandlerHostArch.X64Only,
            _ => PreviewHandlerHostArch.Any // 探测不出（含 arm64）：两种宿主都试，失败即回落
        },
        _ => PreviewHandlerHostArch.Any
    };
}

/// <summary>
/// 扩展名 → 预览处理器 CLSID 解析（与资源管理器预览窗格同一套关联语义）。
///
/// 解析链（与 Explorer 一致，逐级回退）：
///  1. HKCR\{ext}\ShellEx\{8895b1c6-b41f-4c1c-a562-0d564250836f}
///  2. HKCR\{ProgId}\ShellEx\{8895b1c6-...}（ProgId = HKCR\{ext} 默认值）
///  3. HKCR\SystemFileAssociations\{PerceivedType}\ShellEx\{8895b1c6-...}
///     （txt 等按感知类型关联；PerceivedType = HKCR\{ext}\PerceivedType）
///
/// 注册信息读取顺序：64 位视图 → WOW6432Node（32 位视图）。本组件进程内零副作用，
/// 仅做注册表读取与 PE 头探测，可注入读取委托以便单元测试。
/// </summary>
public sealed class PreviewHandlerResolver
{
    /// <summary>预览处理器 ShellEx 关联 GUID（即 IPreviewHandler 的 IID）</summary>
    public const string PreviewHandlerShellExGuid = "8895b1c6-b41f-4c1c-a562-0d564250836f";

    /// <summary>共享实例（带扩展名缓存；注册表读取开销小，但单击高频触发）</summary>
    public static PreviewHandlerResolver Shared { get; } = new();

    private readonly Func<string, string?, string?> _readClassValue;
    private readonly ConcurrentDictionary<string, PreviewHandlerRegistration?> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 默认读真实注册表 HKCR（subKey 相对路径，valueName 为 null 读默认值）；
    /// 测试可注入假读取委托。
    /// </summary>
    public PreviewHandlerResolver(Func<string, string?, string?>? readClassValue = null)
    {
        _readClassValue = readClassValue ?? ReadHkcrValue;
    }

    /// <summary>解析扩展名对应的预览处理器注册信息（无关联返回 null）</summary>
    public PreviewHandlerRegistration? Find(string? extension)
    {
        var ext = NormalizeExtension(extension);
        if (ext is null) return null;
        return _cache.GetOrAdd(ext, FindCore);
    }

    /// <summary>
    /// 该注册能否由指定位数的宿主进程承载。
    /// LocalServer 任意位数可激活；进程内处理器必须位数一致（当前预览宿主为 x64，
    /// 32 位 DLL 会直接跳过回落，避免注定失败的加载尝试）。
    /// </summary>
    public static bool CanHost(PreviewHandlerRegistration registration, bool hostIs64Bit = true)
    {
        return registration.Kind switch
        {
            PreviewHandlerActivationKind.LocalServer => true,
            PreviewHandlerActivationKind.InProcServer when hostIs64Bit =>
                registration.InProcServerArch is not ("x86" or "arm64"),
            PreviewHandlerActivationKind.InProcServer =>
                registration.InProcServerArch is not ("x64" or "arm64"),
            _ => false
        };
    }

    /// <summary>扩展名归一化：小写、带点（空/异常返回 null）</summary>
    private static string? NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return null;
        var ext = extension.Trim().ToLowerInvariant();
        return ext.StartsWith('.') ? ext : "." + ext;
    }

    private PreviewHandlerRegistration? FindCore(string ext)
    {
        var shellEx = $"ShellEx\\{{{PreviewHandlerShellExGuid}}}";

        // 1. 扩展名直连
        var clsidText = _readClassValue($"{ext}\\{shellEx}", null);

        // 2. ProgId 中转（如 docx → Word.Document.12）
        if (clsidText is null)
        {
            var progId = _readClassValue(ext, null);
            if (!string.IsNullOrWhiteSpace(progId))
            {
                clsidText = _readClassValue($"{progId}\\{shellEx}", null);
            }
        }

        // 3. PerceivedType 中转（txt/md → text）：注意 PerceivedType 是 .ext 键上的命名值，不是子键
        if (clsidText is null)
        {
            var perceivedType = _readClassValue(ext, "PerceivedType");
            if (!string.IsNullOrWhiteSpace(perceivedType))
            {
                clsidText = _readClassValue($"SystemFileAssociations\\{perceivedType}\\{shellEx}", null);
            }
        }

        if (string.IsNullOrWhiteSpace(clsidText)) return null;
        if (!Guid.TryParse(clsidText.Trim(), out var clsid)) return null;

        return BuildRegistration(clsid);
    }

    private PreviewHandlerRegistration? BuildRegistration(Guid clsid)
    {
        var clsidKey = clsid.ToString("B");

        var displayName = _readClassValue($"CLSID\\{clsidKey}", null)
            ?? _readClassValue($"WOW6432Node\\CLSID\\{clsidKey}", null);

        // 64 位视图优先，32 位视图兜底（WPS 等仅在 32 位视图注册）
        var inProc = _readClassValue($"CLSID\\{clsidKey}\\InprocServer32", null)
            ?? _readClassValue($"WOW6432Node\\CLSID\\{clsidKey}\\InprocServer32", null);
        var localServer = _readClassValue($"CLSID\\{clsidKey}\\LocalServer32", null)
            ?? _readClassValue($"WOW6432Node\\CLSID\\{clsidKey}\\LocalServer32", null);

        if (!string.IsNullOrWhiteSpace(localServer))
        {
            return new PreviewHandlerRegistration(clsid, displayName, PreviewHandlerActivationKind.LocalServer, null, null);
        }

        if (!string.IsNullOrWhiteSpace(inProc))
        {
            // LocalServer32 可能带命令行参数，InprocServer32 是纯 DLL 路径
            var dllPath = inProc.Trim().Trim('"');
            return new PreviewHandlerRegistration(
                clsid, displayName, PreviewHandlerActivationKind.InProcServer,
                dllPath, ProbePeArch(dllPath));
        }

        return new PreviewHandlerRegistration(clsid, displayName, PreviewHandlerActivationKind.Unknown, null, null);
    }

    /// <summary>
    /// 探测 PE 文件架构（读文件头，不加载 DLL）：返回 x64 / x86 / arm64 / 未知机器码；
    /// 非 PE 或不可读返回 null。
    /// </summary>
    public static string? ProbePeArch(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < 0x40) return null;
            using var reader = new BinaryReader(stream);

            stream.Seek(0x3C, SeekOrigin.Begin);
            var peOffset = reader.ReadInt32();
            if (peOffset <= 0 || peOffset + 6 > stream.Length) return null;

            stream.Seek(peOffset, SeekOrigin.Begin);
            if (reader.ReadUInt32() != 0x0000_4550) return null; // "PE\0\0"

            return reader.ReadUInt16() switch
            {
                0x8664 => "x64",
                0x014C => "x86",
                0xAA64 => "arm64",
                var machine => $"0x{machine:X4}"
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadHkcrValue(string subKey, string? valueName)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(subKey);
            return key?.GetValue(valueName) as string;
        }
        catch
        {
            return null;
        }
    }
}
