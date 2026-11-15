namespace QuickPalette.App.Ocr;

using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QuickPalette.App.Screenshot;
using QuickPalette.Ocr;

/// <summary>
/// OCR 结果面板：文本 / Markdown / 表格 三视图。
/// 表格=几何聚类还原（可复制 TSV 直贴 Excel）；Markdown=表格/代码块/列表自动转换。
/// </summary>
public partial class OcrResultWindow : Window
{
    private string _plainText = string.Empty;
    private string _markdown = string.Empty;
    private string _tsv = string.Empty;

    public OcrResultWindow()
    {
        InitializeComponent();
        TitleIconHost.Content = ToolbarIcons.Create("ocr", 18);

        // 自动列：文字用主题色 + 超宽省略（默认黑字在深色面板上很丑）
        TableGrid.AutoGeneratingColumn += (_, e) =>
        {
            if (e.Column is DataGridTextColumn text)
            {
                text.ElementStyle = new Style(typeof(TextBlock))
                {
                    Setters =
                    {
                        new Setter(TextBlock.ForegroundProperty, (System.Windows.Media.Brush)FindResource("TextPrimary")),
                        new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis),
                        new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center),
                    },
                };
            }
        };
    }

    /// <summary>填充识别结果（识别完成后由服务层调用）</summary>
    public void ShowResult(OcrTextResult result)
    {
        _plainText = result.IsEmpty ? "未识别到文字。可尝试：框选更大范围、或换高精度引擎。" : result.Text;

        // 结构化
        OcrTable? table = null;
        if (!result.IsEmpty && result.Boxes.Count > 0)
        {
            table = OcrFormatter.DetectTable(result.Boxes);
            _markdown = OcrFormatter.ToMarkdown(result.Boxes, table);
            _tsv = table.IsTable ? table.ToTsv() : result.Text;
        }
        else
        {
            _markdown = _plainText;
            _tsv = _plainText;
        }

        ResultBox.Text = _plainText;
        MdBox.Text = _markdown;

        // 表格视图：首行作列头（识别表格首行通常是表头），余下为数据
        if (table is { IsTable: true })
        {
            var rows = table.Rows;
            var dt = new DataTable();
            for (var c = 0; c < table.ColumnCount; c++)
            {
                var header = rows.Count > 0 && rows[0].Count > c && !string.IsNullOrWhiteSpace(rows[0][c])
                    ? rows[0][c]
                    : $"列 {c + 1}";
                dt.Columns.Add(header);
            }

            foreach (var row in rows.Skip(1))
            {
                var dr = dt.NewRow();
                for (var c = 0; c < table.ColumnCount; c++)
                {
                    dr[c] = c < row.Count ? row[c] : string.Empty;
                }

                dt.Rows.Add(dr);
            }

            TableGrid.ItemsSource = dt.DefaultView;
            TabTable.IsEnabled = true;
        }
        else
        {
            TableGrid.ItemsSource = null;
            TabTable.IsEnabled = false;
        }

        var structureNote = table is { IsTable: true }
            ? $"表格 {table.RowCount}×{table.ColumnCount}"
            : OcrFormatter.LooksLikeCode(result.Boxes ?? Array.Empty<OcrLineBox>())
                ? "代码（保留缩进）"
                : "普通文本";
        StatusText.Text = result.IsEmpty
            ? $"引擎：{result.EngineName}"
            : $"{result.EngineName} · {result.Elapsed.TotalSeconds:0.0}s · {structureNote} · 结果可编辑";

        Show();
        Activate();
    }

    /// <summary>识别中占位</summary>
    public void ShowRecognizing()
    {
        StatusText.Text = "正在识别…";
        Show();
        Activate();
    }

    private void Tab_Changed(object sender, RoutedEventArgs e)
    {
        if (ViewText == null)
        {
            return;
        }

        ViewText.Visibility = TabText.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ViewMd.Visibility = TabMd.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ViewTable.Visibility = TabTable.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void CopyText_Click(object sender, RoutedEventArgs e) => Copy(ResultBox.Text);

    private void CopyMd_Click(object sender, RoutedEventArgs e) => Copy(_markdown);

    private void CopyTsv_Click(object sender, RoutedEventArgs e) => Copy(_tsv);

    private void CopyClose_Click(object sender, RoutedEventArgs e)
    {
        Copy(ResultBox.Text);
        Close();
    }

    private void Copy(string content)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(content))
            {
                Clipboard.SetText(content);
            }
        }
        catch
        {
            // 剪贴板占用等
        }
    }

    private void Search_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var q = Uri.EscapeDataString(ResultBox.Text.Trim());
            if (q.Length > 0)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    $"https://www.bing.com/search?q={q}") { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Open search failed");
        }
    }
}
