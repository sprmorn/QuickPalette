using QuickPalette.Domain.Models;
using QuickPalette.Infrastructure.FileIndex;

namespace QuickPalette.Infrastructure.Tests.FileIndex;

/// <summary>
/// InMemoryFileIndex 增量索引测试：删除、改名成对、孤儿父 FRN、FRN=5 卷根判定、
/// GetEntry 字段语义、并发安全。全部不接触真实卷/文件系统。
/// </summary>
public class InMemoryFileIndexTests
{
    // USN Reason 常量（winioctl.h）：测试内用字面量，避免暴露 internal NativeMethods
    private const uint ReasonCreate = 0x00000100;
    private const uint ReasonDelete = 0x00000200;
    private const uint ReasonRenameOld = 0x00001000;
    private const uint ReasonRenameNew = 0x00002000;
    private const uint AttrDirectory = 0x10;

    private static readonly DateTime Stamp = new(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc);

    private static UsnRecord Rec(long frn, long parent, string name, uint reason, uint attr = 0)
        => new(frn, parent, frn * 100, Stamp, reason, attr, name);

    [Fact]
    public void ApplyDelta_Delete_RemovesNode()
    {
        var idx = new InMemoryFileIndex(@"D:\");
        idx.ApplyDelta(new[]
        {
            Rec(10, 5, "a.txt", ReasonCreate),
            Rec(11, 5, "b.txt", ReasonCreate),
        });
        Assert.Equal(3, idx.Count); // 2 条 + 合成卷根

        idx.ApplyDelta(new[] { Rec(10, 5, "a.txt", ReasonDelete) });

        var entries = idx.BuildEntries();
        Assert.Single(entries);
        Assert.Equal("b.txt", entries[0].Name);
        Assert.Null(idx.GetEntry(10));
    }

    [Fact]
    public void ApplyDelta_RenamePair_InOrder_KeepsNewName()
    {
        var idx = new InMemoryFileIndex(@"D:\");
        idx.ApplyDelta(new[] { Rec(10, 5, "old.txt", ReasonCreate) });

        // 改名成对：旧名记录在前、新名记录在后（USN 日志顺序）
        idx.ApplyDelta(new[]
        {
            Rec(10, 5, "old.txt", ReasonRenameOld),
            Rec(10, 5, "new.txt", ReasonRenameNew),
        });

        var entries = idx.BuildEntries();
        Assert.Single(entries);
        Assert.Equal("new.txt", entries[0].Name);
        Assert.Equal(@"D:\new.txt", entries[0].Path);
    }

    [Fact]
    public void ApplyDelta_RenamePair_OutOfOrder_KeepsNewName()
    {
        var idx = new InMemoryFileIndex(@"D:\");
        idx.ApplyDelta(new[] { Rec(10, 5, "old.txt", ReasonCreate) });

        // 乱序（跨批/重放）：新名先到、旧名后到 —— 旧名记录不得把新名状态抹掉
        idx.ApplyDelta(new[]
        {
            Rec(10, 5, "new.txt", ReasonRenameNew),
            Rec(10, 5, "old.txt", ReasonRenameOld),
        });

        var entries = idx.BuildEntries();
        Assert.Single(entries);
        Assert.Equal("new.txt", entries[0].Name);
    }

    [Fact]
    public void ApplyDelta_DeleteThenRecreate_FinalStateIsNewRecord()
    {
        var idx = new InMemoryFileIndex(@"D:\");
        idx.ApplyDelta(new[] { Rec(10, 5, "a.txt", ReasonCreate) });
        idx.ApplyDelta(new[]
        {
            Rec(10, 5, "a.txt", ReasonDelete),
            Rec(10, 5, "a2.txt", ReasonCreate),
        });

        var entries = idx.BuildEntries();
        Assert.Single(entries);
        Assert.Equal("a2.txt", entries[0].Name);
    }

    [Fact]
    public void BuildEntries_OrphanParent_Skipped()
    {
        var idx = new InMemoryFileIndex(@"D:\");
        idx.ApplyDelta(new[]
        {
            Rec(20, 999, "orphan.txt", ReasonCreate),   // 父 FRN 999 不在索引 → 孤儿
            Rec(21, 5, "good.txt", ReasonCreate),
        });

        var entries = idx.BuildEntries();
        Assert.Single(entries);
        Assert.Equal("good.txt", entries[0].Name);

        // 流式/单条查询同样跳过孤儿
        var streamed = idx.GetEntries().ToList();
        Assert.Single(streamed);
        Assert.Null(idx.GetEntry(20));
    }

    [Fact]
    public void BuildEntries_OrphanChain_PartiallyResolved()
    {
        var idx = new InMemoryFileIndex(@"D:\");
        idx.ApplyDelta(new[]
        {
            Rec(30, 5, "dir", ReasonCreate, AttrDirectory),
            Rec(31, 30, "child.txt", ReasonCreate),     // 父链完好
            Rec(32, 888, "loose.txt", ReasonCreate),    // 888 缺失 → 孤儿
        });

        var entries = idx.BuildEntries().OrderBy(e => e.Name).ToList();
        Assert.Equal(2, entries.Count);
        Assert.Equal("child.txt", entries[0].Name);
        Assert.Equal(@"D:\dir\child.txt", entries[0].Path);
        Assert.Equal("dir", entries[1].Name);
    }

    [Fact]
    public void RootJudgement_FRN5_Variants_ResolveUnderRoot()
    {
        var idx = new InMemoryFileIndex(@"D:\data\");
        idx.ApplyDelta(new[]
        {
            Rec(40, 5, "plain.txt", ReasonCreate),                  // 父 = 裸记录号 5
            Rec(41, 0x0005000000000005, "seq.txt", ReasonCreate),   // 父 = 带序列号 5
            Rec(0x0005000000000005, 5, "rootitself.txt", ReasonCreate), // FRN 本身带序列号且归一化为 5 → 卷根，不产出
        });

        var entries = idx.BuildEntries().OrderBy(e => e.Name).ToList();
        Assert.Equal(2, entries.Count);
        Assert.Equal(@"D:\data\plain.txt", entries[0].Path);
        Assert.Equal(@"D:\data\seq.txt", entries[1].Path);
    }

    [Fact]
    public void GetEntry_FieldSemantics_SizeZeroAndNormalizedFrn()
    {
        var idx = new InMemoryFileIndex(@"D:\");
        idx.ApplyDelta(new[]
        {
            Rec(40, 5, "doc.txt", ReasonCreate),
            Rec(41, 5, "folder", ReasonCreate, AttrDirectory),
        });

        var e = idx.GetEntry(40);
        Assert.NotNull(e);
        // 字段语义：Size 恒 0（USN 不含大小）；FileReference/ParentReference 为记录号（低 48 位）
        Assert.Equal(0, e!.Size);
        Assert.Equal(40L, e.FileReference);
        Assert.Equal(5L, e.ParentReference);
        Assert.False(e.IsDirectory);
        Assert.Equal("doc.txt", e.Name);
        Assert.Equal(@"D:\doc.txt", e.Path);
        Assert.Equal(Stamp.Ticks, e.Modified.Ticks);

        var d = idx.GetEntry(41);
        Assert.NotNull(d);
        Assert.True(d!.IsDirectory);

        // 带序列号的 FRN 查询 → 归一化命中（0x0028_000000000028 的记录号仍是 40）
        var bySeq = idx.GetEntry(0x0028000000000028);
        Assert.NotNull(bySeq);
        Assert.Equal(40L, bySeq!.FileReference);

        // 卷根/不存在 → null
        Assert.Null(idx.GetEntry(5));
        Assert.Null(idx.GetEntry(0x0005000000000005));
        Assert.Null(idx.GetEntry(9999));
    }

    [Fact]
    public void Load_FromEntries_ResolvesPaths()
    {
        var idx = new InMemoryFileIndex(@"C:\");
        idx.Load(new[]
        {
            new FileEntry { Name = "dir", Path = @"C:\dir", Modified = Stamp, IsDirectory = true, FileReference = 10, ParentReference = 5 },
            new FileEntry { Name = "f.txt", Path = @"C:\dir\f.txt", Modified = Stamp, IsDirectory = false, FileReference = 11, ParentReference = 10 },
        });

        var entries = idx.BuildEntries().OrderBy(e => e.Name).ToList();
        Assert.Equal(2, entries.Count);
        Assert.Equal(@"C:\dir", entries[0].Path);
        Assert.Equal(@"C:\dir\f.txt", entries[1].Path);
        Assert.Equal(0, entries[1].Size); // Size 未存储 → 0
    }

    [Fact]
    public void ConcurrentApplyDeltaAndEnumeration_DoNotThrow()
    {
        var idx = new InMemoryFileIndex(@"D:\");
        // 数据从 FRN=10 起：FRN=5 是卷根记录号，业务记录撞 5 会被实现忽略（见 ApplyDelta 注释）
        idx.ApplyDelta(Enumerable.Range(10, 200).Select(i => Rec(i, 5, $"f{i}.txt", ReasonCreate)).ToList());

        // 用裸线程并发（本测试运行器不 await async 用例，保持同步方法）
        Exception? failure = null;
        var stop = false;
        var writer = new Thread(() =>
        {
            try
            {
                for (var round = 0; round < 50; round++)
                {
                    idx.ApplyDelta(Enumerable.Range(10, 50)
                        .Select(i => Rec(i, 5, $"f{i}-r{round}.txt", ReasonRenameNew)).ToList());
                }
            }
            catch (Exception ex) { failure = ex; }
            finally { Volatile.Write(ref stop, true); }
        });
        var reader = new Thread(() =>
        {
            try
            {
                while (!Volatile.Read(ref stop))
                {
                    _ = idx.Count;
                    foreach (var e in idx.GetEntries().Take(10)) { _ = e.Path; }
                }
            }
            catch (Exception ex) { failure = ex; }
        });

        writer.Start();
        reader.Start();
        Assert.True(writer.Join(TimeSpan.FromSeconds(30)), "写线程超时（疑似锁契约被破坏）");
        Assert.True(reader.Join(TimeSpan.FromSeconds(30)), "读线程超时（疑似锁契约被破坏）");
        Assert.Null(failure); // 若锁契约被破坏会抛异常
        Assert.Equal(201, idx.Count); // 200 + 合成卷根
    }
}
