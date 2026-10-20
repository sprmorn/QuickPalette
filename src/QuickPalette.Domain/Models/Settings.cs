namespace QuickPalette.Domain.Models;

/// <summary>
/// 应用配置（分层配置：默认 < 用户 < 便携覆盖）
/// </summary>
public class Settings
{
    /// <summary>
    /// 全局热键（默认 Ctrl+Alt+Space）
    /// </summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+Space";

    /// <summary>
    /// 开机自启
    /// </summary>
    public bool AutoStart { get; set; }

    /// <summary>
    /// 主题（dark/light/system，默认跟随系统）
    /// </summary>
    public string Theme { get; set; } = "system";

    /// <summary>
    /// 最大结果条数
    /// </summary>
    public int MaxResults { get; set; } = 10;

    /// <summary>
    /// 本地文件搜索（Everything 式，需管理员权限的索引 Worker）
    /// </summary>
    public bool FileSearchEnabled { get; set; } = true;

    /// <summary>
    /// 文档预览：单击文档结果时在面板旁弹出预览窗口（关闭后单击仅选中）
    /// </summary>
    public bool PreviewEnabled { get; set; } = true;

    /// <summary>
    /// DeepSeek 网络搜索快捷模式（输入 DS 后按 Tab 进入；需登录 DeepSeek 账号才能使用网络搜索）
    /// </summary>
    public bool DeepSeekSearchEnabled { get; set; } = true;

    /// <summary>
    /// 启用的候选来源
    /// </summary>
    public List<string> ScanSources { get; set; } = new()
    {
        "StartMenu",
        "Registry",
        "Uwp",
        "Process"
    };

    /// <summary>
    /// 过滤关键词黑名单（含设计 5.2 全清单）
    /// </summary>
    public List<string> FilterKeywords { get; set; } = new()
    {
        "uninstall", "卸载", "modify", "repair", "update", "updater",
        "setup", "install", "helper", "crash", "report", "telemetry",
        "readme", "documentation", "service", "daemon",
        "launcher", "patch", "redist", "vcredist", "unins000"
    };

    /// <summary>
    /// 过滤路径黑名单
    /// </summary>
    public List<string> FilterPaths { get; set; } = new()
    {
        @"C:\Windows\System32",
        @"C:\Windows\SysWOW64",
        @"C:\Windows\WinSxS",
        @"C:\Windows\Microsoft.NET"
    };

    /// <summary>
    /// 允许运行中捕获
    /// </summary>
    public bool AutoCaptureEnabled { get; set; } = true;

    /// <summary>
    /// 自动备份周期（天）
    /// </summary>
    public int BackupIntervalDays { get; set; } = 7;

    /// <summary>
    /// 匿名统计（默认关闭）
    /// </summary>
    public bool TelemetryEnabled { get; set; }

    /// <summary>
    /// 永不过滤白名单（名称或路径命中即放行，优先于一切过滤规则）
    /// </summary>
    public List<string> Whitelist { get; set; } = new();

    /// <summary>
    /// 过滤 WindowsApps 明显系统组件（设置、反馈中心等）
    /// </summary>
    public bool FilterSystemComponents { get; set; } = true;

    /// <summary>
    /// 语言
    /// </summary>
    public string Locale { get; set; } = "zh-CN";

    /// <summary>
    /// Schema 版本号
    /// </summary>
    public int SchemaVersion { get; set; } = 1;
}
