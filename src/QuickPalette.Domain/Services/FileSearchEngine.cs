namespace QuickPalette.Domain.Services;

using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using QuickPalette.Domain.Models;

/// <summary>
/// 本地文件搜索引擎（Everything 式紧凑索引）：
/// 索引只保存「文件名连续区 + FRN + 父链下标 + 分钟级修改时间 + 目录标志 + 字符掩码」，
/// 不保存完整路径字符串与托管字符串对象——完整路径在命中结果时沿父链惰性拼接。
/// 两级查询加速：①首字符分桶快速产出精确/前缀命中；②字符掩码（64 位桶）粗筛后子串匹配。
/// 注意：文件搜索按文件名匹配（Everything 默认模式），不支持路径片段搜索。
/// </summary>
public class FileSearchEngine
{
    /// <summary>NTFS 卷根目录的文件引用号（FRN 固定为 5）</summary>
    private const long NtfsRootFileReference = 5;

    /// <summary>父 FRN 低位掩码（64 位 FRN 取低 48 位有效）</summary>
    private const long FrnLowBitsMask = 0xFFFFFFFFFFFF;

    /// <summary>字符掩码桶数（名称字符按 char%64 映射到位）</summary>
    private const int CharMaskBuckets = 64;

    /// <summary>单卷紧凑索引</summary>
    public sealed class CompactIndex
    {
        public required string RootPath { get; init; }
        /// <summary>全部文件名连续存储（UTF-16LE），NameOffsets[i]..NameOffsets[i+1] 为第 i 条</summary>
        public required byte[] NameData { get; init; }
        public required int[] NameOffsets { get; init; }
        /// <summary>父条目下标：-2=卷根，-1=断链，>=0=父目录条目</summary>
        public required int[] ParentIndexes { get; init; }
        /// <summary>修改时间（UTC 绝对分钟数，int 安全至约 4081 年）</summary>
        public required int[] ModifiedMins { get; init; }
        public required byte[] Flags { get; init; } // bit0: IsDirectory
        /// <summary>FRN → 条目下标 的排序索引（二分）</summary>
        public required long[] FrnKeys { get; init; }
        public required int[] FrnIndexes { get; init; }
        /// <summary>每条名称包含字符的 64 位掩码（子串查询粗筛，必要条件：掩码包含查询全部字符）</summary>
        public required ulong[] CharMasks { get; init; }
        /// <summary>一级索引：按名称首字符（char%64）分桶的条目下标</summary>
        public required int[][] FirstCharBuckets { get; init; }

        public int Count => NameOffsets.Length - 1;

        /// <summary>第 index 条的文件名 span（零拷贝）</summary>
        public ReadOnlySpan<char> GetName(int index)
        {
            var start = NameOffsets[index];
            var end = NameOffsets[index + 1];
            return MemoryMarshal.Cast<byte, char>(NameData.AsSpan(start, end - start));
        }

        /// <summary>按 FRN 查条目下标（二分；找不到返回 -1）</summary>
        public int IndexOfFrn(long frn)
        {
            var lo = 0;
            var hi = FrnKeys.Length - 1;
            while (lo <= hi)
            {
                var mid = (lo + hi) >> 1;
                var v = FrnKeys[mid];
                if (v == frn) return FrnIndexes[mid];
                if (v < frn) lo = mid + 1;
                else hi = mid - 1;
            }
            return -1;
        }

        /// <summary>沿父链拼接完整路径（仅命中结果调用，代价 O(目录深度)）</summary>
        public string GetPath(int index)
        {
            var chain = new List<string>(12);
            var cur = index;
            while (true)
            {
                var parentIdx = ParentIndexes[cur];
                chain.Add(GetName(cur).ToString());
                if (parentIdx == -2) break; // 卷根
                if (parentIdx < 0) break;   // 断链（父目录已被删除且日志回绕）
                cur = parentIdx;
            }

            var sb = new StringBuilder(256);
            sb.Append(RootPath);
            for (var i = chain.Count - 1; i >= 0; i--)
                sb.Append('\\').Append(chain[i]);
            return sb.ToString();
        }

        /// <summary>
        /// 命中条目（惰性拼路径）。
        /// 字段语义：Size 恒为 0（紧凑索引不保存大小，避免每条 8 字节开销，UI 不展示大小）；
        /// ParentReference 恒为 0（FRN 未随条目常驻，仅排序索引保存；如需 FRN 用 IndexOfFrn 反查）。
        /// </summary>
        public FileEntry GetEntry(int index) => new()
        {
            Name = GetName(index).ToString(),
            Path = GetPath(index),
            Size = 0,
            Modified = new DateTime((long)ModifiedMins[index] * TimeSpan.TicksPerMinute, DateTimeKind.Utc),
            IsDirectory = (Flags[index] & 1) != 0,
            ParentReference = 0
        };
    }

    /// <summary>不可变装载快照（读侧整体替换，无撕裂）</summary>
    private sealed class Snapshot
    {
        public List<CompactIndex> Volumes { get; init; } = new();
        public int Count { get; init; }
        public DateTime LoadedAt { get; init; }
    }

    private readonly object _gate = new(); // 仅写侧串行化
    private Snapshot _snapshot = new();

    /// <summary>当前索引文件数（跨卷合计）</summary>
    public int Count => Volatile.Read(ref _snapshot).Count;

    /// <summary>索引最近一次加载时间</summary>
    public DateTime LoadedAt => Volatile.Read(ref _snapshot).LoadedAt;

    /// <summary>
    /// 流式装载索引（快照逐条消费，不构建完整 FileEntry 列表）。
    /// 按卷根切分到各 CompactIndex；不匹配任何卷根的条目被丢弃。
    /// </summary>
    public void Load(IEnumerable<FileEntry>? entries, IReadOnlyList<string>? rootPaths)
    {
        var builders = (rootPaths ?? Array.Empty<string>()).Select(r => new CompactBuilder(r)).ToList();

        if (entries != null)
        {
            foreach (var e in entries)
            {
                if (e?.Path == null) continue;
                foreach (var b in builders)
                {
                    if (e.Path.StartsWith(b.RootPath, StringComparison.OrdinalIgnoreCase))
                    {
                        b.Add(e);
                        break;
                    }
                }
            }
        }

        PublishSnapshot(builders);
    }

    /// <summary>
    /// 原始条目流式装载（App 用）：RawEntry 已含卷下标且 Path 未构造字符串，
    /// 配合 FileIndexStore.ReadSnapshotRaw 可将加载瞬时内存降至接近常驻水平。
    /// </summary>
    public void LoadRaw(IEnumerable<RawEntry>? entries, IReadOnlyList<string>? rootPaths)
    {
        var builders = (rootPaths ?? Array.Empty<string>()).Select(r => new CompactBuilder(r)).ToList();

        if (entries != null)
        {
            var n = 0;
            foreach (var e in entries)
            {
                if (e.VolumeIndex < 0 || e.VolumeIndex >= builders.Count) continue;
                builders[e.VolumeIndex].AddRaw(e);
                // 周期性做一次非阻塞 Gen0 回收（ReadSnapshotRaw 每条构造临时 Name 字符串），
                // 既压制加载峰值提交内存，又不在每条路径上付出完整 GC 代价。
                if ((++n & 0x7FFFFF) == 0)
                {
                    GC.Collect(0, GCCollectionMode.Optimized, blocking: false);
                }
            }
        }

        PublishSnapshot(builders);
    }

    private void PublishSnapshot(List<CompactBuilder> builders)
    {
        var snapshot = new Snapshot
        {
            Volumes = builders.Where(b => b.Count > 0).Select(b => b.Build()).ToList(),
        };
        snapshot = new Snapshot
        {
            Volumes = snapshot.Volumes,
            Count = snapshot.Volumes.Sum(v => v.Count),
            LoadedAt = DateTime.Now,
        };
        Volatile.Write(ref _snapshot, snapshot); // 读侧一次性热替换（双缓冲）
    }

    /// <summary>
    /// 执行文件搜索（大小写不敏感，按文件名匹配）。
    /// query 为 null/空白返回空；maxResults&lt;=0 视为 0 条（不抛异常）。
    /// </summary>
    public List<FileHit> Match(string? query, int maxResults)
    {
        var result = new List<FileHit>();
        if (maxResults <= 0) return result;
        var q = query?.Trim() ?? string.Empty;
        if (q.Length == 0) return result;
        ReadOnlySpan<char> qSpan = q;

        var snapshot = Volatile.Read(ref _snapshot);
        var hits = new List<FileHit>(Math.Min(snapshot.Count, 4096));

        foreach (var vol in snapshot.Volumes)
        {
            var count = vol.Count;
            var seen = ArrayPool<bool>.Shared.Rent(count);
            Array.Clear(seen, 0, count);
            try
            {
                // 一级：首字符分桶——精确/前缀命中几乎都落在查询首字符桶内，先扫该桶
                var bucket = char.ToLowerInvariant(q[0]) % CharMaskBuckets;
                foreach (var i in vol.FirstCharBuckets[bucket])
                {
                    var kind = MatchName(vol.GetName(i), qSpan);
                    if (kind == FileMatchKind.None) continue;
                    seen[i] = true;
                    hits.Add(new FileHit(vol, i, ScoreOf(kind, vol, i), kind));
                }

                // 二级：字符掩码粗筛 + 子串匹配（掩码不含查询全部字符的条目必然不匹配）
                var qMask = QueryMask(q);
                for (var i = 0; i < count; i++)
                {
                    if (seen[i]) continue;
                    if ((vol.CharMasks[i] & qMask) != qMask) continue;
                    if (MatchName(vol.GetName(i), qSpan) == FileMatchKind.None) continue;
                    hits.Add(new FileHit(vol, i, ScoreOf(FileMatchKind.NameSubstring, vol, i), FileMatchKind.NameSubstring));
                }
            }
            finally
            {
                ArrayPool<bool>.Shared.Return(seen);
            }
        }

        if (hits.Count == 0) return hits;

        // 分高优先，同分按修改时间新→旧（直接读并行数组，不触发路径拼接）
        hits.Sort(static (a, b) =>
        {
            var c = b.Score.CompareTo(a.Score);
            if (c != 0) return c;
            return b.ModifiedMins.CompareTo(a.ModifiedMins);
        });

        return hits.Count <= maxResults ? hits : hits.GetRange(0, maxResults);
    }

    private static double ScoreOf(FileMatchKind kind, CompactIndex vol, int i)
    {
        var score = kind switch
        {
            FileMatchKind.NameExact => 1000,
            FileMatchKind.NamePrefix => 800,
            FileMatchKind.NameSubstring => 600,
            _ => 0
        };
        // 目录略优先，帮助用户快速进入文件夹
        if ((vol.Flags[i] & 1) != 0) score += 50;
        return score;
    }

    private static ulong QueryMask(ReadOnlySpan<char> query)
    {
        ulong mask = 0;
        foreach (var c in query)
            mask |= 1UL << (char.ToLowerInvariant(c) % CharMaskBuckets);
        return mask;
    }

    private static FileMatchKind MatchName(ReadOnlySpan<char> name, ReadOnlySpan<char> query)
    {
        if (name.Equals(query, StringComparison.OrdinalIgnoreCase))
            return FileMatchKind.NameExact;
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return FileMatchKind.NamePrefix;
        if (name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
            return FileMatchKind.NameSubstring;
        return FileMatchKind.None;
    }

    /// <summary>构建 CompactIndex 的中间缓冲（逐条追加，Build 时定形）</summary>
    private sealed class CompactBuilder
    {
        private readonly List<string> _names = new();
        private readonly List<long> _frns = new();
        private readonly List<long> _parents = new();
        private readonly List<long> _modified = new();
        private readonly List<byte> _flags = new();
        // 名称去重驻留：node_modules、index.js 等高频重名只保留一个字符串实例
        private readonly Dictionary<string, string> _intern = new(StringComparer.Ordinal);

        public CompactBuilder(string rootPath)
        {
            RootPath = rootPath.EndsWith('\\') ? rootPath : rootPath + "\\";
        }

        public string RootPath { get; }
        public int Count => _names.Count;

        public void Add(FileEntry e)
        {
            _names.Add(Intern(e.Name));
            _frns.Add(e.FileReference);
            _parents.Add(e.ParentReference);
            _modified.Add(e.Modified.Ticks);
            _flags.Add(e.IsDirectory ? (byte)1 : (byte)0);
        }

        public void AddRaw(RawEntry e)
        {
            _names.Add(Intern(e.Name));
            _frns.Add(e.Frn);
            _parents.Add(e.Parent);
            _modified.Add(e.Ticks);
            _flags.Add(e.IsDir ? (byte)1 : (byte)0);
        }

        private string Intern(string name)
        {
            name ??= string.Empty;
            if (_intern.TryGetValue(name, out var existing)) return existing;
            _intern[name] = name;
            return name;
        }

        public CompactIndex Build()
        {
            var count = _frns.Count;

            // 1. FRN 排序索引（拆分数组，避免元组对齐/装箱开销）
            var frnKeys = new long[count];
            var frnIdx = new int[count];
            {
                var pairs = new (long Frn, int Index)[count];
                for (var i = 0; i < count; i++) pairs[i] = (_frns[i], i);
                Array.Sort(pairs, static (a, b) => a.Frn.CompareTo(b.Frn));
                for (var i = 0; i < count; i++)
                {
                    frnKeys[i] = pairs[i].Frn;
                    frnIdx[i] = pairs[i].Index;
                }
            }

            // 2. 父链转条目下标（-2=卷根，-1=断链）
            var parentIdx = new int[count];
            for (var i = 0; i < count; i++)
            {
                var p = _parents[i];
                if ((p & FrnLowBitsMask) == NtfsRootFileReference)
                {
                    parentIdx[i] = -2;
                    continue;
                }
                var pi = BinarySearch(frnKeys, frnIdx, p);
                parentIdx[i] = pi < 0 ? -1 : pi;
            }

            // 3. 名称连续区（UTF-16LE）+ 字符掩码 + 首字符分桶
            var nameData = new byte[count * 48]; // 预分配（平均 24 字符），超了自动扩
            var offsets = new int[count + 1];
            var charMasks = new ulong[count];
            var bucketCounts = new int[CharMaskBuckets];
            var written = 0;
            for (var i = 0; i < count; i++)
            {
                offsets[i] = written;
                var name = _names[i];
                ulong mask = 0;
                foreach (var c in name)
                {
                    var lower = char.ToLowerInvariant(c);
                    mask |= 1UL << (lower % CharMaskBuckets);
                }
                charMasks[i] = mask;

                var first = name.Length > 0 ? char.ToLowerInvariant(name[0]) % CharMaskBuckets : 0;
                bucketCounts[first]++;

                var bytes = Encoding.Unicode.GetBytes(name);
                if (written + bytes.Length > nameData.Length)
                    Array.Resize(ref nameData, Math.Max(nameData.Length * 2, written + bytes.Length));
                Buffer.BlockCopy(bytes, 0, nameData, written, bytes.Length);
                written += bytes.Length;
            }
            offsets[count] = written;
            if (nameData.Length != written) Array.Resize(ref nameData, written);

            var buckets = new int[CharMaskBuckets][];
            var fill = new int[CharMaskBuckets];
            for (var b = 0; b < CharMaskBuckets; b++) buckets[b] = new int[bucketCounts[b]];
            for (var i = 0; i < count; i++)
            {
                var name = _names[i];
                var first = name.Length > 0 ? char.ToLowerInvariant(name[0]) % CharMaskBuckets : 0;
                buckets[first][fill[first]++] = i;
            }

            // 4. 修改时间 → 绝对分钟数（int）
            var mins = new int[count];
            for (var i = 0; i < count; i++)
                mins[i] = (int)(_modified[i] / TimeSpan.TicksPerMinute);

            return new CompactIndex
            {
                RootPath = RootPath,
                NameData = nameData,
                NameOffsets = offsets,
                ParentIndexes = parentIdx,
                ModifiedMins = mins,
                Flags = _flags.ToArray(),
                FrnKeys = frnKeys,
                FrnIndexes = frnIdx,
                CharMasks = charMasks,
                FirstCharBuckets = buckets
            };
        }

        private static int BinarySearch(long[] keys, int[] values, long target)
        {
            var lo = 0;
            var hi = keys.Length - 1;
            while (lo <= hi)
            {
                var mid = (lo + hi) >> 1;
                var v = keys[mid];
                if (v == target) return values[mid];
                if (v < target) lo = mid + 1;
                else hi = mid - 1;
            }
            return -1;
        }
    }
}

/// <summary>
/// 文件搜索命中结果（惰性：Entry 首次访问时才拼接完整路径）
/// </summary>
public class FileHit
{
    private readonly FileSearchEngine.CompactIndex _vol;
    private readonly int _index;
    private FileEntry? _entry;

    public FileHit(FileSearchEngine.CompactIndex vol, int index, double score, FileMatchKind kind)
    {
        _vol = vol;
        _index = index;
        Score = score;
        Kind = kind;
    }

    /// <summary>命中的文件条目（首次访问时惰性拼路径）</summary>
    public FileEntry Entry => _entry ??= _vol.GetEntry(_index);

    /// <summary>修改时间（分钟刻度，并行数组直读，供排序避免触发拼路径）</summary>
    public int ModifiedMins => _vol.ModifiedMins[_index];

    /// <summary>匹配分数</summary>
    public double Score { get; }

    /// <summary>匹配维度</summary>
    public FileMatchKind Kind { get; }
}

/// <summary>
/// 文件匹配维度
/// </summary>
public enum FileMatchKind
{
    None,
    NameExact,
    NamePrefix,
    NameSubstring
}
