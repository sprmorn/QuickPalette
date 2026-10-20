namespace QuickPalette.Domain.Contracts;

/// <summary>
/// 应用来源接口 - 新增数据源只需实现此接口
/// </summary>
public interface IAppSource
{
    /// <summary>
    /// 来源标识（如："StartMenu", "Registry"）
    /// </summary>
    string SourceId { get; }

    /// <summary>
    /// 扫描并返回原始应用信息
    /// </summary>
    Task<IEnumerable<RawApp>> ScanAsync(CancellationToken ct = default);
}

/// <summary>
/// 扫描到的原始应用信息（未进入白名单）
/// </summary>
public class RawApp
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Args { get; set; } = string.Empty;
    public string WorkDir { get; set; } = string.Empty;
    public string IconPath { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;

    /// <summary>UWP 应用标识（PackageFamilyName!ApplicationId），非 UWP 为 null</summary>
    public string? Aumid { get; set; }

    /// <summary>应用类型（Exe/Lnk/Uwp/Url/Command）</summary>
    public QuickPalette.Domain.Models.AppType Type { get; set; }

    /// <summary>标签（可空）</summary>
    public List<string>? Tags { get; set; }

    /// <summary>拼音全拼（入库时由 PinyinProvider 填充）</summary>
    public string? Pinyin { get; set; }

    /// <summary>拼音首字母（入库时由 PinyinProvider 填充）</summary>
    public string? PinyinInitials { get; set; }
}
