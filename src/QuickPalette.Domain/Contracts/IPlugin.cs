namespace QuickPalette.Domain.Contracts;

/// <summary>
/// 插件生命周期接口（V3 启用，V1 只定义）
/// </summary>
public interface IQuickPalettePlugin
{
    /// <summary>
    /// 插件名称
    /// </summary>
    string Name { get; }

    /// <summary>
    /// 插件版本
    /// </summary>
    string Version { get; }

    /// <summary>
    /// 插件加载时调用
    /// </summary>
    void OnLoad(IPluginContext context);

    /// <summary>
    /// 插件卸载时调用
    /// </summary>
    void OnUnload();
}

/// <summary>
/// 插件上下文（提供宿主能力）
/// </summary>
public interface IPluginContext
{
    /// <summary>
    /// 注册搜索提供器
    /// </summary>
    void RegisterSearchProvider(ISearchProvider provider);

    /// <summary>
    /// 注册应用来源
    /// </summary>
    void RegisterAppSource(IAppSource source);

    /// <summary>
    /// 注册启动器
    /// </summary>
    void RegisterLauncher(ILauncher launcher);

    /// <summary>
    /// 获取数据目录路径
    /// </summary>
    string GetDataPath(string relativePath);

    /// <summary>
    /// 订阅事件
    /// </summary>
    void Subscribe<TEvent>(Action<TEvent> handler);
}
