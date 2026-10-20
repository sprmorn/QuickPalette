namespace QuickPalette.Domain.Contracts;

using QuickPalette.Domain.Models;

/// <summary>
/// 存储仓库接口
/// </summary>
public interface IAppRepository
{
    /// <summary>
    /// 获取所有应用（白名单）
    /// </summary>
    Task<List<AppItem>> GetAllAsync();

    /// <summary>
    /// 根据 ID 获取应用
    /// </summary>
    Task<AppItem?> GetByIdAsync(string id);

    /// <summary>
    /// 添加应用
    /// </summary>
    Task AddAsync(AppItem item);

    /// <summary>
    /// 更新应用
    /// </summary>
    Task UpdateAsync(AppItem item);

    /// <summary>
    /// 删除应用
    /// </summary>
    Task DeleteAsync(string id);

    /// <summary>
    /// 批量保存（原子操作）
    /// </summary>
    Task SaveAllAsync(IEnumerable<AppItem> items);
}

/// <summary>
/// 配置存储接口
/// </summary>
public interface ISettingsRepository
{
    /// <summary>
    /// 获取配置
    /// </summary>
    Task<Settings> GetAsync();

    /// <summary>
    /// 保存配置
    /// </summary>
    Task SaveAsync(Settings settings);
}

/// <summary>
/// 备份服务接口
/// </summary>
public interface IBackupService
{
    /// <summary>
    /// 创建备份
    /// </summary>
    Task<string> CreateBackupAsync();

    /// <summary>
    /// 从备份恢复
    /// </summary>
    Task RestoreFromBackupAsync(string backupPath);

    /// <summary>
    /// 获取最近的备份
    /// </summary>
    Task<string?> GetLatestBackupAsync();

    /// <summary>
    /// 清理旧备份
    /// </summary>
    Task CleanupOldBackupsAsync(int keepCount = 10);

    /// <summary>
    /// 导出全部数据为单一备份文件（quickpalette-backup.json）
    /// </summary>
    Task ExportAsync(string targetFilePath);

    /// <summary>
    /// 从单一备份文件导入（覆盖前自动备份现有数据）
    /// </summary>
    Task ImportAsync(string sourceFilePath);
}
