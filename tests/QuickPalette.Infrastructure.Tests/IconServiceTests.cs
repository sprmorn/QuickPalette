namespace QuickPalette.Infrastructure.Tests;

// xUnit1031：会话期 TestRunner 以反射同步执行测试（不 await 异步返回），
// 测试方法内必须同步阻塞等待 Task，确保断言真正生效。
#pragma warning disable xUnit1031

using System.IO;
using QuickPalette.Infrastructure.Services;
using Serilog;

/// <summary>
/// IconService 测试：文件名消毒、图标提取缓存、按大小/时间清理。
/// 注意：所有测试自建临时目录并在结束时清理。
/// </summary>
public class IconServiceTests
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    // ---------------- SanitizeFileName 纯逻辑 ----------------

    [Theory]
    [InlineData("a/b\\c:d*e?f\"g<h>i|j", "a_b_c_d_e_f_g_h_i_j")]
    [InlineData("normal", "normal")]
    [InlineData(" normal name ", "normal name")]
    [InlineData("name...", "name")]
    [InlineData("name. . ", "name")]
    [InlineData("...", "_")]
    [InlineData("CON", "_CON")]
    [InlineData("con.txt", "_con.txt")]
    [InlineData("LPT1", "_LPT1")]
    [InlineData("nul", "_nul")]
    [InlineData("COM10", "COM10")]
    [InlineData("微信 2024", "微信 2024")]
    public void SanitizeFileName_ReplacesInvalidAndReserves(string input, string expected)
    {
        Assert.Equal(expected, IconService.SanitizeFileName(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void SanitizeFileName_EmptyOrNull_ReturnsEmpty(string? input)
    {
        Assert.Equal(string.Empty, IconService.SanitizeFileName(input));
    }

    [Fact]
    public void SanitizeFileName_Null_ReturnsEmpty()
    {
        // 单独成 [Fact]：[InlineData(null)] 会被解析为 params 数组本身为 null，导致反射传参个数不匹配
        Assert.Equal(string.Empty, IconService.SanitizeFileName(null));
    }

    // ---------------- GetIconPath：消毒 + 提取 + 缓存 ----------------

    [Fact]
    public void GetIconPath_SanitizesAppIdAndCaches()
    {
        var dataDir = CreateTempDir();
        try
        {
            using var service = new IconService(dataDir, Logger);
            var exePath = Environment.ProcessPath!;
            var dirtyId = "app/with:bad*chars";

            var result = service.GetIconPath(exePath, dirtyId);

            Assert.False(string.IsNullOrEmpty(result));
            Assert.Equal("app_with_bad_chars.png", Path.GetFileName(result));
            Assert.True(File.Exists(result));

            // 二次获取命中缓存，返回同一路径
            var again = service.GetIconPath(exePath, dirtyId);
            Assert.Equal(result, again);
        }
        finally
        {
            TryDeleteDir(dataDir);
        }
    }

    [Fact]
    public void GetIconPath_InvalidInput_ReturnsEmpty()
    {
        var dataDir = CreateTempDir();
        try
        {
            using var service = new IconService(dataDir, Logger);

            Assert.Equal(string.Empty, service.GetIconPath("C:\\definitely\\missing\\app.exe", "missing-app"));
            Assert.Equal(string.Empty, service.GetIconPath(Environment.ProcessPath!, "   "));

            // 全非法字符的 appId 消毒后兜底为 "_"，仍可正常缓存
            var fallback = service.GetIconPath(Environment.ProcessPath!, "...");
            Assert.EndsWith("_.png", fallback);
        }
        finally
        {
            TryDeleteDir(dataDir);
        }
    }

    // ---------------- CleanupCache：按时间/大小清理 ----------------

    [Fact]
    public void CleanupCache_RemovesExpiredFiles()
    {
        var dataDir = CreateTempDir();
        try
        {
            using var service = new IconService(dataDir, Logger);
            var iconsDir = Path.Combine(dataDir, "icons");

            var oldFile = CreateCacheFile(iconsDir, "old.png");
            var newFile = CreateCacheFile(iconsDir, "new.png");
            File.SetLastWriteTime(oldFile, DateTime.Now.AddDays(-10));
            File.SetLastWriteTime(newFile, DateTime.Now);

            service.CleanupCache(maxAgeDays: 3);

            Assert.False(File.Exists(oldFile));
            Assert.True(File.Exists(newFile));
        }
        finally
        {
            TryDeleteDir(dataDir);
        }
    }

    [Fact]
    public void CleanupCache_EvictsOldest_WhenOverSizeLimit()
    {
        var dataDir = CreateTempDir();
        try
        {
            using var service = new IconService(dataDir, Logger);
            var iconsDir = Path.Combine(dataDir, "icons");

            var older = CreateCacheFile(iconsDir, "older.png", 10);
            var newer = CreateCacheFile(iconsDir, "newer.png", 10);
            File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddMinutes(-5));
            File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);

            // 不按时间删（阈值极长），只按总大小 15 字节淘汰 → 最旧的 older.png 被删
            service.CleanupCache(maxAgeDays: 3650, maxTotalBytes: 15);

            Assert.False(File.Exists(older));
            Assert.True(File.Exists(newer));
        }
        finally
        {
            TryDeleteDir(dataDir);
        }
    }

    [Fact]
    public void CleanupCache_EmptyDir_NoThrow()
    {
        var dataDir = CreateTempDir();
        try
        {
            using var service = new IconService(dataDir, Logger);
            service.CleanupCache();
        }
        finally
        {
            TryDeleteDir(dataDir);
        }
    }

    // ---------------- 工具方法 ----------------

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qp-icon-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string CreateCacheFile(string iconsDir, string name, int sizeBytes = 10)
    {
        Directory.CreateDirectory(iconsDir);
        var path = Path.Combine(iconsDir, name);
        File.WriteAllBytes(path, new byte[sizeBytes]);
        return path;
    }

    private static void TryDeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // 测试清理失败不影响断言结果
        }
    }
}
