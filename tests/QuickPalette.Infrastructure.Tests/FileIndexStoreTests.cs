using System.Text;
using QuickPalette.Domain.Models;
using QuickPalette.Infrastructure.FileIndex;

namespace QuickPalette.Infrastructure.Tests.FileIndex;

/// <summary>
/// FileIndexStore 快照持久化（v6）测试：
/// 往返一致性（含卷下标）、截断/损坏快照返回 null、7-bit 长度前缀边界长名、
/// 条目区恶意前缀防护、节点级 LoadRaw/WriteSnapshotEntries 往返。
/// v6 起快照不再持久化完整 Path（由 FRN 父链在查询时重建），往返断言不含 Path。
/// 全部使用 %TEMP% 临时目录，不接触真实索引数据。
/// </summary>
public class FileIndexStoreTests : IDisposable
{
    private readonly string _dir;

    public FileIndexStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "QuickPaletteTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 清理失败忽略 */ }
    }

    private static FileEntry Entry(string name, string path, long frn, long parent, long size = 0, bool isDir = false)
        => new()
        {
            Name = name,
            Path = path,
            Size = size,
            Modified = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc),
            IsDirectory = isDir,
            FileReference = frn,
            ParentReference = parent
        };

    private static List<VolumeState> TwoVolumes() => new()
    {
        new() { RootPath = @"C:\", LastUsn = 42, JournalId = 7 },
        new() { RootPath = @"D:\", LastUsn = 43, JournalId = 8 },
    };

    [Fact]
    public void SaveAndLoadSnapshot_RoundTrip_PreservesAllFields()
    {
        var store = new FileIndexStore(_dir);
        var entries = new List<FileEntry>
        {
            Entry("a.txt", @"C:\Users\me\a.txt", 100, 5),
            Entry("中文名.pdf", @"D:\docs\中文名.pdf", 101, 6, size: 12345, isDir: false),
            Entry("folder", @"D:\folder", 102, 5, isDir: true),
        };
        var volumes = TwoVolumes();

        store.SaveSnapshot(entries, volumes);
        var loaded = store.ReadSnapshotRaw()?.ToList();

        Assert.NotNull(loaded);
        Assert.Equal(3, loaded!.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            Assert.Equal(entries[i].Name, loaded[i].Name);
            Assert.Equal(entries[i].Size, loaded[i].Size);
            Assert.Equal(entries[i].Modified.Ticks, loaded[i].Ticks);
            Assert.Equal(entries[i].IsDirectory, loaded[i].IsDir);
            Assert.Equal(entries[i].FileReference, loaded[i].Frn);
            Assert.Equal(entries[i].ParentReference, loaded[i].Parent);
            // 卷下标 = Path 与卷根前缀匹配推导（C:\ → 0，D:\ → 1）
            Assert.Equal(i == 0 ? 0 : 1, loaded[i].VolumeIndex);
        }

        // 元数据随快照更新：版本推进、计数、卷断点
        var meta = store.LoadMeta();
        Assert.NotNull(meta);
        Assert.Equal("ready", meta!.Status);
        Assert.Equal(3, meta.Count);
        Assert.Equal(2, meta.Volumes.Count);
        Assert.Equal(42L, meta.Volumes[0].LastUsn);
        Assert.Equal(8UL, meta.Volumes[1].JournalId);
    }

    [Fact]
    public void SaveSnapshotStreaming_ReadBack_MatchesEntries()
    {
        var store = new FileIndexStore(_dir);

        var count = store.SaveSnapshotStreaming(writer =>
        {
            FileIndexStore.WriteRawEntry(writer, "s1.txt", 0, 11, 638000000000000000L, false, 1, 5);
            FileIndexStore.WriteRawEntry(writer, "s2.txt", 1, 0, 638000000000000001L, true, 2, 5);
            return 2;
        }, TwoVolumes());

        Assert.Equal(2, count);
        var loaded = store.ReadSnapshotRaw()?.ToList();
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Count);
        Assert.Equal("s1.txt", loaded[0].Name);
        Assert.Equal(11, loaded[0].Size);
        Assert.Equal(1L, loaded[0].Frn);
        Assert.Equal("s2.txt", loaded[1].Name);
        Assert.Equal(1, loaded[1].VolumeIndex);
        Assert.True(loaded[1].IsDir);
    }

    [Fact]
    public void TruncatedSnapshot_ReturnsNull_OnAllReaders()
    {
        var store = new FileIndexStore(_dir);
        store.SaveSnapshot(new List<FileEntry> { Entry("a.txt", @"C:\a.txt", 1, 5), Entry("b.txt", @"C:\b.txt", 2, 5) },
            new List<VolumeState>());

        // 截断文件（去掉尾部 10 字节 → 页脚缺失）
        var bytes = File.ReadAllBytes(store.SnapshotPath);
        File.WriteAllBytes(store.SnapshotPath, bytes[..Math.Max(0, bytes.Length - 10)]);

        Assert.Null(store.ReadSnapshotRaw());
    }

    [Fact]
    public void CorruptHeader_ReturnsNull_OnAllReaders()
    {
        var store = new FileIndexStore(_dir);
        store.SaveSnapshot(new List<FileEntry> { Entry("a.txt", @"C:\a.txt", 1, 5) }, new List<VolumeState>());

        // 损坏头部 magic
        var bytes = File.ReadAllBytes(store.SnapshotPath);
        bytes[0] = (byte)'X';
        bytes[1] = (byte)'X';
        bytes[2] = (byte)'X';
        bytes[3] = (byte)'X';
        File.WriteAllBytes(store.SnapshotPath, bytes);

        Assert.Null(store.ReadSnapshotRaw());
    }

    [Fact]
    public void MissingSnapshot_ReturnsNull_OnAllReaders()
    {
        var store = new FileIndexStore(_dir);
        Assert.Null(store.ReadSnapshotRaw());
    }

    [Fact]
    public void SevenBitBoundary_LongNames_RoundTrip()
    {
        // BinaryWriter.Write(string) = 7-bit 长度前缀 + UTF-8：
        // 名称 UTF-8 字节数覆盖 1 字节前缀（<=127）与 2 字节前缀（128~16383）边界
        var store = new FileIndexStore(_dir);
        var longAscii127 = new string('a', 127);   // 恰好 127 字节 → 1 字节前缀上限
        var longAscii128 = new string('b', 128);   // 128 字节 → 2 字节前缀
        var longAscii300 = new string('c', 300);
        var cjk100 = new string('测', 100);        // 300 字节 UTF-8

        var entries = new List<FileEntry>
        {
            Entry(longAscii127, $@"C:\{longAscii127}", 1, 5),
            Entry(longAscii128, $@"C:\{longAscii128}", 2, 5),
            Entry(longAscii300, $@"D:\{longAscii300}", 3, 5),
            Entry(cjk100, $@"D:\{cjk100}\{cjk100}", 4, 5),
        };
        store.SaveSnapshot(entries, TwoVolumes());

        var raw = store.ReadSnapshotRaw()?.ToList();
        Assert.NotNull(raw);
        Assert.Equal(4, raw!.Count);
        Assert.Equal(longAscii127, raw[0].Name);
        Assert.Equal(longAscii128, raw[1].Name);
        Assert.Equal(longAscii300, raw[2].Name);
        Assert.Equal(cjk100, raw[3].Name);
        Assert.Equal(0, raw[0].VolumeIndex); // C:\
        Assert.Equal(1, raw[3].VolumeIndex); // D:\
    }

    [Fact]
    public void SaveSnapshot_VolumeMatch_IsCaseInsensitive_AndUnmatchedFallsToZero()
    {
        // 卷下标推导（FileEntry 兼容入口）：路径前缀大小写不敏感、自动补尾斜杠；
        // 未列入卷根的卷按 0 处理（v6 字节域不接受 -1）
        var store = new FileIndexStore(_dir);
        store.SaveSnapshot(new List<FileEntry>
        {
            Entry("a.txt", @"C:\Users\me\a.txt", 10, 5),
            Entry("b.txt", @"d:\other\b.txt", 11, 5),   // 小写盘符
            Entry("c.txt", @"E:\c.txt", 12, 5),          // E: 未列入卷根
        }, TwoVolumes());

        var raw = store.ReadSnapshotRaw()?.ToList();
        Assert.NotNull(raw);
        Assert.Equal(3, raw!.Count);
        Assert.Equal(0, raw[0].VolumeIndex);
        Assert.Equal(1, raw[1].VolumeIndex); // d:\ → D:\ 大小写不敏感
        Assert.Equal(0, raw[2].VolumeIndex); // 无匹配 → 0
    }

    [Fact]
    public void MaliciousSevenBitPrefix_ThrowsInvalidData()
    {
        // 手工构造"头/页脚合法、条目区 Name 7-bit 前缀恶意（6 个续字节）"的快照，
        // 验证溢出防护（>5 字节必须抛 InvalidDataException，不能死循环/回绕）
        var path = Path.Combine(_dir, "index.bin");
        using (var fs = File.Create(path))
        using (var w = new BinaryWriter(fs))
        {
            w.Write("QPFS");        // magic
            w.Write(6);             // 格式版本（v6）
            w.Write(1);             // 条目数
            for (var i = 0; i < 6; i++) w.Write((byte)0x80); // 恶意 7-bit 长度前缀（Name）
            w.Write((byte)0x01);
            w.Write((byte)0); w.Write(0L); w.Write(0L); w.Write(false); w.Write(1L); w.Write(5L); // 其余字段（不应被读到）
            w.Write(0x45504651u);   // 页脚魔数 "QPFE"
            w.Write(1);             // 页脚步数
        }

        var store = new FileIndexStore(_dir);
        var raw = store.ReadSnapshotRaw();
        Assert.NotNull(raw);
        Assert.Throws<InvalidDataException>(() =>
        {
            foreach (var _ in raw!) { /* 枚举时必须抛出 */ }
        });
    }

    [Fact]
    public void LoadRaw_And_WriteSnapshotEntries_RoundTrip()
    {
        // Worker 的节点级路径：LoadRaw（快照 → 节点）+ WriteSnapshotEntries（节点 → 快照）零路径往返
        var index = new InMemoryFileIndex(@"C:\");
        index.LoadRaw(new[]
        {
            new RawEntry("win-notepad.exe", 0, 100, 5, 638000000000000000L, false),
            new RawEntry("用户文档", 0, 200, 5, 638000000000000001L, true),
        });

        var store = new FileIndexStore(_dir);
        var count = store.SaveSnapshotStreaming(writer => index.WriteSnapshotEntries(writer, 0), TwoVolumes());
        Assert.Equal(2, count);

        var raw = store.ReadSnapshotRaw()?.ToList();
        Assert.NotNull(raw);
        Assert.Equal(2, raw!.Count);
        var byFrn = raw.ToDictionary(r => r.Frn);
        Assert.Equal("win-notepad.exe", byFrn[100].Name);
        Assert.Equal("用户文档", byFrn[200].Name);
        Assert.Equal(5L, byFrn[100].Parent);
        Assert.True(byFrn[200].IsDir);
    }

    [Fact]
    public void WriteSnapshotEntries_SkipsExcludedAndOrphans()
    {
        // 排除规则（卷根锚定）与孤儿过滤必须与 GetEntries 同语义：
        // \windows\ 命中排除；断链（父缺失）不落盘；users\ 下受保护
        var index = new InMemoryFileIndex(@"C:\");
        index.LoadRaw(new[]
        {
            new RawEntry("windows", 0, 10, 5, 1, true),            // C:\windows → 排除
            new RawEntry("system32", 0, 11, 10, 1, true),          // C:\windows\system32 → 随父排除
            new RawEntry("orphan.txt", 0, 20, 999, 1, false),      // 父 999 不存在 → 孤儿
            new RawEntry("users", 0, 30, 5, 1, true),              // C:\users
            new RawEntry("me", 0, 31, 30, 1, true),                // C:\users\me
            new RawEntry("notes.txt", 0, 32, 31, 1, false),        // C:\users\me\notes.txt → 保留
        });

        var store = new FileIndexStore(_dir);
        var count = store.SaveSnapshotStreaming(writer => index.WriteSnapshotEntries(writer, 0), TwoVolumes());

        // windows 树（2）与孤儿（1）被过滤；users 树 3 条保留
        Assert.Equal(3, count);
        var raw = store.ReadSnapshotRaw()?.ToList();
        Assert.NotNull(raw);
        Assert.Equal(3, raw!.Count);
        Assert.DoesNotContain(raw, r => r.Frn == 10 || r.Frn == 11 || r.Frn == 20);
        Assert.Contains(raw, r => r.Frn == 32 && r.Name == "notes.txt");
    }

    [Fact]
    public void BuildRawFromEnumeration_And_FilterExcluded_WorkerRebuildPath()
    {
        // Worker 重建路径：BuildRawFromEnumeration（去重语义）→ LoadRaw → FilterExcluded（链级排除）
        // （NativeMethods 为 internal，此处用 NTFS 标准常量字面值：
        //   FILE_ATTRIBUTE_DIRECTORY=0x10, USN_REASON_FILE_DELETE=0x200, USN_REASON_RENAME_NEW_NAME=0x2000）
        const uint attrDir = 0x10;
        const uint reasonDelete = 0x200;
        const uint reasonRenameNew = 0x2000;
        var t = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var records = new List<UsnRecord>
        {
            new(10, 5, 1, t, 0u, attrDir, "windows"),
            new(11, 10, 2, t, 0u, 0u, "notepad.exe"),
            new(20, 5, 3, t, 0u, attrDir, "docs"),
            new(21, 20, 4, t, 0u, 0u, "a.txt"),
            new(21, 20, 5, t.AddDays(1), reasonRenameNew, 0u, "b.txt"), // 同 FRN 保留最新
            new(30, 5, 6, t, 0u, 0u, "gone.txt"),
            new(30, 5, 7, t.AddDays(2), reasonDelete, 0u, "gone.txt"),  // 删除记录：整体丢弃
        };

        var nodes = FileIndexBuilder.BuildRawFromEnumeration(records);
        Assert.Equal(4, nodes.Count); // windows/notepad/docs/b.txt（gone 被删除记录丢弃）

        var index = new InMemoryFileIndex(@"C:\");
        index.LoadRaw(nodes);
        var removed = index.FilterExcluded();

        Assert.Equal(2, removed);                          // windows 树（自身+子项）链级排除
        Assert.Equal(3, index.Count);                      // docs + b.txt + 卷根合成节点
        Assert.Equal("b.txt", index.GetEntry(21)?.Name);   // 改名保留新名
        Assert.Null(index.GetEntry(11));                   // 排除项不驻留
    }

    [Fact]
    public void Meta_RoundTrip_PreservesStatusAndError()
    {
        var store = new FileIndexStore(_dir);
        store.SaveMeta(new IndexMeta
        {
            Version = 3,
            Count = 9,
            Status = "warning",
            Error = "警告: 卷 X 无法解析，已排除",
            UpdatedAt = DateTime.Now
        });

        var meta = store.LoadMeta();
        Assert.NotNull(meta);
        Assert.Equal(3, meta!.Version);
        Assert.Equal(9, meta.Count);
        Assert.Equal("warning", meta.Status);
        Assert.Equal("警告: 卷 X 无法解析，已排除", meta.Error);
    }

    [Fact]
    public void CorruptMeta_ReturnsNull()
    {
        var store = new FileIndexStore(_dir);
        File.WriteAllText(store.MetaPath, "{ not valid json !!!");
        Assert.Null(store.LoadMeta());
    }

    [Fact]
    public void WorkerLog_RollsBySize_KeepsRecentHistory()
    {
        var store = new FileIndexStore(_dir);
        var line = new string('x', 4096);
        for (var i = 0; i < 600; i++) store.Log(line); // 累计 > 2MB，触发滚动

        Assert.True(File.Exists(store.WorkerLogPath), "worker.log 应存在");
        Assert.True(File.Exists(Path.Combine(_dir, "worker.log.1")), "滚动后应有 worker.log.1");
        Assert.False(File.Exists(Path.Combine(_dir, "worker.log.4")), "历史只保留最近 3 份");
    }
}
