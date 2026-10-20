namespace QuickPalette.Infrastructure.Storage;

using System.Text.Json;
using QuickPalette.Domain.Contracts;
using Serilog;

/// <summary>
/// 备份服务：手动备份 / 恢复 / 轮转 / 单文件导出导入。
/// 恢复前先校验备份可解析（拒绝把坏数据写回去）；导入前自动备份现有数据。
/// </summary>
public class BackupService : IBackupService
{
    private readonly string _dataDir;
    private readonly string _backupsDir;
    private readonly ILogger _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public BackupService(string dataDir, ILogger logger)
    {
        _dataDir = dataDir;
        _backupsDir = Path.Combine(dataDir, "backups");
        _logger = logger;
        Directory.CreateDirectory(_backupsDir);
    }

    /// <summary>创建备份（apps.json + settings.json），返回备份目录路径</summary>
    public Task<string> CreateBackupAsync()
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
        foreach (var file in new[] { "apps.json", "settings.json" })
        {
            var src = Path.Combine(_dataDir, file);
            if (!File.Exists(src)) continue;
            var category = Path.GetFileNameWithoutExtension(src);
            File.Copy(src, Path.Combine(_backupsDir, $"{category}_{stamp}.json"), true);
        }

        _logger.Information("手动备份完成：{Stamp}", stamp);
        CleanupRotation(10);
        return Task.FromResult(_backupsDir);
    }

    /// <summary>
    /// 从备份恢复：先解析校验（坏备份抛 InvalidDataException），再原子写回。
    /// </summary>
    public Task RestoreFromBackupAsync(string backupPath)
    {
        if (string.IsNullOrWhiteSpace(backupPath) || !File.Exists(backupPath))
            throw new FileNotFoundException("备份文件不存在", backupPath);

        var json = File.ReadAllText(backupPath);
        try
        {
            JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"备份文件已损坏，拒绝恢复：{backupPath}", ex);
        }

        // 备份文件名决定还原目标：apps_*.json → apps.json；settings_*.json → settings.json
        var name = Path.GetFileNameWithoutExtension(backupPath);
        var target = name.StartsWith("settings", StringComparison.OrdinalIgnoreCase) ? "settings.json" : "apps.json";
        var targetPath = Path.Combine(_dataDir, target);

        var tmp = targetPath + ".restore.tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, targetPath, true);

        _logger.Information("已从备份恢复 {Target}：{Backup}", target, backupPath);
        return Task.CompletedTask;
    }

    /// <summary>最近备份文件完整路径（无则 null）</summary>
    public Task<string?> GetLatestBackupAsync()
    {
        var latest = Directory.Exists(_backupsDir)
            ? Directory.GetFiles(_backupsDir, "*.json").OrderByDescending(f => f, StringComparer.Ordinal).FirstOrDefault()
            : null;
        return Task.FromResult(latest);
    }

    /// <summary>按类别清理旧备份（apps_/settings_/pre_migration_ 各保留 keepCount 份）</summary>
    public Task CleanupOldBackupsAsync(int keepCount = 10)
    {
        CleanupRotation(keepCount);
        return Task.CompletedTask;
    }

    /// <summary>导出全部数据为单一 quickpalette-backup.json</summary>
    public Task ExportAsync(string targetFilePath)
    {
        var apps = File.Exists(Path.Combine(_dataDir, "apps.json"))
            ? File.ReadAllText(Path.Combine(_dataDir, "apps.json"))
            : "[]";
        var settings = File.Exists(Path.Combine(_dataDir, "settings.json"))
            ? File.ReadAllText(Path.Combine(_dataDir, "settings.json"))
            : "{}";

        var payload = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            exportedAt = DateTime.UtcNow,
            apps = JsonSerializer.Deserialize<object>(apps),
            settings = JsonSerializer.Deserialize<object>(settings)
        }, JsonOptions);

        var tmp = targetFilePath + ".tmp";
        File.WriteAllText(tmp, payload);
        File.Move(tmp, targetFilePath, true);

        _logger.Information("数据已导出：{Path}", targetFilePath);
        return Task.CompletedTask;
    }

    /// <summary>从单一备份文件导入（覆盖前自动备份现有数据）</summary>
    public Task ImportAsync(string sourceFilePath)
    {
        if (!File.Exists(sourceFilePath))
            throw new FileNotFoundException("导入文件不存在", sourceFilePath);

        var json = File.ReadAllText(sourceFilePath);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"导入文件已损坏：{sourceFilePath}", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("apps", out var appsEl) || !root.TryGetProperty("settings", out var settingsEl))
                throw new InvalidDataException("导入文件缺少 apps/settings 字段");

            // 覆盖前先自动备份现有数据
            CreateBackupAsync().GetAwaiter().GetResult();

            WriteAtomic(Path.Combine(_dataDir, "apps.json"), appsEl.GetRawText());
            WriteAtomic(Path.Combine(_dataDir, "settings.json"), settingsEl.GetRawText());
        }

        _logger.Information("数据已导入：{Path}", sourceFilePath);
        return Task.CompletedTask;
    }

    private static void WriteAtomic(string targetPath, string content)
    {
        var tmp = targetPath + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, targetPath, true);
    }

    private void CleanupRotation(int keepCount)
    {
        if (!Directory.Exists(_backupsDir)) return;
        foreach (var group in Directory.GetFiles(_backupsDir, "*.json").GroupBy(CategoryOf))
        {
            foreach (var old in group.OrderByDescending(f => f, StringComparer.Ordinal).Skip(keepCount))
            {
                try
                {
                    File.Delete(old);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "清理备份失败：{Path}", old);
                }
            }
        }
    }

    private static string CategoryOf(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var idx = name.IndexOf('_');
        return idx > 0 ? name[..idx] : name;
    }
}
