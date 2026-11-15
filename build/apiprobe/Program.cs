using System;
using System.Collections.Generic;
using QuickPalette.Ocr;

class FormatSmoke
{
    static void Main()
    {
        // ① 表格：3 行 × 3 列对齐几何
        var tableBoxes = new List<OcrLineBox>
        {
            new("姓名", 50, 10, 60, 20),  new("年龄", 250, 10, 60, 20),  new("城市", 450, 10, 60, 20),
            new("张三", 50, 50, 60, 20),  new("28", 250, 50, 30, 20),    new("北京", 450, 50, 60, 20),
            new("李四", 50, 90, 60, 20),  new("31", 250, 90, 30, 20),    new("上海", 450, 90, 60, 20),
        };
        var t = OcrFormatter.DetectTable(tableBoxes);
        Console.WriteLine($"[表格] IsTable={t.IsTable} {t.RowCount}x{t.ColumnCount}");
        Console.WriteLine(t.ToMarkdown());
        Console.WriteLine("TSV 首行: " + t.ToTsv().Split('\n')[0].Replace("\t", " ⇥ "));
        Console.WriteLine(t.IsTable && t.ColumnCount == 3 ? "[✓] 表格通过" : "[✗] 表格失败");

        // ② 代码：缩进层级 + 符号
        var codeBoxes = new List<OcrLineBox>
        {
            new("def add(a, b):", 50, 10, 120, 20),
            new("if a > 0:", 90, 50, 90, 20),
            new("return a + b;", 130, 90, 110, 20),
            new("print(add(1, 2));", 50, 130, 150, 20),
        };
        var isCode = OcrFormatter.LooksLikeCode(codeBoxes);
        var mdCode = OcrFormatter.ToMarkdown(codeBoxes);
        Console.WriteLine($"[代码] LooksLikeCode={isCode}");
        Console.WriteLine(mdCode);
        Console.WriteLine(isCode && mdCode.Contains("```") && mdCode.Contains("    if") ? "[✓] 代码通过（缩进保留）" : "[✗] 代码失败");

        // ③ 列表
        var listBoxes = new List<OcrLineBox>
        {
            new("- 苹果", 50, 10, 60, 20),
            new("- 香蕉", 50, 50, 60, 20),
            new("普通段落文字在这里。", 50, 90, 160, 20),
        };
        var mdList = OcrFormatter.ToMarkdown(listBoxes);
        Console.WriteLine("[列表] " + mdList.Replace("\n", " ⏎ "));
        Console.WriteLine(mdList.Contains("- 苹果") && mdList.Contains("普通段落") ? "[✓] 列表通过" : "[✗] 列表失败");
    }
}
