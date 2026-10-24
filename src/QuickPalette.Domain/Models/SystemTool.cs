namespace QuickPalette.Domain.Models;

using QuickPalette.Domain.Contracts;

/// <summary>系统组件分类</summary>
public enum SystemToolCategory
{
    /// <summary>经典系统工具（注册表编辑器、任务管理器、设备管理器……）</summary>
    Tool,

    /// <summary>控制面板项（appwiz.cpl、control /name Microsoft.Xxx……）</summary>
    ControlPanel,

    /// <summary>MMC 管理控制台（services.msc、devmgmt.msc……）</summary>
    MscConsole,

    /// <summary>Windows 设置页（ms-settings: URI）</summary>
    Settings,

    /// <summary>系统特殊文件夹（explorer.exe shell:Downloads……）</summary>
    ShellFolder
}

/// <summary>
/// Windows 系统组件条目（注册表编辑器、任务管理器、设备管理器、控制面板项、
/// MMC 控制台、设置页、系统文件夹等），静态内置目录，供搜索直达。
/// </summary>
public sealed class SystemTool
{
    /// <summary>唯一标识（小写英文短名，如 "regedit"、"devmgmt"、"settings.display"）</summary>
    public required string Id { get; init; }

    /// <summary>中文名（微软官方简体名称）</summary>
    public required string Name { get; init; }

    /// <summary>英文名（英文系统显示名 / 命令名）</summary>
    public string NameEn { get; init; } = string.Empty;

    /// <summary>
    /// 启动命令：可为 URI（ms-settings:xxx）或 命令行（支持 %SystemRoot% 等环境变量，
    /// 如 "%SystemRoot%\System32\devmgmt.msc"、"control.exe /name Microsoft.System"）。
    /// </summary>
    public required string Launch { get; init; }

    /// <summary>分类</summary>
    public SystemToolCategory Category { get; init; } = SystemToolCategory.Tool;

    /// <summary>是否通常需要管理员权限（UAC）</summary>
    public bool RequiresAdmin { get; init; }

    /// <summary>图标来源文件（可选，如 "%SystemRoot%\System32\mmsys.cpl"；缺省按启动命令推导）</summary>
    public string IconFile { get; init; } = string.Empty;

    /// <summary>别名/俗称/关键词（如 "注册表"、"gpedit"、"组策略"）</summary>
    public List<string> Keywords { get; init; } = new();
}

/// <summary>系统组件搜索命中</summary>
public sealed class SystemToolHit
{
    /// <summary>命中的系统组件</summary>
    public required SystemTool Tool { get; init; }

    /// <summary>匹配分数（越高越靠前；仅层内比较）</summary>
    public double Score { get; init; }

    /// <summary>匹配维度</summary>
    public MatchDimension Dimension { get; init; }
}
