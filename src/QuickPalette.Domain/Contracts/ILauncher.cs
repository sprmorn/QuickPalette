namespace QuickPalette.Domain.Contracts;

using QuickPalette.Domain.Models;

/// <summary>
/// 启动器接口 - 新增类型只需实现此接口
/// </summary>
public interface ILauncher
{
    /// <summary>
    /// 是否能启动该应用
    /// </summary>
    bool CanLaunch(AppItem item);

    /// <summary>
    /// 启动应用
    /// </summary>
    Task<LaunchResult> LaunchAsync(AppItem item, LaunchOptions? options = null);
}

/// <summary>
/// 启动选项
/// </summary>
public class LaunchOptions
{
    /// <summary>
    /// 以管理员身份运行
    /// </summary>
    public bool RunAsAdmin { get; set; }

    /// <summary>
    /// 打开所在目录而不是启动
    /// </summary>
    public bool OpenDirectory { get; set; }
}

/// <summary>
/// 启动失败分类（用于"路径失效→重新定位"等引导）
/// </summary>
public enum LaunchErrorKind
{
    /// <summary>无错误</summary>
    None = 0,
    /// <summary>目标不存在（路径失效）</summary>
    NotFound = 1,
    /// <summary>拒绝访问（权限不足）</summary>
    AccessDenied = 2,
    /// <summary>路径/参数非法</summary>
    InvalidPath = 3,
    /// <summary>被安全策略阻止</summary>
    Blocked = 4,
    /// <summary>不支持的类型/协议</summary>
    Unsupported = 5,
    /// <summary>其他失败</summary>
    Failed = 6,
}

/// <summary>
/// 启动结果
/// </summary>
public class LaunchResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public int? ProcessId { get; set; }

    /// <summary>失败分类（成功时为 None）</summary>
    public LaunchErrorKind ErrorKind { get; set; } = LaunchErrorKind.None;

    public static LaunchResult Ok(int? pid = null) => new() { Success = true, ProcessId = pid };
    public static LaunchResult Fail(string error, LaunchErrorKind kind = LaunchErrorKind.Failed)
        => new() { Success = false, ErrorMessage = error, ErrorKind = kind };
}
