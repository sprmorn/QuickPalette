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

    /// <summary>两套记忆值：切换工具不丢已选的粗细/字号</summary>
    private double _lastWidth = 3;
    private double _lastFont = 24;

    public ScreenshotToolbar()
    {
        // 必须在 InitializeComponent 前置位：XAML 解析中 WidthSlider.Value=3 会触发 ValueChanged，
        // 此刻 WidthText 尚未解析（null），不加防护会 NRE 炸掉整个覆盖层创建
        _loading = true;
        InitializeComponent();

        // 工具栏拖动移动（按钮自身消费点击，不会触发拖动）
        MouseLeftButtonDown += Toolbar_MouseLeftButtonDown;
        MouseMove += Toolbar_MouseMove;
        MouseLeftButtonUp += Toolbar_MouseLeftButtonUp;

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

        // 属性栏（颜色/粗细/字号）只在绘制类工具时显示；选择/橡皮擦不需要
        StylePanel.Visibility = tag is "Select" or "Eraser"
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    /// <summary>滑条语义切换：文字工具=字号（12-72），其余=粗细（1-24）</summary>
    private void SetSliderMode(bool fontSizeMode)
    {
        if (_fontSizeMode == fontSizeMode)
        {
            return;
        }

        // 当前值存回旧模式（注意：必须先存值再改范围——先抬 Minimum 会把值强 coerced 抬走）
        if (_fontSizeMode)
        {
            _lastFont = WidthSlider.Value;
        }
        else
        {
            _lastWidth = WidthSlider.Value;
        }

        _fontSizeMode = fontSizeMode;

        _loading = true;
        if (fontSizeMode)
        {
            WidthLabel.Text = "字号";
            WidthSlider.Minimum = 12;
            WidthSlider.Maximum = 72;
            WidthSlider.Value = Math.Clamp(_lastFont, 12, 72);
        }
        else
        {
            WidthLabel.Text = "粗细";
            WidthSlider.Minimum = 1;
            WidthSlider.Maximum = 24;
            WidthSlider.Value = Math.Clamp(_lastWidth, 1, 24);
        }

        WidthText.Text = ((int)WidthSlider.Value).ToString();
        _loading = false;

        // 切换后立即同步语义值：保证显示值与实际生效值一致
        if (fontSizeMode)
        {
            FontSizeSelected?.Invoke(WidthSlider.Value);
        }
        else
        {
            WidthSelected?.Invoke(WidthSlider.Value);
        }
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

        e.Handled = true; // 不冒泡成工具栏拖动
    }

    #region 工具栏拖动

    private bool _draggingToolbar;
    private Point _toolbarDragStart;
    private double _toolbarStartLeft;
    private double _toolbarStartTop;

    /// <summary>坐标基准：父画布（静止）。用工具栏自身做基准会因它跟着动而只移动一半距离</summary>
    private UIElement? ToolbarDragReference => Parent as UIElement ?? this;

    /// <summary>工具栏空白处按下 → 开始拖动（按钮/滑条消费了各自的点击，不会到这）</summary>
    private void Toolbar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _draggingToolbar = true;
        _toolbarDragStart = e.GetPosition(ToolbarDragReference);
        _toolbarStartLeft = double.IsNaN(Canvas.GetLeft(this)) ? 0 : Canvas.GetLeft(this);
        _toolbarStartTop = double.IsNaN(Canvas.GetTop(this)) ? 0 : Canvas.GetTop(this);
        CaptureMouse();
        e.Handled = true;
    }

    private void Toolbar_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_draggingToolbar)
        {
            return;
        }

        var pos = e.GetPosition(ToolbarDragReference);
        var dx = pos.X - _toolbarDragStart.X;
        var dy = pos.Y - _toolbarDragStart.Y;

        // 钳制在覆盖层范围内
        var parent = Parent as FrameworkElement;
        var maxLeft = Math.Max(0, (parent?.ActualWidth ?? 0) - ActualWidth);
        var maxTop = Math.Max(0, (parent?.ActualHeight ?? 0) - ActualHeight);
        Canvas.SetLeft(this, Math.Clamp(_toolbarStartLeft + dx, 0, maxLeft));
        Canvas.SetTop(this, Math.Clamp(_toolbarStartTop + dy, 0, maxTop));
    }

    private void Toolbar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_draggingToolbar)
        {
            return;
        }

        _draggingToolbar = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    #endregion

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
