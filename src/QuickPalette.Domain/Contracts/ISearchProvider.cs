namespace QuickPalette.Domain.Contracts;

using QuickPalette.Domain.Models;

/// <summary>
/// 搜索提供器接口 - 插件可注册自定义匹配器
/// </summary>
public interface ISearchProvider
{
    /// <summary>
    /// 提供器名称
    /// </summary>
    string Name { get; }

    /// <summary>
    /// 执行搜索匹配
    /// </summary>
    IEnumerable<SearchHit> Match(string query, IEnumerable<AppItem> pool);
}

/// <summary>
/// 搜索命中结果
/// </summary>
public class SearchHit
{
    /// <summary>
    /// 命中的应用
    /// </summary>
    public required AppItem Item { get; init; }

    /// <summary>
    /// 匹配分数（越高越靠前）
    /// </summary>
    public double Score { get; init; }

    /// <summary>
    /// 匹配维度
    /// </summary>
    public MatchDimension Dimension { get; init; }
}

/// <summary>
/// 匹配维度
/// </summary>
public enum MatchDimension
{
    /// <summary>名称精确匹配</summary>
    NameExact,
    /// <summary>名称前缀匹配</summary>
    NamePrefix,
    /// <summary>名称子串匹配</summary>
    NameSubstring,
    /// <summary>拼音全拼</summary>
    PinyinFull,
    /// <summary>拼音首字母</summary>
    PinyinInitials,
    /// <summary>别名/标签</summary>
    AliasOrTag,

    /// <summary>无匹配维度（空输入场景：收藏 + 最近使用）</summary>
    None
}
