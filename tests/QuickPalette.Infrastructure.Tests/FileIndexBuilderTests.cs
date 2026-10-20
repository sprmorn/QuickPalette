using QuickPalette.Infrastructure.FileIndex;

namespace QuickPalette.Infrastructure.Tests.FileIndex;

/// <summary>
/// FileIndexBuilder 测试：排除规则（路径段/卷根锚定、windows vs windows-app 不误伤、用户目录保护）
/// 与 FRN=5 卷根判定、FRN 序列号归一化。全部不接触真实卷。
/// </summary>
public class FileIndexBuilderTests
{
    private const uint AttrNone = 0;
    private const uint AttrDirectory = 0x10;
    private const uint AttrSystem = 0x4;

    // USN Reason 常量（winioctl.h）
    private const uint ReasonCreate = 0x00000100;
    private const uint ReasonDelete = 0x00000200;

    private static readonly DateTime Stamp = new(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    [Theory]
    // windows vs windows-app：路径段边界，规则 "windows" 绝不误伤 "windows-app"
    [InlineData(@"D:\windows-app\tool.exe", "tool.exe", AttrNone, false)]
    [InlineData(@"D:\windows-app\windows\inner.txt", "inner.txt", AttrNone, false)]
    [InlineData(@"D:\windows\tool.exe", "tool.exe", AttrNone, true)]
    [InlineData(@"D:\windows\system32\notepad.exe", "notepad.exe", AttrNone, true)]
    // 卷根锚定：非卷根位置的同名目录不命中
    [InlineData(@"D:\projects\windows\src\a.cs", "a.cs", AttrNone, false)]
    [InlineData(@"D:\projects\windows-app\src\a.cs", "a.cs", AttrNone, false)]
    [InlineData(@"D:\deep\recovery\file.wim", "file.wim", AttrNone, false)]
    [InlineData(@"D:\Recovery\file.wim", "file.wim", AttrNone, true)]
    // 用户目录保护：用户主目录一律保留
    [InlineData(@"C:\Users\bob\windows\x.txt", "x.txt", AttrNone, false)]
    [InlineData(@"C:\Users\bob\Recovery\x.txt", "x.txt", AttrNone, false)]
    [InlineData(@"C:\Users\bob\Documents\recovery-tool\a.txt", "a.txt", AttrNone, false)]
    [InlineData(@"D:\Users\alice\programdata\microsoft\a.txt", "a.txt", AttrNone, false)]
    // 系统目录（卷根锚定命中）
    [InlineData(@"C:\$Recycle.Bin\S-1-5-21\a", "a", AttrNone, true)]
    [InlineData(@"D:\System Volume Information\IndexerVolumeGuid", "IndexerVolumeGuid", AttrNone, true)]
    [InlineData(@"C:\ProgramData\Microsoft\foo\bar.dll", "bar.dll", AttrNone, true)]
    [InlineData(@"C:\ProgramData\Package Cache\setup.exe", "setup.exe", AttrNone, true)]
    [InlineData(@"C:\Program Files\WindowsApps\App\app.exe", "app.exe", AttrNone, true)]
    [InlineData(@"C:\WindowsApps\App\app.exe", "app.exe", AttrNone, true)]
    // 非卷根位置的同名段不受规则影响
    [InlineData(@"D:\custom\programdata\microsoft\a.txt", "a.txt", AttrNone, false)]
    // NTFS 元文件（$ 前缀 + 系统属性）
    [InlineData(@"D:\$MFT", "$MFT", AttrSystem, true)]
    [InlineData(@"C:\Users\bob\$foo", "$foo", AttrSystem, true)]
    // 普通文件 / 目录不误伤
    [InlineData(@"C:\Users\bob\Documents\report.docx", "report.docx", AttrNone, false)]
    [InlineData(@"D:\projects\myapp\bin\app.dll", "app.dll", AttrNone, false)]
    [InlineData(@"C:\Program Files\MyApp\app.exe", "app.exe", AttrNone, false)]
    public void IsExcluded_PathSegmentAndRootAnchoredRules(string path, string name, uint attr, bool expected)
    {
        Assert.Equal(expected, FileIndexBuilder.IsExcluded(path, name, attr));
    }

    [Fact]
    public void IsExcluded_NtfsMetaFiles_RequireSystemAttribute()
    {
        // $ 开头但非系统属性（用户文件）不按元文件排除
        Assert.False(FileIndexBuilder.IsExcluded(@"C:\Users\bob\$money.xlsx", "$money.xlsx", AttrNone));
        // $ 开头且系统属性 → 元文件，排除（即便位于用户目录保护段）
        Assert.True(FileIndexBuilder.IsExcluded(@"C:\Users\bob\$UsnJrnl", "$UsnJrnl", AttrSystem));
    }

    [Fact]
    public void IsExcluded_RootItself_NotExcluded()
    {
        Assert.False(FileIndexBuilder.IsExcluded(@"D:\", "", AttrDirectory));
        Assert.False(FileIndexBuilder.IsExcluded("D:", "", AttrDirectory));
    }

    [Fact]
    public void BuildFromEnumeration_KeepsUserDirsAndWindowsApp_ExcludesSystemDirs()
    {
        // 卷结构：
        //  D:\ (FRN 5)
        //   ├─ windows-app\ (10) ─ tool.exe (11)
        //   ├─ windows\ (20) ─ system32\ (21) ─ notepad.exe (22)   ← 应排除
        //   ├─ projects\ (30) ─ windows\ (31) ─ src\ (32) ─ a.cs (33)  ← 用户自己的 windows 目录，保留
        //   └─ Users\ (40) ─ bob\ (41) ─ Recovery\ (42) ─ x.txt (43)  ← 用户目录保护，保留
        var records = new List<UsnRecord>
        {
            Rec(10, 5, "windows-app", AttrDirectory),
            Rec(11, 10, "tool.exe", AttrNone),
            Rec(20, 5, "windows", AttrDirectory),
            Rec(21, 20, "system32", AttrDirectory),
            Rec(22, 21, "notepad.exe", AttrNone),
            Rec(30, 5, "projects", AttrDirectory),
            Rec(31, 30, "windows", AttrDirectory),
            Rec(32, 31, "src", AttrDirectory),
            Rec(33, 32, "a.cs", AttrNone),
            Rec(40, 5, "Users", AttrDirectory),
            Rec(41, 40, "bob", AttrDirectory),
            Rec(42, 41, "Recovery", AttrDirectory),
            Rec(43, 42, "x.txt", AttrNone),
        };

        var built = FileIndexBuilder.BuildFromEnumeration(records, @"D:\");
        var paths = built.Select(e => e.Path).OrderBy(p => p).ToList();

        Assert.Contains(@"D:\windows-app\tool.exe", paths);
        Assert.Contains(@"D:\projects\windows\src\a.cs", paths);
        Assert.Contains(@"D:\Users\bob\Recovery\x.txt", paths);
        Assert.DoesNotContain(@"D:\windows\system32\notepad.exe", paths);
        // 卷根本身不作为条目
        Assert.DoesNotContain(@"D:\", paths);
    }

    [Fact]
    public void BuildFromEnumeration_FRN5_RootJudgement_AllVariants()
    {
        // 父引用三种形态：裸 5、带序列号 5、以及根记录本身缺失（日志回绕）
        var records = new List<UsnRecord>
        {
            Rec(10, 5, "plain.txt", AttrNone),
            Rec(11, 0x0005000000000005, "seq.txt", AttrNone),
            Rec(0x0005000000000005, 5, "rootitself.txt", AttrNone), // 归一化为卷根 → 不产出条目
        };

        var built = FileIndexBuilder.BuildFromEnumeration(records, @"E:\data\");
        var paths = built.Select(e => e.Path).OrderBy(p => p).ToList();

        Assert.Equal(2, built.Count);
        Assert.Equal(@"E:\data\plain.txt", paths[0]);
        Assert.Equal(@"E:\data\seq.txt", paths[1]);
    }

    [Fact]
    public void BuildFromEnumeration_NormalizesSequenceNumbers()
    {
        // FRN/父 FRN 带高 16 位序列号：索引键与输出字段都归一化为记录号（低 48 位）
        var records = new List<UsnRecord>
        {
            Rec(0x0007000000000007, 0x0005000000000005, "seq-file.txt", AttrNone),
        };

        var built = FileIndexBuilder.BuildFromEnumeration(records, @"D:\");
        Assert.Single(built);
        Assert.Equal(7L, built[0].FileReference);
        Assert.Equal(5L, built[0].ParentReference);
        Assert.Equal(@"D:\seq-file.txt", built[0].Path);
    }

    [Fact]
    public void BuildFromEnumeration_SkipsDeletedRecords_KeepsNewest()
    {
        var old = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var fresh = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var records = new List<UsnRecord>
        {
            new(10, 5, 1, old, ReasonCreate, AttrNone, "first.txt"),
            new(10, 5, 2, fresh, ReasonCreate, AttrNone, "renamed.txt"), // 同 FRN 更新记录（时间更新）
            new(11, 5, 3, fresh, ReasonCreate, AttrNone, "gone.txt"),
            new(11, 5, 4, fresh, ReasonDelete, AttrNone, "gone.txt"),   // 已删除
        };

        var built = FileIndexBuilder.BuildFromEnumeration(records, @"D:\");
        Assert.Single(built);
        Assert.Equal("renamed.txt", built[0].Name); // 保留时间最新的记录
    }

    [Fact]
    public void BuildFromEnumeration_SkipsOrphanChains()
    {
        var records = new List<UsnRecord>
        {
            Rec(20, 999, "orphan.txt", AttrNone), // 父 999 不在记录内 → 断链
        };

        var built = FileIndexBuilder.BuildFromEnumeration(records, @"D:\");
        Assert.Empty(built);
    }

    [Fact]
    public void NormalizeFrn_MasksSequenceNumber()
    {
        Assert.Equal(5L, FileIndexBuilder.NormalizeFrn(5));
        Assert.Equal(5L, FileIndexBuilder.NormalizeFrn(0x0005000000000005));
        Assert.Equal(40L, FileIndexBuilder.NormalizeFrn(0x0028000000000028));
    }

    private static UsnRecord Rec(long frn, long parent, string name, uint attr)
        => new(frn, parent, frn & 0xFFFFFFFF, Stamp, ReasonCreate, attr, name);
}
