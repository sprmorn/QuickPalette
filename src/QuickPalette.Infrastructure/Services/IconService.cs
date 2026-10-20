namespace QuickPalette.Infrastructure.Services;

using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using Serilog;

/// <summary>
/// 图标服务 - 提取和缓存应用图标（线程安全、GDI 资源全释放、缓存可按大小/时间清理）
/// </summary>
public class IconService : IDisposable
{
    /// <summary>默认缓存总大小上限（100MB）</summary>
    private const long DefaultMaxCacheBytes = 100L * 1024 * 1024;

    /// <summary>Windows 保留设备名（不能作为文件名）</summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private readonly string _iconsDir;
    private readonly ILogger _logger;

    /// <summary>内存缓存（appId → 图标文件路径），线程安全</summary>
    private readonly ConcurrentDictionary<string, string> _iconCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>清理操作互斥，防止并发清理互相踩踏</summary>
    private readonly object _cleanupLock = new();

    private bool _disposed;

    public IconService(string dataDir, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDir);
        _iconsDir = Path.Combine(dataDir, "icons");
        _logger = logger;
        Directory.CreateDirectory(_iconsDir);
    }

    /// <summary>
    /// 获取应用图标路径（带缓存）；提取失败返回 string.Empty
    /// </summary>
    public string GetIconPath(string appPath, string appId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(appId))
            return string.Empty;

        // 内存缓存命中
        if (_iconCache.TryGetValue(appId, out var cachedPath) && File.Exists(cachedPath))
            return cachedPath;

        // appId 作文件名时消毒非法字符
        var safeId = SanitizeFileName(appId);
        if (safeId.Length == 0)
            return string.Empty;

        var iconPath = Path.Combine(_iconsDir, safeId + ".png");

        // 磁盘缓存命中
        if (File.Exists(iconPath))
        {
            _iconCache[appId] = iconPath;
            return iconPath;
        }

        // 提取图标
        try
        {
            using var icon = ExtractIcon(appPath);
            if (icon == null)
                return string.Empty;

            SaveIcon(icon, iconPath);
            _iconCache[appId] = iconPath;
            return iconPath;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to extract icon for: {Path}", appPath);
            return string.Empty;
        }
    }

    /// <summary>
    /// 文件名消毒（纯函数，可独立测试）：
    /// 非法字符替换为 '_'，去掉结尾点/空格，空结果兜底 "_"，Windows 保留设备名加 "_" 前缀。
    /// </summary>
    public static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name.Trim())
        {
            sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        }

        // Windows 文件名不允许结尾的点与空格
        var result = sb.ToString().TrimEnd('.', ' ');
        if (result.Length == 0)
            return "_";

        // 保留设备名（CON/PRN/AUX/NUL/COM1-9/LPT1-9，含带扩展名形式如 con.txt）加前缀
        var stem = result;
        var dotIndex = result.IndexOf('.');
        if (dotIndex > 0)
            stem = result[..dotIndex];
        if (ReservedNames.Contains(stem))
            result = "_" + result;

        return result;
    }

    /// <summary>
    /// 提取 exe/关联文件的图标（失败返回 null）
    /// </summary>
    private static Icon? ExtractIcon(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return null;

        try
        {
            return Icon.ExtractAssociatedIcon(filePath);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 保存图标到文件：
    /// 全量编码进内存再原子落盘（等价于 WPF BitmapCacheOption.OnLoad + Freeze 的"完全载入、脱离句柄"语义，
    /// Bitmap/Icon 均 using 释放，不遗留 GDI 句柄与被占用文件）。
    /// </summary>
    private void SaveIcon(Icon icon, string path)
    {
        using var bitmap = icon.ToBitmap();
        using var buffer = new MemoryStream();
        bitmap.Save(buffer, ImageFormat.Png);

        var tempPath = path + ".tmp";
        File.WriteAllBytes(tempPath, buffer.ToArray());
        File.Move(tempPath, path, overwrite: true);
    }

    /// <summary>
    /// 清理缓存目录：先按时间（maxAgeDays）删除过期文件，再按总大小（maxTotalBytes）从最旧开始淘汰。
    /// </summary>
    public void CleanupCache(int maxAgeDays = 30, long maxTotalBytes = DefaultMaxCacheBytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_cleanupLock)
        {
            try
            {
                if (!Directory.Exists(_iconsDir))
                    return;

                var dir = new DirectoryInfo(_iconsDir);
                var removed = 0;

                // 1. 按时间清理
                var ageDays = Math.Max(0, maxAgeDays);
                var threshold = DateTime.Now.AddDays(-ageDays);
                foreach (var file in dir.GetFiles("*.png"))
                {
                    if (file.LastWriteTime < threshold && TryDelete(file))
                    {
                        removed++;
                    }
                }

                // 2. 按总大小清理（最旧优先）
                if (maxTotalBytes > 0)
                {
                    var remaining = dir.GetFiles("*.png")
                        .OrderBy(f => f.LastWriteTimeUtc)
                        .ToList();
                    var total = remaining.Sum(f => f.Length);

                    foreach (var file in remaining)
                    {
                        if (total <= maxTotalBytes)
                            break;

                        total -= file.Length;
                        if (TryDelete(file))
                        {
                            removed++;
                        }
                    }
                }

                // 3. 同步内存缓存：文件已被删除的条目移除
                foreach (var kv in _iconCache)
                {
                    if (!File.Exists(kv.Value))
                        _iconCache.TryRemove(kv.Key, out _);
                }

                if (removed > 0)
                    _logger.Information("Icon cache cleaned: {Removed} files removed", removed);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to cleanup icon cache");
            }
        }
    }

    /// <summary>
    /// 尝试删除单个缓存文件（失败记日志继续，不让单文件阻塞整体清理）
    /// </summary>
    private bool TryDelete(FileInfo file)
    {
        try
        {
            file.Delete();
            return true;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Failed to delete icon cache file: {File}", file.FullName);
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _iconCache.Clear();
    }
}
