namespace QuickPalette.Domain.Services;

using QuickPalette.Domain.Contracts;
using QuickPalette.Domain.Models;

/// <summary>
/// 过滤原因（供 UI 展示"为什么没出现"）
/// </summary>
public enum FilterReason
{
    None = 0,
    Keyword = 1,
    Path = 2,
    SystemComponent = 3,
    Duplicate = 4
}

/// <summary>
/// 过滤判定结果
/// </summary>
public readonly record struct FilterDecision(bool Filtered, FilterReason Reason)
{
    public static readonly FilterDecision Pass = new(false, FilterReason.None);
}

/// <summary>
/// 过滤服务 - 默认过滤规则（关键词黑名单 / 路径黑名单 / 系统组件 / 去重）。
/// Whitelist 优先于一切过滤；路径按「路径段」匹配（System32 不误伤 System32Backup）；
/// 系统目录由环境推导（%SystemRoot%、ProgramFiles 等），不硬编码盘符。
/// </summary>
public class FilterService
{
    private readonly Settings _settings;
    private readonly Lazy<string[]> _keywordCache;
    private readonly Lazy<string[]> _pathCache;
    private readonly Lazy<string[]> _whitelistCache;

    /// <summary>WindowsApps 明显系统组件包名前缀（设置/反馈中心/安全中心等）</summary>
    private static readonly string[] SystemComponentPatterns =
    {
        "microsoft.windowsstore", "windows.immersivecontrolpanel", "microsoft.windowsfeedbackhub",
        "microsoft.gethelp", "microsoft.getstarted", "microsoft.sechealthui", "microsoft.desktopappinstaller",
        "microsoft.windows.photopicker", "microsoft.accountscontrol", "microsoft.lockapp",
        "windows.captiveportal", "microsoft.549981c3f5f10", // Cortana
    };

    public FilterService(Settings settings)
    {
        _settings = settings ?? new Settings();
        _keywordCache = new Lazy<string[]>(() =>
            (_settings.FilterKeywords ?? new List<string>())
            .Where(static k => !string.IsNullOrWhiteSpace(k))
            .Select(static k => k.Trim().ToLowerInvariant())
            .Distinct()
            .ToArray());
        _pathCache = new Lazy<string[]>(() =>
            BuildSystemPaths(_settings.FilterPaths));
        _whitelistCache = new Lazy<string[]>(() =>
            (_settings.Whitelist ?? new List<string>())
            .Where(static k => !string.IsNullOrWhiteSpace(k))
            .Select(static k => k.Trim().ToLowerInvariant())
            .Distinct()
            .ToArray());
    }

    /// <summary>
    /// 检查应用是否应该被过滤（保留旧签名；细粒度原因用 Evaluate）
    /// </summary>
    public bool ShouldFilter(string? name, string? path) => Evaluate(name, path).Filtered;

    /// <summary>
    /// 细粒度过滤判定（含原因）
    /// </summary>
    public FilterDecision Evaluate(string? name, string? path)
    {
        var normName = (name ?? string.Empty).Trim().ToLowerInvariant();
        var normPath = NormalizePath(path);

        // 白名单最高优先：永不过滤
        if (IsWhitelisted(normName, normPath)) return FilterDecision.Pass;

        // 关键词黑名单（空关键词安全跳过——修复 Contains("") 恒真过滤一切的问题）
        foreach (var kw in _keywordCache.Value)
        {
            if (normName.Contains(kw)) return new FilterDecision(true, FilterReason.Keyword);
        }

        // 路径黑名单（路径段边界匹配）
        if (normPath.Length > 0)
        {
            foreach (var blocked in _pathCache.Value)
            {
                if (IsSameOrUnderPath(normPath, blocked))
                    return new FilterDecision(true, FilterReason.Path);
            }
        }

        return FilterDecision.Pass;
    }

    /// <summary>
    /// 候选过滤（含 WindowsApps 系统组件过滤）
    /// </summary>
    public IEnumerable<RawApp> FilterCandidates(IEnumerable<RawApp>? candidates)
    {
        if (candidates is null) yield break;
        foreach (var c in candidates)
        {
            if (c is null) continue;

            if (_settings.FilterSystemComponents && IsSystemComponent(c))
                continue;

            if (!Evaluate(c.Name, c.Path).Filtered)
                yield return c;
        }
    }

    /// <summary>
    /// 去重（多键：exe 路径 / AUMID / 规范化名称；空键不参与合并）。
    /// 保留优先级：用户级开始菜单 &gt; 全局级 &gt; 桌面 &gt; 其他；非卸载目录优先。
    /// </summary>
    public IEnumerable<RawApp> Deduplicate(IEnumerable<RawApp>? candidates)
    {
        if (candidates is null) yield break;

        var seenPath = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenAumid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenName = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in candidates.OrderByDescending(PreferenceScore))
        {
            if (c is null) continue;
            var duplicate = false;

            var pathKey = NormalizePath(c.Path);
            if (pathKey.Length > 0 && !seenPath.Add(pathKey)) duplicate = true;

            var aumidKey = (c.Aumid ?? string.Empty).Trim().ToLowerInvariant();
            if (aumidKey.Length > 0 && !seenAumid.Add(aumidKey)) duplicate = true;

            var nameKey = (c.Name ?? string.Empty).Trim().ToLowerInvariant();
            if (nameKey.Length > 0 && !seenName.Add(nameKey)) duplicate = true;

            if (!duplicate) yield return c;
        }
    }

    /// <summary>是否命中白名单（名称或路径）</summary>
    public bool IsWhitelisted(string? name, string? path)
    {
        var whitelist = _whitelistCache.Value;
        if (whitelist.Length == 0) return false;
        var normName = (name ?? string.Empty).Trim().ToLowerInvariant();
        var normPath = NormalizePath(path);
        foreach (var w in whitelist)
        {
            if (normName == w) return true;
            if (normPath.Length > 0 && (normPath == w || normPath.Contains(w))) return true;
        }
        return false;
    }

    /// <summary>是否 WindowsApps 明显系统组件</summary>
    private static bool IsSystemComponent(RawApp app)
    {
        var aumid = (app.Aumid ?? string.Empty).ToLowerInvariant();
        if (aumid.Length > 0)
        {
            foreach (var pattern in SystemComponentPatterns)
            {
                if (aumid.StartsWith(pattern, StringComparison.Ordinal)) return true;
            }
        }

        // WindowsApps 目录下的系统包按包名再拦一轮（非 AUMID 来源时路径兜底）
        var path = (app.Path ?? string.Empty).ToLowerInvariant();
        return path.Contains("\\windowsapps\\") && SystemComponentPatterns.Any(p => path.Contains(p));
    }

    /// <summary>候选保留优先级（越大越优先）</summary>
    private static int PreferenceScore(RawApp app)
    {
        var score = 0;
        var source = (app.SourceId ?? string.Empty).ToLowerInvariant();
        var path = (app.Path ?? string.Empty).ToLowerInvariant();

        if (source.Contains("startmenu"))
            score += path.Contains("programs") && !path.Contains("allusers") ? 500 : 400; // 用户级 > 全局级
        else if (source.Contains("desktop"))
            score += 300;
        else if (source.Contains("registry"))
            score += 200;
        else if (source.Contains("uwp"))
            score += 150;
        else
            score += 100;

        // 卸载目录/卸载程序降权
        if (path.Contains("unins") || path.Contains("\\uninstall") || path.Contains("\\install\\"))
            score -= 250;

        return score;
    }

    /// <summary>路径段边界匹配：blocked 是 normPath 的根前缀（目录边界处截断）</summary>
    private static bool IsSameOrUnderPath(string normPath, string blockedNormPath)
    {
        if (blockedNormPath.Length == 0) return false;
        if (!normPath.StartsWith(blockedNormPath, StringComparison.OrdinalIgnoreCase)) return false;
        return normPath.Length == blockedNormPath.Length ||
               normPath[blockedNormPath.Length] == '\\';
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var normalized = path.Replace('/', '\\').Trim().TrimEnd('\\');
        return normalized.ToLowerInvariant();
    }

    /// <summary>
    /// 系统目录 = 设置中的 FilterPaths + 由环境推导的 Windows/程序目录（%SystemRoot% 等），
    /// 保证 Windows 不在 C 盘时同样生效。
    /// </summary>
    private static string[] BuildSystemPaths(List<string>? configured)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? p)
        {
            var normalized = NormalizePath(p);
            if (normalized.Length > 0) set.Add(normalized);
        }

        // 环境推导（优先于配置，盘符无关）：
        // 注意只拉黑 Windows 系统子目录（System32/SysWOW64/WinSxS/.NET/servicing），
        // ①不拉黑整个 %SystemRoot%（其下偶有用户可用工具，如 System32Backup 不该被 StartsWith 误杀）；
        // ②不拉黑 Program Files（正常软件安装位置）
        Add(Environment.GetFolderPath(Environment.SpecialFolder.System));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "WinSxS"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "servicing"));

        if (configured != null)
        {
            foreach (var p in configured) Add(p);
        }

        return set.ToArray();
    }
}
