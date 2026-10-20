namespace QuickPalette.Infrastructure.Storage;

using System.Text.Json;
using Serilog;

/// <summary>
/// 隐藏记录存储（hidden.json）：被用户"隐藏/不再推荐"的条目 key 集合，
/// 防止候选池更新时把已隐藏项重新"复活"。key 规范化：小写。
/// </summary>
public class HiddenItemStore
{
    private readonly string _filePath;
    private readonly ILogger? _logger;
    private readonly object _gate = new();
    private HashSet<string> _keys;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public HiddenItemStore(string dataDir, ILogger? logger = null)
    {
        _filePath = Path.Combine(dataDir, "hidden.json");
        _logger = logger;
        _keys = Load();
    }

    /// <summary>全部隐藏 key</summary>
    public IReadOnlyCollection<string> Keys
    {
        get
        {
            lock (_gate) return _keys.ToArray();
        }
    }

    public bool IsHidden(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        lock (_gate) return _keys.Contains(Normalize(key));
    }

    public void Hide(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        lock (_gate)
        {
            if (!_keys.Add(Normalize(key))) return;
            Save();
        }
    }

    public void Unhide(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        lock (_gate)
        {
            if (!_keys.Remove(Normalize(key))) return;
            Save();
        }
    }

    private static string Normalize(string key) => key.Trim().ToLowerInvariant();

    private HashSet<string> Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var data = JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(_filePath), JsonOptions);
            return data != null
                ? new HashSet<string>(data, StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger?.Warning(ex, "hidden.json 读取失败，按空集合处理");
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Save()
    {
        try
        {
            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_keys.ToList(), JsonOptions));
            File.Move(tmp, _filePath, true);
        }
        catch (Exception ex)
        {
            _logger?.Warning(ex, "hidden.json 写入失败");
        }
    }
}
