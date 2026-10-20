namespace QuickPalette.Infrastructure.Scanners;

using QuickPalette.Domain.Contracts;
using QuickPalette.Domain.Models;
using QuickPalette.Domain.Services;
using Serilog;

/// <summary>
/// 扫描器管理器 - 统一管理所有数据源，按 Settings.ScanSources 过滤来源
/// </summary>
public class ScannerManager
{
    private readonly List<IAppSource> _sources = new();
    private readonly FilterService _filterService;
    private readonly ILogger _logger;

    /// <summary>
    /// 来源别名 → 规范 SourceId（兼容旧值/常见写法，匹配时忽略大小写与分隔符）
    /// </summary>
    private static readonly Dictionary<string, string> SourceAliases = new(StringComparer.Ordinal)
    {
        // 开始菜单
        ["startmenu"] = "StartMenu",
        ["startmenushortcuts"] = "StartMenu",
        ["shortcuts"] = "StartMenu",
        ["lnk"] = "StartMenu",
        // 注册表
        ["registry"] = "Registry",
        ["reg"] = "Registry",
        ["apppaths"] = "Registry",
        // UWP / 商店
        ["uwp"] = "Uwp",
        ["store"] = "Uwp",
        ["appsfolder"] = "Uwp",
        ["msix"] = "Uwp",
        ["modernapp"] = "Uwp",
        // 运行中进程
        ["process"] = "Process",
        ["proc"] = "Process",
        ["running"] = "Process",
        ["runningprocess"] = "Process",
        ["capture"] = "Process",
        ["autocapture"] = "Process",
        ["foreground"] = "Process",
    };

    public ScannerManager(FilterService filterService, ILogger logger)
    {
        _filterService = filterService;
        _logger = logger;

        // 注册全部 4 个默认扫描器
        RegisterSource(new StartMenuScanner(logger));
        RegisterSource(new RegistryScanner(logger));
        RegisterSource(new UwpScanner(logger));
        RegisterSource(new ProcessScanner(logger));
    }

    public void RegisterSource(IAppSource source)
    {
        _sources.Add(source);
        _logger.Debug("Registered scanner: {SourceId}", source.SourceId);
    }

    /// <summary>
    /// 扫描所有启用的数据源（按 settings.ScanSources 大小写不敏感过滤，支持旧值别名）
    /// </summary>
    public async Task<List<RawApp>> ScanAllAsync(Settings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var allApps = new List<RawApp>();
        var configured = settings.ScanSources ?? new List<string>();

        foreach (var source in _sources)
        {
            try
            {
                ct.ThrowIfCancellationRequested();

                if (!IsSourceEnabled(source.SourceId, configured))
                {
                    _logger.Debug("Scanner disabled by settings: {SourceId}", source.SourceId);
                    continue;
                }

                _logger.Information("Scanning: {SourceId}", source.SourceId);
                var apps = await source.ScanAsync(ct).ConfigureAwait(false);
                allApps.AddRange(apps);
            }
            catch (OperationCanceledException)
            {
                // 取消必须向上抛
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Scanner failed: {SourceId}", source.SourceId);
            }
        }

        // 过滤
        var filtered = _filterService.FilterCandidates(allApps).ToList();

        // 去重
        var deduplicated = _filterService.Deduplicate(filtered).ToList();

        _logger.Information("Scan complete: {Total} found, {Filtered} after filter, {Deduplicated} after dedup",
            allApps.Count, filtered.Count, deduplicated.Count);

        return deduplicated;
    }

    /// <summary>
    /// 兼容旧签名：无 Settings 时按默认配置（StartMenu/Registry/Uwp）扫描
    /// </summary>
    public Task<List<RawApp>> ScanAllAsync(CancellationToken ct = default)
    {
        return ScanAllAsync(new Settings(), ct);
    }

    /// <summary>
    /// 判断某来源是否被启用（大小写不敏感 + 旧值别名）。
    /// 纯函数，可独立测试。
    /// </summary>
    public static bool IsSourceEnabled(string sourceId, IReadOnlyList<string> configuredSources)
    {
        if (string.IsNullOrWhiteSpace(sourceId) || configuredSources == null || configuredSources.Count == 0)
            return false;

        var canonicalTarget = Canonicalize(sourceId);

        foreach (var entry in configuredSources)
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;

            var trimmed = entry.Trim();

            // "*" / "all" 表示全部来源
            if (trimmed == "*")
                return true;

            var token = Canonicalize(entry);
            if (token.Length == 0)
                continue;

            if (token == "all")
                return true;

            // 别名映射后比较，或直接与 SourceId 比较（支持自定义来源）
            if (SourceAliases.TryGetValue(token, out var mapped))
            {
                if (string.Equals(mapped, sourceId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            if (string.Equals(token, canonicalTarget, StringComparison.Ordinal))
                return true;

            if (string.Equals(entry.Trim(), sourceId, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 归一化来源值：去掉分隔符并转小写（"Start Menu" / "start_menu" → "startmenu"）
    /// </summary>
    private static string Canonicalize(string value)
    {
        var chars = new char[value.Length];
        var n = 0;
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
                chars[n++] = char.ToLowerInvariant(ch);
        }

        return new string(chars, 0, n);
    }
}
