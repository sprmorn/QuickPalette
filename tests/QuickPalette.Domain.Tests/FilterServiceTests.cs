namespace QuickPalette.Domain.Tests;

using QuickPalette.Domain.Contracts;
using QuickPalette.Domain.Models;
using QuickPalette.Domain.Services;
using Xunit;

/// <summary>
/// FilterService 过滤/白名单/去重/解释性测试
/// </summary>
public class FilterServiceTests
{
    private static FilterService Service(Action<Settings>? setup = null)
    {
        var settings = new Settings();
        setup?.Invoke(settings);
        return new FilterService(settings);
    }

    // ---------- 关键词 ----------

    [Fact]
    public void EmptyKeyword_DoesNot_FilterEverything()
    {
        // 修复回归：Contains("") 恒真曾把全部候选过滤掉
        var service = Service(s => s.FilterKeywords = new List<string> { "", "   " });

        Assert.False(service.ShouldFilter("微信", @"C:\Apps\WeChat\WeChat.exe"));
    }

    [Fact]
    public void Keyword_Matches_Name_CaseInsensitive()
    {
        var service = Service();

        Assert.True(service.ShouldFilter("Foo Uninstall Tool", ""));
        Assert.False(service.ShouldFilter("微信", ""));
    }

    [Fact]
    public void Evaluate_Returns_Reason()
    {
        var service = Service();

        var decision = service.Evaluate("xx uninstall", "");
        Assert.True(decision.Filtered);
        Assert.Equal(FilterReason.Keyword, decision.Reason);

        Assert.Equal(FilterReason.None, service.Evaluate("正常应用", "").Reason);
    }

    // ---------- 路径段匹配 ----------

    [Fact]
    public void PathBlacklist_Uses_SegmentBoundary()
    {
        var service = Service(s => s.FilterPaths = new List<string> { @"C:\Windows\System32" });

        Assert.True(service.ShouldFilter("x", @"C:\Windows\System32\cmd.exe"));
        Assert.False(service.ShouldFilter("x", @"C:\Windows\System32Backup\tool.exe")); // 修复回归：不误伤
        Assert.False(service.ShouldFilter("x", @"D:\projects\windows-app\app.exe"));
    }

    [Fact]
    public void SystemPaths_DerivedFrom_Environment()
    {
        var service = Service(s => s.FilterPaths = new List<string>());
        var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        Assert.True(service.ShouldFilter("x", System.IO.Path.Combine(windowsDir, "System32", "foo.exe")));
    }

    // ---------- 白名单 ----------

    [Fact]
    public void Whitelist_Overrides_AllFilters()
    {
        var service = Service(s =>
        {
            s.FilterKeywords = new List<string> { "uninstall" };
            s.FilterPaths = new List<string> { @"C:\Windows\System32" };
            s.Whitelist = new List<string> { "my uninstall tool", @"c:\windows\system32\cmd.exe" };
        });

        Assert.False(service.ShouldFilter("My Uninstall Tool", ""));
        Assert.False(service.ShouldFilter("cmd", @"C:\Windows\System32\cmd.exe"));
    }

    // ---------- 系统组件 ----------

    [Fact]
    public void SystemComponent_Filtered_WhenEnabled()
    {
        var store = new RawApp { Name = "Microsoft Store", Aumid = "Microsoft.WindowsStore_8wekyb3d8bbwe!App", SourceId = "Uwp" };
        var normal = new RawApp { Name = "VS Code", Aumid = "Microsoft.VisualStudioCode_8wekyb3d8bbwe!Code", SourceId = "Uwp" };

        var enabled = Service(s => s.FilterSystemComponents = true);
        var result = enabled.FilterCandidates(new[] { store, normal }).ToList();
        Assert.Single(result);
        Assert.Equal("VS Code", result[0].Name);

        var disabled = Service(s => s.FilterSystemComponents = false);
        Assert.Equal(2, disabled.FilterCandidates(new[] { store, normal }).Count());
    }

    // ---------- 去重 ----------

    [Fact]
    public void Deduplicate_MultiKey_Prefers_UserStartMenu()
    {
        var userStartMenu = new RawApp { Name = "微信", Path = @"C:\Users\a\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\WeChat\WeChat.lnk", SourceId = "StartMenu" };
        var desktop = new RawApp { Name = "微信", Path = @"C:\Users\a\Desktop\WeChat.lnk", SourceId = "Desktop" };
        var samePathAgain = new RawApp { Name = "WeChat", Path = userStartMenu.Path, SourceId = "StartMenu" };

        var result = Service().Deduplicate(new[] { desktop, samePathAgain, userStartMenu }).ToList();

        Assert.Single(result);
        Assert.Equal(userStartMenu.Path, result[0].Path);
    }

    [Fact]
    public void Deduplicate_EmptyPath_DoesNot_Collapse()
    {
        var a = new RawApp { Name = "UWP甲", Path = "", Aumid = "pkg.a!App", SourceId = "Uwp" };
        var b = new RawApp { Name = "UWP乙", Path = "", Aumid = "pkg.b!App", SourceId = "Uwp" };

        var result = Service().Deduplicate(new[] { a, b }).ToList();

        Assert.Equal(2, result.Count); // 修复回归：空路径条目不许坍缩合并
    }

    [Fact]
    public void Deduplicate_SameAumid_IsMerged()
    {
        var a = new RawApp { Name = "A", Path = "", Aumid = "pkg.x!App", SourceId = "Uwp" };
        var b = new RawApp { Name = "A copy", Path = "", Aumid = "pkg.x!App", SourceId = "Uwp" };

        Assert.Single(Service().Deduplicate(new[] { a, b }));
    }
}
