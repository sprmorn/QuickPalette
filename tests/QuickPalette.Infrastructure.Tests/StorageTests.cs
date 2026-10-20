namespace QuickPalette.Infrastructure.Tests;

using QuickPalette.Domain.Models;
using QuickPalette.Infrastructure.Storage;
using Serilog;
using Xunit;

/// <summary>
/// JsonRepository / BackupService / HiddenItemStore / UsageStore 存储安全测试
/// </summary>
public class StorageTests : IDisposable
{
    private readonly string _dir;
    private readonly JsonRepository _repo;
    private readonly BackupService _backup;

    public StorageTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "qp-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var logger = new LoggerConfiguration().CreateLogger();
        _repo = new JsonRepository(_dir, logger);
        _backup = new BackupService(_dir, logger);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 清理尽力而为 */ }
    }

    private string AppsPath => Path.Combine(_dir, "apps.json");

    [Fact]
    public async Task AtomicWrite_LeavesNo_TempFile()
    {
        await _repo.AddAsync(new AppItem { Name = "微信" });

        Assert.True(File.Exists(AppsPath));
        Assert.False(File.Exists(AppsPath + ".tmp"));
        Assert.Equal("微信", (await _repo.GetAllAsync()).Single().Name);
    }

    [Fact]
    public async Task Backups_Rotated_PerCategory_Keep10()
    {
        for (var i = 0; i < 15; i++)
        {
            await _repo.SaveAllAsync(new[] { new AppItem { Name = $"应用{i}" } });
        }

        var appsBackups = Directory.GetFiles(Path.Combine(_dir, "backups"), "apps_*.json");
        Assert.True(appsBackups.Length <= 10, $"apps 备份数 {appsBackups.Length} 应 ≤10");
        Assert.True(appsBackups.Length >= 9);
    }

    [Fact]
    public async Task SameSecond_Writes_DoNotOverwrite_Backups()
    {
        for (var i = 0; i < 5; i++)
        {
            await _repo.SaveAllAsync(new[] { new AppItem { Name = $"应用{i}" } });
        }

        var appsBackups = Directory.GetFiles(Path.Combine(_dir, "backups"), "apps_*.json");
        Assert.True(appsBackups.Length >= 4, "同秒多次写入的备份不应互相覆盖");
    }

    [Fact]
    public async Task CorruptFile_ReadFailure_NeverOverwritten()
    {
        await File.WriteAllTextAsync(AppsPath, "这不是 JSON {{{");

        // 读：损坏且无备份 → 空表（不抛）
        var items = await _repo.GetAllAsync();
        Assert.Empty(items);

        // 写：拒绝覆盖用户数据（抛 IOException，文件保持原样）
        await Assert.ThrowsAsync<IOException>(() => _repo.AddAsync(new AppItem { Name = "x" }));
        Assert.Equal("这不是 JSON {{{", await File.ReadAllTextAsync(AppsPath));
    }

    [Fact]
    public async Task CorruptFile_Recovers_From_LatestBackup()
    {
        await _repo.SaveAllAsync(new[] { new AppItem { Name = "微信" } }); // 先有数据
        await _repo.SaveAllAsync(new[] { new AppItem { Name = "微信2" } }); // 第二次写入产生备份
        await File.WriteAllTextAsync(AppsPath, "损坏内容");

        var items = await _repo.GetAllAsync(); // 自愈路径

        Assert.NotEmpty(items);
    }

    [Fact]
    public async Task PropertyNames_Are_CaseInsensitive()
    {
        await File.WriteAllTextAsync(AppsPath,
            "[{\"NAME\":\"微信\",\"id\":\"a1\",\"favorite\":true}]");

        var items = await _repo.GetAllAsync();

        Assert.Equal("微信", items.Single().Name);
    }

    [Fact]
    public async Task BackupService_ExportImport_Roundtrip()
    {
        await _repo.SaveAllAsync(new[] { new AppItem { Name = "微信" } });
        await _repo.SaveAsync(new Settings { Hotkey = "Ctrl+Alt+Space" });

        var exportPath = Path.Combine(_dir, "quickpalette-backup.json");
        await _backup.ExportAsync(exportPath);

        // 清空后导入
        await _repo.SaveAllAsync(Array.Empty<AppItem>());
        await _backup.ImportAsync(exportPath);

        Assert.Equal("微信", (await _repo.GetAllAsync()).Single().Name);
        Assert.Equal("Ctrl+Alt+Space", (await _repo.GetAsync()).Hotkey);
    }

    [Fact]
    public async Task Restore_Rejects_CorruptBackup()
    {
        var bad = Path.Combine(_dir, "bad.json");
        await File.WriteAllTextAsync(bad, "{{{ 坏文件");

        await Assert.ThrowsAsync<InvalidDataException>(() => _backup.RestoreFromBackupAsync(bad));
    }

    [Fact]
    public async Task GetLatestBackup_Returns_PathOrNull()
    {
        Assert.Null(await _backup.GetLatestBackupAsync());

        await _repo.SaveAllAsync(new[] { new AppItem { Name = "a" } });
        await _backup.CreateBackupAsync(); // 手动备份

        var latest = await _backup.GetLatestBackupAsync();
        Assert.NotNull(latest);
        Assert.True(File.Exists(latest));
    }

    [Fact]
    public void HiddenItemStore_Persists()
    {
        var store = new HiddenItemStore(_dir, null);
        store.Hide(@"C:\App\tool.exe");
        Assert.True(store.IsHidden(@"c:\app\TOOL.EXE")); // 规范化小写

        var reloaded = new HiddenItemStore(_dir, null);
        Assert.True(reloaded.IsHidden("c:\\app\\tool.exe"));

        reloaded.Unhide("c:\\app\\tool.exe");
        Assert.False(new HiddenItemStore(_dir, null).IsHidden("c:\\app\\tool.exe"));
    }

    [Fact]
    public void UsageStore_Tracks_CountAndTime()
    {
        var store = new UsageStore(_dir, null);
        store.RecordLaunch("app1");
        store.RecordLaunch("APP1"); // 大小写归一
        store.Save();

        var reloaded = new UsageStore(_dir, null);
        Assert.Equal(2, reloaded.GetCount("app1"));
        Assert.NotNull(reloaded.GetLastLaunched("app1"));
        Assert.Equal(0, reloaded.GetCount("unknown"));
    }
}
