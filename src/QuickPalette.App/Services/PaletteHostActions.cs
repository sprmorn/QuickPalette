namespace QuickPalette.App.Services;

using QuickPalette.Domain.Models;

/// <summary>
/// 命令模式/主面板需要的宿主能力（由 App 注入）。
/// 集中在这里是为了让 MainWindow 不直接依赖 App 的生命周期细节。
/// </summary>
public sealed class PaletteHostActions
{
    /// <summary>打开设置窗口（设置保存后由 App 负责重新应用主题/MaxResults 等）</summary>
    public required Action OpenSettings { get; init; }

    /// <summary>打开"添加应用"窗口（可预填；保存入库与刷新由 App 负责）</summary>
    public required Action<AppItem?> ShowAddApp { get; init; }

    /// <summary>切换并持久化主题（dark / light / system）</summary>
    public required Action<string> SwitchTheme { get; init; }

    /// <summary>立即备份数据</summary>
    public required Action BackupNow { get; init; }

    /// <summary>捕获当前前台窗口进程并预填入库</summary>
    public required Action CaptureCurrentWindow { get; init; }

    /// <summary>文档预览开关是否启用（设置中可关闭；关闭后单击文档仅选中）</summary>
    public required Func<bool> IsPreviewEnabled { get; init; }

    /// <summary>DeepSeek 网络搜索快捷模式是否启用（设置中可关闭；关闭后 DS+Tab 不触发）</summary>
    public required Func<bool> IsDeepSeekSearchEnabled { get; init; }
}
