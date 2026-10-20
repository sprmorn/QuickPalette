namespace QuickPalette.Infrastructure.Tests;

// xUnit1031：会话期 TestRunner 以反射同步执行测试（不 await 异步返回），
// 测试方法内必须同步阻塞等待 Task，确保断言真正生效。
#pragma warning disable xUnit1031

using System.IO;
using QuickPalette.Domain.Contracts;
using QuickPalette.Domain.Models;
using QuickPalette.Domain.Services;
using QuickPalette.Infrastructure.Scanners;
using Serilog;

/// <summary>
/// 扫描器测试：来源过滤（大小写不敏感 + 旧值别名）、无效输入返回空不抛、命令行拆分等纯逻辑。
/// 注意：所有异步 API 均以阻塞方式调用，保证断言真正执行。
/// </summary>
public class ScannerTests
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    // ---------------- ScannerManager.IsSourceEnabled ----------------

    [Theory]
    [InlineData("StartMenu", "StartMenu", true)]
    [InlineData("startmenu", "StartMenu", true)]
    [InlineData("start_menu", "StartMenu", true)]
    [InlineData("START MENU", "StartMenu", true)]
    [InlineData("start-menu", "StartMenu", true)]
    [InlineData("reg", "Registry", true)]
    [InlineData("REGISTRY", "Registry", true)]
    [InlineData("app paths", "Registry", true)]
    [InlineData("uwp", "Uwp", true)]
    [InlineData("store", "Uwp", true)]
    [InlineData("AppsFolder", "Uwp", true)]
    [InlineData("process", "Process", true)]
    [InlineData("running", "Process", true)]
    [InlineData("bogus", "StartMenu", false)]
    [InlineData("uwp", "StartMenu", false)]
    [InlineData("", "StartMenu", false)]
    public void IsSourceEnabled_CaseInsensitiveWithAliases(string configured, string sourceId, bool expected)
    {
        Assert.Equal(expected, ScannerManager.IsSourceEnabled(sourceId, new[] { configured }));
    }

    [Fact]
    public void IsSourceEnabled_EmptyConfig_ReturnsFalse()
    {
        Assert.False(ScannerManager.IsSourceEnabled("StartMenu", Array.Empty<string>()));
    }

    [Fact]
    public void IsSourceEnabled_Star_EnablesAll()
    {
        Assert.True(ScannerManager.IsSourceEnabled("StartMenu", new[] { "*" }));
        Assert.True(ScannerManager.IsSourceEnabled("Process", new[] { "all" }));
    }

    [Fact]
    public void IsSourceEnabled_CustomSource_MatchedLiterally()
    {
        Assert.True(ScannerManager.IsSourceEnabled("FakeSource", new[] { "fakesource" }));
        Assert.False(ScannerManager.IsSourceEnabled("FakeSource", new[] { "startmenu" }));
    }

    // ---------------- ScannerManager.ScanAllAsync：无效输入返回空不抛 ----------------

    [Fact]
    public void ScanAllAsync_EmptySources_ReturnsEmpty()
    {
        var settings = new Settings { ScanSources = new List<string>() };
        var manager = new ScannerManager(new FilterService(settings), Logger);

        var result = manager.ScanAllAsync(settings).GetAwaiter().GetResult();

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void ScanAllAsync_UnknownSources_ReturnsEmptyNotThrow()
    {
        var settings = new Settings { ScanSources = new List<string> { "bogus", "???", "", "  " } };
        var manager = new ScannerManager(new FilterService(settings), Logger);

        var result = manager.ScanAllAsync(settings).GetAwaiter().GetResult();

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void ScanAllAsync_EnabledCustomSource_InvokedAndResultsReturned()
    {
        var settings = new Settings { ScanSources = new List<string> { "FakeSource" } };
        var manager = new ScannerManager(new FilterService(settings), Logger);
        var fake = new FakeSource();
        manager.RegisterSource(fake);

        var result = manager.ScanAllAsync(settings).GetAwaiter().GetResult();

        Assert.Equal(1, fake.CallCount);
        Assert.Single(result);
        Assert.Equal("FakeApp", result[0].Name);
    }

    [Fact]
    public void ScanAllAsync_DisabledCustomSource_NotInvoked()
    {
        var settings = new Settings { ScanSources = new List<string> { "StartMenu" } };
        var manager = new ScannerManager(new FilterService(settings), Logger);
        var fake = new FakeSource();
        manager.RegisterSource(fake);

        manager.ScanAllAsync(settings).GetAwaiter().GetResult();

        Assert.Equal(0, fake.CallCount);
    }

    [Fact]
    public void ScanAllAsync_Cancelled_Throws()
    {
        var settings = new Settings { ScanSources = new List<string> { "FakeSource" } };
        var manager = new ScannerManager(new FilterService(settings), Logger);
        manager.RegisterSource(new FakeSource());

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
        {
            manager.ScanAllAsync(settings, cts.Token).GetAwaiter().GetResult();
        });
    }

    // ---------------- 各扫描器：无效输入/真实输入均不抛 ----------------

    [Fact]
    public void StartMenuScanner_Scan_ReturnsValidEntriesOnly()
    {
        var scanner = new StartMenuScanner(Logger);

        var apps = scanner.ScanAsync().GetAwaiter().GetResult().ToList();

        Assert.NotNull(apps);
        Assert.All(apps, a => Assert.False(string.IsNullOrWhiteSpace(a.Path)));
        Assert.All(apps, a => Assert.Equal("StartMenu", a.SourceId));
        // 解析失败的快捷方式被跳过：条目目标必须真实存在
        Assert.All(apps, a => Assert.True(File.Exists(a.Path) || Directory.Exists(a.Path)));
    }

    [Fact]
    public void RegistryScanner_Scan_ReturnsExistingTargetsOnly()
    {
        var scanner = new RegistryScanner(Logger);

        var apps = scanner.ScanAsync().GetAwaiter().GetResult().ToList();

        Assert.NotNull(apps);
        Assert.All(apps, a => Assert.False(string.IsNullOrWhiteSpace(a.Path)));
        Assert.All(apps, a => Assert.True(File.Exists(a.Path)));
        Assert.All(apps, a => Assert.Equal("Registry", a.SourceId));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\app.exe\" --flag", "C:\\Program Files\\app.exe", "--flag")]
    [InlineData("\"C:\\Program Files\\app.exe\"", "C:\\Program Files\\app.exe", "")]
    [InlineData("\"C:\\Program Files\\app.exe\"   ", "C:\\Program Files\\app.exe", "")]
    public void SplitCommandLine_Quoted(string raw, string expectedPath, string expectedArgs)
    {
        var (path, args) = RegistryScanner.SplitCommandLine(raw);

        Assert.Equal(expectedPath, path);
        Assert.Equal(expectedArgs, args);
    }

    [Fact]
    public void SplitCommandLine_PlainPathWithArgs()
    {
        // 真实存在的 exe + 参数：路径/参数正确拆分
        var (path, args) = RegistryScanner.SplitCommandLine("C:\\Windows\\notepad.exe /A");

        Assert.Equal("C:\\Windows\\notepad.exe", path);
        Assert.Equal("/A", args);
    }

    [Fact]
    public void SplitCommandLine_Empty_ReturnsEmpty()
    {
        var (path, args) = RegistryScanner.SplitCommandLine("   ");

        Assert.Equal(string.Empty, path);
        Assert.Equal(string.Empty, args);
    }

    [Fact]
    public void ProcessScanner_CaptureForeground_DoesNotThrow()
    {
        var scanner = new ProcessScanner(Logger);

        var raw = scanner.CaptureForeground();
        if (raw != null)
        {
            Assert.False(string.IsNullOrWhiteSpace(raw.Path));
            Assert.Equal("Process", raw.SourceId);
        }

        // 异步版本同样不抛
        var raw2 = scanner.CaptureForegroundAsync().GetAwaiter().GetResult();
        _ = raw2;
    }

    [Fact]
    public void ProcessScanner_Scan_DoesNotThrow()
    {
        var scanner = new ProcessScanner(Logger);

        var apps = scanner.ScanAsync().GetAwaiter().GetResult().ToList();

        Assert.NotNull(apps);
        Assert.All(apps, a => Assert.False(string.IsNullOrWhiteSpace(a.Path)));
    }

    [Fact]
    public void UwpScanner_Scan_ReturnsUwpEntriesOrEmpty()
    {
        var scanner = new UwpScanner(Logger);

        var apps = scanner.ScanAsync().GetAwaiter().GetResult().ToList();

        Assert.NotNull(apps);
        Assert.All(apps, a => Assert.Equal(AppType.Uwp, a.Type));
        Assert.All(apps, a => Assert.False(string.IsNullOrWhiteSpace(a.Aumid)));
        Assert.All(apps, a => Assert.Equal("Uwp", a.SourceId));
    }

    /// <summary>
    /// 测试用假数据源
    /// </summary>
    private sealed class FakeSource : IAppSource
    {
        public string SourceId => "FakeSource";

        public int CallCount { get; private set; }

        public Task<IEnumerable<RawApp>> ScanAsync(CancellationToken ct = default)
        {
            CallCount++;
            IEnumerable<RawApp> apps = new List<RawApp>
            {
                new() { Name = "FakeApp", Path = "C:\\fake\\app.exe", Type = AppType.Exe, SourceId = SourceId }
            };
            return Task.FromResult(apps);
        }
    }
}
