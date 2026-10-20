namespace QuickPalette.Domain.Models;

/// <summary>
/// 应用白名单项 - 核心实体
/// </summary>
public class AppItem
{
    /// <summary>
    /// UUID，跨设备导入的唯一标识
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// 显示名称（如：微信）
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 英文显示名称（如：WeChat）
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// 别名列表（如：["wx", "wechat"]）
    /// </summary>
    public List<string> Aliases { get; set; } = new();

    /// <summary>
    /// 拼音全拼（如：weixin）
    /// </summary>
    public string Pinyin { get; set; } = string.Empty;

    /// <summary>
    /// 拼音首字母（如：wx）
    /// </summary>
    public string PinyinInitials { get; set; } = string.Empty;

    /// <summary>
    /// 应用类型
    /// </summary>
    public AppType Type { get; set; } = AppType.Exe;

    /// <summary>
    /// 可执行文件路径或目标路径
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// 启动参数
    /// </summary>
    public string Args { get; set; } = string.Empty;

    /// <summary>
    /// 工作目录
    /// </summary>
    public string WorkDir { get; set; } = string.Empty;

    /// <summary>
    /// UWP 应用用户模型 ID（type=uwp 时使用）
    /// </summary>
    public string Aumid { get; set; } = string.Empty;

    /// <summary>
    /// 图标文件路径（相对于 icons 目录）
    /// </summary>
    public string IconPath { get; set; } = string.Empty;

    /// <summary>
    /// 来源
    /// </summary>
    public AppSource Source { get; set; } = AppSource.Manual;

    /// <summary>
    /// 标签列表（如：["社交"]）
    /// </summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>
    /// 是否收藏
    /// </summary>
    public bool Favorite { get; set; }

    /// <summary>
    /// 启动次数
    /// </summary>
    public int LaunchCount { get; set; }

    /// <summary>
    /// 最后启动时间
    /// </summary>
    public DateTime? LastLaunched { get; set; }

    /// <summary>
    /// 是否以管理员身份运行
    /// </summary>
    public bool RunAsAdmin { get; set; }

    /// <summary>
    /// 是否启用（false = 隐藏，不出现在任何列表）
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 手动排序权重
    /// </summary>
    public int SortWeight { get; set; }

    /// <summary>
    /// 扩展字段：插件可写入任意业务数据
    /// </summary>
    public Dictionary<string, object> CustomFields { get; set; } = new();

    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 更新时间
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Schema 版本号（写入时记录，迁移引擎据此升级）
    /// </summary>
    public int SchemaVersion { get; set; } = 1;
}

/// <summary>
/// 应用类型枚举
/// </summary>
public enum AppType
{
    /// <summary>可执行文件</summary>
    Exe,
    /// <summary>快捷方式</summary>
    Lnk,
    /// <summary>UWP/Store 应用</summary>
    Uwp,
    /// <summary>网页链接</summary>
    Url,
    /// <summary>文件夹</summary>
    Folder,
    /// <summary>命令行</summary>
    Command
}

/// <summary>
/// 应用来源枚举
/// </summary>
public enum AppSource
{
    /// <summary>手动添加</summary>
    Manual,
    /// <summary>开始菜单</summary>
    StartMenu,
    /// <summary>注册表</summary>
    Registry,
    /// <summary>运行中进程</summary>
    Process,
    /// <summary>UWP 应用</summary>
    Uwp,
    /// <summary>导入</summary>
    Import
}
