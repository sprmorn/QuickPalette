using System.Diagnostics;
using System.Text;
using QuickPalette.Infrastructure.FileIndex;
using QuickPalette.Domain.Services;

// 快照解析调试工具
// 用法：dotnet run --project tools\ParseTest -- "关键词"
//   （关键词缺省 = "report"；快照目录缺省 = %APPDATA%\QuickPalette\FileIndex）
class Program
{
    static void Mem(string tag)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var p = Process.GetCurrentProcess();
        Console.WriteLine($"  [mem:{tag}] ws={p.WorkingSet64 / 1048576.0:F0}MB  heap={GC.GetTotalMemory(false) / 1048576.0:F0}MB");
    }

    static void Main(string[] args)
    {
        // 调试查询关键字走命令行参数：dotnet run --project tools\ParseTest -- "关键词"
        var query = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]) ? args[0] : "report";
        Console.WriteLine($"query keyword: '{query}'");

        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickPalette", "FileIndex");
        var store = new FileIndexStore(dataDir);
        var meta = store.LoadMeta();
        Console.WriteLine($"meta: version={meta?.Version} count={meta?.Count} status={meta?.Status} error={meta?.Error ?? "-"}");
        var roots = meta?.Volumes?.Select(v => v.RootPath).ToList() ?? new List<string>();
        Console.WriteLine($"roots: [{string.Join(", ", roots)}]");

        // 手工解剖快照头与首条条目。
        // 格式与 FileIndexStore 实现对齐：
        //   头 = BinaryWriter.Write(string) 的 magic("QPFS"：7-bit 长度前缀 + UTF-8) + int 格式版本 + int 条目数
        //   条目 = Name + Path（同为 7-bit 长度前缀 + UTF-8）+ Size + Ticks + IsDir + FRN + ParentFRN
        //   尾 = uint "QPFE" + int 条目数（截断检测）
        if (File.Exists(Path.Combine(dataDir, "index.bin")))
        {
            using var fs = File.OpenRead(Path.Combine(dataDir, "index.bin"));
            using var reader = new BinaryReader(fs, Encoding.UTF8);
            Console.WriteLine($"magic={reader.ReadString()} format={reader.ReadInt32()} count={reader.ReadInt32()}");
            var name = reader.ReadString();                    // 7-bit 长度前缀 + UTF-8
            var len = Read7BitEncodedInt(reader);              // Path 的 7-bit 长度前缀（UTF-8 字节数）
            var pathBytes = reader.ReadBytes(len);
            var pathPreview = Encoding.UTF8.GetString(pathBytes, 0, Math.Min(64, pathBytes.Length));
            Console.WriteLine($"name='{name}' pathLen={len} pathPreview='{pathPreview}' first8={string.Join(" ", pathBytes.Take(8).Select(b => b.ToString("X2")))}");

            // 页脚：末尾 8 字节 = "QPFE" + 条目数
            if (fs.Length >= 8)
            {
                fs.Seek(-8, SeekOrigin.End);
                var footerMagic = reader.ReadUInt32();
                var footerCount = reader.ReadInt32();
                Console.WriteLine($"footer: magic=0x{footerMagic:X8} count={footerCount}");
            }
        }
        else
        {
            Console.WriteLine("index.bin 不存在");
        }

        var sample = store.ReadSnapshotRaw();
        if (sample == null) { Console.WriteLine("ReadSnapshotRaw returned null (快照缺失/旧格式/损坏)!"); return; }
        var shown = 0;
        foreach (var s in sample)
        {
            if (shown++ < 5) Console.WriteLine($"  vol={s.VolumeIndex} frn={s.Frn} name={s.Name}");
        }

        Console.WriteLine("== App side (compact index, raw streaming load) ==");
        var engine = new FileSearchEngine();
        var sw = Stopwatch.StartNew();
        engine.LoadRaw(store.ReadSnapshotRaw(), roots);
        sw.Stop();
        Console.WriteLine($"Raw Load (no path strings): {engine.Count} entries in {sw.ElapsedMilliseconds}ms");
        Mem("after load");

        // 命令行关键词 + 两个固定对照词
        foreach (var q in new[] { query, "everything", "report" }.Distinct())
        {
            sw.Restart();
            var results = engine.Match(q, 5);
            sw.Stop();
            Console.WriteLine($"Search('{q}'): {results.Count} results in {sw.ElapsedMilliseconds}ms");
            foreach (var r in results.Take(3))
                Console.WriteLine($"  [{r.Kind}] {r.Entry.Name} -> {r.Entry.Path}");
        }

        Mem("after search");
    }

    /// <summary>
    /// BinaryWriter.Write(string) 的 7-bit 长度前缀读取（与 FileIndexStore.Read7BitEncodedInt 同语义，
    /// 超过 5 字节视为损坏）。
    /// </summary>
    static int Read7BitEncodedInt(BinaryReader reader)
    {
        var result = 0;
        var shift = 0;
        for (var i = 0; i < 5; i++)
        {
            var b = reader.ReadByte();
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
        }
        throw new InvalidDataException("7-bit 长度前缀超过 5 字节，快照损坏");
    }
}
