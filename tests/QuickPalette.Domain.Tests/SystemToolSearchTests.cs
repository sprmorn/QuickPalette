namespace QuickPalette.Domain.Tests;

using QuickPalette.Domain.Contracts;
using QuickPalette.Domain.Models;
using QuickPalette.Domain.Services;
using Xunit;

/// <summary>
/// SystemToolSearch 系统组件搜索匹配测试（名称/英文名/拼音/首字母/别名 + 分层排序）
/// </summary>
public class SystemToolSearchTests
{
    private static SystemToolSearch Search() => new(new[]
    {
        new SystemTool
        {
            Id = "devmgmt",
            Name = "设备管理器",
            NameEn = "Device Manager",
            Launch = @"%SystemRoot%\System32\devmgmt.msc",
            Category = SystemToolCategory.MscConsole,
            Keywords = new List<string> { "devices", "驱动" }
        },
        new SystemTool
        {
            Id = "taskmgr",
            Name = "任务管理器",
            NameEn = "Task Manager",
            Launch = "taskmgr",
            Keywords = new List<string> { "tm", "进程" }
        },
        new SystemTool
        {
            Id = "regedit",
            Name = "注册表编辑器",
            NameEn = "Registry Editor",
            Launch = "regedit",
            RequiresAdmin = true,
            Keywords = new List<string> { "注册表", "regedit" }
        },
        new SystemTool
        {
            Id = "perfmon",
            Name = "性能监视器",
            NameEn = "Performance Monitor",
            Launch = "perfmon",
            Keywords = new List<string> { "任务" }
        }
    });

    // ---------- 名称 ----------

    [Fact]
    public void Chinese_Name_Exact_Match()
    {
        var hits = Search().Match("任务管理器");

        var hit = Assert.Single(hits);
        Assert.Equal("taskmgr", hit.Tool.Id);
        Assert.Equal(MatchDimension.NameExact, hit.Dimension);
    }

    [Fact]
    public void Chinese_Name_Prefix_Match()
    {
        var hits = Search().Match("设备管");

        var hit = Assert.Single(hits);
        Assert.Equal("devmgmt", hit.Tool.Id);
        Assert.Equal(MatchDimension.NamePrefix, hit.Dimension);
    }

    [Fact]
    public void Chinese_Name_Substring_Match()
    {
        var hits = Search().Match("管理器");

        Assert.Equal(2, hits.Count);
        Assert.All(hits, h => Assert.Equal(MatchDimension.NameSubstring, h.Dimension));
    }

    [Fact]
    public void English_Name_Match_CaseInsensitive()
    {
        var hits = Search().Match("device");

        var hit = Assert.Single(hits);
        Assert.Equal("devmgmt", hit.Tool.Id);
        Assert.Equal(MatchDimension.NamePrefix, hit.Dimension);
    }

    // ---------- 拼音 ----------

    [Fact]
    public void Pinyin_Full_Match()
    {
        var hits = Search().Match("shebeiguanliqi");

        var hit = Assert.Single(hits);
        Assert.Equal("devmgmt", hit.Tool.Id);
        Assert.Equal(MatchDimension.PinyinFull, hit.Dimension);
    }

    [Fact]
    public void Pinyin_Full_Substring_Match()
    {
        var hits = Search().Match("shebei");

        Assert.Contains(hits, h => h.Tool.Id == "devmgmt" && h.Dimension == MatchDimension.PinyinFull);
    }

    [Fact]
    public void Pinyin_Initials_Match()
    {
        var hits = Search().Match("rwglq");

        var hit = Assert.Single(hits);
        Assert.Equal("taskmgr", hit.Tool.Id);
        Assert.Equal(MatchDimension.PinyinInitials, hit.Dimension);
    }

    [Fact]
    public void Pinyin_Initials_FullWidth_Query_Match()
    {
        // 全角输入应归一化后命中（ｒｗｇｌｑ → rwglq）
        var hits = Search().Match("ｒｗｇｌｑ");

        Assert.Contains(hits, h => h.Tool.Id == "taskmgr");
    }

    // ---------- 别名 ----------

    [Fact]
    public void Keyword_Alias_Match()
    {
        var hits = Search().Match("驱动");

        var hit = Assert.Single(hits);
        Assert.Equal("devmgmt", hit.Tool.Id);
        Assert.Equal(MatchDimension.AliasOrTag, hit.Dimension);
    }

    [Fact]
    public void Keyword_Alias_English_Match()
    {
        var hits = Search().Match("tm");

        Assert.Contains(hits, h => h.Tool.Id == "taskmgr" && h.Dimension == MatchDimension.AliasOrTag);
    }

    // ---------- 分层排序 / 边界 ----------

    [Fact]
    public void Name_Match_Ranks_Above_Alias_Match()
    {
        // "任务"：任务管理器=名称前缀(800) vs 性能监视器=别名(200)——名称层必胜
        var hits = Search().Match("任务");

        Assert.True(hits.Count >= 2);
        Assert.Equal("taskmgr", hits[0].Tool.Id);
        Assert.Equal(MatchDimension.NamePrefix, hits[0].Dimension);
        Assert.Contains(hits, h => h.Tool.Id == "perfmon" && h.Dimension == MatchDimension.AliasOrTag);
    }

    [Fact]
    public void Empty_Or_Whitespace_Query_Returns_Nothing()
    {
        var search = Search();

        Assert.Empty(search.Match(null));
        Assert.Empty(search.Match(""));
        Assert.Empty(search.Match("   "));
    }

    [Fact]
    public void MaxResults_Caps_Output()
    {
        var hits = Search().Match("管理器", 1);

        Assert.Single(hits);
    }

    [Fact]
    public void Invalid_MaxResults_Falls_Back_To_Default()
    {
        var hits = Search().Match("管理器", 0);

        Assert.Equal(2, hits.Count);
    }

    [Fact]
    public void No_Match_Returns_Empty()
    {
        Assert.Empty(Search().Match("绝对不存在的查询zzzz"));
    }

    [Fact]
    public void RequiresAdmin_Flag_Preserved()
    {
        var hits = Search().Match("regedit");

        var hit = Assert.Single(hits);
        Assert.Equal("regedit", hit.Tool.Id);
        Assert.True(hit.Tool.RequiresAdmin);
    }

    [Fact]
    public void Null_Or_Blank_Tools_Are_Skipped()
    {
        var search = new SystemToolSearch(new[]
        {
            new SystemTool { Id = "ok", Name = "任务管理器", Launch = "taskmgr" },
            new SystemTool { Id = "blank", Name = "   ", Launch = "x" }
        });

        Assert.Single(search.Match("任务管理器"));
    }
}
