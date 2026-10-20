namespace QuickPalette.Infrastructure.Storage;

using System.Text.Json;
using System.Text.Json.Serialization;
using QuickPalette.Domain.Contracts;
using QuickPalette.Domain.Models;
using Serilog;

/// <summary>
/// JSON 文件存储实现 - 原子写入（fsync）+ 自动分类备份轮转 + 损坏自愈。
/// 安全保证：
///   ① 读失败（IO/解析损坏）绝不覆盖写——只有"文件不存在"才允许以默认值起步；
///   ② 写入 = 临时文件 + Flush(true) + 原子替换，共享冲突自动重试；
///   ③ 每次写主文件前先备份，按类别（apps_/settings_/pre_migration_）各保留最近 10 份。
/// </summary>
public class JsonRepository : IAppRepository, ISettingsRepository
{
    private readonly string _appsPath;
    private readonly string _settingsPath;
    private readonly string _backupsDir;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private static int _backupCounter; // 同毫秒多次写入的序号防覆盖

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true, // 修复：大小写不兼容导致的丢字段
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public JsonRepository(string dataDir, ILogger logger)
    {
        DataDirectory = dataDir;
        _appsPath = Path.Combine(dataDir, "apps.json");
        _settingsPath = Path.Combine(dataDir, "settings.json");
        _backupsDir = Path.Combine(dataDir, "backups");
        _logger = logger;

        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(_backupsDir);
    }

    /// <summary>数据目录（供 BackupService/HiddenItemStore/UsageStore 复用）</summary>
    public string DataDirectory { get; }

    #region IAppRepository

    public async Task<List<AppItem>> GetAllAsync()
    {
        await _lock.WaitAsync();
        try
        {
            return await ReadJsonFileAsync<List<AppItem>>(_appsPath) ?? new List<AppItem>();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<AppItem?> GetByIdAsync(string id)
    {
        var items = await GetAllAsync();
        return items.FirstOrDefault(x => x.Id == id);
    }

    public async Task AddAsync(AppItem item)
    {
        await _lock.WaitAsync();
        try
        {
            var items = await ReadForWriteAsync(_appsPath);
            items.Add(item);
            await WriteJsonFileAtomicAsync(_appsPath, items);
            _logger.Information("Added app: {Name} ({Id})", item.Name, item.Id);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpdateAsync(AppItem item)
    {
        await _lock.WaitAsync();
        try
        {
            var items = await ReadForWriteAsync(_appsPath);
            var index = items.FindIndex(x => x.Id == item.Id);
            if (index >= 0)
            {
                item.UpdatedAt = DateTime.UtcNow;
                items[index] = item;
                await WriteJsonFileAtomicAsync(_appsPath, items);
                _logger.Information("Updated app: {Name} ({Id})", item.Name, item.Id);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeleteAsync(string id)
    {
        await _lock.WaitAsync();
        try
        {
            var items = await ReadForWriteAsync(_appsPath);
            var removed = items.RemoveAll(x => x.Id == id);
            if (removed > 0)
            {
                await WriteJsonFileAtomicAsync(_appsPath, items);
                _logger.Information("Deleted app: {Id}", id);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAllAsync(IEnumerable<AppItem> items)
    {
        await _lock.WaitAsync();
        try
        {
            await WriteJsonFileAtomicAsync(_appsPath, items.ToList());
        }
        finally
        {
            _lock.Release();
        }
    }

    #endregion

    #region ISettingsRepository

    public async Task<Settings> GetAsync()
    {
        await _lock.WaitAsync();
        try
        {
            return await ReadJsonFileAsync<Settings>(_settingsPath) ?? new Settings();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(Settings settings)
    {
        await _lock.WaitAsync();
        try
        {
            await WriteJsonFileAtomicAsync(_settingsPath, settings);
            _logger.Information("Settings saved");
        }
        finally
        {
            _lock.Release();
        }
    }

    #endregion

    #region 原子写入与备份

    /// <summary>
    /// 供写路径读取：文件不存在返回空表；读失败（IO/损坏且备份恢复也失败）抛异常——
    /// 绝不以空表继续写入覆盖用户数据（修复数据覆盖缺陷）。
    /// </summary>
    private async Task<List<AppItem>> ReadForWriteAsync(string filePath)
    {
        if (!File.Exists(filePath))
            return new List<AppItem>();

        var data = await ReadJsonFileAsync<List<AppItem>>(filePath);
        return data ?? throw new IOException(
            $"数据文件读取失败且无法从备份恢复，已中止写入以保护数据：{filePath}");
    }

    /// <summary>
    /// 原子写入：临时文件 + fsync + 原子替换（共享冲突重试 3 次），随后备份轮转。
    /// </summary>
    private async Task WriteJsonFileAtomicAsync<T>(string filePath, T data)
    {
        // 1. 备份旧文件（分类命名，ms+序号防同秒覆盖）
        if (File.Exists(filePath))
        {
            var category = Path.GetFileNameWithoutExtension(filePath); // apps / settings
            BackupFile(filePath, category);
        }

        // 2. 写临时文件并强制落盘（fsync）
        var tempPath = filePath + ".tmp";
        var json = JsonSerializer.Serialize(data, JsonOptions);
        await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096,
                       FileOptions.WriteThrough))
        {
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(json);
            await writer.FlushAsync();
            stream.Flush(true);
        }

        // 3. 原子替换（共享冲突重试）
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(tempPath, filePath, true);
                break;
            }
            catch (IOException) when (attempt < 3)
            {
                await Task.Delay(attempt * 50);
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }
        }

        // 4. 分类轮转（每类保留最近 10 份）
        CleanupOldBackups();
    }

    /// <summary>备份到 backups\（类别_时间戳_序号.json）</summary>
    private void BackupFile(string filePath, string category)
    {
        try
        {
            var seq = Interlocked.Increment(ref _backupCounter);
            var backupName = $"{category}_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{seq}.json";
            File.Copy(filePath, Path.Combine(_backupsDir, backupName), true);
            _logger.Debug("Backup created: {BackupPath}", backupName);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Backup failed for {FilePath}", filePath);
        }
    }

    /// <summary>
    /// 读取 JSON：区分三种情况——
    /// 文件不存在→null（允许以默认值起步）；
    /// 解析损坏→从最近可读备份自愈（原子写回）；
    /// 读取 IO 失败/无法自愈→null 并记错误（调用方写路径会据此拒绝覆盖）。
    /// </summary>
    private async Task<T?> ReadJsonFileAsync<T>(string filePath) where T : class
    {
        if (!File.Exists(filePath))
            return null;

        string json;
        try
        {
            json = await File.ReadAllTextAsync(filePath);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "读取数据文件失败（不会覆盖写）：{FilePath}", filePath);
            return null;
        }

        try
        {
            var data = JsonSerializer.Deserialize<T>(json, JsonOptions);
            if (data is null)
                _logger.Warning("数据文件解析为空：{FilePath}", filePath);
            return data;
        }
        catch (JsonException ex)
        {
            _logger.Warning(ex, "JSON 文件损坏：{FilePath}，尝试从备份恢复", filePath);
            return await RecoverFromBackupAsync<T>(filePath);
        }
    }

    /// <summary>
    /// 从备份恢复（按时间倒序找最近可读备份；恢复回写用原子方式）
    /// </summary>
    private async Task<T?> RecoverFromBackupAsync<T>(string filePath) where T : class
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var backups = Directory.GetFiles(_backupsDir, $"{fileName}_*.json")
            .OrderByDescending(f => f, StringComparer.Ordinal)
            .ToArray();

        foreach (var backup in backups)
        {
            try
            {
                var json = await File.ReadAllTextAsync(backup);
                var data = JsonSerializer.Deserialize<T>(json, JsonOptions);
                if (data == null) continue;

                _logger.Information("从备份恢复数据：{BackupPath}", backup);
                await WriteJsonFileAtomicAsync(filePath, data); // 原子写回（含再次备份损坏文件的替代动作）
                return data;
            }
            catch
            {
                continue; // 试下一个备份
            }
        }

        _logger.Error("找不到可用备份：{FilePath}", filePath);
        return null;
    }

    /// <summary>
    /// 清理旧备份：按前缀类别（apps_/settings_/pre_migration_ 等）各保留最近 keepCount 份
    /// </summary>
    public void CleanupOldBackups(int keepCount = 10)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.GetFiles(_backupsDir, "*.json");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "列出备份目录失败");
            return;
        }

        foreach (var group in files.GroupBy(CategoryOf))
        {
            foreach (var backup in group
                         .OrderByDescending(f => f, StringComparer.Ordinal)
                         .Skip(keepCount))
            {
                try
                {
                    File.Delete(backup);
                    _logger.Debug("Deleted old backup: {BackupPath}", backup);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Failed to delete backup: {BackupPath}", backup);
                }
            }
        }
    }

    /// <summary>备份类别 = 文件名第一个下划线前的前缀（apps / settings / pre 等）</summary>
    private static string CategoryOf(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var idx = name.IndexOf('_');
        return idx > 0 ? name[..idx] : name;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 尽力清理 .tmp
        }
    }

    #endregion
}
