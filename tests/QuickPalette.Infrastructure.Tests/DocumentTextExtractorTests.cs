using System.IO.Compression;
using System.Text;
using QuickPalette.Infrastructure.Preview;

namespace QuickPalette.Infrastructure.Tests.Preview;

/// <summary>
/// DocumentTextExtractor 文档预览抽取测试：
/// txt/md 直读与截断、docx/pptx/xlsx 的 Zip+XML 解析、pdf 文本算子抽取、格式判定。
/// 全部使用 %TEMP% 临时目录构建样本文档，不接触真实文件。
/// </summary>
public class DocumentTextExtractorTests : IDisposable
{
    private readonly string _dir;

    public DocumentTextExtractorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "QuickPaletteTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 清理失败忽略 */ }
    }

    private string File_With(string name, byte[] bytes)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    private string Zip_With(string name, params (string Entry, string Content)[] entries)
    {
        var p = Path.Combine(_dir, name);
        using var zip = ZipFile.Open(p, ZipArchiveMode.Create);
        foreach (var (entry, content) in entries)
        {
            var e = zip.CreateEntry(entry);
            using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
            w.Write(content);
        }
        return p;
    }

    [Fact]
    public void Extract_Txt_ReadsAndTruncates()
    {
        var small = File_With("a.txt", Encoding.UTF8.GetBytes("第一行\nsecond line"));
        var content = DocumentTextExtractor.Extract(small);
        Assert.Equal(PreviewFormat.Text, content.Format);
        Assert.Contains("第一行", content.Text);
        Assert.Contains("second line", content.Text);
        Assert.False(content.Truncated);

        // 超长 → 截断标记
        var big = File_With("big.txt", Encoding.UTF8.GetBytes(new string('x', 300_000)));
        var truncated = DocumentTextExtractor.Extract(big);
        Assert.True(truncated.Truncated);
        Assert.True(truncated.Text.Length <= 200_000);
    }

    [Fact]
    public void Extract_Txt_GbkEncoding_DoesNotMojibake()
    {
        // GB18030 编码的中文旧文本应正确解码（严格 UTF-8 失败后回退）
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var gbk = Encoding.GetEncoding("GB18030").GetBytes("中文文档内容");
        var p = File_With("gbk.txt", gbk);
        var content = DocumentTextExtractor.Extract(p);
        Assert.Contains("中文文档内容", content.Text);
    }

    [Fact]
    public void Extract_Md_TreatedAsText()
    {
        var p = File_With("note.md", Encoding.UTF8.GetBytes("# 标题\n\n- 项目 1"));
        var content = DocumentTextExtractor.Extract(p);
        Assert.Equal(PreviewFormat.Text, content.Format);
        Assert.Contains("# 标题", content.Text);
    }

    [Fact]
    public void Extract_Docx_Paragraphs()
    {
        var xml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" +
            "<w:p><w:r><w:t>段落一</w:t></w:r></w:p>" +
            "<w:p><w:r><w:t>Hello </w:t></w:r><w:r><w:t>World</w:t></w:r></w:p>" +
            "</w:body></w:document>";
        var p = Zip_With("doc.docx", ("word/document.xml", xml));

        var content = DocumentTextExtractor.Extract(p);
        Assert.Equal(PreviewFormat.Word, content.Format);
        Assert.Contains("段落一", content.Text);
        Assert.Contains("Hello World", content.Text); // 同段多 run 合并
    }

    [Fact]
    public void Extract_Pptx_SlidesWithSeparators()
    {
        var slideXml = (string title) =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<p:sld xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" " +
            "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><p:cSld><p:spTree>" +
            "<p:sp><p:txBody><a:p><a:r><a:t>" + title + "</a:t></a:r></a:p></p:txBody></p:sp>" +
            "</p:spTree></p:cSld></p:sld>";
        var p = Zip_With("s.pptx",
            ("ppt/slides/slide1.xml", slideXml("第一页标题")),
            ("ppt/slides/slide2.xml", slideXml("第二页标题")));

        var content = DocumentTextExtractor.Extract(p);
        Assert.Equal(PreviewFormat.PowerPoint, content.Format);
        Assert.Contains("幻灯片 1", content.Text);
        Assert.Contains("第一页标题", content.Text);
        Assert.Contains("幻灯片 2", content.Text);
        Assert.Contains("第二页标题", content.Text);
    }

    [Fact]
    public void Extract_Xlsx_SharedStringsAndColumns()
    {
        var sst =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
            "<si><t>名称</t></si><si><t>轻启</t></si></sst>";
        var sheet =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>" +
            "<row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"inlineStr\"><is><t>值</t></is></c></row>" +
            "<row r=\"2\"><c r=\"A2\" t=\"s\"><v>1</v></c><c r=\"B2\"><v>42</v></c></row>" +
            "</sheetData></worksheet>";
        var p = Zip_With("t.xlsx",
            ("xl/sharedStrings.xml", sst),
            ("xl/worksheets/sheet1.xml", sheet));

        var content = DocumentTextExtractor.Extract(p);
        Assert.Equal(PreviewFormat.Excel, content.Format);
        Assert.Contains("名称", content.Text);   // 共享字符串
        Assert.Contains("轻启", content.Text);
        Assert.Contains("值", content.Text);     // inlineStr
        Assert.Contains("42", content.Text);     // 数值
    }

    [Fact]
    public void Extract_Pdf_TextOperators()
    {
        // 未压缩内容流 + Tj 文本算子（最小 PDF 骨架）
        var pdf = "%PDF-1.4\n" +
                  "1 0 obj<<>>endobj\n" +
                  "2 0 obj<</Length 30>>stream\n" +
                  "BT (Hello Pdf) Tj ET\n" +
                  "endstream\nendobj\n" +
                  "trailer<</Root 1 0 R>>\n%%EOF";
        var p = File_With("t.pdf", Encoding.ASCII.GetBytes(pdf));

        var content = DocumentTextExtractor.Extract(p);
        Assert.Equal(PreviewFormat.Pdf, content.Format);
        Assert.Contains("Hello Pdf", content.Text);
        Assert.Contains("文本模式", content.Text); // 明示文本模式
    }

    [Fact]
    public void Extract_Pdf_FlateCompressedStream()
    {
        // zlib 压缩的内容流（真实 PDF 常见形态）
        var payload = Encoding.ASCII.GetBytes("BT (Compressed Text) Tj ET\n");
        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionMode.Compress, true))
            {
                z.Write(payload, 0, payload.Length);
            }
            compressed = ms.ToArray();
        }

        using var fs = new MemoryStream();
        using (var w = new BinaryWriter(fs, Encoding.ASCII, true))
        {
            w.Write(Encoding.ASCII.GetBytes("%PDF-1.4\n2 0 obj<</Length " + compressed.Length + ">>stream\n"));
            w.Write(compressed);
            w.Write(Encoding.ASCII.GetBytes("\nendstream\nendobj\n%%EOF"));
        }
        var p = File_With("c.pdf", fs.ToArray());

        var content = DocumentTextExtractor.Extract(p);
        Assert.Contains("Compressed Text", content.Text);
    }

    [Fact]
    public void Extract_UnsupportedFormat_FriendlyMessage()
    {
        var p = File_With("old.doc", new byte[] { 0xD0, 0xCF, 0x11, 0xE0 }); // 旧版二进制 doc
        var content = DocumentTextExtractor.Extract(p);
        Assert.Equal(PreviewFormat.Unsupported, content.Format);
        Assert.Contains("暂不支持", content.Text);
    }

    [Fact]
    public void IsSupported_And_GetFormat_Mapping()
    {
        Assert.True(DocumentTextExtractor.IsSupported(".pdf"));
        Assert.True(DocumentTextExtractor.IsSupported(".DOCX"));
        Assert.False(DocumentTextExtractor.IsSupported(".doc"));
        Assert.False(DocumentTextExtractor.IsSupported(null));

        Assert.Equal(PreviewFormat.Text, DocumentTextExtractor.GetFormat(".md"));
        Assert.Equal(PreviewFormat.Word, DocumentTextExtractor.GetFormat(".docx"));
        Assert.Equal(PreviewFormat.Excel, DocumentTextExtractor.GetFormat(".xlsx"));
        Assert.Equal(PreviewFormat.PowerPoint, DocumentTextExtractor.GetFormat(".pptx"));
        Assert.Equal(PreviewFormat.Pdf, DocumentTextExtractor.GetFormat(".pdf"));
        Assert.Equal(PreviewFormat.Unsupported, DocumentTextExtractor.GetFormat(".exe"));
    }

    [Fact]
    public void ImageExtensions_AreSupportedAndClassified()
    {
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".ico" })
        {
            Assert.True(DocumentTextExtractor.IsSupported(ext), ext + " 应可预览");
            Assert.True(DocumentTextExtractor.IsImage(ext), ext + " 应为图片格式");
        }
        Assert.False(DocumentTextExtractor.IsImage(".pdf"));
        Assert.False(DocumentTextExtractor.IsImage(".docx"));
    }

    [Fact]
    public void PdfRenderer_BuildScript_EscapesSingleQuotes()
    {
        // 路径中的单引号必须成对转义（PowerShell 单引号字面量规则），防脚本注入/解析失败
        var script = PdfPageRenderer.BuildScript(@"D:\it's\a'b.pdf", @"C:\tmp\o'ut", 5, 900);
        Assert.Contains(@"D:\it''s\a''b.pdf", script);
        Assert.Contains(@"C:\tmp\o''ut", script);
        Assert.Contains("$MaxPages = 5", script);
        Assert.Contains("$Width = 900", script);
        Assert.Contains("Windows.Data.Pdf.PdfDocument", script);
    }

    [Fact]
    public void PdfRenderer_ParsePageCount_FromBridgeOutput()
    {
        Assert.Equal(228, PdfPageRenderer.ParsePageCount("PAGES=228\nPAGE 1 OK\n"));
        Assert.Equal(1, PdfPageRenderer.ParsePageCount("noise\r\nPAGES=1"));
        Assert.Equal(0, PdfPageRenderer.ParsePageCount("no marker"));
        Assert.Equal(0, PdfPageRenderer.ParsePageCount(null!));
    }
}
