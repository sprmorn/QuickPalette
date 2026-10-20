namespace QuickPalette.Infrastructure.Storage;

using System.Text.Json;
using Serilog;

/// <summary>
/// 使用统计存储（usage.json）：启动次数 + 最近使用时间，
/// 服务排序（指数衰减）与"最近使用"展示。调用方按 Save() 落盘（如退出/节流）。
/// </summary>
public class UsageStore
{
    private readonly string _filePath;
    private readonly ILogger? _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, UsageEntry> _entries;

    private sealed class UsageEntry
    {
        public int Count { get; set; }
        public DateTime? LastLaunched { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public UsageStore(string dataDir, ILogger? logger = null)
    {
        _filePath = Path.Combine(dataDir, "usage.json");
        _logger = logger;
        _entries = Load();
    }

    /// <summary>记录一次启动</summary>
    public void RecordLaunch(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return;
        var key = appId.Trim().ToLowerInvariant();
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new UsageEntry();
                _entries[key] = entry;
            }
            entry.Count++;
            entry.LastLaunched = DateTime.UtcNow;
        }
    }

    public int GetCount(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return 0;
        lock (_gate)
            return _entries.TryGetValue(appId.Trim().ToLowerInvariant(), out var e) ? e.Count : 0;
    }

    public DateTime? GetLastLaunched(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return null;
        lock (_gate)
            return _entries.TryGetValue(appId.Trim().ToLowerInvariant(), out var e) ? e.LastLaunched : null;
    }

    /// <summary>持久化（原子写）</summary>
    public void Save()
    {
        try
        {
            Dictionary<string, UsageEntry> snapshot;
            lock (_gate) snapshot = new Dictionary<string, UsageEntry>(_entries);

            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, JsonOptions));
            File.Move(tmp, _filePath, true);
        }
        catch (Exception ex)
        {
            _logger?.Warning(ex, "usage.json 写入失败");
        }
    }

    private Dictionary<string, UsageEntry> Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return new Dictionary<string, UsageEntry>(StringComparer.OrdinalIgnoreCase);
            var data = JsonSerializer.Deserialize<Dictionary<string, UsageEntry>>(File.ReadAllText(_filePath), JsonOptions);
            return data != null
                ? new Dictionary<string, UsageEntry>(data, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, UsageEntry>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger?.Warning(ex, "usage.json 读取失败，按空集合处理");
            return new Dictionary<string, UsageEntry>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
