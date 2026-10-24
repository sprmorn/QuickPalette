namespace QuickPalette.Domain.Services;

using QuickPalette.Domain.Contracts;
using QuickPalette.Domain.Models;

/// <summary>
/// 系统组件搜索 —— 中文名/英文名/别名/拼音（全拼 + 首字母）匹配，分层与
/// <see cref="SearchEngine"/> 口径一致：精确 &gt; 前缀 &gt; 子串 &gt; 拼音全拼 &gt; 首字母 &gt; 别名。
/// 空输入不返回结果（系统组件只在有查询时直达，不进空态收藏/最近）。
/// </summary>
public sealed class SystemToolSearch
{
    /// <summary>单个条目生成的拼音候选数量上限（防多音字组合爆炸）</summary>
    private const int MaxPinyinVariants = 4;

    /// <summary>归一化后的检索索引（构造时一次性建好，查询零分配热点小）</summary>
    private sealed record Indexed(
        SystemTool Tool,
        string Name,
        string NameEn,
        List<string> Keywords,
        List<string> PinyinFull,
        List<string> PinyinInitials);

    private readonly List<Indexed> _items;

    public SystemToolSearch(IEnumerable<SystemTool>? tools)
    {
        _items = (tools ?? Enumerable.Empty<SystemTool>())
            .Where(static t => t != null && !string.IsNullOrWhiteSpace(t.Name))
            .Select(Index)
            .ToList();
    }

    /// <summary>
    /// 执行匹配。maxResults ≤ 0 时按 10 处理；命中按维度分层 + 层内分稳定排序。
    /// </summary>
    public IReadOnlyList<SystemToolHit> Match(string? query, int maxResults = 10)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<SystemToolHit>();

        var normalized = QueryNormalizer.Normalize(query);
        if (normalized.Length == 0) return Array.Empty<SystemToolHit>();

        var limit = maxResults > 0 ? maxResults : 10;
        var hits = new List<SystemToolHit>();
        foreach (var entry in _items)
        {
            var hit = MatchItem(normalized, entry);
            if (hit != null) hits.Add(hit);
        }

        return hits
            .OrderBy(static x => (int)x.Dimension)
            .ThenByDescending(static x => x.Score)
            .ThenBy(static x => x.Tool.Name, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
    }

    private static SystemToolHit? MatchItem(string query, Indexed entry)
    {
        // 名称（中文名 / 英文名）取更优档
        var best = MatchField(query, entry.Name, 1000, 800, 600);
        if (entry.NameEn.Length > 0 && entry.NameEn != entry.Name)
        {
            var alt = MatchField(query, entry.NameEn, 1000, 800, 600);
            if (alt != null && (best == null || alt.Value.Tier < best.Value.Tier))
                best = alt;
        }

        MatchDimension? dimension = best?.Tier switch
        {
            Tier.Exact => MatchDimension.NameExact,
            Tier.Prefix => MatchDimension.NamePrefix,
            Tier.Substring => MatchDimension.NameSubstring,
            _ => null
        };
        var score = best?.Score ?? 0;

        // 拼音全拼 / 首字母（多音字候选命中任一即可）
        if (dimension == null)
        {
            foreach (var variant in entry.PinyinFull)
            {
                if (variant.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    dimension = MatchDimension.PinyinFull;
                    score = 400;
                    break;
                }
            }
        }

        if (dimension == null)
        {
            foreach (var variant in entry.PinyinInitials)
            {
                if (variant.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    dimension = MatchDimension.PinyinInitials;
                    score = 300;
                    break;
                }
            }
        }

        // 别名/关键词：精确优于子串
        if (dimension == null)
        {
            foreach (var kw in entry.Keywords)
            {
                if (kw.Equals(query, StringComparison.OrdinalIgnoreCase))
                {
                    dimension = MatchDimension.AliasOrTag;
                    score = 250;
                    break;
                }

                if (kw.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    dimension = MatchDimension.AliasOrTag;
                    score = 200;
                }
            }
        }

        if (dimension == null) return null;

        return new SystemToolHit { Tool = entry.Tool, Score = score, Dimension = dimension.Value };
    }

    private enum Tier
    {
        Exact = 0,
        Prefix = 1,
        Substring = 2
    }

    private static (Tier Tier, double Score)? MatchField(string query, string field, double exact, double prefix, double substring)
    {
        if (field.Length == 0) return null;
        if (field.Equals(query, StringComparison.OrdinalIgnoreCase)) return (Tier.Exact, exact);
        if (field.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return (Tier.Prefix, prefix);
        if (field.Contains(query, StringComparison.OrdinalIgnoreCase)) return (Tier.Substring, substring);
        return null;
    }

    private static Indexed Index(SystemTool tool)
    {
        var pinyinSource = tool.Name; // 中文名生成拼音（含多音字候选）
        return new Indexed(
            tool,
            QueryNormalizer.Normalize(tool.Name),
            QueryNormalizer.Normalize(tool.NameEn),
            tool.Keywords
                .Where(static k => !string.IsNullOrWhiteSpace(k))
                .Select(QueryNormalizer.Normalize)
                .ToList(),
            PinyinProvider.Instance.FullPinyinVariants(pinyinSource, MaxPinyinVariants)
                .Select(QueryNormalizer.Normalize)
                .Where(static p => p.Length > 0)
                .ToList(),
            PinyinProvider.Instance.InitialVariants(pinyinSource, MaxPinyinVariants)
                .Select(QueryNormalizer.Normalize)
                .Where(static p => p.Length > 0)
                .ToList());
    }
}
