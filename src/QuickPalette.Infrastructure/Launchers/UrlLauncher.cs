namespace QuickPalette.Infrastructure.Launchers;

using System.Diagnostics;
using System.Text.RegularExpressions;
using QuickPalette.Domain.Contracts;
using QuickPalette.Domain.Models;
using Serilog;

/// <summary>
/// URL 启动器 - 协议白名单校验 + 本地路径注入防护
/// </summary>
public class UrlLauncher : ILauncher
{
    /// <summary>
    /// 允许启动的协议白名单（大小写不敏感）
    /// </summary>
    private static readonly string[] AllowedSchemes = { "http", "https", "ftp", "mailto", "ms-settings" };

    /// <summary>
    /// 显式协议前缀形如 "scheme:"（scheme 由 RFC 3986 规定的字符组成）
    /// </summary>
    private static readonly Regex ExplicitSchemeRegex = new(@"^[A-Za-z][A-Za-z0-9+.\-]*:", RegexOptions.Compiled);

    /// <summary>
    /// 本地路径特征：盘符（C:）、UNC（\\server）、反斜杠、相对路径（./ ../ .\）、用户目录（~）
    /// </summary>
    private static readonly Regex DriveLetterRegex = new(@"^[A-Za-z]:", RegexOptions.Compiled);

    private readonly ILogger _logger;

    public UrlLauncher(ILogger logger)
    {
        _logger = logger;
    }

    public bool CanLaunch(AppItem item)
    {
        return item.Type == AppType.Url;
    }

    public Task<LaunchResult> LaunchAsync(AppItem item, LaunchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        // "打开所在目录"：本地 .url/.lnk 文件可定位，纯网络链接没有本地目录
        if (options?.OpenDirectory == true)
        {
            var raw = string.IsNullOrWhiteSpace(item.Path) ? item.Aumid : item.Path;
            if (!string.IsNullOrWhiteSpace(raw) && File.Exists(raw) &&
                ExplorerHelper.RevealInExplorer(raw, msg => _logger.Warning("Reveal degraded: {Msg}", msg)))
            {
                return Task.FromResult(LaunchResult.Ok(null));
            }
            return Task.FromResult(LaunchResult.Fail("链接没有本地目录，无法打开文件位置", LaunchErrorKind.InvalidPath));
        }
        return Task.FromResult(LaunchCore(item));
    }

    private LaunchResult LaunchCore(AppItem item)
    {
        // URL 可从 item 推断：优先 Path，其次 Aumid（部分导入数据把链接放在 Aumid）
        var raw = string.IsNullOrWhiteSpace(item.Path) ? item.Aumid : item.Path;

        if (!TryNormalizeUrl(raw, out var url, out var kind, out var error))
        {
            _logger.Warning("URL launch rejected: {Name} ({Raw}): {Error}", item.Name, raw, error);
            return LaunchResult.Fail(error ?? "非法 URL", kind);
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });

            _logger.Information("Launched URL: {Name} ({Url})", item.Name, url);
            return LaunchResult.Ok(process?.Id);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            var wKind = LaunchErrorMapper.FromWin32(ex.NativeErrorCode);
            _logger.Error(ex, "Win32 failure launching URL: {Name} ({Url}), code={Code}", item.Name, url, ex.NativeErrorCode);
            return LaunchResult.Fail($"打开链接失败 (0x{ex.NativeErrorCode:X}): {ex.Message}", wKind);
        }
        catch (Exception ex)
        {
            var cKind = LaunchErrorMapper.Classify(ex);
            _logger.Error(ex, "Failed to launch URL: {Name} ({Url})", item.Name, url);
            return LaunchResult.Fail(ex.Message, cKind);
        }
    }

    /// <summary>
    /// 校验并规范化 URL：仅放行白名单协议，拒绝本地路径注入。
    /// 纯函数，可独立测试。
    /// </summary>
    /// <param name="raw">原始字符串（来自 item）</param>
    /// <param name="normalizedUrl">校验通过时输出规范化 URL</param>
    /// <param name="errorKind">被拒绝时的失败分类</param>
    /// <param name="error">被拒绝时的错误描述</param>
    /// <returns>true 表示放行</returns>
    public static bool TryNormalizeUrl(string? raw, out string normalizedUrl, out LaunchErrorKind errorKind, out string? error)
    {
        normalizedUrl = string.Empty;
        errorKind = LaunchErrorKind.None;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            errorKind = LaunchErrorKind.InvalidPath;
            error = "URL 为空";
            return false;
        }

        var s = raw.Trim();

        // 1. 本地路径注入一律拒绝（不是 URI 就不允许伪装成链接启动）
        if (LooksLikeLocalPath(s))
        {
            errorKind = LaunchErrorKind.InvalidPath;
            error = $"拒绝本地路径注入: {s}";
            return false;
        }

        // 2. 绝对 URI 且协议在白名单 → 放行
        //    含点的"协议"（如 example.com:8080）实为 host:port，交给下面的 https 补全逻辑
        if (Uri.TryCreate(s, UriKind.Absolute, out var uri) && !uri.Scheme.Contains('.'))
        {
            if (IsAllowedScheme(uri.Scheme))
            {
                normalizedUrl = s;
                return true;
            }

            errorKind = LaunchErrorKind.Blocked;
            error = $"协议 {uri.Scheme} 不在白名单";
            return false;
        }

        // 3. 显式协议但无法解析或不在白名单 → 阻止（scheme 含点视为 host:port，不拦截）
        if (ExplicitSchemeRegex.IsMatch(s))
        {
            var colonIndex = s.IndexOf(':');
            if (colonIndex > 0 && !s[..colonIndex].Contains('.'))
            {
                errorKind = LaunchErrorKind.Blocked;
                error = $"协议不在白名单或无法解析: {s}";
                return false;
            }
        }

        // 4. 无协议的裸域名/主机 → 按 https 补全后复验
        var candidate = "https://" + s;
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var httpsUri)
            && IsAllowedScheme(httpsUri.Scheme)
            && !string.IsNullOrEmpty(httpsUri.Host))
        {
            normalizedUrl = candidate;
            return true;
        }

        errorKind = LaunchErrorKind.InvalidPath;
        error = $"无法解析为合法 URL: {s}";
        return false;
    }

    /// <summary>
    /// 判断字符串是否是本地路径形态（盘符 / UNC / 反斜杠 / 相对路径 / 用户目录）
    /// </summary>
    private static bool LooksLikeLocalPath(string s)
    {
        return DriveLetterRegex.IsMatch(s)
               || s.StartsWith(@"\\", StringComparison.Ordinal)
               || s.StartsWith("//", StringComparison.Ordinal)
               || s.Contains('\\')
               || s.StartsWith("/", StringComparison.Ordinal)
               || s.StartsWith(".", StringComparison.Ordinal)
               || s.StartsWith("~", StringComparison.Ordinal);
    }

    private static bool IsAllowedScheme(string scheme)
    {
        foreach (var allowed in AllowedSchemes)
        {
            if (string.Equals(allowed, scheme, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
