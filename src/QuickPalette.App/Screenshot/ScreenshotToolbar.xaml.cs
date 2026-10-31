namespace QuickPalette.App.Screenshot;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using QuickPalette.Screenshot.Model;

/// <summary>
/// 截图工具栏：工具选择、色板、粗细、撤销/重做、复制/保存/钉图/退出。
/// 只做 UI 与事件上抛，动作由 OverlayWindow 执行。
/// </summary>
public partial class ScreenshotToolbar : UserControl
{
    /// <summary>工具切换（参数为 Tag：Select/Rectangle/Ellipse/Arrow/Brush/Highlight/Text/Mosaic/Eraser）</summary>
    public event Action<string>? ToolSelected;
    public event Action? UndoRequested;
    public event Action? RedoRequested;
    public event Action? CopyRequested;
    public event Action? SaveRequested;
    public event Action? PinRequested;
    public event Action? ExitRequested;
    public event Action<RgbaColor>? ColorSelected;

    /// <summary>粗细（非文字工具）</summary>
    public event Action<double>? WidthSelected;

    /// <summary>字号（文字工具）</summary>
    public event Action<double>? FontSizeSelected;

    private bool _loading;

    /// <summary>当前滑条语义：true=字号（文字工具），false=粗细</summary>
    private bool _fontSizeMode;

    public ScreenshotToolbar()
    {
        // 必须在 InitializeComponent 前置位：XAML 解析中 WidthSlider.Value=3 会触发 ValueChanged，
        // 此刻 WidthText 尚未解析（null），不加防护会 NRE 炸掉整个覆盖层创建
        _loading = true;
        InitializeComponent();

        Palette.ItemsSource = new[]
        {
            new SwatchItem(RgbaColor.Red),
            new SwatchItem(RgbaColor.Orange),
            new SwatchItem(RgbaColor.Yellow),
            new SwatchItem(RgbaColor.Green),
            new SwatchItem(RgbaColor.Blue),
            new SwatchItem(RgbaColor.Purple),
            new SwatchItem(RgbaColor.Black),
            new SwatchItem(RgbaColor.White)
        };
        _loading = false;

        ToolRect.IsChecked = true;
    }

    /// <summary>同步工具按钮选中态（键盘快捷键切换时调用）；并切换滑条语义（文字=字号）</summary>
    public void SetTool(string tag)
    {
        var target = tag switch
        {
            "Select" => ToolSelect,
            "Rectangle" => ToolRect,
            "Ellipse" => ToolEllipse,
            "Arrow" => ToolArrow,
            "Brush" => ToolBrush,
            "Highlight" => ToolHighlight,
            "Text" => ToolText,
            "Mosaic" => ToolMosaic,
            "Eraser" => ToolEraser,
            _ => ToolRect
        };
        target.IsChecked = true;
        SetSliderMode(tag == "Text");
    }

    /// <summary>滑条语义切换：文字工具=字号（12-72），其余=粗细（1-24）</summary>
    private void SetSliderMode(bool fontSizeMode)
    {
        if (_fontSizeMode == fontSizeMode)
        {
            return;
        }

        _fontSizeMode = fontSizeMode;
        _loading = true;
        if (fontSizeMode)
        {
            WidthLabel.Text = "字号";
            WidthSlider.Minimum = 12;
            WidthSlider.Maximum = 72;
            WidthSlider.Value = Math.Clamp(WidthSlider.Value * 8, 12, 72);
        }
        else
        {
            WidthLabel.Text = "粗细";
            WidthSlider.Minimum = 1;
            WidthSlider.Maximum = 24;
            WidthSlider.Value = Math.Clamp(WidthSlider.Value / 8, 1, 24);
        }

        WidthText.Text = ((int)WidthSlider.Value).ToString();
        _loading = false;
    }

    /// <summary>同步撤销/重做按钮可用态</summary>
    public void SetHistoryState(bool canUndo, bool canRedo)
    {
        UndoButton.IsEnabled = canUndo;
        RedoButton.IsEnabled = canRedo;
    }

    private void Tool_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (sender is RadioButton { Tag: string tag })
        {
            ToolSelected?.Invoke(tag);
        }
    }

    private void Swatch_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SwatchItem item)
        {
            ColorSelected?.Invoke(item.Color);
        }
    }

    private void WidthSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        if (WidthText != null)
        {
            WidthText.Text = ((int)e.NewValue).ToString();
        }

        if (_fontSizeMode)
        {
            FontSizeSelected?.Invoke(e.NewValue);
        }
        else
        {
            WidthSelected?.Invoke(e.NewValue);
        }
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => UndoRequested?.Invoke();
    private void Redo_Click(object sender, RoutedEventArgs e) => RedoRequested?.Invoke();
    private void Copy_Click(object sender, RoutedEventArgs e) => CopyRequested?.Invoke();
    private void Save_Click(object sender, RoutedEventArgs e) => SaveRequested?.Invoke();
    private void Pin_Click(object sender, RoutedEventArgs e) => PinRequested?.Invoke();
    private void Exit_Click(object sender, RoutedEventArgs e) => ExitRequested?.Invoke();

    /// <summary>色板项（颜色 + 画刷）</summary>
    public sealed record SwatchItem(RgbaColor Color)
    {
        public Brush Brush { get; } = new SolidColorBrush(
            System.Windows.Media.Color.FromArgb(Color.A, Color.R, Color.G, Color.B));
    }
}
