using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace QuickPalette.Infrastructure.Preview;

/// <summary>预览内容类型</summary>
public enum PreviewFormat
{
    Text,       // txt / md
    Word,       // docx
    Excel,      // xlsx
    PowerPoint, // pptx
    Pdf,        // pdf（文本模式）
    Unsupported // 其余（含旧版 .doc/.xls/.ppt 二进制格式）
}

/// <summary>预览抽取结果：纯文本（已按上限截断）</summary>
public sealed record PreviewContent(PreviewFormat Format, string Text, bool Truncated);

/// <summary>
/// 文档预览文本抽取（零第三方依赖）：
/// txt/md 直读；docx/pptx/xlsx 为 Zip+XML 结构用 ZipArchive+XDocument 解析；
/// pdf 用 Flate 解压内容流抽取 Tj/TJ 文本（文本模式，无排版）。
/// 全部输出纯文本并按上限截断（内存友好，与索引/搜索路径零交集）；
/// 任何解析失败都返回友好提示文本而非抛异常。
/// </summary>
public static class DocumentTextExtractor
{
    // ── 内容上限（内存保护）──
    private const int MaxTextChars = 200_000;   // txt/md/pdf 文本
    private const int MaxLines = 2000;
    private const int MaxParagraphs = 2000;     // docx
    private const int MaxSlides = 30;           // pptx
    private const int MaxSheets = 3;            // xlsx
    private const int MaxRows = 50;             // xlsx
    private const int MaxCols = 10;             // xlsx

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".log", ".csv",
        ".docx",
        ".xlsx",
        ".pptx",
        ".pdf",
        // 图片：直接图像化预览（PreviewWindow 走位图模式，不经文本抽取）
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".ico",
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".ico",
    };

    /// <summary>是否为图片格式（预览走位图模式）</summary>
    public static bool IsImage(string? extension)
        => !string.IsNullOrEmpty(extension) && ImageExtensions.Contains(extension);

    /// <summary>是否为可预览扩展名（决定单击是否触发预览）</summary>
    public static bool IsSupported(string? extension)
        => !string.IsNullOrEmpty(extension) && SupportedExtensions.Contains(extension);

    /// <summary>按扩展名判格式</summary>
    public static PreviewFormat GetFormat(string? extension)
    {
        switch (extension?.ToLowerInvariant())
        {
            case ".txt" or ".md" or ".markdown" or ".log" or ".csv":
                return PreviewFormat.Text;
            case ".docx":
                return PreviewFormat.Word;
            case ".xlsx":
                return PreviewFormat.Excel;
            case ".pptx":
                return PreviewFormat.PowerPoint;
            case ".pdf":
                return PreviewFormat.Pdf;
            default:
                return PreviewFormat.Unsupported;
        }
    }

    /// <summary>
    /// 抽取预览文本。绝不抛异常：失败返回 <see cref="PreviewFormat.Unsupported"/> 与友好提示（提示文本由调用方本地化，此处给中文）。
    /// </summary>
    public static PreviewContent Extract(string path, CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new PreviewContent(PreviewFormat.Unsupported, "文件不存在或已被移动。", false);
            }

            var ext = Path.GetExtension(path);
            return GetFormat(ext) switch
            {
                PreviewFormat.Text => ExtractPlainText(path),
                PreviewFormat.Word => ExtractDocx(path, ct),
                PreviewFormat.Excel => ExtractXlsx(path, ct),
                PreviewFormat.PowerPoint => ExtractPptx(path, ct),
                PreviewFormat.Pdf => ExtractPdfText(path),
                _ => new PreviewContent(PreviewFormat.Unsupported,
                    "该格式暂不支持预览。\n支持：PDF / Word(docx) / Excel(xlsx) / PPT(pptx) / Markdown / TXT。", false)
            };
        }
        catch (OperationCanceledException)
        {
            throw; // 取消交给调用方（切换预览时旧任务作废）
        }
        catch (Exception ex)
        {
            return new PreviewContent(PreviewFormat.Unsupported, $"预览解析失败：{ex.Message}", false);
        }
    }

    // ───────────────────────── txt / md ─────────────────────────

    private static PreviewContent ExtractPlainText(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var limit = (int)Math.Min(fs.Length, MaxTextChars * 4); // 字节上限按字符上限×4 容纳多字节
        var bytes = new byte[limit];
        var read = 0;
        while (read < limit)
        {
            var n = fs.Read(bytes, read, limit - read);
            if (n <= 0) break;
            read += n;
        }

        var text = DecodeText(bytes.AsSpan(0, read));
        return TrimToLimit(text, fs.Length > read);
    }

    /// <summary>
    /// 文本解码：BOM 识别 → UTF-8 严格校验 → 失败回退 GB18030（中文旧文本）→ 再失败 UTF-8 替换字符。
    /// </summary>
    private static string DecodeText(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes[3..]);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes[2..]);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes[2..]);

        try
        {
            // 严格 UTF-8：非法序列抛 DecoderFallbackException → 说明是本地代码页文本
            var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return strictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding("GB18030").GetString(bytes);
            }
            catch
            {
                return Encoding.UTF8.GetString(bytes); // 替换字符兜底
            }
        }
    }

    private static PreviewContent TrimToLimit(string text, bool sourceLonger)
    {
        var truncated = sourceLonger;
        var lines = text.Split('\n');
        if (lines.Length > MaxLines)
        {
            text = string.Join('\n', lines[..MaxLines]);
            truncated = true;
        }
        if (text.Length > MaxTextChars)
        {
            text = text[..MaxTextChars];
            truncated = true;
        }
        return new PreviewContent(PreviewFormat.Text, text, truncated);
    }

    // ───────────────────────── docx ─────────────────────────

    private static PreviewContent ExtractDocx(string path, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var truncated = false;
        using var zip = ZipFile.OpenRead(path);
        var doc = zip.GetEntry("word/document.xml");
        if (doc == null)
        {
            return new PreviewContent(PreviewFormat.Unsupported, "文档结构无法识别（word/document.xml 缺失）。", false);
        }

        var xdoc = LoadEntry(doc);
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var paragraphs = xdoc.Descendants(w + "p").Take(MaxParagraphs + 1).ToList();
        truncated = paragraphs.Count > MaxParagraphs;

        foreach (var p in paragraphs.Take(MaxParagraphs))
        {
            ct.ThrowIfCancellationRequested();
            var line = string.Concat(p.Descendants(w + "t").Select(t => (string?)t.Value ?? string.Empty));
            sb.AppendLine(line);
            if (sb.Length > MaxTextChars)
            {
                truncated = true;
                break;
            }
        }

        return new PreviewContent(PreviewFormat.Word, sb.ToString().TrimEnd(), truncated);
    }

    // ───────────────────────── pptx ─────────────────────────

    private static PreviewContent ExtractPptx(string path, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var truncated = false;
        using var zip = ZipFile.OpenRead(path);

        var slides = zip.Entries
            .Where(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase)
                        && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => SlideNumber(e.FullName))
            .ToList();
        if (slides.Count == 0)
        {
            return new PreviewContent(PreviewFormat.Unsupported, "文档结构无法识别（未找到幻灯片）。", false);
        }
        if (slides.Count > MaxSlides)
        {
            slides = slides.Take(MaxSlides).ToList();
            truncated = true;
        }

        var index = 0;
        foreach (var slide in slides)
        {
            ct.ThrowIfCancellationRequested();
            index++;
            sb.AppendLine($"── 幻灯片 {index} ──");

            var xdoc = LoadEntry(slide);
            XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main";
            foreach (var p in xdoc.Descendants(a + "p"))
            {
                var line = string.Concat(p.Descendants(a + "t").Select(t => (string?)t.Value ?? string.Empty));
                if (line.Length > 0) sb.AppendLine(line);
            }
            sb.AppendLine();

            if (sb.Length > MaxTextChars)
            {
                truncated = true;
                break;
            }
        }

        return new PreviewContent(PreviewFormat.PowerPoint, sb.ToString().TrimEnd(), truncated);
    }

    private static int SlideNumber(string fullName)
    {
        var name = Path.GetFileNameWithoutExtension(fullName);
        var digits = new string(name.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : int.MaxValue;
    }

    // ───────────────────────── xlsx ─────────────────────────

    private static PreviewContent ExtractXlsx(string path, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var truncated = false;
        using var zip = ZipFile.OpenRead(path);

        // 共享字符串表（t="s" 单元格引用）
        var shared = new List<string>();
        var sstEntry = zip.GetEntry("xl/sharedStrings.xml");
        if (sstEntry != null)
        {
            XNamespace s = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var sst = LoadEntry(sstEntry);
            foreach (var si in sst.Descendants(s + "si"))
            {
                shared.Add(string.Concat(si.Descendants(s + "t").Select(t => (string?)t.Value ?? string.Empty)));
            }
        }

        var sheets = zip.Entries
            .Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase)
                        && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => SlideNumber(e.FullName))
            .Take(MaxSheets)
            .ToList();
        if (sheets.Count == 0)
        {
            return new PreviewContent(PreviewFormat.Unsupported, "文档结构无法识别（未找到工作表）。", false);
        }

        for (var s = 0; s < sheets.Count; s++)
        {
            ct.ThrowIfCancellationRequested();
            if (sheets.Count > 1) sb.AppendLine($"══ 工作表 {s + 1} ══");

            var xdoc = LoadEntry(sheets[s]);
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

            // 行 → 限行列值
            var grid = new List<string[]>();
            foreach (var row in xdoc.Descendants(ns + "row").Take(MaxRows))
            {
                ct.ThrowIfCancellationRequested();
                var cells = new string[MaxCols];
                for (var i = 0; i < MaxCols; i++) cells[i] = string.Empty;

                foreach (var c in row.Elements(ns + "c"))
                {
                    var colIndex = ColumnIndex((string?)c.Attribute("r"));
                    if (colIndex < 0 || colIndex >= MaxCols) { truncated = truncated || colIndex >= MaxCols; continue; }

                    var type = (string?)c.Attribute("t");
                    string value;
                    if (type == "s")
                    {
                        value = int.TryParse((string?)c.Element(ns + "v"), out var si) && si >= 0 && si < shared.Count
                            ? shared[si]
                            : string.Empty;
                    }
                    else if (type == "inlineStr")
                    {
                        value = string.Concat(c.Descendants(ns + "t").Select(t => (string?)t.Value ?? string.Empty));
                    }
                    else
                    {
                        value = (string?)c.Element(ns + "v") ?? string.Empty;
                    }
                    cells[colIndex] = value;
                }
                grid.Add(cells);
            }
            if (xdoc.Descendants(ns + "row").Count() > MaxRows) truncated = true;

            // 列宽对齐（等宽字体显示）
            var widths = new int[MaxCols];
            foreach (var cells in grid)
            {
                for (var i = 0; i < MaxCols; i++)
                {
                    widths[i] = Math.Min(28, Math.Max(widths[i], Math.Min(28, cells[i].Length)));
                }
            }

            foreach (var cells in grid)
            {
                var parts = new string[MaxCols];
                for (var i = 0; i < MaxCols; i++)
                {
                    var v = cells[i].Length > 28 ? cells[i][..27] + "…" : cells[i];
                    parts[i] = i == MaxCols - 1 ? v : v.PadRight(widths[i] + 2);
                }
                sb.AppendLine(string.Concat(parts).TrimEnd());
            }
            sb.AppendLine();
        }

        return new PreviewContent(PreviewFormat.Excel, sb.ToString().TrimEnd(), truncated);
    }

    /// <summary>单元格地址 "BC12" → 列下标（0 起）；无效返回 -1</summary>
    private static int ColumnIndex(string? cellRef)
    {
        if (string.IsNullOrEmpty(cellRef)) return -1;
        var col = 0;
        foreach (var ch in cellRef)
        {
            if (ch is >= 'A' and <= 'Z') col = col * 26 + (ch - 'A' + 1);
            else if (ch is >= 'a' and <= 'z') col = col * 26 + (ch - 'a' + 1);
            else break;
        }
        return col == 0 ? -1 : col - 1;
    }

    // ───────────────────────── pdf（文本模式） ─────────────────────────

    /// <summary>
    /// PDF 文本抽取（零依赖文本模式）：定位内容流（Flate 压缩则解压），
    /// 解析 Tj/TJ/'/" 文本算子的括号字符串。无排版、可能不完整，但足以预览正文。
    /// </summary>
    private static PreviewContent ExtractPdfText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var sb = new StringBuilder();
        var truncated = false;

        // 逐个 stream ... endstream 块尝试
        var pos = 0;
        while (true)
        {
            var start = IndexOf(bytes, "stream"u8, pos);
            if (start < 0) break;
            var dataStart = start + 6;
            if (dataStart < bytes.Length && bytes[dataStart] == '\r') dataStart++;
            if (dataStart < bytes.Length && bytes[dataStart] == '\n') dataStart++;

            var end = IndexOf(bytes, "endstream"u8, dataStart);
            if (end < 0) break;

            var raw = bytes.AsSpan(dataStart, end - dataStart);
            var content = TryInflate(raw);

            if (content != null)
            {
                ExtractPdfTextOperators(content, sb);
            }

            if (sb.Length > MaxTextChars)
            {
                truncated = true;
                break;
            }
            pos = end + 9;
        }

        var text = sb.ToString().Trim();
        if (text.Length == 0)
        {
            text = "（PDF 文本模式未抽取到文本：可能是扫描件或图片型 PDF。）";
        }
        else
        {
            text = "── PDF 文本模式（自动抽取，排版可能不完整）──\n\n" + text;
        }
        return new PreviewContent(PreviewFormat.Pdf, text, truncated);
    }

    private static byte[]? TryInflate(ReadOnlySpan<byte> raw)
    {
        // zlib 流（PDF FlateDecode：0x78 开头常见）；失败视为未压缩原样返回
        try
        {
            using var input = new MemoryStream(raw.ToArray());
            using var z = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            z.CopyTo(output);
            return output.ToArray();
        }
        catch
        {
            return raw.Length > 0 && raw[0] is not (byte)'<' ? raw.ToArray() : null;
        }
    }

    /// <summary>从内容流字节里抽 Tj/TJ/'/" 的括号字符串（含转义处理）</summary>
    private static void ExtractPdfTextOperators(byte[] content, StringBuilder sb)
    {
        var i = 0;
        var len = content.Length;
        while (i < len)
        {
            // 找 ( 字符串
            if (content[i] == (byte)'(')
            {
                var text = ReadPdfString(content, ref i);
                if (text.Length > 0)
                {
                    // 后面紧跟 Tj/TJ/'/" 才是文本；粗略启发：全部收集（PDF 内容流中括号串多为文本）
                    sb.Append(text);
                }
                continue;
            }

            if (content[i] == (byte)'<' && i + 1 < len && content[i + 1] != (byte)'<')
            {
                // 十六进制字符串 <48656C6C6F>
                var close = Array.IndexOf(content, (byte)'>', i + 1);
                if (close > i + 1)
                {
                    var hex = Encoding.ASCII.GetString(content, i + 1, close - i - 1);
                    try
                    {
                        var bytes = Convert.FromHexString(hex.Trim());
                        sb.Append(Encoding.BigEndianUnicode.GetString(bytes));
                    }
                    catch
                    {
                        // 非法 hex 跳过
                    }
                    i = close + 1;
                    continue;
                }
            }

            var ch = (char)content[i];
            if (ch == 'T' && i + 1 < len && (content[i + 1] == (byte)'*' || content[i + 1] == (byte)'d'))
            {
                sb.AppendLine(); // Td/TD/T* 近似换行
            }
            else if (ch == 'E' && i + 1 < len && content[i + 1] == (byte)'T')
            {
                sb.AppendLine(); // 文本对象结束
            }
            i++;
        }
    }

    /// <summary>读取 PDF 字面字符串（处理 \( \) \\ \n \r \t 及八进制转义），i 停在右括号后</summary>
    private static string ReadPdfString(byte[] content, ref int i)
    {
        var sb = new StringBuilder();
        var depth = 1;
        i++; // 跳过 (
        var len = content.Length;
        while (i < len && depth > 0)
        {
            var b = content[i];
            if (b == (byte)'\\' && i + 1 < len)
            {
                i++;
                var e = (char)content[i];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case '(': case ')': case '\\': sb.Append(e); break;
                    default:
                        if (e is >= '0' and <= '7')
                        {
                            // 八进制转义（最多 3 位）
                            var val = e - '0';
                            var count = 1;
                            while (count < 3 && i + 1 < len && content[i + 1] is >= (byte)'0' and <= (byte)'7')
                            {
                                i++;
                                val = val * 8 + (content[i] - '0');
                                count++;
                            }
                            sb.Append((char)val);
                        }
                        break;
                }
                i++;
                continue;
            }

            if (b == (byte)'(') depth++;
            else if (b == (byte)')')
            {
                depth--;
                if (depth == 0) { i++; break; }
            }
            else if (b is < 0x80 or >= 0xC0) // 粗略跳过 UTF-8 首字节判定（PDF 文本串多为 PDFDocEncoding/ASCII）
            {
                sb.Append((char)b);
            }
            i++;
        }
        return sb.ToString();
    }

    private static int IndexOf(byte[] haystack, ReadOnlySpan<byte> needle, int start)
    {
        for (var i = start; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        }
        return -1;
    }

    // ───────────────────────── 公共 ─────────────────────────

    private static XDocument LoadEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }
}
