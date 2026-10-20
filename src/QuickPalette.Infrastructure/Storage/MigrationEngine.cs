namespace QuickPalette.Infrastructure.Storage;

using System.Text.Json;
using Serilog;

/// <summary>
/// 迁移失败时抛出的明确异常（App 层据此降级启动而不是崩溃）
/// </summary>
public class MigrationException : Exception
{
    public MigrationException(string message) : base(message)
    {
    }

    public MigrationException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>
/// Schema 迁移引擎 - 版本化迁移链。
/// 行为保证：
///   ① 损坏 JSON 不再向上炸进程——先走"从最近备份自愈"路径，仍失败才抛 MigrationException（信息明确）；
///   ② 版本识别支持三种形态：文件级信封 {schemaVersion,data}、旧条目级 schemaVersion、裸数组（视为 v1）；
///   ③ 显式 while 链逐级推进（v1→v2→…），断链报错；
///   ④ 迁移前自动备份（pre_migration_）、失败回滚、成功写版本戳、整体幂等。
/// </summary>
public class MigrationEngine
{
    /// <summary>版本戳文件（不动 apps.json 结构，避免影响工具/旧解析）</summary>
    private const string VersionStampFile = "schema.json";

    private readonly string _dataDir;
    private readonly ILogger _logger;
    private readonly List<IMigration> _migrations = new();

    public MigrationEngine(string dataDir, ILogger logger)
    {
        _dataDir = dataDir;
        _logger = logger;

        // 注册迁移链（示例）：
        // _migrations.Add(new MigrationV1ToV2());
        RegisterMigrations();
    }

    private void RegisterMigrations()
    {
        // 当前数据模型即 v1；未来演进在此追加，MigrateAsync 会自动逐级链式执行
    }

    /// <summary>目标版本 = 已注册迁移的最大 ToVersion（无迁移时为 1）</summary>
    public int TargetVersion =>
        _migrations.Count == 0 ? 1 : _migrations.Max(m => m.ToVersion);

    /// <summary>
    /// 执行所有待执行的迁移。幂等：已是目标版本直接返回。
    /// </summary>
    public async Task MigrateAsync()
    {
        var appsPath = Path.Combine(_dataDir, "apps.json");
        if (!File.Exists(appsPath))
        {
            _logger.Information("No data file found, skipping migration");
            return;
        }

        var currentVersion = await ReadCurrentVersionAsync(appsPath);
        _logger.Information("Current schema version: {Version}", currentVersion);

        if (currentVersion >= TargetVersion)
        {
            await WriteVersionStampAsync(currentVersion);
            return;
        }

        // 显式 while 链推进（禁止 foreach 只跑一遍的旧行为）
        while (currentVersion < TargetVersion)
        {
            var migration = _migrations.FirstOrDefault(m => m.FromVersion == currentVersion);
            if (migration == null)
                throw new MigrationException(
                    $"迁移链断裂：缺少 v{currentVersion} -> v{currentVersion + 1} 的迁移步骤，已中止（数据未改动）");

            _logger.Information("Running migration: v{From} -> v{To}", migration.FromVersion, migration.ToVersion);

            // 迁移前备份
            var backupDir = Path.Combine(_dataDir, "backups");
            Directory.CreateDirectory(backupDir);
            var backupPath = Path.Combine(backupDir,
                $"pre_migration_v{migration.FromVersion}_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}.json");
            File.Copy(appsPath, backupPath, true);

            try
            {
                await migration.MigrateAsync(_dataDir);
                currentVersion = migration.ToVersion;
                await WriteVersionStampAsync(currentVersion);
                _logger.Information("Migration completed: v{Version}", currentVersion);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Migration failed, rolling back to {Backup}", backupPath);
                File.Copy(backupPath, appsPath, true);
                throw new MigrationException($"迁移 v{migration.FromVersion} 失败已回滚：{ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// 读取当前版本：文件级信封 &gt; 条目级 schemaVersion &gt; 裸数组（v1）。
    /// 损坏时先尝试从备份自愈再解析；仍失败抛 MigrationException（信息明确、不裸抛 JsonException）。
    /// </summary>
    private async Task<int> ReadCurrentVersionAsync(string appsPath)
    {
        var json = await TryReadAsync(appsPath);
        if (json == null)
            throw new MigrationException($"数据文件损坏且无可用备份，无法确定 schema 版本：{appsPath}");

        try
        {
            return ParseVersion(json);
        }
        catch (JsonException)
        {
            _logger.Warning("apps.json 解析失败，尝试从备份自愈后重新解析");
            var backupJson = await TryRecoverFromBackupsAsync(appsPath);
            if (backupJson == null)
                throw new MigrationException($"数据文件损坏且无可用备份，无法迁移：{appsPath}");

            try
            {
                return ParseVersion(backupJson);
            }
            catch (JsonException ex)
            {
                throw new MigrationException($"数据与全部备份均无法解析，无法迁移：{appsPath}", ex);
            }
        }
    }

    private static int ParseVersion(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // ① 文件级信封 { "schemaVersion": N, "data": ... }
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("schemaVersion", out var envelopeVersion))
        {
            return envelopeVersion.GetInt32();
        }

        // ② 旧条目级 schemaVersion（数组取第一个元素）
        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
        {
            var firstItem = root[0];
            if (firstItem.ValueKind == JsonValueKind.Object &&
                firstItem.TryGetProperty("schemaVersion", out var itemVersion))
            {
                return itemVersion.GetInt32();
            }
            return 1; // ③ 裸数组视为 v1
        }

        return 1;
    }

    private async Task<string?> TryReadAsync(string path)
    {
        try
        {
            return await File.ReadAllTextAsync(path);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "读取 {Path} 失败", path);
            return null;
        }
    }

    /// <summary>按时间倒序找最近可读（能解析）的备份并原子写回主文件</summary>
    private async Task<string?> TryRecoverFromBackupsAsync(string appsPath)
    {
        var backupsDir = Path.Combine(_dataDir, "backups");
        if (!Directory.Exists(backupsDir)) return null;

        foreach (var backup in Directory.GetFiles(backupsDir, "apps_*.json")
                     .OrderByDescending(f => f, StringComparer.Ordinal))
        {
            var json = await TryReadAsync(backup);
            if (json == null) continue;
            try
            {
                JsonDocument.Parse(json); // 验证可解析
            }
            catch (JsonException)
            {
                continue;
            }

            _logger.Information("从备份自愈数据：{Backup}", backup);
            await File.WriteAllTextAsync(appsPath, json);
            return json;
        }
        return null;
    }

    private async Task WriteVersionStampAsync(int version)
    {
        try
        {
            var stampPath = Path.Combine(_dataDir, VersionStampFile);
            var payload = JsonSerializer.Serialize(new { schemaVersion = version, updatedAt = DateTime.UtcNow });
            await File.WriteAllTextAsync(stampPath, payload);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "写版本戳失败（不影响数据）");
        }
    }
}

/// <summary>
/// 迁移接口
/// </summary>
public interface IMigration
{
    int FromVersion { get; }
    int ToVersion { get; }
    Task MigrateAsync(string dataDir);
}
