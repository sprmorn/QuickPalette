namespace QuickPalette.Domain.Models;

/// <summary>
/// 本地文件索引条目（Everything 式文件名索引）
/// </summary>
public class FileEntry
{
    /// <summary>文件名（不含路径）</summary>
    public required string Name { get; init; }

    /// <summary>完整路径（如 C:\Users\name\Documents\a.pdf）</summary>
    public required string Path { get; init; }

    /// <summary>文件大小（字节），目录为 0</summary>
    public long Size { get; init; }

    /// <summary>最后修改时间</summary>
    public DateTime Modified { get; init; }

    /// <summary>是否目录</summary>
    public bool IsDirectory { get; init; }

    /// <summary>NTFS 文件引用号（FRN），用于增量更新定位</summary>
    public long FileReference { get; init; }

    /// <summary>父目录的 FRN（快照 v4 起持久化，供 Worker 恢复轻量索引；主进程搜索不需要）</summary>
    public long ParentReference { get; init; }
}

/// <summary>
/// 快照原始条目（App/Worker 流式装载用）：v6 快照不再含完整 Path，
/// 仅保留搜索/重建所需的字段 + 卷下标；Path 由 FRN 父链在查询时重建。
/// </summary>
public readonly record struct RawEntry(string Name, int VolumeIndex, long Frn, long Parent, long Ticks, bool IsDir, long Size = 0);
