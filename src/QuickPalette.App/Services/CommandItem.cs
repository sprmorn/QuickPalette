namespace QuickPalette.App.Services;

/// <summary>
/// 命令模式（"&gt;" 开头）的单条命令数据源。
/// 命令以独立数据源渲染进结果列表（复用 ResultDisplayItem 管线），可上下选择回车执行。
/// </summary>
public sealed class CommandItem
{
    /// <summary>命令名（如 "settings"）</summary>
    public required string Name { get; init; }

    /// <summary>用法（如 "&gt;theme dark|light|system"）</summary>
    public required string Usage { get; init; }

    /// <summary>命令说明（已本地化）</summary>
    public required string Description { get; init; }

    /// <summary>执行体；入参为命令参数串（如 "dark"）</summary>
    public required Action<string> Execute { get; init; }

    /// <summary>执行后是否自动隐藏面板（弹窗类命令隐藏，切主题/刷新类保留面板）</summary>
    public bool CloseAfterExecute { get; init; } = true;
}
