namespace QuickPalette.App.Services;

using QuickPalette.Domain.Links;
using QuickPalette.Domain.Models;
using Serilog;

/// <summary>
/// 链接助手编排：监听 → 分类开关/协议可用性/去重过滤 → 弹提示卡 → 跳转。
/// 去重记录仅驻内存（重启即清，不落盘、不记录剪贴板历史）。
/// </summary>
public sealed class LinkAssistantService : IDisposable
{
    /// <summary>网页类按域名去重的固定窗口（复制网页链接最频繁）</summary>
    private static readonly TimeSpan WebDomainDedupWindow = TimeSpan.FromMinutes(5);

    private readonly ILogger _logger;
    private readonly ClipboardLinkMonitor _monitor;
    private readonly Dictionary<string, DateTime> _recent = new();
    private readonly Dictionary<string, Task<string?>> _resolves = new();
    private Settings _settings = new();

    public LinkAssistantService(ILogger logger)
    {
        _logger = logger;
        _monitor = new ClipboardLinkMonitor(logger);
        _monitor.LinkFound += OnLinkFound;
    }

    /// <summary>启动监听（首次设置生效后调用）</summary>
    public void Start(Settings settings)
    {
        _settings = settings ?? _settings;
        if (_settings.LinkAssistantEnabled)
        {
            _monitor.Start();
        }
    }

    public void UpdateSettings(Settings settings) => _settings = settings ?? _settings;

    private void OnLinkFound(LinkSuggestion suggestion)
    {
        try
        {
            if (!IsCategoryEnabled(suggestion.Category))
            {
                _logger.Debug("Link skipped (category off): {Category}", suggestion.Category);
                return;
            }

            if (!LinkLauncher.CanLaunch(suggestion.LaunchUri))
            {
                _logger.Information("Link skipped (target app not installed): {App} {Uri}",
                    suggestion.TargetApp, suggestion.LaunchUri);
                return;
            }

            if (IsDuplicate(suggestion))
            {
                _logger.Debug("Link skipped (dedup): {Uri}", suggestion.LaunchUri);
                return;
            }

            StartSmartResolve(suggestion);
            var prompt = new LinkPromptWindow(_logger, suggestion, onOpen: s => _ = OpenSmartAsync(s));
            prompt.Show();
            _logger.Information("Link prompt shown: {Category} {App}", suggestion.Category, suggestion.TargetApp);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Link assistant handling failed");
        }
    }

    /// <summary>智能直跳：先用后台嗅探结果（真实协议）；超时/未命中/失败回退浏览器打开原链接</summary>
    private async Task OpenSmartAsync(LinkSuggestion suggestion)
    {
        var uri = suggestion.LaunchUri;
        try
        {
            Task<string?>? pending = null;
            lock (_resolves)
            {
                _resolves.TryGetValue(uri, out pending);
            }

            if (pending != null)
            {
                // 极值兜底：最多再等 2s，还没结果就走浏览器
                var done = await Task.WhenAny(pending, Task.Delay(2000));
                if (done == pending && !string.IsNullOrEmpty(pending.Result))
                {
                    uri = pending.Result!;
                    _logger.Information("Smart jump to scheme: {Uri}", uri);
                }
                else
                {
                    _logger.Information("Smart jump timeout/none, fallback to browser: {Url}", uri);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Smart jump failed, fallback to browser");
        }

        LinkLauncher.Launch(uri, _logger);
    }

    /// <summary>提示卡出现即后台开始嗅探邀请页（限时 1.5s，失败静默）</summary>
    private void StartSmartResolve(LinkSuggestion suggestion)
    {
        if (!_settings.LinkSmartJump)
        {
            return;
        }

        if (!suggestion.LaunchUri.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (suggestion.Category is not (LinkCategory.Meeting or LinkCategory.Remote or LinkCategory.CloudDisk))
        {
            return;
        }

        lock (_resolves)
        {
            if (!_resolves.ContainsKey(suggestion.LaunchUri))
            {
                _resolves[suggestion.LaunchUri] =
                    SmartLinkResolver.ResolveAsync(suggestion.LaunchUri, _logger);
            }
        }
    }

    private bool IsCategoryEnabled(LinkCategory category) => category switch
    {
        LinkCategory.Meeting => _settings.LinkCatMeeting,
        LinkCategory.Remote => _settings.LinkCatRemote,
        LinkCategory.CloudDisk => _settings.LinkCatCloud,
        LinkCategory.Download => _settings.LinkCatDownload,
        LinkCategory.Media => _settings.LinkCatMedia,
        LinkCategory.Web => _settings.LinkCatWeb,
        LinkCategory.Other => _settings.LinkCatOther,
        _ => true
    };

    /// <summary>同链接去重（按设置时长）；网页类额外按域名 5 分钟去重</summary>
    private bool IsDuplicate(LinkSuggestion suggestion)
    {
        var now = DateTime.UtcNow;
        var window = TimeSpan.FromMinutes(Math.Max(0, _settings.LinkDedupMinutes));

        // 顺手清理过期记录（仅内存，规模很小）
        foreach (var key in _recent.Where(kv => now - kv.Value > TimeSpan.FromMinutes(60)).Select(kv => kv.Key).ToList())
        {
            _recent.Remove(key);
        }

        if (window > TimeSpan.Zero && _recent.TryGetValue(suggestion.LaunchUri, out var last) && now - last < window)
        {
            return true;
        }

        if (suggestion.Category == LinkCategory.Web)
        {
            try
            {
                var host = new Uri(suggestion.LaunchUri).Host;
                if (_recent.TryGetValue("host:" + host, out var hostLast) && now - hostLast < WebDomainDedupWindow)
                {
                    return true;
                }

                _recent["host:" + host] = now;
            }
            catch
            {
                // host 解析失败按全链接去重即可
            }
        }

        _recent[suggestion.LaunchUri] = now;
        return false;
    }

    public void Dispose() => _monitor.Dispose();
}
