namespace QuickPalette.App.Screenshot;

using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using QuickPalette.Screenshot.Capture;
using QuickPalette.Screenshot.Detect;
using QuickPalette.Screenshot.Model;
using Serilog;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingImaging = System.Drawing.Imaging;
using Path = System.Windows.Shapes.Path;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;

/// <summary>
/// 截图覆盖层（单显示器）：选区 → 标注 → 复制/保存/钉图。
/// 交互对标 Snipaste：拖拽框选 / 智能框选（窗口级）/ Tab 暂无 / Esc 退出 / 右键退出；
/// 标注工具（V/R/O/A/P/H/T/M/E）、Ctrl+Z/Y 撤销重做、Ctrl+C 复制、Ctrl+S 保存、Ctrl+D 钉图。
/// 窗口只隐藏不销毁（由 ScreenshotService 缓存复用）。
/// </summary>
public partial class OverlayWindow : Window
{
    /// <summary>覆盖层行为配置（来自 settings.json）</summary>
    public sealed record OverlayConfig(
        bool SmartSelectEnabled,
        AnnotationKind DefaultTool,
        RgbaColor DefaultColor,
        double DefaultWidth,
        int MosaicGranularity,
        ColorFormatKind ColorFormat);

    private readonly ILogger _logger;
    private readonly CommandStack _stack = new();

    private MonitorInfo _monitor;
    private BitmapSource _baseImage;
    private double _scale = 1.0;
    private OverlayConfig _config;

    private AnnotationDocument _doc;

    // 选区状态
    private bool _hasSelection;
    private Rect _selection;
    private Point _dragStart;
    private bool _dragging;

    // 智能框选候选：按下时暂存，抬起未拖拽才算"单击选中"（拖拽始终是手动框选）
    private bool _smartCandidatePending;
    private Rect _pendingSmartRect;

    // 当前工具
    private string _tool = "Rectangle";
    private RgbaColor _color;
    private double _width = 3;
    private double _fontSize = 24;
    private bool _erasing;
    private AnnotationObject? _drawing;

    /// <summary>绘制起点（按下瞬间固定；不能用当前 Frame 左上角——反向拖拽时会跟着鼠标漂移）</summary>
    private Point _drawingStart;

    // 取色器当前采样色
    private RgbaColor _pickerColor = RgbaColor.White;

    /// <summary>结果回调（由 ScreenshotService 处理剪贴板/文件/钉图）；钉图附带选区屏幕像素位置（钉回原位）</summary>
    public event Action<BitmapSource>? Copied;
    public event Action<BitmapSource>? Saved;
    public event Action<BitmapSource, System.Drawing.Rectangle>? Pinned;
    public event Action? Exited;

    public OverlayWindow(ILogger logger)
    {
        InitializeComponent();

        _logger = logger;
        _monitor = new MonitorInfo(0, new System.Drawing.Rectangle(0, 0, 1920, 1080), 1, 1);
        _config = new OverlayConfig(true, AnnotationKind.Rectangle, RgbaColor.Default, 3, 12, ColorFormatKind.Hex);
        _baseImage = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
        _baseImage.Freeze();
        _doc = new AnnotationDocument(1, 1);

        SourceInitialized += (_, _) => PositionPixelPerfect();
        Loaded += (_, _) => Keyboard.Focus(this);

        WireToolbar();
    }

    /// <summary>装填本次捕获并复用窗口（覆盖层缓存：只隐藏不销毁）</summary>
    public void Prepare(DrawingBitmap captured, MonitorInfo monitor, OverlayConfig config)
    {
        _monitor = monitor;
        _scale = monitor.ScaleX;
        _config = config;
        _color = config.DefaultColor;
        _width = config.DefaultWidth;
        _tool = config.DefaultTool switch
        {
            AnnotationKind.Rectangle => "Rectangle",
            AnnotationKind.Ellipse => "Ellipse",
            AnnotationKind.Arrow => "Arrow",
            AnnotationKind.Brush => "Brush",
            AnnotationKind.Highlight => "Highlight",
            AnnotationKind.Text => "Text",
            AnnotationKind.Mosaic => "Mosaic",
            _ => "Rectangle"
        };

        _baseImage = ToFrozenBitmapSource(captured);
        BaseImage.Source = _baseImage;

        // 覆盖整块显示器（DIP = 像素/scale；SourceInitialized 后用像素坐标再精确置位）
        Width = monitor.PixelBounds.Width / _scale;
        Height = monitor.PixelBounds.Height / _scale;
        Left = monitor.PixelBounds.Left / _scale;
        Top = monitor.PixelBounds.Top / _scale;
        FullRect.Rect = new Rect(0, 0, Width, Height);
        _doc = new AnnotationDocument((float)Width, (float)Height);

        ResetForReuse();
        Toolbar.SetTool(_tool);

        // 窗口句柄就绪后置位像素级覆盖；未就绪则等 SourceInitialized
        if (new WindowInteropHelper(this).Handle != IntPtr.Zero)
        {
            PositionPixelPerfect();
        }
    }

    /// <summary>像素级精确置位（绕过 WPF 跨 DPI 坐标系的模糊地带）</summary>
    private void PositionPixelPerfect()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var b = _monitor.PixelBounds;
            SetWindowPos(hwnd, HwndTopmost, b.Left, b.Top, b.Width, b.Height, SwpShowWindow);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Overlay SetWindowPos failed, fallback to WPF placement");
        }
    }

    private void WireToolbar()
    {
        Toolbar.ToolSelected += tag => SetTool(tag);
        Toolbar.UndoRequested += Undo;
        Toolbar.RedoRequested += Redo;
        Toolbar.CopyRequested += () => Finish(emit: Copy);
        Toolbar.SaveRequested += () => Finish(emit: Save);
        Toolbar.PinRequested += () => Finish(emit: Pin);
        Toolbar.ExitRequested += Exit;
        Toolbar.ColorSelected += c =>
        {
            _color = c;
        };
        Toolbar.WidthSelected += w => _width = w;
        Toolbar.FontSizeSelected += f => _fontSize = f;
    }

    private void SetTool(string tag)
    {
        _tool = tag;
        Toolbar.SetTool(tag);
    }

    #region 撤销 / 重做 / 结果输出

    private void Undo()
    {
        _stack.Undo(_doc);
        SyncCanvas();
        RefreshHistoryState();
    }

    private void Redo()
    {
        _stack.Redo(_doc);
        SyncCanvas();
        RefreshHistoryState();
    }

    private void RefreshHistoryState() => Toolbar.SetHistoryState(_stack.CanUndo, _stack.CanRedo);

    /// <summary>合成"底图 + 标注"的最终图像（像素级，按显示器缩放）</summary>
    private BitmapSource RenderComposite()
    {
        var dipWidth = Math.Max(1, _selection.Width);
        var dipHeight = Math.Max(1, _selection.Height);
        var pixelWidth = Math.Max(1, (int)Math.Round(dipWidth * _scale));
        var pixelHeight = Math.Max(1, (int)Math.Round(dipHeight * _scale));

        var grid = new Grid { Width = dipWidth, Height = dipHeight };

        var crop = CropBase(_selection);
        if (crop != null)
        {
            grid.Children.Add(new Image
            {
                Source = crop,
                Width = dipWidth,
                Height = dipHeight,
                Stretch = Stretch.Fill
            });
        }

        var canvas = new Canvas { Width = dipWidth, Height = dipHeight, ClipToBounds = true };
        foreach (var annotation in _doc.Annotations)
        {
            var element = BuildElement(annotation);
            if (element != null)
            {
                canvas.Children.Add(element);
            }
        }

        grid.Children.Add(canvas);
        grid.Measure(new Size(dipWidth, dipHeight));
        grid.Arrange(new Rect(0, 0, dipWidth, dipHeight));
        grid.UpdateLayout();

        var rtb = new RenderTargetBitmap(pixelWidth, pixelHeight, 96 * _scale, 96 * _scale, PixelFormats.Pbgra32);
        rtb.Render(grid);
        rtb.Freeze();
        return rtb;
    }

    private void Copy(BitmapSource image)
    {
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            var data = new System.Windows.DataObject();
            using var ms = new MemoryStream();
            encoder.Save(ms);
            data.SetData(System.Windows.DataFormats.Bitmap, image);
            data.SetData("PNG", ms.ToArray()); // 带 PNG 格式，保透明与质量
            Clipboard.SetDataObject(data, true);
            Copied?.Invoke(image);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Screenshot copy failed");
        }
    }

    private void Save(BitmapSource image)
    {
        try
        {
            Saved?.Invoke(image);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Screenshot save failed");
        }
    }

    private void Pin(BitmapSource image)
    {
        try
        {
            Pinned?.Invoke(image, GetSelectionPixelBounds());
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Screenshot pin failed");
        }
    }

    /// <summary>选区的屏幕像素矩形（钉图定位用：钉回截图原位置）</summary>
    private System.Drawing.Rectangle GetSelectionPixelBounds()
    {
        var b = _monitor.PixelBounds;
        return new System.Drawing.Rectangle(
            b.X + (int)Math.Round(_selection.X * _scale),
            b.Y + (int)Math.Round(_selection.Y * _scale),
            Math.Max(1, (int)Math.Round(_selection.Width * _scale)),
            Math.Max(1, (int)Math.Round(_selection.Height * _scale)));
    }

    /// <summary>输出类动作：有选区先合成，无选区按全屏</summary>
    private void Finish(Action<BitmapSource> emit)
    {
        if (!_hasSelection)
        {
            SelectAll();
        }

        if (!_hasSelection)
        {
            return;
        }

        emit(RenderComposite());
        Exit();
    }

    #endregion

    #region 鼠标交互

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(this);

        if (_dragging)
        {
            UpdateSelectionFromDrag(pos);
            return;
        }

        if (_erasing)
        {
            EraseAt(pos);
            return;
        }

        if (_drawing != null)
        {
            UpdateDrawing(pos);
            return;
        }

        if (!_hasSelection && _config.SmartSelectEnabled)
        {
            UpdateSmartRect(pos);
        }

        // 取色器：空闲移动时实时采样（放大镜 + HEX/RGB/HSL）
        UpdatePicker(pos);
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 工具栏/文字输入框上的点击不算画布操作（点进输入框不应重置文字）
        if (IsInsideElement(e.OriginalSource, Toolbar) || IsInsideElement(e.OriginalSource, TextInput))
        {
            return;
        }

        var pos = e.GetPosition(this);
        Keyboard.Focus(this);

        if (!_hasSelection)
        {
            // 智能框选候选：先暂存，抬手时按"单击/拖拽"分流（拖拽=手动框选，单击=选中候选）
            if (_config.SmartSelectEnabled && SmartRect.Visibility == Visibility.Visible)
            {
                _pendingSmartRect = new Rect(
                    Canvas.GetLeft(SmartRect), Canvas.GetTop(SmartRect),
                    SmartRect.Width, SmartRect.Height);
                _smartCandidatePending = true;
                _dragging = true;
                _dragStart = pos;
                CaptureMouse();
                return;
            }

            _dragging = true;
            _dragStart = pos;
            CaptureMouse();
            return;
        }

        // 已有选区：选区外拖拽 = 重新框选；选区内按工具绘制
        if (!_selection.Contains(pos))
        {
            _dragging = true;
            _dragStart = pos;
            CaptureMouse();
            return;
        }

        BeginToolAction(pos);
    }

    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            ReleaseMouseCapture();

            var pos = e.GetPosition(this);
            var moved = Math.Abs(pos.X - _dragStart.X) > 4 || Math.Abs(pos.Y - _dragStart.Y) > 4;

            if (_smartCandidatePending)
            {
                _smartCandidatePending = false;
                if (moved)
                {
                    // 拖拽 = 手动框选（忽略智能候选）
                    UpdateSelectionFromDrag(pos);
                    CommitSelection();
                }
                else
                {
                    // 单击 = 选中悬停高亮的候选窗口
                    SelectRect(_pendingSmartRect);
                }

                return;
            }

            if (moved)
            {
                UpdateSelectionFromDrag(pos);
                CommitSelection();
            }

            return;
        }

        if (_erasing)
        {
            _erasing = false;
            ReleaseMouseCapture();
            return;
        }

        EndToolAction();
    }

    private void Window_MouseRightButtonUp(object sender, MouseButtonEventArgs e) => Exit();

    private void UpdateSelectionFromDrag(Point pos)
    {
        SmartRect.Visibility = Visibility.Collapsed; // 手动框选中，不再显示智能高亮

        var x = Math.Min(_dragStart.X, pos.X);
        var y = Math.Min(_dragStart.Y, pos.Y);
        var w = Math.Abs(pos.X - _dragStart.X);
        var h = Math.Abs(pos.Y - _dragStart.Y);

        ShowSelectionRect(new Rect(x, y, w, h));
    }

    private void CommitSelection()
    {
        if (_selection.Width < 4 || _selection.Height < 4)
        {
            return;
        }

        _hasSelection = true;
        _doc = new AnnotationDocument((float)_selection.Width, (float)_selection.Height);
        _stack.Clear();
        SmartRect.Visibility = Visibility.Collapsed;

        // 标注画布铺到选区
        Canvas.SetLeft(AnnotationCanvas, _selection.X);
        Canvas.SetTop(AnnotationCanvas, _selection.Y);
        AnnotationCanvas.Width = _selection.Width;
        AnnotationCanvas.Height = _selection.Height;
        AnnotationCanvas.Visibility = Visibility.Visible;
        SyncCanvas();
        RefreshHistoryState();

        // 工具栏置于选区下方（越界翻到上方）
        PositionToolbar();
    }

    private void SelectRect(Rect rect)
    {
        ShowSelectionRect(rect);
        CommitSelection();
    }

    private void SelectAll()
    {
        SelectRect(new Rect(0, 0, Width, Height));
    }

    private void ShowSelectionRect(Rect rect)
    {
        _selection = rect;
        _hasSelection = rect.Width >= 4 && rect.Height >= 4;

        Canvas.SetLeft(SelectionBorder, rect.X);
        Canvas.SetTop(SelectionBorder, rect.Y);
        SelectionBorder.Width = rect.Width;
        SelectionBorder.Height = rect.Height;
        SelectionBorder.Visibility = _hasSelection ? Visibility.Visible : Visibility.Collapsed;

        HoleRect.Rect = rect;

        SizeBadge.Visibility = _hasSelection ? Visibility.Visible : Visibility.Collapsed;
        SizeText.Text = string.Create(CultureInfo.InvariantCulture,
            $"{(int)Math.Round(rect.Width * _scale)} × {(int)Math.Round(rect.Height * _scale)}");
        Canvas.SetLeft(SizeBadge, rect.X);
        Canvas.SetTop(SizeBadge, Math.Max(0, rect.Y - 30));
    }

    private void UpdateSmartRect(Point pos)
    {
        var pixelPoint = new System.Drawing.Point(
            (int)Math.Round(_monitor.PixelBounds.X + pos.X * _scale),
            (int)Math.Round(_monitor.PixelBounds.Y + pos.Y * _scale));

        var window = WindowDetector.FindWindowAtPixel(pixelPoint);
        if (window == null)
        {
            SmartRect.Visibility = Visibility.Collapsed;
            return;
        }

        var b = window.PixelBounds;
        var rect = new Rect(
            (b.X - _monitor.PixelBounds.X) / _scale,
            (b.Y - _monitor.PixelBounds.Y) / _scale,
            b.Width / _scale,
            b.Height / _scale);

        SmartRect.Visibility = Visibility.Visible;
        Canvas.SetLeft(SmartRect, rect.X);
        Canvas.SetTop(SmartRect, rect.Y);
        SmartRect.Width = rect.Width;
        SmartRect.Height = rect.Height;
        ToolTip = string.IsNullOrEmpty(window.Title) ? null : window.Title;
    }

    #region 取色器

    /// <summary>实时取色：光标处采样，放大镜 + HEX/RGB/HSL 三格式跟随显示（在渲染进程内读底图，无跨进程开销）</summary>
    private void UpdatePicker(Point pos)
    {
        try
        {
            var px = (int)Math.Round(pos.X * _scale);
            var py = (int)Math.Round(pos.Y * _scale);
            if (px < 0 || py < 0 || px >= _baseImage.PixelWidth || py >= _baseImage.PixelHeight)
            {
                PickerBadge.Visibility = Visibility.Collapsed;
                PickerCursor.Visibility = Visibility.Collapsed;
                return;
            }

            // 采样 1px（Bgra32：B,G,R,A）
            var pixel = new byte[4];
            _baseImage.CopyPixels(new Int32Rect(px, py, 1, 1), pixel, 4, 0);
            _pickerColor = new RgbaColor(pixel[2], pixel[1], pixel[0], pixel[3]);

            // 放大镜：11×11 像素网格，最近邻放大
            const int gridSize = 11;
            var gx = Math.Clamp(px - gridSize / 2, 0, Math.Max(0, _baseImage.PixelWidth - gridSize));
            var gy = Math.Clamp(py - gridSize / 2, 0, Math.Max(0, _baseImage.PixelHeight - gridSize));
            var magnifier = new CroppedBitmap(_baseImage, new Int32Rect(gx, gy, gridSize, gridSize));
            RenderOptions.SetBitmapScalingMode(MagnifierImage, BitmapScalingMode.NearestNeighbor);
            MagnifierImage.Source = magnifier;

            PickerSwatch.Background = new SolidColorBrush(
                System.Windows.Media.Color.FromArgb(_pickerColor.A, _pickerColor.R, _pickerColor.G, _pickerColor.B));
            PickerText.Text =
                $"{ColorFormat.Format(_pickerColor, ColorFormatKind.Hex)}\n" +
                $"{ColorFormat.Format(_pickerColor, ColorFormatKind.Rgb)}\n" +
                $"{ColorFormat.Format(_pickerColor, ColorFormatKind.Hsl)}";

            // 浮层跟随光标（越界翻转）
            var badgeX = pos.X + 24;
            if (badgeX + 140 > Width)
            {
                badgeX = pos.X - 164;
            }

            var badgeY = pos.Y + 24;
            if (badgeY + 190 > Height)
            {
                badgeY = pos.Y - 214;
            }

            Canvas.SetLeft(PickerBadge, Math.Max(0, badgeX));
            Canvas.SetTop(PickerBadge, Math.Max(0, badgeY));
            Canvas.SetLeft(PickerCursor, pos.X - 7);
            Canvas.SetTop(PickerCursor, pos.Y - 7);
            PickerBadge.Visibility = Visibility.Visible;
            PickerCursor.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Color picker sample failed");
        }
    }

    /// <summary>中键：复制当前颜色（主格式在设置中选择：HEX/RGB/HSL），不退出截图态</summary>
    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle)
        {
            return;
        }

        try
        {
            var text = ColorFormat.Format(_pickerColor, _config.ColorFormat);
            Clipboard.SetText(text);
            PickerText.Text = $"✓ {text}";
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Color picker copy failed");
        }
    }

    #endregion

    private void PositionToolbar()
    {
        Toolbar.Visibility = Visibility.Visible;
        Toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = Toolbar.DesiredSize;
        if (size.Width < 1 || size.Height < 1)
        {
            size = new Size(460, 80); // 未完成测量时的兜底尺寸（防定位算出 NaN/越界）
        }

        var x = Math.Max(0, Math.Min(_selection.Right - size.Width, Width - size.Width));
        var y = _selection.Bottom + 8;
        if (y + size.Height > Height)
        {
            y = Math.Max(0, _selection.Top - size.Height - 8);
        }

        Canvas.SetLeft(Toolbar, x);
        Canvas.SetTop(Toolbar, y);
    }

    private bool IsInsideElement(object? source, DependencyObject target)
    {
        for (DependencyObject? d = source as DependencyObject; d != null; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
        {
            if (ReferenceEquals(d, target))
            {
                return true;
            }
        }

        return false;
    }

    #endregion

    #region 标注绘制

    private void BeginToolAction(Point pos)
    {
        switch (_tool)
        {
            case "Eraser":
                EraseAt(pos);
                _erasing = true;
                CaptureMouse(); // 拖动连续擦
                return;
            case "Text":
                BeginText(pos);
                return;
            case "Select":
                _dragging = true;
                _dragStart = pos;
                CaptureMouse();
                return;
        }

        var kind = _tool switch
        {
            "Rectangle" => AnnotationKind.Rectangle,
            "Ellipse" => AnnotationKind.Ellipse,
            "Arrow" => AnnotationKind.Arrow,
            "Brush" => AnnotationKind.Brush,
            "Highlight" => AnnotationKind.Highlight,
            "Mosaic" => AnnotationKind.Mosaic,
            _ => AnnotationKind.Rectangle
        };

        _drawing = new AnnotationObject
        {
            Kind = kind,
            Style = new AnnotationStyle
            {
                Color = _color,
                StrokeWidth = kind == AnnotationKind.Highlight ? Math.Max(8, _width * 4) : _width,
                Opacity = kind == AnnotationKind.Highlight ? 0.4 : 1.0,
                FontSize = 16,
                MosaicGranularity = _config.MosaicGranularity
            }
        };

        _drawingStart = pos; // 按下瞬间固定起点
        if (kind == AnnotationKind.Arrow)
        {
            // 箭头保留拖拽方向：Points[0]=起点、Points[1]=终点（外接框只用于命中/定位）
            _drawing.Points.Add(ToDoc(pos));
        }
        else if (kind is AnnotationKind.Brush or AnnotationKind.Highlight)
        {
            _drawing.Points.Add(ToDoc(pos));
        }
        else
        {
            _drawing.Frame = new System.Drawing.RectangleF(
                (float)(pos.X - _selection.X), (float)(pos.Y - _selection.Y), 0, 0);
        }

        CaptureMouse(); // 拖出窗口仍跟手

        _doc.Add(_drawing);
        SyncCanvas();
    }

    private void EndToolAction()
    {
        if (_drawing == null)
        {
            return;
        }

        var drawing = _drawing;
        _drawing = null;
        ReleaseMouseCapture();

        // 拖拽太小（误点）不入历史
        if (drawing.Kind is AnnotationKind.Brush or AnnotationKind.Highlight)
        {
            if (drawing.Points.Count < 2)
            {
                _doc.Remove(drawing.Id);
                SyncCanvas();
                return;
            }
        }
        else if (drawing.Kind == AnnotationKind.Arrow)
        {
            // 箭头按起止距离判退化（水平/垂直箭头是合法图形，不能用外接框判）
            var valid = drawing.Points.Count >= 2 && Distance(drawing.Points[0], drawing.Points[1]) >= 2;
            if (!valid)
            {
                _doc.Remove(drawing.Id);
                SyncCanvas();
                return;
            }
        }
        else if (drawing.Frame.Width < 2 || drawing.Frame.Height < 2)
        {
            // 退化图形（误点/水平垂直拖拽）不入历史
            _doc.Remove(drawing.Id);
            SyncCanvas();
            return;
        }

        // 绘制预览已挂在文档上：撤掉预览再走命令栈入正式历史（保证撤销语义一致）
        var index = _doc.Remove(drawing);
        _stack.Execute(new AddAnnotationCommand(drawing, index >= 0 ? index : _doc.Annotations.Count), _doc);
        RefreshHistoryState();
        SyncCanvas();
    }

    /// <summary>绘制/拖拽过程中刷新当前对象几何</summary>
    private void UpdateDrawing(Point pos)
    {
        if (_drawing == null)
        {
            return;
        }

        var local = ToDoc(pos);
        if (_drawing.Kind is AnnotationKind.Brush or AnnotationKind.Highlight)
        {
            _drawing.Points.Add(local);
        }
        else
        {
            // 始终以按下瞬间的起点为对角基准（反向拖拽也不会漂移）
            var sx = (float)(_drawingStart.X - _selection.X);
            var sy = (float)(_drawingStart.Y - _selection.Y);
            var frame = System.Drawing.RectangleF.FromLTRB(
                Math.Min(sx, local.X), Math.Min(sy, local.Y),
                Math.Max(sx, local.X), Math.Max(sy, local.Y));
            _drawing.Frame = frame;

            if (_drawing.Kind == AnnotationKind.Arrow)
            {
                // 箭头终点跟随鼠标（方向锚点）
                if (_drawing.Points.Count >= 2)
                {
                    _drawing.Points[1] = local;
                }
                else
                {
                    _drawing.Points.Add(local);
                }
            }
        }

        SyncCanvas();
    }

    /// <summary>
    /// 橡皮擦：画笔/荧光笔 = 点擦（擦掉光标附近的笔迹片段，剩余段拆成新笔画，可撤销）；
    /// 形状/文字/马赛克 = 点击删除整个对象。
    /// </summary>
    private void EraseAt(Point pos)
    {
        var doc = ToDoc(pos);
        var hit = _doc.HitTest(doc, 6);
        if (hit == null)
        {
            return;
        }

        if (hit.Kind is AnnotationKind.Brush or AnnotationKind.Highlight)
        {
            var radius = (float)(6 + hit.Style.StrokeWidth / 2);
            var pieces = StrokeSplitter.Split(hit, doc, radius);

            // 未命中任何点（擦过空隙）：不产生命令
            if (pieces.Count == 1 && pieces[0].Points.Count == hit.Points.Count)
            {
                return;
            }

            var index = _doc.IndexOf(hit.Id);
            _stack.Execute(new SplitStrokeCommand(hit, pieces, index), _doc);
        }
        else
        {
            var index = _doc.IndexOf(hit.Id);
            _stack.Execute(new RemoveAnnotationCommand(hit, index), _doc);
        }

        SyncCanvas();
        RefreshHistoryState();
    }

    /// <summary>文档坐标两点距离（箭头退化判定用）</summary>
    private static double Distance(System.Drawing.PointF a, System.Drawing.PointF b)
        => Math.Sqrt((a.X - b.X) * (double)(a.X - b.X) + (a.Y - b.Y) * (double)(a.Y - b.Y));

    private System.Drawing.PointF ToDoc(Point windowPos)
        => new((float)(windowPos.X - _selection.X), (float)(windowPos.Y - _selection.Y));

    /// <summary>文档 → 画布元素（展示与导出共用）</summary>
    private UIElement? BuildElement(AnnotationObject annotation)
    {
        var brush = new SolidColorBrush(
            System.Windows.Media.Color.FromArgb(
                annotation.Style.Color.A, annotation.Style.Color.R, annotation.Style.Color.G, annotation.Style.Color.B))
        {
            Opacity = annotation.Style.Opacity
        };

        switch (annotation.Kind)
        {
            case AnnotationKind.Rectangle:
                return Place(new Rectangle
                {
                    Width = Math.Max(0, annotation.Frame.Width),
                    Height = Math.Max(0, annotation.Frame.Height),
                    Stroke = brush,
                    StrokeThickness = annotation.Style.StrokeWidth
                }, annotation.Frame.X, annotation.Frame.Y);

            case AnnotationKind.Ellipse:
                return Place(new Ellipse
                {
                    Width = Math.Max(0, annotation.Frame.Width),
                    Height = Math.Max(0, annotation.Frame.Height),
                    Stroke = brush,
                    StrokeThickness = annotation.Style.StrokeWidth
                }, annotation.Frame.X, annotation.Frame.Y);

            case AnnotationKind.Arrow:
            {
                var geometry = BuildArrowGeometry(annotation);
                return Place(new Path
                {
                    Data = geometry,
                    Stroke = brush,
                    StrokeThickness = annotation.Style.StrokeWidth,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                }, 0, 0);
            }

            case AnnotationKind.Brush:
            case AnnotationKind.Highlight:
            {
                var polyline = new Polyline
                {
                    Stroke = brush,
                    StrokeThickness = annotation.Style.StrokeWidth,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round
                };
                foreach (var p in annotation.Points)
                {
                    polyline.Points.Add(new Point(p.X, p.Y));
                }

                return polyline;
            }

            case AnnotationKind.Text:
                return Place(new TextBlock
                {
                    Text = annotation.Text,
                    Foreground = brush,
                    FontSize = annotation.Style.FontSize,
                    FontWeight = FontWeights.SemiBold
                }, annotation.Frame.X, annotation.Frame.Y);

            case AnnotationKind.Mosaic:
            {
                var crop = CropBase(ToWindowRect(annotation.Frame));
                if (crop == null)
                {
                    return null;
                }

                var granularity = Math.Max(2, annotation.Style.MosaicGranularity);
                var down = new TransformedBitmap(crop, new ScaleTransform(
                    1.0 / granularity, 1.0 / granularity));
                down.Freeze();

                var image = new Image
                {
                    Source = down,
                    Width = Math.Max(0, annotation.Frame.Width),
                    Height = Math.Max(0, annotation.Frame.Height),
                    Stretch = Stretch.Fill
                };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.LowQuality); // 最近邻 = 像素化
                return Place(image, annotation.Frame.X, annotation.Frame.Y);
            }

            default:
                return null;
        }
    }

    private static UIElement Place(UIElement element, double x, double y)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        return element;
    }

    private static Geometry BuildArrowGeometry(AnnotationObject annotation)
    {
        // 方向锚点优先（Points[0]=起点 → Points[1]=终点）；无锚点时退回外接框对角
        Point start, end;
        if (annotation.Points.Count >= 2)
        {
            start = new Point(annotation.Points[0].X, annotation.Points[0].Y);
            end = new Point(annotation.Points[1].X, annotation.Points[1].Y);
        }
        else
        {
            var f = annotation.Frame;
            start = new Point(f.Left, f.Top);
            end = new Point(f.Right, f.Bottom);
        }

        var direction = end - start;
        if (direction.Length < 0.01)
        {
            return new LineGeometry(start, end);
        }

        direction.Normalize();
        // 箭头头部长度随箭头长度收敛（10~40 DIP），并随描边加粗
        var headLength = Math.Clamp(direction.Length * 0.25, 10, 40) + annotation.Style.StrokeWidth * 2;
        var basePoint = end - direction * headLength;
        var normal = new Vector(-direction.Y, direction.X);

        var geometry = new GeometryGroup();
        geometry.Children.Add(new LineGeometry(start, end));
        geometry.Children.Add(new LineGeometry(end, basePoint + normal * headLength * 0.45));
        geometry.Children.Add(new LineGeometry(end, basePoint - normal * headLength * 0.45));
        return geometry;
    }

    /// <summary>选区 DIP 矩形 → 底图像素裁剪框（越界钳制）</summary>
    private CroppedBitmap? CropBase(Rect dipRect)
    {
        var x = Math.Max(0, (int)Math.Round(dipRect.X * _scale));
        var y = Math.Max(0, (int)Math.Round(dipRect.Y * _scale));
        var w = Math.Min(_baseImage.PixelWidth - x, (int)Math.Round(dipRect.Width * _scale));
        var h = Math.Min(_baseImage.PixelHeight - y, (int)Math.Round(dipRect.Height * _scale));
        if (w <= 0 || h <= 0)
        {
            return null;
        }

        var cropped = new CroppedBitmap(_baseImage, new Int32Rect(x, y, w, h));
        cropped.Freeze();
        return cropped;
    }

    /// <summary>文档坐标矩形 → 窗口坐标矩形（换算选区原点）</summary>
    private Rect ToWindowRect(System.Drawing.RectangleF frame)
        => new(_selection.X + frame.X, _selection.Y + frame.Y, frame.Width, frame.Height);

    /// <summary>重建标注画布内容</summary>
    private void SyncCanvas()
    {
        AnnotationCanvas.Children.Clear();
        foreach (var annotation in _doc.Annotations)
        {
            var element = BuildElement(annotation);
            if (element != null)
            {
                AnnotationCanvas.Children.Add(element);
            }
        }

        // 绘制中的对象也占文档一席，但 Undo 栈里还没有 → 无需处理
    }

    #endregion

    #region 文字标注

    private void BeginText(Point pos)
    {
        // 已有输入框未提交：先提交旧文字，再在新位置开新框（点别处=放置完成+继续添加）
        CommitText();

        TextInput.Visibility = Visibility.Visible;
        Canvas.SetLeft(TextInput, pos.X);
        Canvas.SetTop(TextInput, pos.Y);
        TextInput.Width = 240;
        TextInput.Text = string.Empty;
        TextInput.FontSize = _fontSize;
        TextInput.Foreground = new SolidColorBrush(
            System.Windows.Media.Color.FromArgb(_color.A, _color.R, _color.G, _color.B));
        TextInput.Focus();
    }

    private void TextInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitText();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            TextInput.Visibility = Visibility.Collapsed;
            Focus();
            e.Handled = true;
        }
    }

    private void TextInput_LostFocus(object sender, RoutedEventArgs e) => CommitText();

    private void CommitText()
    {
        if (TextInput.Visibility != Visibility.Visible)
        {
            return;
        }

        TextInput.Visibility = Visibility.Collapsed;
        Focus();

        var text = TextInput.Text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        var pos = new Point(Canvas.GetLeft(TextInput), Canvas.GetTop(TextInput));
        var annotation = new AnnotationObject
        {
            Kind = AnnotationKind.Text,
            Text = text,
            Style = new AnnotationStyle { Color = _color, FontSize = (int)_fontSize },
            Frame = new System.Drawing.RectangleF(
                (float)(pos.X - _selection.X), (float)(pos.Y - _selection.Y), 240, (float)_fontSize + 8)
        };
        _stack.Execute(new AddAnnotationCommand(annotation, _doc.Annotations.Count), _doc);
        SyncCanvas();
        RefreshHistoryState();
    }

    #endregion

    #region 键盘

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (TextInput.Visibility == Visibility.Visible)
        {
            return; // 文字输入中把键盘让给输入框
        }

        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.Z: Undo(); e.Handled = true; return;
                case Key.Y: Redo(); e.Handled = true; return;
                case Key.C: Finish(Copy); e.Handled = true; return;
                case Key.S: Finish(Save); e.Handled = true; return;
                case Key.D: Finish(Pin); e.Handled = true; return;
                case Key.A:
                    if (!_hasSelection)
                    {
                        SelectAll();
                    }

                    e.Handled = true;
                    return;
            }

            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                Exit();
                e.Handled = true;
                break;
            case Key.V: SetTool("Select"); break;
            case Key.R: SetTool("Rectangle"); break;
            case Key.O: SetTool("Ellipse"); break;
            case Key.A: SetTool("Arrow"); break;
            case Key.P: SetTool("Brush"); break;
            case Key.H: SetTool("Highlight"); break;
            case Key.T: SetTool("Text"); break;
            case Key.M: SetTool("Mosaic"); break;
            case Key.E: SetTool("Eraser"); break;
        }
    }

    #endregion

    private void Exit()
    {
        try
        {
            Hide();
            Exited?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Overlay exit failed");
        }
    }

    /// <summary>重置到初始态（窗口复用前调用）</summary>
    public void ResetForReuse()
    {
        _hasSelection = false;
        _selection = new Rect();
        _dragging = false;
        _erasing = false;
        _drawing = null;
        _doc = new AnnotationDocument((float)Width, (float)Height);
        _stack.Clear();
        RefreshHistoryState();
        AnnotationCanvas.Children.Clear();
        AnnotationCanvas.Visibility = Visibility.Collapsed;
        SelectionBorder.Visibility = Visibility.Collapsed;
        SizeBadge.Visibility = Visibility.Collapsed;
        SmartRect.Visibility = Visibility.Collapsed;
        TextInput.Visibility = Visibility.Collapsed;
        Toolbar.Visibility = Visibility.Collapsed;
        HoleRect.Rect = Rect.Empty;
        ToolTip = null;
    }

    /// <summary>捕获位图 → 冻结 BitmapSource（不经 HBitmap，无泄漏）</summary>
    private static BitmapSource ToFrozenBitmapSource(DrawingBitmap bitmap)
    {
        var rect = new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, DrawingImaging.ImageLockMode.ReadOnly, bitmap.PixelFormat);
        try
        {
            var source = BitmapSource.Create(
                bitmap.Width, bitmap.Height, 96, 96,
                PixelFormats.Bgra32, null, data.Scan0, data.Stride * bitmap.Height, data.Stride);
            source.Freeze();
            return source;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private const int HwndTopmost = -1;
    private const uint SwpShowWindow = 0x0040;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, int hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
