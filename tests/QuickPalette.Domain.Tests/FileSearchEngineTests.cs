namespace QuickPalette.Domain.Tests;

using System.Diagnostics;
using QuickPalette.Domain.Models;
using QuickPalette.Domain.Services;
using Xunit;

/// <summary>
/// FileSearchEngine 装载/查询/边界测试
/// </summary>
public class FileSearchEngineTests
{
    private static FileEntry Entry(string name, string path, long frn, long parent, bool isDir = false) => new()
    {
        Name = name,
        Path = path,
        FileReference = frn,
        ParentReference = parent,
        IsDirectory = isDir,
        Modified = DateTime.UtcNow
    };

    [Fact]
    public void Load_And_Match_Basic()
    {
        var engine = new FileSearchEngine();
        var entries = new[]
        {
            Entry("readme.md", @"D:\docs\readme.md", 101, 5),
            Entry("report.docx", @"D:\docs\report.docx", 102, 5),
            Entry("readme-old.md", @"D:\docs\readme-old.md", 103, 5),
        };
        engine.Load(entries, new[] { @"D:\" });

        var hits = engine.Match("readme", 10);

        Assert.Equal(2, hits.Count);
        Assert.Equal(FileMatchKind.NamePrefix, hits[0].Kind); // readme.md 精确/前缀优于 readme-old 子串
    }

    [Fact]
    public void MaxResults_Truncates()
    {
        var engine = new FileSearchEngine();
        var entries = Enumerable.Range(1, 50)
            .Select(i => Entry($"file{i}.txt", $@"D:\f{i}.txt", 1000 + i, 5))
            .ToArray();
        engine.Load(entries, new[] { @"D:\" });

        Assert.Equal(5, engine.Match("file", 5).Count);
    }

    [Fact]
    public void InvalidQueryOrMaxResults_DoNotThrow()
    {
        var engine = new FileSearchEngine();
        engine.Load(new[] { Entry("a.txt", @"D:\a.txt", 101, 5) }, new[] { @"D:\" });

        Assert.Empty(engine.Match(null!, 10));
        Assert.Empty(engine.Match("   ", 10));
        Assert.Empty(engine.Match("a", 0));
        Assert.Empty(engine.Match("a", -3));
    }

    [Fact]
    public void BrokenParentChain_StillReturns_PathWithoutThrow()
    {
        var engine = new FileSearchEngine();
        // 父 FRN 200 不在索引中（断链）
        var entries = new[] { Entry("orphan.txt", @"D:\orphan.txt", 101, 200) };
        engine.Load(entries, new[] { @"D:\" });

        var hit = engine.Match("orphan", 10).Single();

        Assert.Contains("orphan.txt", hit.Entry.Path);
    }

    [Fact]
    public void ManyDuplicates_LoadCorrectly_WithoutBlowingUp()
    {
        var engine = new FileSearchEngine();
        var entries = Enumerable.Range(1, 100)
            .Select(i => Entry("index.js", $@"D:\p{i}\index.js", 1000 + i, 5))
            .ToArray();
        engine.Load(entries, new[] { @"D:\" });

        var hits = engine.Match("index", 100).ToList();

        Assert.Equal(100, hits.Count); // 高频重名全部保留（装载期已做字符串驻留压缩）
        Assert.Equal(100, engine.Count);
    }

    [Fact]
    public void LoadRaw_Works_WithVolumeIndex()
    {
        var engine = new FileSearchEngine();
        var raws = new[]
        {
            new RawEntry("notes.txt", 0, 301, 5, DateTime.UtcNow.Ticks, false),
            new RawEntry("docs", 0, 302, 5, DateTime.UtcNow.Ticks, true),
        };

        engine.LoadRaw(raws, new[] { @"C:\" });

        Assert.Equal(2, engine.Count);
        Assert.Single(engine.Match("notes", 10));
    }

    [Fact]
    public void PerfSmoke_20kEntries_QueryFast()
    {
        var engine = new FileSearchEngine();
        var entries = Enumerable.Range(1, 20000)
            .Select(i => Entry($"document{i}.pdf", $@"D:\d{i % 50}\document{i}.pdf", 10_000 + i, 5))
            .ToArray();

        var sw = Stopwatch.StartNew();
        engine.Load(entries, new[] { @"D:\" });
        var hits = engine.Match("document123", 10);
        sw.Stop();

        Assert.NotEmpty(hits);
        Assert.True(sw.ElapsedMilliseconds < 5000, $"装载+查询耗时 {sw.ElapsedMilliseconds}ms");
    }
}
