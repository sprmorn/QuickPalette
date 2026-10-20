namespace QuickPalette.Infrastructure.Tests;

// xUnit1031：会话期 TestRunner 以反射同步执行测试（不 await 异步返回），
// 测试方法内必须同步阻塞等待 Task，确保断言真正生效。
#pragma warning disable xUnit1031

using System.IO;
using QuickPalette.Domain.Contracts;
using QuickPalette.Domain.Models;
using QuickPalette.Infrastructure.Launchers;
using Serilog;

/// <summary>
/// 启动器纯逻辑测试：URL 协议白名单/本地路径注入、cmd 引号转义、AUMID 校验、失败分类。
/// 注意：所有异步 API 均以阻塞方式调用，保证断言真正执行。
/// </summary>
public class LauncherTests
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    // ---------------- UrlLauncher：白名单放行 ----------------

    [Theory]
    [InlineData("https://example.com", "https://example.com")]
    [InlineData("HTTPS://EXAMPLE.COM", "HTTPS://EXAMPLE.COM")]
    [InlineData("Http://Example.com/path", "Http://Example.com/path")]
    [InlineData("http://a.b/c", "http://a.b/c")]
    [InlineData("ftp://files.example.com", "ftp://files.example.com")]
    [InlineData("FTP://files.example.com", "FTP://files.example.com")]
    [InlineData("mailto:a@b.com", "mailto:a@b.com")]
    [InlineData("MAILTO:a@b.com", "MAILTO:a@b.com")]
    [InlineData("ms-settings:display", "ms-settings:display")]
    [InlineData("Ms-Settings:Display", "Ms-Settings:Display")]
    [InlineData("example.com", "https://example.com")]
    [InlineData("www.example.com/path", "https://www.example.com/path")]
    [InlineData("example.com:8080/path", "https://example.com:8080/path")]
    public void TryNormalizeUrl_Whitelisted_Accepted(string raw, string expected)
    {
        var ok = UrlLauncher.TryNormalizeUrl(raw, out var normalized, out _, out var error);

        Assert.True(ok);
        Assert.Equal(expected, normalized);
        Assert.Null(error);
    }

    // ---------------- UrlLauncher：非法协议 / 本地路径注入 ----------------

    [Theory]
    [InlineData("javascript:alert(1)", LaunchErrorKind.Blocked)]
    [InlineData("JAVASCRIPT:alert(1)", LaunchErrorKind.Blocked)]
    [InlineData("data:text/html,x", LaunchErrorKind.Blocked)]
    [InlineData("vbscript:x", LaunchErrorKind.Blocked)]
    [InlineData("file:///C:/Windows/System32/cmd.exe", LaunchErrorKind.Blocked)]
    [InlineData("FILE://server/share", LaunchErrorKind.Blocked)]
    [InlineData("shell:AppsFolder", LaunchErrorKind.Blocked)]
    [InlineData("shell:AppsFolder\\evil", LaunchErrorKind.InvalidPath)]
    [InlineData(@"C:\Windows\System32\cmd.exe", LaunchErrorKind.InvalidPath)]
    [InlineData("c:/windows/system32/cmd.exe", LaunchErrorKind.InvalidPath)]
    [InlineData(@"\\server\share\evil.exe", LaunchErrorKind.InvalidPath)]
    [InlineData("//server/share/evil.exe", LaunchErrorKind.InvalidPath)]
    [InlineData("./setup.exe", LaunchErrorKind.InvalidPath)]
    [InlineData("../setup.exe", LaunchErrorKind.InvalidPath)]
    [InlineData(".\\setup.exe", LaunchErrorKind.InvalidPath)]
    [InlineData("~/secret", LaunchErrorKind.InvalidPath)]
    [InlineData("/etc/passwd", LaunchErrorKind.InvalidPath)]
    [InlineData("", LaunchErrorKind.InvalidPath)]
    [InlineData("   ", LaunchErrorKind.InvalidPath)]
    [InlineData("::not a url::", LaunchErrorKind.InvalidPath)]
    [InlineData("%%%", LaunchErrorKind.InvalidPath)]
    public void TryNormalizeUrl_UnsafeOrInvalid_Rejected(string raw, LaunchErrorKind expectedKind)
    {
        var ok = UrlLauncher.TryNormalizeUrl(raw, out _, out var kind, out var error);

        Assert.False(ok);
        Assert.Equal(expectedKind, kind);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void TryNormalizeUrl_Null_Rejected()
    {
        var ok = UrlLauncher.TryNormalizeUrl(null, out _, out var kind, out var error);

        Assert.False(ok);
        Assert.Equal(LaunchErrorKind.InvalidPath, kind);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void UrlLauncher_Launch_LocalPathInjection_FailsWithoutLaunching()
    {
        var launcher = new UrlLauncher(Logger);
        var item = new AppItem { Name = "evil", Type = AppType.Url, Path = @"C:\Windows\System32\cmd.exe" };

        var result = launcher.LaunchAsync(item).GetAwaiter().GetResult();

        Assert.False(result.Success);
        Assert.Equal(LaunchErrorKind.InvalidPath, result.ErrorKind);
    }

    [Fact]
    public void UrlLauncher_Launch_BlockedScheme_FailsWithoutLaunching()
    {
        var launcher = new UrlLauncher(Logger);
        var item = new AppItem { Name = "evil", Type = AppType.Url, Path = "file:///C:/Windows/System32/cmd.exe" };

        var result = launcher.LaunchAsync(item).GetAwaiter().GetResult();

        Assert.False(result.Success);
        Assert.Equal(LaunchErrorKind.Blocked, result.ErrorKind);
    }

    [Fact]
    public void UrlLauncher_Launch_EmptyUrl_Fails()
    {
        var launcher = new UrlLauncher(Logger);
        var item = new AppItem { Name = "empty", Type = AppType.Url, Path = "" };

        var result = launcher.LaunchAsync(item).GetAwaiter().GetResult();

        Assert.False(result.Success);
        Assert.Equal(LaunchErrorKind.InvalidPath, result.ErrorKind);
    }

    // ---------------- CommandLauncher：cmd /c 引号转义 ----------------

    [Fact]
    public void BuildCmdArguments_SimpleCommand()
    {
        Assert.Equal("/c \"echo hello\"", CommandLauncher.BuildCmdArguments("echo hello"));
    }

    [Fact]
    public void BuildCmdArguments_AppendsItemArgs()
    {
        Assert.Equal("/c \"echo hello world\"", CommandLauncher.BuildCmdArguments("echo hello", "world"));
    }

    [Fact]
    public void BuildCmdArguments_PreservesInnerQuotes()
    {
        // 内嵌引号原样保留（cmd 剥首尾引号规则），参数含空格/引号不被破坏
        Assert.Equal(
            "/c \"myapp.exe --name \"John Doe\"\"",
            CommandLauncher.BuildCmdArguments("myapp.exe", "--name \"John Doe\""));
    }

    [Fact]
    public void BuildCmdArguments_QuotesCommandWithSpacedPath()
    {
        // 命令自身是带空格的路径时先加引号，再外包一层，避免 cmd 按空格截断
        Assert.Equal(
            "/c \"\"C:\\Program Files\\a.exe\" --x\"",
            CommandLauncher.BuildCmdArguments("C:\\Program Files\\a.exe", "--x"));
    }

    [Fact]
    public void BuildCmdArguments_NoArgs_NoTrailingSpace()
    {
        Assert.Equal("/c \"myapp.exe\"", CommandLauncher.BuildCmdArguments("myapp.exe", null));
    }

    [Fact]
    public void CommandLauncher_Launch_EmptyCommand_FailsInvalidPath()
    {
        var launcher = new CommandLauncher(Logger);
        var item = new AppItem { Name = "empty", Type = AppType.Command, Path = "  " };

        var result = launcher.LaunchAsync(item).GetAwaiter().GetResult();

        Assert.False(result.Success);
        Assert.Equal(LaunchErrorKind.InvalidPath, result.ErrorKind);
    }

    // ---------------- UwpLauncher：AUMID 校验 ----------------

    [Theory]
    [InlineData("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")]
    [InlineData("Microsoft.AutoGenerated.{ABC-123}!App")]
    [InlineData("App!App")]
    [InlineData("Microsoft.WindowsNotepad_8wekyb3d8bbwe!App")]
    [InlineData("  Microsoft.WindowsCalculator_8wekyb3d8bbwe!App  ")]
    public void IsValidAumid_Accepts(string aumid)
    {
        Assert.True(UwpLauncher.IsValidAumid(aumid));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nobang")]
    [InlineData("a!b!c")]
    [InlineData("!App")]
    [InlineData("pkg!")]
    [InlineData("pkg!App extra")]
    [InlineData("pkg!App;cmd")]
    [InlineData("pkg!App|pipe")]
    [InlineData("pkg!App\"quote")]
    [InlineData(@"pkg!App\..\x")]
    [InlineData(@"C:\x!App")]
    [InlineData("pkg !App")]
    [InlineData("pkg!App/../..\\evil")]
    public void IsValidAumid_Rejects(string aumid)
    {
        Assert.False(UwpLauncher.IsValidAumid(aumid));
    }

    [Fact]
    public void IsValidAumid_Null_Rejected()
    {
        Assert.False(UwpLauncher.IsValidAumid(null));
    }

    [Fact]
    public void UwpLauncher_Launch_MissingAumid_FailsInvalidPath()
    {
        var launcher = new UwpLauncher(Logger);
        var item = new AppItem { Name = "uwp", Type = AppType.Uwp, Aumid = "" };

        var result = launcher.LaunchAsync(item).GetAwaiter().GetResult();

        Assert.False(result.Success);
        Assert.Equal(LaunchErrorKind.InvalidPath, result.ErrorKind);
    }

    [Fact]
    public void UwpLauncher_Launch_InvalidAumid_FailsInvalidPath()
    {
        var launcher = new UwpLauncher(Logger);
        var item = new AppItem { Name = "uwp", Type = AppType.Uwp, Aumid = "bad aumid!x/../..\\evil" };

        var result = launcher.LaunchAsync(item).GetAwaiter().GetResult();

        Assert.False(result.Success);
        Assert.Equal(LaunchErrorKind.InvalidPath, result.ErrorKind);
    }

    // ---------------- ExeLauncher：路径失效必须给 NotFound ----------------

    [Fact]
    public void ExeLauncher_Launch_MissingTarget_ReturnsNotFound()
    {
        var launcher = new ExeLauncher(Logger);
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "app.exe");
        var item = new AppItem { Name = "ghost", Type = AppType.Exe, Path = missing };

        var result = launcher.LaunchAsync(item).GetAwaiter().GetResult();

        Assert.False(result.Success);
        Assert.Equal(LaunchErrorKind.NotFound, result.ErrorKind);
    }

    [Fact]
    public void ExeLauncher_Launch_EmptyPath_ReturnsInvalidPath()
    {
        var launcher = new ExeLauncher(Logger);
        var item = new AppItem { Name = "empty", Type = AppType.Exe, Path = "" };

        var result = launcher.LaunchAsync(item).GetAwaiter().GetResult();

        Assert.False(result.Success);
        Assert.Equal(LaunchErrorKind.InvalidPath, result.ErrorKind);
    }

    [Fact]
    public void ExeLauncher_CanLaunch_TypeDispatch()
    {
        var launcher = new ExeLauncher(Logger);
        Assert.True(launcher.CanLaunch(new AppItem { Type = AppType.Exe }));
        Assert.True(launcher.CanLaunch(new AppItem { Type = AppType.Lnk }));
        Assert.False(launcher.CanLaunch(new AppItem { Type = AppType.Url }));
        Assert.False(launcher.CanLaunch(new AppItem { Type = AppType.Uwp }));
    }

    // ---------------- LaunchErrorMapper 分类 ----------------

    [Theory]
    [InlineData(2, LaunchErrorKind.NotFound)]
    [InlineData(3, LaunchErrorKind.NotFound)]
    [InlineData(5, LaunchErrorKind.AccessDenied)]
    [InlineData(740, LaunchErrorKind.AccessDenied)]
    [InlineData(123, LaunchErrorKind.InvalidPath)]
    [InlineData(87, LaunchErrorKind.InvalidPath)]
    [InlineData(193, LaunchErrorKind.Unsupported)]
    [InlineData(1223, LaunchErrorKind.Blocked)]
    [InlineData(-1, LaunchErrorKind.Failed)]
    public void FromWin32_Classifies(int code, LaunchErrorKind expected)
    {
        Assert.Equal(expected, LaunchErrorMapper.FromWin32(code));
    }

    [Fact]
    public void Classify_MapsCommonExceptions()
    {
        Assert.Equal(LaunchErrorKind.NotFound, LaunchErrorMapper.Classify(new FileNotFoundException()));
        Assert.Equal(LaunchErrorKind.NotFound, LaunchErrorMapper.Classify(new DirectoryNotFoundException()));
        Assert.Equal(LaunchErrorKind.AccessDenied, LaunchErrorMapper.Classify(new UnauthorizedAccessException()));
        Assert.Equal(LaunchErrorKind.InvalidPath, LaunchErrorMapper.Classify(new ArgumentException("bad")));
        Assert.Equal(LaunchErrorKind.Failed, LaunchErrorMapper.Classify(new InvalidOperationException("x")));
    }
}
