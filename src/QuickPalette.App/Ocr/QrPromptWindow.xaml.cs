namespace QuickPalette.App.Ocr;

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using QuickPalette.App.Screenshot;
using QuickPalette.Ocr;

/// <summary>
/// 扫码卡片：识别到的二维码分行展示（URL 显示域名），URL 行提供「打开/复制」，
/// 全部内容可一键复制；非链接仅提供复制。30 秒自动收起（可手动关）。
/// </summary>
public partial class QrPromptWindow : Window
{
    public sealed class CodeItem
    {
        public string Text { get; init; } = string.Empty;
        public string Display { get; init; } = string.Empty;
        public bool CanOpen { get; init; }
    }

    private readonly System.Windows.Threading.DispatcherTimer _autoClose;

    public QrPromptWindow()
    {
        InitializeComponent();
        TitleIconHost.Content = ToolbarIcons.Create("qr", 16);

        // WPF 无内置 BoolToVisibility 的 StaticResource —— 用代码注册转换器
        Resources["BoolToVisibility"] = new BoolToVisibilityConverter();

        _autoClose = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30),
        };
        _autoClose.Tick += (_, _) => Close();
        Loaded += (_, _) => _autoClose.Start();
    }

    /// <summary>填充识别结果并显示（右上角）</summary>
    public void ShowCodes(IReadOnlyList<QrResult> codes)
    {
        var items = new ObservableCollection<CodeItem>();
        foreach (var c in codes)
        {
            var display = c.Text;
            if (c.IsUrl && Uri.TryCreate(c.Text, UriKind.Absolute, out var u))
            {
                display = $"{u.Host}{u.PathAndQuery}";
            }

            items.Add(new CodeItem
            {
                Text = c.Text,
                Display = display,
                CanOpen = c.IsUrl,
            });
        }

        CodeList.ItemsSource = items;

        // 右上角（与链接助手一致）
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 18;
        Top = 18;
        Show();
        Activate();
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CodeItem item)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(item.Text)
                {
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Open QR link failed");
            }
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CodeItem item)
        {
            try
            {
                Clipboard.SetText(item.Text);
            }
            catch
            {
            }
        }
    }

    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (CodeList.ItemsSource is ObservableCollection<CodeItem> items && items.Count > 0)
            {
                Clipboard.SetText(string.Join(Environment.NewLine, items.Select(i => i.Text)));
            }
        }
        catch
        {
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

/// <summary>bool → Visibility</summary>
public sealed class BoolToVisibilityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}
