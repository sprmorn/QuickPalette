namespace QuickPalette.Domain.Tests;

using QuickPalette.Domain.Models;
using QuickPalette.Domain.Services;
using Xunit;

/// <summary>
/// SystemToolCatalog 内置目录完整性测试（数据质量守卫：唯一性、必填字段、覆盖度）
/// </summary>
public class SystemToolCatalogTests
{
    [Fact]
    public void Catalog_Is_Comprehensive()
    {
        // 全面收录：系统工具/控制面板/管理控制台/设置页/系统文件夹，条目不应太少
        Assert.True(SystemToolCatalog.Tools.Count >= 120,
            $"目录条目过少：{SystemToolCatalog.Tools.Count}");
    }

    [Fact]
    public void All_Categories_Are_Covered()
    {
        var categories = SystemToolCatalog.Tools.Select(t => t.Category).ToHashSet();

        Assert.Contains(SystemToolCategory.Tool, categories);
        Assert.Contains(SystemToolCategory.ControlPanel, categories);
        Assert.Contains(SystemToolCategory.MscConsole, categories);
        Assert.Contains(SystemToolCategory.Settings, categories);
        Assert.Contains(SystemToolCategory.ShellFolder, categories);
    }

    [Fact]
    public void Ids_Are_Unique_And_NonEmpty()
    {
        var ids = SystemToolCatalog.Tools.Select(t => t.Id).ToList();

        Assert.All(ids, id => Assert.False(string.IsNullOrWhiteSpace(id)));
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Names_Are_Unique_And_NonEmpty()
    {
        var names = SystemToolCatalog.Tools.Select(t => t.Name).ToList();

        Assert.All(names, n => Assert.False(string.IsNullOrWhiteSpace(n)));
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Launch_Is_NonEmpty()
    {
        Assert.All(SystemToolCatalog.Tools,
            t => Assert.False(string.IsNullOrWhiteSpace(t.Launch), $"空启动命令：{t.Id}"));
    }

    [Fact]
    public void Keywords_Are_Clean()
    {
        foreach (var tool in SystemToolCatalog.Tools)
        {
            Assert.DoesNotContain(tool.Keywords, k => string.IsNullOrWhiteSpace(k));
        }
    }

    [Fact]
    public void WellKnown_Tools_Are_Findable()
    {
        var search = new SystemToolSearch(SystemToolCatalog.Tools);

        // 名称直达（用户最常用的例子）
        Assert.Contains(search.Match("任务管理器"), h => h.Tool.Name == "任务管理器");
        Assert.Contains(search.Match("设备管理器"), h => h.Tool.Name == "设备管理器");
        Assert.Contains(search.Match("注册表"), h => h.Tool.Name.Contains("注册表"));
        Assert.Contains(search.Match("控制面板"), h => h.Tool.Name == "控制面板");
        Assert.Contains(search.Match("服务"), h => h.Tool.Name == "服务");

        // 命令名 / 英文名直达
        Assert.Contains(search.Match("regedit"), h => h.Tool.Id.Contains("regedit"));
        Assert.Contains(search.Match("devmgmt"), h => h.Tool.Id.Contains("devmgmt"));
        Assert.Contains(search.Match("msconfig"), h => h.Tool.Id.Contains("msconfig"));
    }

    [Fact]
    public void Pinyin_Search_Works_Against_Real_Catalog()
    {
        var search = new SystemToolSearch(SystemToolCatalog.Tools);

        Assert.Contains(search.Match("renwuguanliqi"), h => h.Tool.Name == "任务管理器");
        Assert.Contains(search.Match("sbglq"), h => h.Tool.Name == "设备管理器");
    }
}
