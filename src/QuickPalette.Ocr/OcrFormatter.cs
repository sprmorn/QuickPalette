namespace QuickPalette.Ocr;

/// <summary>
/// 识别结果结构化：表格还原（几何聚类）、Markdown 转换、代码缩进保留、TSV 导出。
/// 检测策略（启发式、保守）：
///  - 表格：多行且同列数 ≥2 的行按 X 起点聚类 → 网格
///  - 代码：行首 X 呈层级偏移 或 行含编程符号（{}();=&lt;&gt; 等）占比高
///  - 列表：行首 -/•/*/①/1. 等符号
///  - 其余为普通段落
/// </summary>
public sealed record OcrTable(IReadOnlyList<IReadOnlyList<string>> Rows, bool IsTable)
{
    public int RowCount => Rows.Count;
    public int ColumnCount => Rows.Count > 0 ? Rows.Max(r => r.Count) : 0;

    /// <summary>TSV（Excel 粘贴自动分列）</summary>
    public string ToTsv()
        => string.Join(Environment.NewLine, Rows.Select(r => string.Join("\t", r)));

    /// <summary>Markdown 表格</summary>
    public string ToMarkdown()
    {
        if (Rows.Count == 0)
        {
            return string.Empty;
        }

        var cols = ColumnCount;
        var sb = new System.Text.StringBuilder();
        sb.Append("| ").Append(string.Join(" | ", Pad(Rows[0], cols))).Append(" |").Append('\n');
        sb.Append("|").Append(string.Join("|", Enumerable.Repeat(" --- ", cols))).Append("|").Append('\n');
        foreach (var row in Rows.Skip(1))
        {
            sb.Append("| ").Append(string.Join(" | ", Pad(row, cols))).Append(" |").Append('\n');
        }

        return sb.ToString();

        static IEnumerable<string> Pad(IReadOnlyList<string> row, int cols)
            => Enumerable.Range(0, cols).Select(i => i < row.Count ? row[i].Replace("|", "\\|") : string.Empty);
    }
}

public static class OcrFormatter
{
    /// <summary>表格还原：行聚类（Y）→ 列聚类（X 起点）→ 网格</summary>
    public static OcrTable DetectTable(IReadOnlyList<OcrLineBox> boxes)
    {
        if (boxes.Count < 2)
        {
            return new OcrTable(Array.Empty<IReadOnlyList<string>>(), false);
        }

        // 1) 行聚类：按 Y 中心，间隙超过行高中位数 0.8 倍断行
        var sorted = boxes.OrderBy(b => b.Y).ToList();
        var heights = sorted.Select(b => b.H).OrderBy(h => h).ToList();
        var medianH = heights[heights.Count / 2];
        var rowGap = Math.Max(4, medianH * 0.8);

        var rows = new List<List<OcrLineBox>>();
        var current = new List<OcrLineBox> { sorted[0] };
        for (var i = 1; i < sorted.Count; i++)
        {
            var prevCenter = current[^1].Y + current[^1].H / 2;
            var curCenter = sorted[i].Y + sorted[i].H / 2;
            if (curCenter - prevCenter > rowGap)
            {
                rows.Add(current);
                current = new List<OcrLineBox>();
            }

            current.Add(sorted[i]);
        }

        rows.Add(current);

        // 2) 列聚类：全部行的 X 起点排序后按间隙分组（间隙 > 中位列宽的 1.2 倍）
        var xs = boxes.Select(b => b.X).OrderBy(x => x).ToList();
        var widths = boxes.Select(b => b.W).OrderBy(w => w).ToList();
        var medianW = widths[widths.Count / 2];
        var colGap = Math.Max(12, medianW * 0.6);

        var colStarts = new List<double>();
        foreach (var x in xs)
        {
            if (colStarts.Count == 0 || x - colStarts[^1] > colGap)
            {
                colStarts.Add(x);
            }
        }

        // 3) 每行按列归位（就近列）
        var grid = new List<IReadOnlyList<string>>();
        foreach (var row in rows)
        {
            var cells = new string[colStarts.Count];
            for (var i = 0; i < cells.Length; i++)
            {
                cells[i] = string.Empty;
            }

            foreach (var box in row.OrderBy(b => b.X))
            {
                var best = 0;
                var bestDist = double.MaxValue;
                for (var c = 0; c < colStarts.Count; c++)
                {
                    var d = Math.Abs(box.X - colStarts[c]);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = c;
                    }
                }

                cells[best] = string.IsNullOrWhiteSpace(cells[best])
                    ? box.Text
                    : cells[best] + " " + box.Text;
            }

            grid.Add(cells);
        }

        // 4) 判定：≥2 行且 ≥2 列、多数行列数一致
        var consistent = grid.Count(r => r.Count >= 2) >= 2 && grid.Count(r => r.Count(c => !string.IsNullOrWhiteSpace(c)) >= 2) >= Math.Max(2, grid.Count - 1);
        return new OcrTable(grid, consistent);
    }

    /// <summary>代码块检测：缩进层级 或 编程符号密度</summary>
    public static bool LooksLikeCode(IReadOnlyList<OcrLineBox> boxes)
    {
        if (boxes.Count < 2)
        {
            return false;
        }

        var indentLevels = boxes.Select(b => b.X).Distinct().Count();
        var symbolLines = boxes.Count(b =>
        {
            var t = b.Text;
            var symbols = t.Count(ch => "{}();=<>[]|&+-*/".Contains(ch));
            return symbols >= 2 || t.TrimEnd().EndsWith(';') || t.TrimEnd().EndsWith('{');
        });

        // 命中：≥3 个缩进层级，或 ≥40% 行带编程符号
        return indentLevels >= 3 || (boxes.Count >= 3 && symbolLines >= Math.Max(2, boxes.Count * 0.4));
    }

    /// <summary>生成 Markdown：表格 / 代码 / 列表 / 段落</summary>
    public static string ToMarkdown(IReadOnlyList<OcrLineBox> boxes, OcrTable? table = null)
    {
        table ??= DetectTable(boxes);
        if (table.IsTable)
        {
            return table.ToMarkdown();
        }

        if (LooksLikeCode(boxes))
        {
            return ToCodeBlock(boxes);
        }

        var sb = new System.Text.StringBuilder();
        foreach (var b in boxes.OrderBy(b => b.Y))
        {
            var t = b.Text.Trim();
            if (t.Length == 0)
            {
                continue;
            }

            if (IsListItem(t))
            {
                sb.Append("- ").Append(StripBullet(t)).Append('\n');
            }
            else
            {
                sb.Append(t).Append('\n').Append('\n');
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>代码块：按 X 起点还原缩进层级（最小 X = 0 层）</summary>
    public static string ToCodeBlock(IReadOnlyList<OcrLineBox> boxes)
    {
        var minX = boxes.Min(b => b.X);
        var sb = new System.Text.StringBuilder();
        sb.Append("```").Append('\n');
        foreach (var b in boxes.OrderBy(b => b.Y))
        {
            // 层级 = 24px 一档（启发式）
            var indent = (int)Math.Round(Math.Max(0, b.X - minX) / 24.0) * 4;
            sb.Append(new string(' ', indent)).Append(b.Text.TrimStart()).Append('\n');
        }

        sb.Append("```");
        return sb.ToString();
    }

    private static bool IsListItem(string t)
        => System.Text.RegularExpressions.Regex.IsMatch(t, @"^\s*([-•*·○●]\s+|[0-9①-⑳]+[.、)]\s+|[a-zA-Z][.、)]\s+)");

    private static string StripBullet(string t)
        => System.Text.RegularExpressions.Regex.Replace(t, @"^\s*([-•*·○●]|[0-9①-⑳]+[.、)]|[a-zA-Z][.、)])\s*", string.Empty);
}
