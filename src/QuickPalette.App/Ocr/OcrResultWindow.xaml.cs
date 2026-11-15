namespace QuickPalette.App.Ocr;

using System.Windows;
using System.Windows.Input;
using QuickPalette.App.Screenshot;

/// <summary>OCR 结果面板：可修正文本 + 复制 / 复制并关闭 / 搜索此文本</summary>
public partial class OcrResultWindow : Window
{
    public OcrResultWindow()
    {
        InitializeComponent();
        TitleIconHost.Content = ToolbarIcons.Create("ocr", 18);
    }

    /// <summary>填充识别结果（识别完成后由服务层调用）</summary>
    public void ShowResult(string text, string engineName, double seconds, bool isEmpty)
    {
        ResultBox.Text = isEmpty
            ? "未识别到文字。可尝试：框选更大范围、或换高精度引擎。"
            : text;
        StatusText.Text = isEmpty
            ? $"引擎：{engineName}"
            : $"引擎：{engineName}　耗时：{seconds:0.0}s　识别结果可直接编辑修正";
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
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            CopyResult();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void CopyAll_Click(object sender, RoutedEventArgs e) => CopyResult();

    private void CopyClose_Click(object sender, RoutedEventArgs e)
    {
        CopyResult();
        Close();
    }

    private void CopyResult()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(ResultBox.Text))
            {
                Clipboard.SetText(ResultBox.Text);
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
