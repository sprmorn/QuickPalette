namespace QuickPalette.Plugins.Sdk.Samples;

using QuickPalette.Domain.Contracts;
using QuickPalette.Domain.Models;

/// <summary>
/// 最小示例插件骨架：注册一个"当前时间"搜索提供器。
/// 仅供插件作者参考格式与生命周期；V1 不实际加载（加载器 V3 启用）。
/// 打包步骤：
///   1. 新建类库项目，引用 QuickPalette.Plugins.Sdk（传递引用 Domain）；
///   2. 实现 IQuickPalettePlugin（本文件即示例）；
///   3. 编写 plugin.json（见 PluginManifest 注释），与 dll 一起放入 plugins\&lt;id&gt;\。
/// </summary>
public sealed class SamplePlugin : IQuickPalettePlugin
{
    private IDisposable? _subscription;

    public string Name => "示例插件";

    public string Version => "1.0.0";

    /// <summary>插件加载：注册扩展点、订阅事件</summary>
    public void OnLoad(IPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.RegisterSearchProvider(new TimeSearchProvider());
        _subscription = null; // IPluginContext.Subscribe 为回调式；句柄式订阅见 EventBus.Subscribe

        // 插件私有数据目录（自动创建）
        var dataFile = context.GetDataPath("sample.dat");
        _ = dataFile;
    }

    /// <summary>插件卸载：释放资源、退订</summary>
    public void OnUnload()
    {
        _subscription?.Dispose();
        _subscription = null;
    }

    /// <summary>示例搜索提供器：查询 "time" 返回当前时间项</summary>
    private sealed class TimeSearchProvider : ISearchProvider
    {
        public string Name => "SampleTimeSearch";

        public IEnumerable<SearchHit> Match(string query, IEnumerable<AppItem> pool)
        {
            if (!string.Equals(query?.Trim(), "time", StringComparison.OrdinalIgnoreCase))
                return Enumerable.Empty<SearchHit>();

            var item = new AppItem
            {
                Name = $"当前时间 {DateTime.Now:HH:mm:ss}",
                Type = AppType.Command
            };
            return new[]
            {
                new SearchHit { Item = item, Score = 100, Dimension = MatchDimension.AliasOrTag }
            };
        }
    }
}
