using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using QuickPalette.Domain.Models;

namespace QuickPalette.Infrastructure.FileIndex;

/// <summary>
/// 单卷增量状态（增量续读的断点）
/// </summary>
public class VolumeState
{
    public string RootPath { get; set; } = string.Empty;
    public long LastUsn { get; set; }
    public ulong JournalId { get; set; }
}

/// <summary>
/// 索引元数据（主进程据此判断是否需要重载快照）
/// </summary>
public class IndexMeta
{
    public int Version { get; set; }
    public int Count { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string Status { get; set; } = "unknown"; // building / ready / error / no_permission / warning
    /// <summary>错误或警告信息（如"卷 X 含无法解析的 USN 记录，已排除"）</summary>
    public string? Error { get; set; }
    public List<VolumeState> Volumes { get; set; } = new();
}

/// <summary>
/// 文件索引快照持久化：index.bin（二进制快照）+ index.meta.json（版本元数据）。
/// 快照格式（v6）：
///   头：magic "QPFS"（BinaryWriter.Write(string)：7-bit 长度前缀 + UTF-8）+ int 格式版本 + int 条目数
///   体：每条 = Name（BinaryWriter.Write(string)）+ byte VolumeIndex
///        + long Size + long ModifiedTicks + bool IsDirectory + long FRN + long ParentFRN
///   尾：uint 页脚魔数 "QPFE" + int 条目数（与头部一致）
/// v6 相比 v5：不再持久化完整 Path（每条约 80B 纯负资产——读方仅做卷前缀比较后即弃，
/// 完整路径由 FRN 父链在查询时重建），改为 1 字节卷下标；
/// 200 万条的快照缩小约 60%，保存/加载全程零路径字符串构造。
/// 页脚用于廉价的完整性校验：文件被截断（最常见损坏形态）时页脚缺失/不一致，
/// 所有读取入口按"损坏快照"语义返回 null，触发全量重建，绝不产出不完整索引。
/// </summary>
public class FileIndexStore
{
    private const string Magic = "QPFS";
    private const int FormatVersion = 6;
    // v6 = v5（含页脚）- Path + VolumeIndex(byte)。
    // v5 及更早（含完整 Path）一律返回 null 触发全量重建（升级后一次性重建）。
    private const uint FooterMagicU32 = 0x45504651; // "QPFE"（小端：'Q''P''F''E'）
    private const int FooterLength = 8;             // uint 魔数 + int 条目数
    private const int MaxEntryCount = 100_000_000;

    // worker.log 滚动策略：单文件超过 2MB 滚动，保留最近 3 份历史
    private const long MaxLogBytes = 2L * 1024 * 1024;
    private const int LogHistoryCount = 3;

    private readonly string _directory;
    private readonly object _logLock = new();

    public FileIndexStore(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public string DirectoryPath => _directory;
    public string SnapshotPath => Path.Combine(_directory, "index.bin");
    public string MetaPath => Path.Combine(_directory, "index.meta.json");
    public string WorkerLogPath => Path.Combine(_directory, "worker.log");

    /// <summary>读取元数据（不存在或损坏返回 null）</summary>
    public IndexMeta? LoadMeta()
    {
        try
        {
            if (!File.Exists(MetaPath)) return null;
            var json = File.ReadAllText(MetaPath);
            return JsonSerializer.Deserialize<IndexMeta>(json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>原子写入元数据（临时文件 fsync + 原子替换）</summary>
    public void SaveMeta(IndexMeta meta)
    {
        var json = JsonSerializer.Serialize(meta);
        var tmp = MetaPath + ".tmp";
        WriteAllTextFsync(tmp, json);
        File.Move(tmp, MetaPath, true);
    }

    /// <summary>保存快照（临时文件 + fsync + 原子替换），并更新元数据版本。
    /// FileEntry 便捷入口：卷下标按 Path 与卷根前缀匹配推导（v5 兼容语义）。</summary>
    public void SaveSnapshot(IReadOnlyList<FileEntry> entries, IReadOnlyList<VolumeState> volumes)
    {
        var rootBytes = EncodeRoots(volumes.Select(v => v.RootPath));
        SaveSnapshotStreaming(writer =>
        {
            var i = 0;
            foreach (var e in entries)
            {
                var vi = MatchVolume(PathToUtf8(e.Path), rootBytes);
                var volIdx = (byte)(vi < 0 ? 0 : vi); // 无匹配卷根按 0 处理（v5 读方的 -1 语义不进字节域）
                WriteRawEntry(writer, e.Name, (byte)volIdx, e.Size, e.Modified.Ticks,
                    e.IsDirectory, e.FileReference, e.ParentReference);
                i++;
            }
            return i;
        }, volumes);
    }

    /// <summary>
    /// 流式保存快照（Worker 落盘用）：回调逐条写条目，写完后回填条目数并追加页脚。
    /// 相比 SaveSnapshot 不构建完整 List&lt;FileEntry&gt;，落盘峰值显著降低。
    /// 写入完成后 fsync（FlushToDisk）再原子替换，掉电不会留下"看似完整"的半截快照。
    /// </summary>
    public int SaveSnapshotStreaming(Func<BinaryWriter, int> writeEntries, IReadOnlyList<VolumeState> volumes)
    {
        var tmp = SnapshotPath + ".tmp";
        int count;
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new BinaryWriter(fs))
        {
            writer.Write(Magic);
            writer.Write(FormatVersion);
            var countPos = fs.Position;
            writer.Write(0); // 占位，写完回填

            count = writeEntries(writer);

            // 页脚：魔数 + 条目数（读取端据此检测截断）
            writer.Write(FooterMagicU32);
            writer.Write(count);

            fs.Seek(countPos, SeekOrigin.Begin);
            writer.Write(count);
            writer.Flush();
            fs.Flush(true); // fsync：数据真正落盘后再替换
        }

        File.Move(tmp, SnapshotPath, true);

        var meta = LoadMeta() ?? new IndexMeta();
        meta.Version++;
        meta.Count = count;
        meta.UpdatedAt = DateTime.Now;
        meta.Status = "ready";
        meta.Volumes = volumes.ToList();
        SaveMeta(meta);

        return count;
    }

    /// <summary>写一条 v6 原始条目（Worker 流式落盘与兼容入口共用）</summary>
    public static void WriteRawEntry(
        BinaryWriter writer, string name, byte volumeIndex, long size,
        long ticks, bool isDir, long frn, long parent)
    {
        writer.Write(name);
        writer.Write(volumeIndex);
        writer.Write(size);
        writer.Write(ticks);
        writer.Write(isDir);
        writer.Write(frn);
        writer.Write(parent);
    }

    /// <summary>
    /// 写一条 v6 原始条目（名称为 UTF-8 字节，来自名字池——零字符串构造路径专用）。
    /// 名称编码与 BinaryWriter.Write(string) 字节格式完全一致：7-bit 长度前缀 + UTF-8。
    /// </summary>
    public static void WriteRawEntry(
        BinaryWriter writer, ReadOnlySpan<byte> nameUtf8, byte volumeIndex, long size,
        long ticks, bool isDir, long frn, long parent)
    {
        Write7BitEncodedInt(writer, nameUtf8.Length);
        writer.Write(nameUtf8);
        writer.Write(volumeIndex);
        writer.Write(size);
        writer.Write(ticks);
        writer.Write(isDir);
        writer.Write(frn);
        writer.Write(parent);
    }

    /// <summary>7-bit 变长整数写入（BinaryWriter.Write(string) 的长度前缀格式）</summary>
    private static void Write7BitEncodedInt(BinaryWriter writer, int value)
    {
        var v = (uint)value;
        while (v >= 0x80)
        {
            writer.Write((byte)(v | 0x80));
            v >>= 7;
        }
        writer.Write((byte)v);
    }

    // v5 的 LoadSnapshot / ReadSnapshotEntries / ReadEntry（逐条构造 FileEntry + 完整 Path，
    // 214 万条 ≈ 0.9GB 瞬时驻留）已在 v6 删除：读取统一走 ReadSnapshotRaw（零路径构造）。

    /// <summary>
    /// 原始流式读取快照（App 与 Worker 装载共用）：逐条产出 <see cref="RawEntry"/>，
    /// 全程零路径字符串构造（v6 条目自带卷下标，Name 之外不保留任何字符串）。
    /// 头部/页脚校验失败 → 返回 null；条目区损坏 → 枚举时抛 <see cref="InvalidDataException"/>。
    /// </summary>
    public IEnumerable<RawEntry>? ReadSnapshotRaw()
    {
        try
        {
            if (!TryOpenSnapshot(out var fs, out var reader, out var count)) return null;
            return ReadRawCore(reader, fs, count);
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<RawEntry> ReadRawCore(BinaryReader reader, FileStream fs, int count)
    {
        using (fs)
        using (reader)
        {
            for (var i = 0; i < count; i++)
            {
                string name;
                byte volIdx;
                long size, ticks, frn, parent;
                bool isDir;
                try
                {
                    name = reader.ReadString();
                    volIdx = reader.ReadByte();
                    size = reader.ReadInt64();
                    ticks = reader.ReadInt64();
                    isDir = reader.ReadBoolean();
                    frn = reader.ReadInt64();
                    parent = reader.ReadInt64();
                }
                catch (Exception ex) when (ex is EndOfStreamException or IOException or InvalidDataException
                    or FormatException or ArgumentOutOfRangeException or DecoderFallbackException)
                {
                    // 条目区损坏：显式失败（不能默默少返回条目），调用方按重建处理
                    throw new InvalidDataException($"快照第 {i + 1}/{count} 条条目损坏，无法解析", ex);
                }

                yield return new RawEntry(name, volIdx, frn, parent, ticks, isDir, size);
            }
        }
    }

    /// <summary>编码卷根前缀（UTF-8，含尾斜杠）：FileEntry 入口按 Path 前缀推导卷下标用</summary>
    private static byte[][] EncodeRoots(IEnumerable<string> rootPaths)
        => rootPaths
            .Select(r => Encoding.UTF8.GetBytes(r.EndsWith('\\') ? r : r + "\\"))
            .ToArray();

    /// <summary>Path → UTF-8 字节（卷前缀匹配用；仅 FileEntry 兼容入口调用，Worker 流式路径不经过）</summary>
    private static byte[] PathToUtf8(string path)
        => Encoding.UTF8.GetBytes(path);

    /// <summary>
    /// 卷前缀匹配：大小写不敏感（盘符/路径段多为 ASCII，做 ASCII 折叠；非 ASCII 字节要求完全一致）。
    /// rootBytes 均含尾斜杠，天然按路径段边界匹配。无匹配返回 -1。
    /// 仅供 FileEntry 兼容保存入口推导卷下标；v6 读取端直接使用条目内卷下标。
    /// </summary>
    private static int MatchVolume(ReadOnlySpan<byte> path, byte[][] rootBytes)
    {
        for (var v = 0; v < rootBytes.Length; v++)
        {
            var root = rootBytes[v];
            if (path.Length >= root.Length && PathPrefixEqualsIgnoreCase(path, root))
                return v;
        }
        return -1;
    }

    private static bool PathPrefixEqualsIgnoreCase(ReadOnlySpan<byte> path, byte[] root)
    {
        for (var i = 0; i < root.Length; i++)
        {
            if (FoldAscii(path[i]) != FoldAscii(root[i])) return false;
        }
        return true;
    }

    private static byte FoldAscii(byte b) => b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b + 32) : b;

    /// <summary>
    /// 打开并校验快照（头部 magic/格式版本/条目数 + 页脚完整性）。
    /// 校验失败返回 false 并释放已打开的流；成功时调用方接管 fs/reader 的释放。
    /// </summary>
    private bool TryOpenSnapshot(
        [NotNullWhen(true)] out FileStream? fs,
        [NotNullWhen(true)] out BinaryReader? reader,
        out int count)
    {
        fs = null;
        reader = null;
        count = 0;

        try
        {
            if (!File.Exists(SnapshotPath)) return false;

            var stream = new FileStream(SnapshotPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var br = new BinaryReader(stream);
            try
            {
                var magic = br.ReadString();
                if (magic != Magic) return Fail();

                var format = br.ReadInt32();
                if (format != FormatVersion) return Fail(); // 旧格式/未知格式：返回 null 触发全量重建

                var headerCount = br.ReadInt32();
                if (headerCount < 0 || headerCount > MaxEntryCount) return Fail();
                var entriesStart = stream.Position;

                // 页脚校验：末尾 8 字节 = "QPFE" + 条目数。截断文件必然缺失或不一致 → 按损坏返回 null
                if (stream.Length < entriesStart + FooterLength) return Fail();
                stream.Seek(-FooterLength, SeekOrigin.End);
                var footerMagic = br.ReadUInt32();
                var footerCount = br.ReadInt32();
                if (footerMagic != FooterMagicU32 || footerCount != headerCount) return Fail();

                stream.Seek(entriesStart, SeekOrigin.Begin);

                count = headerCount;
                fs = stream;
                reader = br;
                return true;

                bool Fail()
                {
                    br.Dispose();
                    stream.Dispose();
                    return false;
                }
            }
            catch
            {
                br.Dispose();
                stream.Dispose();
                return false;
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>写文本并 fsync（元数据临时文件用）</summary>
    private static void WriteAllTextFsync(string path, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        fs.Write(bytes, 0, bytes.Length);
        fs.Flush(true);
    }

    /// <summary>
    /// 简单追加日志（Worker 使用）：按大小滚动，保留最近 <see cref="LogHistoryCount"/> 份历史
    /// （worker.log → worker.log.1 → ... ，超龄删除），单实例内用锁串行化写入。
    /// </summary>
    public void Log(string message)
    {
        lock (_logLock)
        {
            try
            {
                RollLogIfNeeded();
                File.AppendAllText(WorkerLogPath, $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
            }
            catch
            {
                // 日志失败忽略
            }
        }
    }

    private void RollLogIfNeeded()
    {
        var fi = new FileInfo(WorkerLogPath);
        if (!fi.Exists || fi.Length < MaxLogBytes) return;

        // 删除最旧一份 → 依次改名 worker.log.N → worker.log.N+1 → worker.log 改为 worker.log.1
        var oldest = Path.Combine(_directory, $"worker.log.{LogHistoryCount}");
        if (File.Exists(oldest)) File.Delete(oldest);
        for (var i = LogHistoryCount - 1; i >= 1; i--)
        {
            var src = Path.Combine(_directory, $"worker.log.{i}");
            if (File.Exists(src)) File.Move(src, Path.Combine(_directory, $"worker.log.{i + 1}"), true);
        }
        File.Move(WorkerLogPath, Path.Combine(_directory, "worker.log.1"), true);
    }
}
