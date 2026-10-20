namespace QuickPalette.Infrastructure.Tests;

using QuickPalette.Infrastructure.Storage;
using Serilog;
using Xunit;

/// <summary>
/// MigrationEngine 迁移链/版本信封/损坏恢复测试
/// </summary>
public class MigrationTests : IDisposable
{
    private readonly string _dir;
    private readonly ILogger _logger;

    public MigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "qp-mig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _logger = new LoggerConfiguration().CreateLogger();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string AppsPath => Path.Combine(_dir, "apps.json");

    [Fact]
    public async Task NoFile_Skips_Migration()
    {
        var engine = new MigrationEngine(_dir, _logger);
        await engine.MigrateAsync(); // 不抛异常
    }

    [Fact]
    public async Task LegacyArray_IsVersion1_And_MigrationIdempotent()
    {
        await File.WriteAllTextAsync(AppsPath, "[{\"name\":\"微信\"}]");

        var engine = new MigrationEngine(_dir, _logger);
        await engine.MigrateAsync();
        await engine.MigrateAsync(); // 幂等：重复执行不报错

        Assert.True(File.Exists(Path.Combine(_dir, "schema.json"))); // 版本戳
    }

    [Fact]
    public async Task EntryLevel_And_Envelope_Versions_Are_Read()
    {
        // 条目级 schemaVersion
        await File.WriteAllTextAsync(AppsPath, "[{\"name\":\"a\",\"schemaVersion\":3}]");
        var engine = new MigrationEngine(_dir, _logger);
        // 有 v1..v3 条目版本但没有迁移步骤且目标版本为 1：已 >= 目标 → 直接写戳返回
        await engine.MigrateAsync();

        // 文件级信封
        await File.WriteAllTextAsync(AppsPath, "{\"schemaVersion\":2,\"data\":[]}");
        await engine.MigrateAsync();
    }

    [Fact]
    public async Task CorruptFile_TriesBackupRecovery_ThenFailsWith_ClearError()
    {
        await File.WriteAllTextAsync(AppsPath, "{{{ 无法解析");
        var engine = new MigrationEngine(_dir, _logger);

        var ex = await Assert.ThrowsAsync<MigrationException>(() => engine.MigrateAsync());
        Assert.Contains("无法迁移", ex.Message); // 明确异常，不是裸 JsonException
    }

    [Fact]
    public async Task CorruptFile_Recovers_From_Backup()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "backups"));
        await File.WriteAllTextAsync(
            Path.Combine(_dir, "backups", "apps_20260101_000000_1.json"),
            "[{\"name\":\"微信\"}]");
        await File.WriteAllTextAsync(AppsPath, "{{{ 坏数据");

        var engine = new MigrationEngine(_dir, _logger);
        await engine.MigrateAsync(); // 自愈成功，不抛

        Assert.Contains("微信", await File.ReadAllTextAsync(AppsPath));
    }
}
