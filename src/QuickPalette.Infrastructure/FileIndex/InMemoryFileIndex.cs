using System.Text;
using QuickPalette.Domain.Models;

namespace QuickPalette.Infrastructure.FileIndex;

/// <summary>
/// Worker 常驻的轻量文件索引（内存压缩版）：FRN(记录号) → (父记录号, 名称, 时间, 属性)。
///
/// 存储布局（目标：2M 条目常驻 ≤ 300MB）：
///  - 节点 = 28B 定长结构（父记录号/时间/属性/名字池区间），存于连续槽位数组（删除用空闲槽回收）；
///  - 名称统一存 UTF-8 名字池（2M 条 ≈ 40MB，替代 2M 个独立 string 的 ≈ 110MB）；
///  - FRN → 槽位 用 Dictionary&lt;long,int&gt;（每条 ≈ 24B，替代整节点入字典的 ≈ 48B）。
/// 合计每节点 ≈ 72B；路径仅在落盘/查询时沿 FRN 父链临时拼接（v6 快照零路径持久化）。
/// 名字池只增不缩（改名旧名成为空洞），增量场景下增长极缓，可接受。
///
/// 线程安全：所有公开成员内部持锁（ReaderWriterLockSlim）：
///  - Load / LoadRaw / ApplyDelta（写）互斥且与读互斥；
///  - Count / BuildEntries / GetEntries / GetEntry（读）可并发；
///  - GetEntries 为惰性迭代，读锁在枚举期间持有、枚举结束（Dispose）释放，
///    因此消费方应尽快完成遍历；写入方在落盘枚举期间短暂等待（秒级），不涉及任何阻塞 I/O。
/// </summary>
public class InMemoryFileIndex
{
    /// <summary>
    /// 内部压缩节点：NameOff/NameLen 定位名字池区间；NameOff &lt; 0 = 空槽（已删除）。
    /// Pack=4 紧凑到 28B（默认对齐会补到 32B）。
    /// </summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 4)]
    private struct Node
    {
        public long Parent;
        public long Time;   // FileTime
        public uint Attr;
        public int NameOff; // 名字池起始（<0 = 空槽）
        public int NameLen; // 名字 UTF-8 字节数
    }

    private Node[] _slots = new Node[1024];
    private int _slotCount;                            // 已用槽位数（含空洞）
    private readonly Stack<int> _freeSlots = new();    // 删除回收的槽位
    private readonly Dictionary<long, int> _index = new(); // FRN → 槽位
    private readonly HashSet<long> _excludedFrns = new(); // 被排除目录的 FRN：子项即使先到/晚到也不驻留
    private byte[] _namePool = new byte[1 << 16];      // UTF-8 名字池
    private int _poolLen;

    private readonly string _rootPath;
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);

    /// <summary>规则比较的 UTF-8 缓冲（与 FileIndexBuilder.ExcludedSegments 同源）</summary>
    private static readonly byte[][][] RuleSegmentsUtf8 = FileIndexBuilder.ExcludedSegmentsUtf8;
    private static readonly byte[] UsersSegmentUtf8 = Encoding.UTF8.GetBytes(FileIndexBuilder.UserProfilesSegment);

    public InMemoryFileIndex(string rootPath)
    {
        _rootPath = rootPath.EndsWith('\\') ? rootPath : rootPath + "\\";
    }

    /// <summary>卷根路径（含尾斜杠）</summary>
    public string RootPath => _rootPath;

    /// <summary>当前节点数（含卷根合成节点）</summary>
    public int Count
    {
        get
        {
            _lock.EnterReadLock();
            try { return _index.Count; }
            finally { _lock.ExitReadLock(); }
        }
    }

    // ───────────────────────── 装载 ─────────────────────────

    /// <summary>全量装载（快照恢复或枚举后），立即丢弃 Path 字符串</summary>
    public void Load(IEnumerable<FileEntry> entries)
    {
        _lock.EnterWriteLock();
        try
        {
            Reset();
            foreach (var e in entries)
            {
                var frn = FileIndexBuilder.NormalizeFrn(e.FileReference);
                if (frn == FileIndexBuilder.VolumeRootRecordNumber) continue; // 卷根记录忽略（根节点随后合成）
                UpsertNode(frn,
                    FileIndexBuilder.NormalizeFrn(e.ParentReference),
                    Encoding.UTF8.GetBytes(e.Name),
                    NativeMethods.SafeDateTimeToFileTimeUtc(e.Modified), // 坏时间单条防护
                    e.IsDirectory ? NativeMethods.FileAttributeDirectory : 0u);
            }
            EnsureRootNode();
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// 全量装载（v6 快照原始条目，Worker 恢复用）：仅取节点字段，
    /// 全程零路径字符串构造；名称仅在写入名字池时做一次 UTF-8 编码（瞬时小缓冲）。
    /// </summary>
    public void LoadRaw(IEnumerable<RawEntry> entries)
    {
        _lock.EnterWriteLock();
        try
        {
            Reset();
            foreach (var e in entries)
            {
                var frn = FileIndexBuilder.NormalizeFrn(e.Frn);
                if (frn == FileIndexBuilder.VolumeRootRecordNumber) continue; // 卷根记录忽略（根节点随后合成）
                UpsertNode(frn,
                    FileIndexBuilder.NormalizeFrn(e.Parent),
                    Encoding.UTF8.GetBytes(e.Name),
                    SafeTicksToFileTime(e.Ticks), // 坏时间单条防护
                    e.IsDir ? NativeMethods.FileAttributeDirectory : 0u);
            }
            EnsureRootNode();
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// 装载后过滤（重建路径配套）：沿 FRN 父链剔除命中排除规则的节点。
    /// 两遍式（先判定后删除）：若边遍历边删，先删父目录会让子项断链成孤儿逃过排除。
    /// 被排除的目录 FRN 记入 _excludedFrns：其子项即使随后续增量到来也不驻留。
    /// 返回剔除条数。
    /// </summary>
    public int FilterExcluded()
    {
        var chain = new List<long>(32);
        var doomed = new List<long>();
        _lock.EnterWriteLock();
        try
        {
            foreach (var (frn, slot) in _index)
            {
                if (frn == FileIndexBuilder.VolumeRootRecordNumber) continue;
                ref readonly var node = ref _slots[slot];
                if (EvaluateChain(node.Parent, GetNameSpan(slot), node.Attr, chain) == ChainVerdict.Excluded)
                {
                    doomed.Add(frn);
                    if ((node.Attr & NativeMethods.FileAttributeDirectory) != 0)
                    {
                        _excludedFrns.Add(frn); // 记住被排除的目录：子项不驻留
                    }
                }
            }

            foreach (var frn in doomed)
            {
                RemoveNode(frn);
            }
        }
        finally { _lock.ExitWriteLock(); }
        return doomed.Count;
    }

    /// <summary>坏时间单条防护：RawEntry.Ticks（DateTime ticks）→ FileTime</summary>
    private static long SafeTicksToFileTime(long ticks)
    {
        try
        {
            return NativeMethods.SafeDateTimeToFileTimeUtc(new DateTime(ticks, DateTimeKind.Utc));
        }
        catch
        {
            return DateTime.UtcNow.ToFileTimeUtc();
        }
    }

    /// <summary>坏 FileTime 单条防护：FileTime → DateTime ticks</summary>
    private static long SafeFileTimeToTicks(long fileTime)
    {
        try
        {
            return DateTime.FromFileTimeUtc(fileTime).Ticks;
        }
        catch
        {
            return DateTime.UtcNow.Ticks;
        }
    }

    // ───────────────────────── 增量 ─────────────────────────

    /// <summary>
    /// 应用一批 USN 增量记录。删除直接移除节点：
    /// USN 日志对整树删除会为其中每个子项各发一条 FILE_DELETE，无需递归清理；
    /// 若日志回绕丢失记录，残留孤儿节点会在落盘拼路径时被过滤。
    /// 改名成对处理：RENAME_OLD_NAME（仅旧名）记录只在索引里"仍是旧名"时移除节点，
    /// 随后的 RENAME_NEW_NAME 记录写入新名；若新名记录已先行应用（乱序/跨批），保留新名状态。
    /// 插入前按排除规则过滤（与落盘同语义）：被排除目录下的新增文件不再驻留内存。
    /// </summary>
    public void ApplyDelta(IReadOnlyList<UsnRecord> records)
    {
        var chain = new List<long>(32);
        _lock.EnterWriteLock();
        try
        {
            foreach (var r in records)
            {
                var reason = r.Reason;
                var frn = FileIndexBuilder.NormalizeFrn(r.FileReference);

                // 卷根（FRN=5）由合成节点负责；USN 实务中卷根不会作为普通文件变更出现，
                // 业务记录撞 FRN=5 一律忽略（也消除"父=自身"的自环污染）
                if (frn == FileIndexBuilder.VolumeRootRecordNumber) continue;

                // 已删除（文件已不存在）
                if ((reason & NativeMethods.UsnReasonFileDelete) != 0)
                {
                    RemoveNode(frn);
                    _excludedFrns.Remove(frn); // 被排除目录删除后，同 FRN 重建按新位置重新判定
                    continue;
                }

                // 纯旧名记录（无新名）：该名称已被替换，成对的新名记录随后写入新状态
                if ((reason & NativeMethods.UsnReasonRenameOldName) != 0 &&
                    (reason & NativeMethods.UsnReasonRenameNewName) == 0)
                {
                    if (_index.TryGetValue(frn, out var slot) &&
                        NameEqualsUtf8(GetNameSpan(slot), r.FileName))
                    {
                        RemoveNode(frn);
                    }
                    continue;
                }

                // 创建 / 改名（新名）/ 其他变更：覆盖为该记录的最新状态。
                // 同批内同一 FRN 的多条记录按 USN 顺序（旧名在前）先后处理，最终为新名。
                var nameUtf8 = Encoding.UTF8.GetBytes(r.FileName);
                var parent = FileIndexBuilder.NormalizeFrn(r.ParentReference);
                var attr = r.FileAttributes;

                // 排除规则入口过滤：系统目录/元文件下的新增节点不驻留（祖先链可判定时）；
                // 孤儿（父链断）不排除——由落盘过滤，父节点到来后自动恢复
                if (EvaluateChain(parent, nameUtf8, attr, chain) == ChainVerdict.Excluded)
                {
                    RemoveNode(frn); // 改名进排除区等场景：旧节点一并移除
                    if ((attr & NativeMethods.FileAttributeDirectory) != 0)
                    {
                        _excludedFrns.Add(frn); // 被排除目录：后续其子项不驻留
                    }
                    continue;
                }

                UpsertNode(frn, parent, nameUtf8,
                    NativeMethods.SafeDateTimeToFileTimeUtc(r.TimeStamp), // 坏 FILETIME 单条防护
                    attr);
            }

            EnsureRootNode(); // 合成卷根节点兜底（增量期间不会出现 FRN=5 的删除；防御性补齐）
        }
        finally { _lock.ExitWriteLock(); }
    }

    // ───────────────────────── 查询/产出 ─────────────────────────

    /// <summary>
    /// 拼出完整条目列表（一次性落盘用，诊断/测试）。
    /// </summary>
    public List<FileEntry> BuildEntries()
    {
        var pathCache = new Dictionary<long, string?>();
        var result = new List<FileEntry>();

        _lock.EnterReadLock();
        try
        {
            foreach (var (frn, slot) in _index)
            {
                var entry = BuildEntry(frn, slot, pathCache);
                if (entry != null) result.Add(entry);
            }
        }
        finally { _lock.ExitReadLock(); }

        return result;
    }

    /// <summary>
    /// 流式产出完整条目（诊断/测试）：逐条拼路径并 yield，临时路径缓存随迭代结束释放。
    /// 读锁在枚举期间持有（见类注释锁契约）。
    /// </summary>
    public IEnumerable<FileEntry> GetEntries()
    {
        var pathCache = new Dictionary<long, string?>();
        _lock.EnterReadLock();
        try
        {
            foreach (var (frn, slot) in _index)
            {
                var entry = BuildEntry(frn, slot, pathCache);
                if (entry != null) yield return entry;
            }
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// 节点级流式落盘（v6 快照）：直接写节点字段（名称从名字池直写），
    /// 零路径字符串构造、零 FileEntry 物化，消除旧 GetEntries() 路径缓存（214 万条 ≈ 0.4GB 瞬时）的落盘尖峰。
    /// 排除规则（系统目录/元文件/用户目录保护）沿 FRN 父链按"卷根起路径段"同语义判定
    /// （见 FileIndexBuilder.IsExcludedSegments）；孤儿/断链/自环节点跳过（与 GetEntries 一致）。
    /// 返回写入条数。
    /// </summary>
    public int WriteSnapshotEntries(BinaryWriter writer, int volumeIndex)
    {
        var chain = new List<long>(32); // 复用缓冲：父链 FRN（叶→根）
        var count = 0;

        _lock.EnterReadLock();
        try
        {
            foreach (var (frn, slot) in _index)
            {
                ref readonly var node = ref _slots[slot];
                if (frn == FileIndexBuilder.VolumeRootRecordNumber) continue; // 卷根合成节点不落盘
                if (node.NameLen == 0) continue;

                switch (EvaluateChain(node.Parent, GetNameSpan(slot), node.Attr, chain))
                {
                    case ChainVerdict.Orphan:   // 断链/孤儿/自环跳过
                    case ChainVerdict.Excluded: // 排除项跳过
                        continue;
                }

                FileIndexStore.WriteRawEntry(
                    writer, GetNameSpan(slot), (byte)volumeIndex, size: 0, // USN 索引不含大小（见 GetEntry 注释）
                    SafeFileTimeToTicks(node.Time),
                    (node.Attr & NativeMethods.FileAttributeDirectory) != 0,
                    frn, node.Parent);
                count++;
            }
        }
        finally { _lock.ExitReadLock(); }

        return count;
    }

    /// <summary>
    /// 按 FRN（任意形态，内部归一化为记录号）取单条完整条目，用于增量定位/诊断。
    /// 字段语义（USN 索引未存储的字段给 0/有效值）：
    ///  - Size：恒为 0 —— USN 记录不含文件大小（大小在 MFT $STANDARD_INFORMATION 才有）；
    ///  - FileReference：记录号（FRN 低 48 位，已去高 16 位序列号）；
    ///  - ParentReference：父目录记录号（同上）；
    ///  - Modified：USN 记录时间（UTC）；
    ///  - IsDirectory：记录 FileAttributes 的 FILE_ATTRIBUTE_DIRECTORY 位。
    /// 不存在 / 断链（父目录已丢）/ 卷根本身 → 返回 null。
    /// </summary>
    public FileEntry? GetEntry(long fileReference)
    {
        var frn = FileIndexBuilder.NormalizeFrn(fileReference);
        if (frn == FileIndexBuilder.VolumeRootRecordNumber) return null; // 卷根不是可检索条目

        var pathCache = new Dictionary<long, string?>();
        _lock.EnterReadLock();
        try
        {
            if (!_index.TryGetValue(frn, out var slot)) return null;
            return BuildEntry(frn, slot, pathCache);
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>构建单条条目（调用方必须已持读锁）；孤儿/断链/卷根/排除项返回 null</summary>
    private FileEntry? BuildEntry(long frn, int slot, Dictionary<long, string?> pathCache)
    {
        if (frn == FileIndexBuilder.VolumeRootRecordNumber) return null; // 卷根自身不作为可检索条目
        ref readonly var node = ref _slots[slot];
        if (node.NameLen == 0) return null;

        var path = ResolvePath(frn, pathCache);
        if (path == null) return null; // 孤儿/断链（父目录已被删除且日志回绕），跳过

        var name = Encoding.UTF8.GetString(GetNameSpan(slot));
        if (FileIndexBuilder.IsExcluded(path, name, node.Attr)) return null;

        return new FileEntry
        {
            Name = name,
            Path = path,
            // 字段语义同 GetEntry 注释：USN 索引不含大小 → Size 恒 0
            Size = 0,
            Modified = DateTime.FromFileTimeUtc(node.Time),
            IsDirectory = (node.Attr & NativeMethods.FileAttributeDirectory) != 0,
            FileReference = frn,
            ParentReference = node.Parent
        };
    }

    /// <summary>从 FRN 解析完整路径（迭代 + 记忆化，复用 FileIndexBuilder 的根/断链语义；调用方必须已持读锁）</summary>
    private string? ResolvePath(long frn, Dictionary<long, string?> cache)
    {
        if (cache.TryGetValue(frn, out var cached)) return cached;

        var chain = new List<long>();
        var cur = frn;
        while (true)
        {
            if (cache.TryGetValue(cur, out cached))
            {
                var path = cached;
                for (var i = chain.Count - 1; i >= 0; i--)
                {
                    if (path == null) break;
                    var name = Encoding.UTF8.GetString(GetNameSpanByFrn(chain[i]));
                    path = path.EndsWith('\\') ? path + name : path + "\\" + name;
                    cache.TryAdd(chain[i], path);
                }
                return path;
            }

            // 卷根判定：父引用可能带序列号（如 0x0005000000000005），比较记录号（低 48 位）
            if (FileIndexBuilder.NormalizeFrn(cur) == FileIndexBuilder.VolumeRootRecordNumber)
            {
                var root = _rootPath;
                for (var i = chain.Count - 1; i >= 0; i--)
                {
                    var name = Encoding.UTF8.GetString(GetNameSpanByFrn(chain[i]));
                    root = root.EndsWith('\\') ? root + name : root + "\\" + name;
                    cache.TryAdd(chain[i], root);
                }
                cache[FileIndexBuilder.NormalizeFrn(cur)] = _rootPath; // 根引用（记录号 5）的路径恒为卷根
                cache[frn] = root;
                return root;
            }

            if (!_index.TryGetValue(cur, out var slot) || chain.Count > 256) break;
            var node = _slots[slot];
            if (node.Parent == cur) break; // 自环，防御

            chain.Add(cur);
            cur = node.Parent;
        }

        // 断链：标记缓存，避免反复解析
        cache[frn] = null;
        return null;
    }

    // ───────────────────────── 排除规则（链级，零字符串） ─────────────────────────

    private enum ChainVerdict : byte
    {
        Keep = 0,
        Excluded = 1,
        Orphan = 2, // 断链/自环：无法判定（语义上不除外，由落盘过滤孤儿）
    }

    /// <summary>
    /// 沿 FRN 父链做排除判定（与 FileIndexBuilder.IsExcludedSegments 同语义，零字符串构造）：
    /// $ 元文件（自身名+属性）、&lt;卷根&gt;\users\... 保护、卷根锚定的规则段前缀。
    /// chain 为可复用缓冲。调用方必须已持锁。
    /// </summary>
    private ChainVerdict EvaluateChain(long parentFrn, ReadOnlySpan<byte> nameUtf8, uint attr, List<long> chain)
    {
        // NTFS 元文件（$MFT 等）：名称首字符 '$' + 系统属性
        if (nameUtf8.Length > 0 && nameUtf8[0] == (byte)'$' &&
            (attr & NativeMethods.FileAttributeSystem) != 0)
        {
            return ChainVerdict.Excluded;
        }

        // 祖先链（叶→根，含卷根）：祖先缺失时——在"已排除目录"集合中 → 整链排除；否则孤儿
        chain.Clear();
        var cur = parentFrn;
        var reachedRoot = false;
        while (chain.Count <= 512) // 防自环死循环（正常深度 < 64）
        {
            chain.Add(cur);
            if (cur == FileIndexBuilder.VolumeRootRecordNumber)
            {
                reachedRoot = true;
                break;
            }
            if (!_index.TryGetValue(cur, out var slot))
            {
                return _excludedFrns.Contains(cur) ? ChainVerdict.Excluded : ChainVerdict.Orphan;
            }
            var parent = _slots[slot].Parent;
            if (parent == cur && cur != FileIndexBuilder.VolumeRootRecordNumber) break; // 自环
            cur = parent;
        }
        if (!reachedRoot) return ChainVerdict.Orphan;

        // 路径段（卷根起，根→叶）：祖先段 = chain[Count-2]（顶端）… chain[0]（直接父），末段 = 自身。
        // segments 总数 = chain.Count（祖先 Count-1 段 + 自身 1 段）。
        var segCount = chain.Count;

        // 用户目录保护：segments.Count >= 2 且顶端段 == "users" → 一律保留
        if (segCount >= 2 && NameEqualsUtf8(GetNameSpan(FindSlot(chain[chain.Count - 2])), UsersSegmentUtf8))
        {
            return ChainVerdict.Keep;
        }

        // 卷根锚定：规则段序列必须从段 0 开始完整命中（规则最长 2 段）
        foreach (var rule in RuleSegmentsUtf8)
        {
            if (rule.Length == 0 || segCount < rule.Length) continue;
            var hit = true;
            for (var i = 0; i < rule.Length; i++)
            {
                // 段 i 的节点：i < Count-1 → chain[Count-2-i]；i == Count-1 → 自身
                ReadOnlySpan<byte> seg = i < chain.Count - 1
                    ? GetNameSpan(FindSlot(chain[chain.Count - 2 - i]))
                    : nameUtf8;
                if (!NameEqualsUtf8(seg, rule[i]))
                {
                    hit = false;
                    break;
                }
            }
            if (hit) return ChainVerdict.Excluded;
        }

        return ChainVerdict.Keep;
    }

    /// <summary>FRN → 槽位（调用方必须已持锁且保证存在；找不到按 0 防御）</summary>
    private int FindSlot(long frn) => _index.TryGetValue(frn, out var slot) ? slot : 0;

    /// <summary>UTF-8 名字与规则段比较：ASCII 折叠大小写，非 ASCII 要求字节完全一致（与 PathPrefixEqualsIgnoreCase 同语义）</summary>
    private static bool NameEqualsUtf8(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
        {
            var x = a[i];
            var y = b[i];
            if (x is >= (byte)'A' and <= (byte)'Z') x = (byte)(x + 32);
            if (y is >= (byte)'A' and <= (byte)'Z') y = (byte)(y + 32);
            if (x != y) return false;
        }
        return true;
    }

    /// <summary>字符串与 UTF-8 名字比较（改名旧名判定用）</summary>
    private static bool NameEqualsUtf8(ReadOnlySpan<byte> a, string b)
        => NameEqualsUtf8(a, Encoding.UTF8.GetBytes(b));

    // ───────────────────────── 存储原语（调用方必须已持写锁） ─────────────────────────

    private void Reset()
    {
        _slots = new Node[1024];
        _slotCount = 0;
        _freeSlots.Clear();
        _index.Clear();
        _excludedFrns.Clear();
        _poolLen = 0;
    }

    private void EnsureRootNode()
    {
        if (_index.ContainsKey(FileIndexBuilder.VolumeRootRecordNumber)) return;
        AddNode(FileIndexBuilder.VolumeRootRecordNumber, FileIndexBuilder.VolumeRootRecordNumber,
            ReadOnlySpan<byte>.Empty, DateTime.UtcNow.ToFileTimeUtc(), NativeMethods.FileAttributeDirectory);
    }

    private void UpsertNode(long frn, long parent, ReadOnlySpan<byte> nameUtf8, long time, uint attr)
    {
        if (_index.TryGetValue(frn, out var slot))
        {
            ref var node = ref _slots[slot];
            node.Parent = parent;
            node.Time = time;
            node.Attr = attr;
            (node.NameOff, node.NameLen) = AddName(nameUtf8);
        }
        else
        {
            AddNode(frn, parent, nameUtf8, time, attr);
        }
    }

    private void AddNode(long frn, long parent, ReadOnlySpan<byte> nameUtf8, long time, uint attr)
    {
        var slot = _freeSlots.Count > 0 ? _freeSlots.Pop() : _slotCount++;
        if (slot >= _slots.Length)
        {
            Array.Resize(ref _slots, Math.Max(_slots.Length * 2, slot + 1));
        }

        var (off, len) = AddName(nameUtf8);
        _slots[slot] = new Node
        {
            Parent = parent,
            Time = time,
            Attr = attr,
            NameOff = off,
            NameLen = len
        };
        _index[frn] = slot;
    }

    private void RemoveNode(long frn)
    {
        if (!_index.Remove(frn, out var slot)) return;
        _slots[slot].NameOff = -1; // 标空槽
        _slots[slot].NameLen = 0;
        _freeSlots.Push(slot);
    }

    /// <summary>名字写入池（返回区间）；池只增不缩（改名旧名留空洞，增量场景增长极缓）</summary>
    private (int Off, int Len) AddName(ReadOnlySpan<byte> utf8)
    {
        var off = _poolLen;
        var len = utf8.Length;
        if (len == 0) return (off, 0);

        if (_poolLen + len > _namePool.Length)
        {
            var next = _namePool.Length * 2;
            while (next < _poolLen + len) next *= 2;
            Array.Resize(ref _namePool, next);
        }

        utf8.CopyTo(_namePool.AsSpan(_poolLen, len));
        _poolLen += len;
        return (off, len);
    }

    private ReadOnlySpan<byte> GetNameSpan(int slot)
    {
        ref readonly var node = ref _slots[slot];
        return _namePool.AsSpan(node.NameOff, node.NameLen);
    }

    private ReadOnlySpan<byte> GetNameSpanByFrn(long frn)
        => _index.TryGetValue(frn, out var slot) ? GetNameSpan(slot) : ReadOnlySpan<byte>.Empty;
}
