namespace QuickPalette.Domain.Services;

using QuickPalette.Domain.Contracts;
using QuickPalette.Domain.Models;

/// <summary>
/// 搜索引擎 —— 名称/别名/拼音匹配 + 分层排序。
/// 排序规则：匹配维度分层绝对主导（精确 &gt; 前缀 &gt; 子串 &gt; 拼音全拼 &gt; 拼音首字母 &gt; 别名/标签），
/// 收藏/使用频率/最近使用（指数衰减）只决定层内次序，绝不跨层反超。
/// 空输入 = 收藏 + 最近使用（MatchDimension.None）。
/// </summary>
public class SearchEngine : ISearchProvider
{
    /// <summary>最近使用衰减半衰期（天）</summary>
    private const double RecencyHalfLifeDays = 7.0;

    /// <summary>最近使用加分上限</summary>
    private const double RecencyMaxBonus = 50.0;

    private int _maxResults;

    public string Name => "DefaultSearch";

    public SearchEngine(int maxResults = 10)
    {
        _maxResults = maxResults > 0 ? maxResults : 10;
    }

    /// <summary>最大结果条数（&gt;0 校验；设置页保存后可热更新）</summary>
    public int MaxResults
    {
        get => _maxResults;
        set => _maxResults = value > 0 ? value : 10;
    }

    /// <summary>
    /// 执行搜索。query 为 null/空白时返回收藏 + 最近使用；pool 为 null/含 null 不抛异常。
    /// </summary>
    public IEnumerable<SearchHit> Match(string? query, IEnumerable<AppItem>? pool)
    {
        var items = (pool ?? Enumerable.Empty<AppItem>()).Where(static x => x is { Enabled: true }).ToList();
        if (string.IsNullOrWhiteSpace(query))
        {
            // 空输入：只出收藏 + 最近使用（设计 6.1；未使用过的不出）
            return items
                .Where(static x => x.Favorite || x.LastLaunched != null)
                .OrderByDescending(static x => x.Favorite)
                .ThenByDescending(static x => x.LastLaunched)
                .ThenBy(static x => x.Name, StringComparer.Ordinal)
                .Take(_maxResults)
                .Select(static x => new SearchHit
                {
                    Item = x,
                    Score = x.Favorite ? 200 : 100,
                    Dimension = MatchDimension.None
                });
        }

        var normalizedQuery = Normalize(query!);
        if (normalizedQuery.Length == 0) return Enumerable.Empty<SearchHit>();

        var hits = new List<SearchHit>();
        foreach (var item in items)
        {
            var hit = MatchItem(normalizedQuery, item);
            if (hit != null) hits.Add(hit);
        }

        // 分层排序：维度层级升序（数值越小越优）→ 层内分降序 → SortWeight 降序 → 名称稳定序
        return hits
            .OrderBy(static x => (int)x.Dimension)
            .ThenByDescending(static x => x.Score)
            .ThenByDescending(static x => x.Item.SortWeight)
            .ThenBy(static x => x.Item.Name ?? string.Empty, StringComparer.Ordinal)
            .Take(_maxResults);
    }

    private SearchHit? MatchItem(string query, AppItem item)
    {
        var name = Normalize(item.Name ?? string.Empty);
        var displayName = Normalize(item.DisplayName ?? string.Empty);

        // 名称与显示名取更优档
        var best = MatchField(query, name, exact: 1000, prefix: 800, substring: 600);
        if (displayName.Length > 0 && displayName != name)
        {
            var alt = MatchField(query, displayName, exact: 1000, prefix: 800, substring: 600);
            if (alt != null && (best == null || alt.Value.Tier < best.Value.Tier))
                best = alt;
        }

        MatchDimension? dimension = best?.Tier switch
        {
            FileTier.Exact => MatchDimension.NameExact,
            FileTier.Prefix => MatchDimension.NamePrefix,
            FileTier.Substring => MatchDimension.NameSubstring,
            _ => null
        };
        var baseScore = best?.Score ?? 0;

        // 拼音（含多音字候选，命中任一即可）
        if (dimension == null && !string.IsNullOrEmpty(item.Pinyin))
        {
            foreach (var variant in item.Pinyin.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (variant.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    dimension = MatchDimension.PinyinFull;
                    baseScore = 400;
                    break;
                }
            }
        }

        if (dimension == null && !string.IsNullOrEmpty(item.PinyinInitials))
        {
            foreach (var variant in item.PinyinInitials.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (variant.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    dimension = MatchDimension.PinyinInitials;
                    baseScore = 300;
                    break;
                }
            }
        }

        // 别名/标签：精确优于子串（层内分拉开）
        if (dimension == null)
        {
            var aliasScore = MatchList(query, item.Aliases, item.Tags);
            if (aliasScore > 0)
            {
                dimension = MatchDimension.AliasOrTag;
                baseScore = aliasScore;
            }
        }

        if (dimension == null) return null;

        return new SearchHit
        {
            Item = item,
            Score = baseScore + GetUsageBonus(item),
            Dimension = dimension.Value
        };
    }

    private enum FileTier
    {
        Exact = 0,
        Prefix = 1,
        Substring = 2
    }

    private static (FileTier Tier, double Score)? MatchField(string query, string field, int exact, int prefix, int substring)
    {
        if (field.Length == 0) return null;
        if (field.Equals(query, StringComparison.OrdinalIgnoreCase)) return (FileTier.Exact, exact);
        if (field.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return (FileTier.Prefix, prefix);
        if (field.Contains(query, StringComparison.OrdinalIgnoreCase)) return (FileTier.Substring, substring);
        return null;
    }

    private static double MatchList(string query, List<string>? aliases, List<string>? tags)
    {
        var best = 0.0;
        if (aliases != null)
        {
            foreach (var a in aliases)
            {
                if (string.IsNullOrEmpty(a)) continue;
                var normalized = Normalize(a);
                if (normalized.Equals(query, StringComparison.OrdinalIgnoreCase))
                    best = Math.Max(best, 250); // 别名精确
                else if (normalized.Contains(query, StringComparison.OrdinalIgnoreCase))
                    best = Math.Max(best, 200); // 别名子串
            }
        }

        if (tags != null)
        {
            foreach (var t in tags)
            {
                if (string.IsNullOrEmpty(t)) continue;
                var normalized = Normalize(t);
                if (normalized.Equals(query, StringComparison.OrdinalIgnoreCase))
                    best = Math.Max(best, 250);
                else if (normalized.Contains(query, StringComparison.OrdinalIgnoreCase))
                    best = Math.Max(best, 200);
            }
        }

        return best;
    }

    /// <summary>
    /// 层内加分：收藏 +100、使用次数 ×2（封顶 60）、最近使用指数衰减（封顶 50）。
    /// 加分总上限 210，任何情况下都跨不过一层维度差（层间分差 ≥ 100…实际上按维度分层排序，加分根本不参与跨层比较）。
    /// </summary>
    private static double GetUsageBonus(AppItem item)
    {
        var bonus = item.Favorite ? 100.0 : 0.0;
        bonus += Math.Min(item.LaunchCount * 2, 60);
        bonus += GetRecencyBonus(item.LastLaunched);
        return Math.Min(bonus, 210);
    }

    /// <summary>
    /// 最近使用加分：指数衰减 50·exp(-天数/半衰期)，天数钳制 ≥0（防未来时间戳）
    /// </summary>
    private static double GetRecencyBonus(DateTime? lastLaunched)
    {
        if (!lastLaunched.HasValue) return 0;
        var days = Math.Max(0, (DateTime.UtcNow - lastLaunched.Value.ToUniversalTime()).TotalDays);
        return RecencyMaxBonus * Math.Exp(-days / RecencyHalfLifeDays);
    }

    /// <summary>
    /// 查询归一化：Trim + 小写 + 全角转半角（ＡＢＣ１２３→abc123，全角空格→空格）
    /// </summary>
    private static string Normalize(string text) => QueryNormalizer.Normalize(text);
}
