namespace QuickPalette.Plugins.Sdk;

using System.Text.Json.Serialization;

/// <summary>
/// 插件清单（plugin.json 的强类型映射）。
/// 打包约定：插件目录 = `<数据目录>\plugins\<PluginId>\`，含
///   plugin.json   —— 本清单
///   *.dll         —— 实现 IQuickPalettePlugin 的程序集
/// V1 只落契约与 SDK，加载器在 V3 启用（届时校验 MinAppVersion 与 Permissions）。
/// </summary>
public class PluginManifest
{
    /// <summary>插件唯一标识（小写、点分命名，如 "com.contoso.clipboard"）</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>显示名称</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>语义化版本（如 "1.0.0"）</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = "0.0.0";

    /// <summary>功能描述</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>作者</summary>
    [JsonPropertyName("author")]
    public string? Author { get; set; }

    /// <summary>入口程序集文件名（如 "Contoso.Clipboard.dll"）</summary>
    [JsonPropertyName("entryAssembly")]
    public string EntryAssembly { get; set; } = string.Empty;

    /// <summary>入口类型全名（实现 IQuickPalettePlugin，如 "Contoso.Clipboard.ClipboardPlugin"）</summary>
    [JsonPropertyName("entryType")]
    public string EntryType { get; set; } = string.Empty;

    /// <summary>
    /// 权限声明（加载器据此限制能力）：
    /// search / source / launcher / filesystem / network
    /// </summary>
    [JsonPropertyName("permissions")]
    public List<string> Permissions { get; set; } = new();

    /// <summary>要求的最低宿主版本</summary>
    [JsonPropertyName("minAppVersion")]
    public string MinAppVersion { get; set; } = "1.0.0";

    /// <summary>扩展字段（前向兼容）</summary>
    [JsonPropertyName("custom")]
    public Dictionary<string, object?>? Custom { get; set; }
}
