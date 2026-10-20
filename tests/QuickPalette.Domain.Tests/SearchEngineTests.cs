namespace QuickPalette.Domain.Tests;

using QuickPalette.Domain.Contracts;
using QuickPalette.Domain.Models;
using QuickPalette.Domain.Services;
using Xunit;

/// <summary>
/// SearchEngine 排序/匹配/边界行为测试
/// </summary>
public class SearchEngineTests
{
    private static AppItem App(string name, Action<AppItem>? setup = null)
    {
        var item = new AppItem { Name = name };
        setup?.Invoke(item);
        return item;
    }

    // ---------- 空输入语义 ----------

    [Fact]
    public void EmptyQuery_ReturnsOnlyFavoritesAndRecent()
    {
        var engine = new SearchEngine(10);
        var favorite = App("收藏项", x => x.Favorite = true);
        var recent = App("最近项", x => x.LastLaunched = DateTime.UtcNow);
        var unused = App("从未使用");

        var hits = engine.Match("", new[] { unused, recent, favorite }).ToList();

        Assert.Equal(2, hits.Count);
        Assert.All(hits, h => Assert.Equal(MatchDimension.None, h.Dimension));
        Assert.Equal("收藏项", hits[0].Item.Name); // 收藏优先
    }

    [Fact]
    public void EmptyQuery_UsesMaxResultsDefault10()
    {
        var engine = new SearchEngine();
        var items = Enumerable.Range(0, 20)
            .Select(i => App($"应用{i}", x => { x.Favorite = true; x.SortWeight = 100 - i; }))
            .ToList();

        Assert.Equal(10, engine.Match("  ", items).Count());
    }

    // ---------- 维度分层（加分不得反超） ----------

    [Fact]
    public void HigherTier_AlwaysBeats_LowerTier_EvenWithHugeUsage()
    {
        var engine = new SearchEngine(10);
        var exact = App("code", x => x.Pinyin = "code");
        var pinyinHit = App("代码编辑器", x =>
        {
            x.Pinyin = "mycode";             // 拼音维度命中
            x.PinyinInitials = "dmbjq";
            x.LaunchCount = 100000;          // 极端使用量
            x.Favorite = true;
            x.LastLaunched = DateTime.UtcNow;
        });

        var hits = engine.Match("code", new[] { pinyinHit, exact }).ToList();

        Assert.Equal(2, hits.Count);
        Assert.Equal("code", hits[0].Item.Name);
        Assert.Equal(MatchDimension.NameExact, hits[0].Dimension);
        Assert.Equal(MatchDimension.PinyinFull, hits[1].Dimension);
    }

    [Fact]
    public void AliasExact_Beats_AliasSubstring_WithinTier()
    {
        var engine = new SearchEngine(10);
        var exact = App("应用甲", x => x.Aliases = new List<string> { "wx" });
        var substring = App("应用乙", x => x.Aliases = new List<string> { "wxwork" });

        var hits = engine.Match("wx", new[] { substring, exact }).ToList();

        Assert.Equal("应用甲", hits[0].Item.Name);
    }

    // ---------- 排序公式 ----------

    [Fact]
    public void Recency_ExponentialDecay_RecentRanksHigher()
    {
        var engine = new SearchEngine(10);
        var fresh = App("甲", x => x.LastLaunched = DateTime.UtcNow.AddDays(-1));
        var stale = App("乙", x => x.LastLaunched = DateTime.UtcNow.AddDays(-20));

        var hits = engine.Match("甲乙", new[] { stale, fresh })
            .OrderBy(h => h.Score).ToList(); // 只有各自精确命中……改为同名场景
        // 同一名称下比较：构造两个同名条目不合法，改用同一查询命中两者的子串
        var fresh2 = App("工具甲", x => x.LastLaunched = DateTime.UtcNow.AddDays(-1));
        var stale2 = App("工具乙", x => x.LastLaunched = DateTime.UtcNow.AddDays(-20));
        var result = engine.Match("工具", new[] { stale2, fresh2 }).ToList();

        Assert.Equal("工具甲", result[0].Item.Name);
    }

    [Fact]
    public void FutureTimestamp_IsClamped_NoThrow()
    {
        var engine = new SearchEngine(10);
        var future = App("未来项", x => x.LastLaunched = DateTime.UtcNow.AddDays(30));

        var hits = engine.Match("", new[] { future }).ToList();

        Assert.Single(hits); // 不抛异常、钳制后仍可返回
    }

    [Fact]
    public void SameScore_TieBreak_IsStable_BySortWeightThenName()
    {
        var engine = new SearchEngine(10);
        var b = App("app2", x => x.SortWeight = 1);
        var a = App("app1", x => x.SortWeight = 1);
        var c = App("app3", x => x.SortWeight = 5);

        var hits = engine.Match("app", new[] { b, a, c }).ToList();

        Assert.Equal(3, hits.Count);
        Assert.Equal("app3", hits[0].Item.Name); // SortWeight 高者在前
        Assert.Equal("app1", hits[1].Item.Name); // 同分按名称稳定序
        Assert.Equal("app2", hits[2].Item.Name);
    }

    // ---------- 拼音 ----------

    [Fact]
    public void PinyinFull_And_Initials_AreMatched()
    {
        var engine = new SearchEngine(10);
        var item = App("微信");
        PinyinProvider.Instance.EnsurePinyin(item);

        Assert.Contains("weixin", item.Pinyin);
        Assert.Contains("wx", item.PinyinInitials);

        var byFull = engine.Match("weixin", new[] { item }).ToList();
        var byInitial = engine.Match("wx", new[] { item }).ToList();

        Assert.Single(byFull);
        Assert.Equal(MatchDimension.PinyinFull, byFull[0].Dimension);
        Assert.Single(byInitial);
        Assert.Equal(MatchDimension.PinyinInitials, byInitial[0].Dimension);
    }

    [Fact]
    public void DisplayName_ParticipatesIn_Matching()
    {
        var engine = new SearchEngine(10);
        var item = App("微信", x => x.DisplayName = "WeChat");

        var hits = engine.Match("wechat", new[] { item }).ToList();

        Assert.Single(hits);
        Assert.Equal(MatchDimension.NameExact, hits[0].Dimension);
    }

    // ---------- 归一化与防护 ----------

    [Fact]
    public void FullWidth_Query_Matches_HalfWidth_Name()
    {
        var engine = new SearchEngine(10);
        var item = App("vscode");

        var hits = engine.Match("ＶＳＣＯＤＥ", new[] { item }).ToList();

        Assert.Single(hits);
    }

    [Fact]
    public void NullPool_And_NullFields_DoNotThrow()
    {
        var engine = new SearchEngine(10);
        var broken = new AppItem
        {
            Name = null!, // JSON 反序列化会产生 null
            Aliases = null!,
            Tags = null!,
            Pinyin = null!,
            PinyinInitials = null!,
            DisplayName = null!
        };

        Assert.Empty(engine.Match("abc", null!));
        Assert.Empty(engine.Match("abc", new AppItem?[] { null!, broken }));
    }

    [Fact]
    public void MaxResults_Truncates_And_SetterValidates()
    {
        var engine = new SearchEngine(2);
        var items = Enumerable.Range(0, 5).Select(i => App($"应用{i}")).ToList();

        Assert.Equal(2, engine.Match("应用", items).Count());

        engine.MaxResults = -5;
        Assert.Equal(10, engine.MaxResults); // 非法值回落默认
        Assert.Equal(5, engine.Match("应用", items).Count()); // 全部 5 条都可返回
    }

    [Fact]
    public void DisabledItems_AreExcluded()
    {
        var engine = new SearchEngine(10);
        var item = App("停用项", x => x.Enabled = false);

        Assert.Empty(engine.Match("停用", new[] { item }));
    }
}
