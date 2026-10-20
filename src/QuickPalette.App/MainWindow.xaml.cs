namespace QuickPalette.App;

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuickPalette.App.Services;
using QuickPalette.Application.Events;
using QuickPalette.Domain.Contracts;
using QuickPalette.Domain.Models;
using QuickPalette.Domain.Services;
using QuickPalette.Infrastructure.Launchers;
using QuickPalette.Infrastructure.Services;
using QuickPalette.Infrastructure.Storage;
using Serilog;

/// <summary>
/// 命令面板主窗口。
/// 行为要点：
///  - Closing 拦截（Alt+F4 只隐藏不销毁）；失焦自动隐藏，弹对话框期间抑制；
///  - 每次唤起定位到光标所在屏幕中心（多显示器 + DPI 换算）；
///  - 异步搜索带版本号，stale 结果不回填；应用池写时复制；图标按 appId 缓存（OnLoad + Freeze）；
///  - 命令模式（"&gt;" 前缀）、拖拽添加、命中高亮、分组标题、面板高度自适应。
/// </summary>
public partial class MainWindow : Window
{
    #region 常量

    /// <summary>文件结果最大条数（应用结果优先展示，文件紧随其后）</summary>
    private const int FileResultLimit = 5000;

    /// <summary>每批增量渲染的文件结果条数（列表虚拟化 + 滚动加载更多）</summary>
    private const int FileBatchSize = 30;

    /// <summary>键盘翻页步长（PageUp/PageDown）</summary>
    private const int PageStep = 10;

    /// <summary>结果行估算高度（DIP），用于面板高度自适应</summary>
    private const double RowHeightEstimate = 68; // = 行内容 MinHeight 40 + 模板 Margin 8 + ListBoxItem Padding 16 + Margin 4（原 52 低估导致末行被裁切）

    /// <summary>分组标题估算高度（DIP）</summary>
    private const double GroupHeaderHeightEstimate = 30;

    /// <summary>搜索区 + 底部栏 + 边距的固定高度（DIP）</summary>
    private const double ChromeHeight = 170;

    /// <summary>emoji 字形在 em box 内的左侧留白比例（side bearing ≈ 0.203×Width）</summary>
    private const double EmojiSideBearingRatio = 0.203;

    /// <summary>emoji 视觉中心补偿：直接按 width/2 居中会让符号偏左约 9.5px，按此常量补偿后居中</summary>
    private const double EmojiCenteringCompensation = 4.7;

    /// <summary>面板最大可见结果行数估算用（超过 MaxHeight 后靠滚动条）</summary>
    private const double MaxPanelHeight = 700;

    #endregion

    #region 字段

    private readonly SearchEngine _searchEngine;
    private readonly FileSearchManager _fileSearchManager;
    private readonly EventBus _eventBus;
    private readonly ILogger _logger;
    private readonly JsonRepository _repository;
    private readonly LauncherManager _launcherManager;
    private readonly IconService _iconService;
    private readonly HiddenItemStore _hiddenItemStore;
    private readonly UsageStore _usageStore;
    private readonly PaletteHostActions _host;

    /// <summary>应用池：写时复制（替换整个列表，绝不在原列表上增删，避免与后台搜索枚举并发）</summary>
    private List<AppItem> _appPool;

    private ObservableCollection<ResultDisplayItem> _currentResults = new();

    /// <summary>DeepSeek 网络搜索模式（输入 DS 后按 Tab 进入；Esc/空退格退出）</summary>
    private bool _deepSeekMode;
    private List<FileHit>? _pendingFileHits;
    private bool _loadingMore;
    private readonly Dictionary<FileKind, ImageSource> _fileIconCache = new();
    private readonly Dictionary<string, ImageSource> _appIconCache = new();
    private CancellationTokenSource _searchCts = new();

    /// <summary>搜索版本号：只有最新一次搜索的结果才允许回填 UI（防 stale）</summary>
    private int _searchVersion;

    /// <summary>弹对话框期间抑制 Deactivated 自动隐藏（可重入计数）</summary>
    private int _suppressHideCount;

    /// <summary>真正退出（App.Shutdown）时置位，允许窗口关闭</summary>
    private bool _forceClose;

    private ImageSource? _defaultIcon;

    #endregion

    public MainWindow(
        SearchEngine searchEngine,
        FileSearchManager fileSearchManager,
        EventBus eventBus,
        ILogger logger,
        List<AppItem> appPool,
        JsonRepository repository,
        LauncherManager launcherManager,
        HiddenItemStore hiddenItemStore,
        UsageStore usageStore,
        PaletteHostActions host)
    {
        InitializeComponent();

        _searchEngine = searchEngine;
        _fileSearchManager = fileSearchManager;
        _eventBus = eventBus;
        _logger = logger;
        _repository = repository;
        _launcherManager = launcherManager;
        _hiddenItemStore = hiddenItemStore;
        _usageStore = usageStore;
        _host = host;

        // 写时复制：过滤隐藏项后存为新列表
        _appPool = appPool.Where(a => !_hiddenItemStore.IsHidden(ItemKey(a))).ToList();

        // 初始化图标服务（缓存目录位于数据目录下）
        _iconService = new IconService(repository.DataDirectory, logger);

        // 加载默认图标
        LoadDefaultIcon();

        // 窗口图标默认深色版（App 启动后会按主题再校正一次）
        ApplyIconTheme("dark");

        Closed += (_, _) =>
        {
            _searchCts.Cancel();
            _searchCts.Dispose();
            _iconService.Dispose();
        };
    }

    #region 窗口行为

    /// <summary>按主题更新窗口图标（theme=system 时按实际生效主题选择）</summary>
    public void ApplyIconTheme(string theme)
    {
        try
        {
            // 从嵌入资源加载（相对文件路径会解析到工作目录导致找不到，必须走资源流）
            var resolved = ThemeResolver.Resolve(theme);
            var name = resolved == "light" ? "icon-light.png" : "icon-dark.png";
            using var stream = System.Windows.Application.GetResourceStream(
                new Uri($"Assets/{name}", UriKind.Relative))?.Stream;
            if (stream == null) return;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            Icon = bitmap;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to apply window icon theme: {Theme}", theme);
        }
    }

    private void LoadDefaultIcon()
    {
        try
        {
            // 创建默认图标（灰色圆形）
            var drawingVisual = new DrawingVisual();
            using (var drawingContext = drawingVisual.RenderOpen())
            {
                drawingContext.DrawEllipse(
                    new SolidColorBrush(Color.FromRgb(80, 80, 80)),
                    null,
                    new System.Windows.Point(16, 16),
                    14, 14);
            }

            var renderTarget = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
            renderTarget.Render(drawingVisual);
            renderTarget.Freeze();
            _defaultIcon = renderTarget;
        }
        catch
        {
            _defaultIcon = null;
        }
    }

    /// <summary>
    /// 显示面板：每次唤起都定位到光标所在屏幕中心（多显示器 / DPI 换算）。
    /// </summary>
    public void ShowPalette()
    {
        _deepSeekMode = false; // 每次唤起回到常规搜索
        SearchBox.Text = string.Empty;
        ScheduleSearch();

        PositionAtCursorScreen();
        Show();
        Activate();
        SearchBox.Focus();
    }

    /// <summary>隐藏面板（联动隐藏文档预览）</summary>
    public void HidePalette()
    {
        _previewWindow?.HidePreview();
        Hide();
    }

    /// <summary>真正关闭窗口（App 退出时调用；绕过 Closing 拦截）</summary>
    public void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    /// <summary>抑制失焦自动隐藏（App 打开全局对话框前调用，与 <see cref="ResumeAutoHide"/> 配对）</summary>
    public void SuspendAutoHide() => _suppressHideCount++;

    /// <summary>恢复失焦自动隐藏</summary>
    public void ResumeAutoHide() => _suppressHideCount--;

    /// <summary>Alt+F4 / 关闭请求一律拦截为隐藏，避免窗口销毁后热键/托盘唤起崩溃</summary>
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_forceClose) return;
        e.Cancel = true;
        HidePalette();
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        // 弹对话框期间不自动隐藏（否则对话框 Owner 被藏到后面）
        if (_suppressHideCount > 0) return;
        HideIfNotAppWindow();
    }

    /// <summary>
    /// 焦点离开面板：去了预览窗口不隐藏（预览滚动/复制不应误杀面板）；
    /// 去了其他应用才收起（面板与预览一并隐藏）。
    /// </summary>
    private void HideIfNotAppWindow()
    {
        if (IsActive) return;
        if (_previewWindow?.IsActive == true) return;

        // 跨进程挂载的预览内容（WPS 等处理器窗口）抢焦点时，WPF 的 IsActive 可能滞后/失真，
        // 误判成"去了别的应用"而把面板藏掉；以前台窗口的根祖先为准——
        // 挂载内容的根就是预览窗口本体，属于本应用任一窗口都不隐藏
        var root = GetForegroundRoot();
        if (root != IntPtr.Zero)
        {
            if (root == new WindowInteropHelper(this).Handle) return;
            if (_previewWindow != null && root == new WindowInteropHelper(_previewWindow).Handle) return;
        }

        HidePalette();
    }

    /// <summary>前台窗口的根祖先（跨进程子窗口也归其顶层窗口）；无前台窗口返回 0</summary>
    private static IntPtr GetForegroundRoot()
    {
        var foreground = GetForegroundWindow();
        return foreground == IntPtr.Zero ? IntPtr.Zero : GetAncestor(foreground, GaRoot);
    }

    private const uint GaRoot = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    #region 文档预览（单击文档结果弹出）

    private PreviewWindow? _previewWindow;

    private void ResultsList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        try
        {
            // 设置开关关闭 → 恢复"单击仅选中"现状
            if (!_host.IsPreviewEnabled())
            {
                HidePreviewWindow();
                return;
            }

            // 命中测试：与双击/右键一致，取光标下的结果行
            if (ItemsControl.ContainerFromElement(ResultsList, e.OriginalSource as DependencyObject) is not ListBoxItem container)
                return;
            if (ResultsList.ItemContainerGenerator.ItemFromContainer(container) is not ResultDisplayItem item)
                return;

            // 仅"文档类文件结果"触发预览；应用/命令/文件夹/不支持格式 → 隐藏预览。
            // 可预览 = 自有解析支持，或系统注册了预览处理器（如 rtf/doc/wps/et/dps 等走 Handler）
            if (item.Kind == ResultKind.File && item.FileEntry is { IsDirectory: false })
            {
                var ext = System.IO.Path.GetExtension(item.Path);
                if (QuickPalette.Infrastructure.Preview.DocumentTextExtractor.IsSupported(ext)
                    || QuickPalette.Infrastructure.Preview.PreviewHandlerResolver.Shared.Find(ext) is not null)
                {
                    _previewWindow ??= new PreviewWindow();
                    _previewWindow.SetDeactivatedHandler(HideIfNotAppWindow);
                    _previewWindow.ShowPreview(item.Path, item.Name, item.TypeLabel, this);
                    return;
                }
            }

            HidePreviewWindow();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Preview on click failed");
        }
    }

    private void HidePreviewWindow() => _previewWindow?.HidePreview();

    #endregion

    /// <summary>标题栏（窗口背景空白处）按下左键拖动窗口</summary>
    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 只有落在窗口装饰面上才拖动；点在输入框/列表行上不算
        if (e.OriginalSource is not (Border or Grid or System.Windows.Shapes.Path))
            return;

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标键已释放等竞态，忽略
        }
    }

    /// <summary>把面板定位到光标所在屏幕（显示器）的工作区中心</summary>
    private void PositionAtCursorScreen()
    {
        try
        {
            if (!GetCursorPos(out var cursor))
                return;

            var screen = System.Windows.Forms.Screen.FromPoint(
                new System.Drawing.Point(cursor.X, cursor.Y));
            if (screen == null)
                return;
            var workArea = screen.WorkingArea;

            // 像素 → DIP：用本窗口所在 DPI 换算（GetDpiForWindow，失败退化为 VisualTreeHelper.GetDpi）
            double dpi;
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                dpi = hwnd != IntPtr.Zero ? GetDpiForWindow(hwnd) : VisualTreeHelper.GetDpi(this).DpiScaleX * 96;
            }
            catch (EntryPointNotFoundException)
            {
                dpi = VisualTreeHelper.GetDpi(this).DpiScaleX * 96;
            }

            var scale = dpi / 96.0;
            var workLeft = workArea.Left / scale;
            var workTop = workArea.Top / scale;
            var workWidth = workArea.Width / scale;
            var workHeight = workArea.Height / scale;

            Left = workLeft + (workWidth - Width) / 2;
            Top = workTop + (workHeight - Height) / 2;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to position palette on cursor screen");
        }
    }

    /// <summary>对话框守卫：构造期间抑制失焦隐藏，Dispose 恢复</summary>
    private sealed class DialogGuard : IDisposable
    {
        private readonly MainWindow _window;
        public DialogGuard(MainWindow window)
        {
            _window = window;
            window._suppressHideCount++;
        }

        public void Dispose()
        {
            _window._suppressHideCount--;
        }
    }

    /// <summary>弹系统消息框（自带 Owner + 抑制失焦隐藏）</summary>
    private MessageBoxResult ShowMessage(
        string message,
        string title,
        MessageBoxButton button = MessageBoxButton.OK,
        MessageBoxImage icon = MessageBoxImage.Information)
    {
        using var guard = new DialogGuard(this);
        return MessageBox.Show(this, message, title, button, icon);
    }

    #endregion

    #region 拖拽添加

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        // DragOver 只对文件放行（.exe/.lnk/.url）
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files &&
            files.Any(IsSupportedDropFile))
        {
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files)
                return;

            var file = files.FirstOrDefault(IsSupportedDropFile);
            if (file == null)
            {
                ShowMessage(UiText.T("main.drop.unsupported"), UiText.T("main.drop.title"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var prefill = BuildPrefillFromFile(file);
            if (prefill == null)
            {
                ShowMessage(UiText.T("main.drop.unsupported"), UiText.T("main.drop.title"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 预填打开添加窗口（保存入库由 App 统一处理）
            _host.ShowAddApp(prefill);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Drop handling failed");
        }
    }

    private static bool IsSupportedDropFile(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".exe" or ".lnk" or ".url";

    /// <summary>拖入文件 → 预填 AppItem（.lnk 解析目标、.url 解析链接）</summary>
    private static AppItem? BuildPrefillFromFile(string file)
    {
        switch (Path.GetExtension(file).ToLowerInvariant())
        {
            case ".exe":
                return new AppItem
                {
                    Name = Path.GetFileNameWithoutExtension(file),
                    Path = file,
                    WorkDir = Path.GetDirectoryName(file) ?? string.Empty,
                    Type = AppType.Exe,
                    Source = AppSource.Manual
                };
            case ".lnk":
            {
                var (target, args, workDir) = ResolveLnk(file);
                return new AppItem
                {
                    Name = Path.GetFileNameWithoutExtension(file),
                    Path = string.IsNullOrEmpty(target) ? file : target,
                    Args = args,
                    WorkDir = workDir,
                    Type = AppType.Lnk,
                    Source = AppSource.Manual
                };
            }
            case ".url":
            {
                var url = ResolveUrl(file);
                return new AppItem
                {
                    Name = Path.GetFileNameWithoutExtension(file),
                    Path = url,
                    Type = AppType.Url,
                    Source = AppSource.Manual
                };
            }
            default:
                return null;
        }
    }

    /// <summary>用 WScript.Shell 解析 .lnk 的目标/参数/工作目录</summary>
    private static (string Target, string Args, string WorkDir) ResolveLnk(string lnkPath)
    {
        object? shell = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null)
                return (string.Empty, string.Empty, string.Empty);

            shell = Activator.CreateInstance(type);
            if (shell == null)
                return (string.Empty, string.Empty, string.Empty);

            dynamic s = shell;
            dynamic shortcut = s.CreateShortcut(lnkPath);
            string target = shortcut.TargetPath ?? string.Empty;
            string args = shortcut.Arguments ?? string.Empty;
            string workDir = shortcut.WorkingDirectory ?? string.Empty;
            return (target, args, workDir);
        }
        catch
        {
            return (string.Empty, string.Empty, string.Empty);
        }
        finally
        {
            if (shell != null && Marshal.IsComObject(shell))
                Marshal.FinalReleaseComObject(shell);
        }
    }

    /// <summary>解析 .url（INI 文本）中的 URL= 行</summary>
    private static string ResolveUrl(string urlPath)
    {
        foreach (var line in File.ReadLines(urlPath))
        {
            if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                return line[4..].Trim();
        }

        return urlPath;
    }

    #endregion

    #region 应用池 / 刷新

    /// <summary>刷新应用列表（App 在设置/添加后调用）</summary>
    public void RefreshApps()
    {
        _ = RefreshAppsAsync();
    }

    private async Task RefreshAppsAsync()
    {
        try
        {
            var apps = await _repository.GetAllAsync();
            // 写时复制：整表替换，后台搜索枚举的是旧快照
            _appPool = apps.Where(a => !_hiddenItemStore.IsHidden(ItemKey(a))).ToList();
            if (IsVisible)
            {
                ScheduleSearch();
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Refresh apps failed");
        }
    }

    /// <summary>隐藏/使用统计键：路径（AUMID/名称兜底），小写去空白</summary>
    private static string ItemKey(AppItem item)
        => (item.Path ?? item.Aumid ?? item.Name ?? string.Empty).Trim().ToLowerInvariant();

    #endregion

    #region 搜索

    private bool IsCommandMode => SearchBox.Text.StartsWith('>');

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchWatermark.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        HidePreviewWindow(); // 查询变化 → 上一条文档的预览失效
        ScheduleSearch();
    }

    /// <summary>
    /// 触发搜索：命令模式/空查询同步渲染；非空查询防抖异步合并应用+文件。
    /// 每次触发递增版本号，旧查询的结果不再回填。
    /// </summary>
    private void ScheduleSearch()
    {
        var query = SearchBox.Text;
        var version = Interlocked.Increment(ref _searchVersion);

        var oldCts = _searchCts;
        _searchCts = new CancellationTokenSource();
        oldCts.Cancel();
        oldCts.Dispose(); // CTS 用后即释放

        if (_deepSeekMode)
        {
            ApplyDeepSeekResults(query, version);
            return;
        }

        if (string.IsNullOrEmpty(query) || IsCommandMode)
        {
            UpdateResultsSync(query, version);
            return;
        }

        _ = PerformSearchAsync(query, _searchCts.Token, version);
    }

    /// <summary>同步路径：空查询（收藏+最近）或命令模式</summary>
    private void UpdateResultsSync(string query, int version)
    {
        if (IsCommandMode)
        {
            ApplyCommandResults(query, version);
            return;
        }

        var pool = _appPool;
        var results = _searchEngine.Match(query, pool);
        var displayItems = new ObservableCollection<ResultDisplayItem>();
        var seq = 0;
        foreach (var hit in results)
        {
            displayItems.Add(CreateAppDisplayItem(hit, query, seq++));
        }

        ApplyDisplay(displayItems, null, version);
    }

    /// <summary>防抖异步搜索：应用优先 + 文件结果（stale 版本不回填）</summary>
    private async Task PerformSearchAsync(string query, CancellationToken token, int version)
    {
        try
        {
            // 防抖：连续输入时取消上一次
            await Task.Delay(120, token);
            token.ThrowIfCancellationRequested();

            var pool = _appPool; // 写时复制快照，后台枚举安全
            var appResults = await Task.Run(
                () => _searchEngine.Match(query, pool).ToList(), token);
            token.ThrowIfCancellationRequested();

            var displayItems = new List<ResultDisplayItem>();
            var seq = 0;
            foreach (var hit in appResults)
            {
                displayItems.Add(CreateAppDisplayItem(hit, query, seq++));
            }

            // 文件搜索（索引未就绪时静默跳过）
            List<FileHit>? fileHits = null;
            if (_fileSearchManager.Status != FileIndexStatus.Disabled &&
                _fileSearchManager.IndexedCount > 0)
            {
                fileHits = await Task.Run(
                    () => _fileSearchManager.Engine.Match(query, FileResultLimit), token);
                token.ThrowIfCancellationRequested();
            }

            token.ThrowIfCancellationRequested();

            // 首批只渲染 30 条文件结果，其余在滚动到底时增量加载；
            // 未消费的文件命中在"应用结果"这一刻才交给 _pendingFileHits（防 stale 回填）
            var source = new ObservableCollection<ResultDisplayItem>(displayItems);
            List<FileHit>? pending = null;
            if (fileHits is { Count: > 0 })
            {
                var take = Math.Min(fileHits.Count, FileBatchSize);
                for (var i = 0; i < take; i++)
                {
                    source.Add(CreateFileDisplayItem(fileHits[i], query, seq++));
                }

                pending = take < fileHits.Count ? fileHits.GetRange(take, fileHits.Count - take) : null;
            }

            await Dispatcher.InvokeAsync(() => ApplyDisplay(source, pending, version));
        }
        catch (OperationCanceledException)
        {
            // 输入被新查询取代
        }
        catch (ObjectDisposedException)
        {
            // CTS 已释放（窗口关闭等）
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Search failed for query: {Query}", query);
        }
    }

    /// <summary>把结果应用到 UI（带版本校验；_pendingFileHits 只在这一刻赋值）</summary>
    private void ApplyDisplay(ObservableCollection<ResultDisplayItem> items, List<FileHit>? pendingFiles, int version)
    {
        if (version != Volatile.Read(ref _searchVersion))
            return; // stale 结果：丢弃

        _pendingFileHits = pendingFiles;
        _currentResults = items;
        ResultsList.ItemsSource = CreateGroupedView(items);
        _loadingMore = false;

        UpdatePanelHeight(items);
        UpdateEmptyState();
        UpdateHints();

        if (items.Count > 0)
        {
            ResultsList.SelectedIndex = 0;
        }
    }

    /// <summary>分组视图：应用 / 文件 / 命令 分组标题 + 稳定排序</summary>
    private static ICollectionView CreateGroupedView(ObservableCollection<ResultDisplayItem> items)
    {
        var view = new ListCollectionView(items);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ResultDisplayItem.GroupName)));
        view.SortDescriptions.Add(new SortDescription(nameof(ResultDisplayItem.GroupRank), ListSortDirection.Ascending));
        view.SortDescriptions.Add(new SortDescription(nameof(ResultDisplayItem.Sequence), ListSortDirection.Ascending));
        return view;
    }

    /// <summary>空态：无结果时给引导提示</summary>
    private void UpdateEmptyState()
    {
        var empty = _currentResults.Count == 0;
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Text = _deepSeekMode
            ? UiText.T("main.ds.empty")
            : string.IsNullOrWhiteSpace(SearchBox.Text) && !IsCommandMode
                ? UiText.T("main.empty.guide")
                : UiText.T("main.empty.noMatch");
    }

    /// <summary>面板高度按结果数自适应（MinHeight/MaxHeight 约束内）</summary>
    private void UpdatePanelHeight(ObservableCollection<ResultDisplayItem> items)
    {
        var groupCount = items.Select(i => i.GroupName).Distinct().Count();
        var estimated = ChromeHeight
                        + items.Count * RowHeightEstimate
                        + groupCount * GroupHeaderHeightEstimate;
        Height = Math.Clamp(estimated, MinHeight, MaxPanelHeight);
    }

    /// <summary>底部提示栏：按当前模式/选中项显示可用快捷键</summary>
    private void UpdateHints()
    {
        if (IsCommandMode)
        {
            HintsText.Text = UiText.T("main.hint.command");
            return;
        }

        HintsText.Text = SelectedResult()?.Kind == ResultKind.File
            ? UiText.T("main.hint.file")
            : UiText.T("main.hint.normal");
    }

    private void SetHint(string text)
    {
        HintsText.Text = text;
    }

    /// <summary>滚动到底（或内容未占满视口）时增量加载下一批文件结果</summary>
    private void ResultsList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_pendingFileHits == null || _pendingFileHits.Count == 0) return;
        if (_loadingMore) return;
        if (e.OriginalSource is not ScrollViewer sv) return;

        var atBottom = sv.ExtentHeight <= sv.ViewportHeight + 40 ||
                       sv.VerticalOffset + sv.ViewportHeight >= sv.ExtentHeight - 40;
        if (!atBottom) return;

        _loadingMore = true;
        try
        {
            var take = Math.Min(_pendingFileHits.Count, FileBatchSize);
            var seq = _currentResults.Count;
            for (var i = 0; i < take; i++)
            {
                _currentResults.Add(CreateFileDisplayItem(_pendingFileHits[i], SearchBox.Text, seq++));
            }

            _pendingFileHits = take < _pendingFileHits.Count
                ? _pendingFileHits.GetRange(take, _pendingFileHits.Count - take)
                : null;
            UpdatePanelHeight(_currentResults);
        }
        finally
        {
            _loadingMore = false;
        }
    }

    #endregion

    #region 命令模式（"&gt;"）

    /// <summary>命令数据源（独立 CommandItem 列表，复用结果列表渲染）</summary>
    private List<CommandItem> GetCommands()
    {
        return new List<CommandItem>
        {
            new()
            {
                Name = "settings", Usage = ">settings",
                Description = UiText.T("main.cmd.settings"),
                Execute = _ => _host.OpenSettings()
            },
            new()
            {
                Name = "capture", Usage = ">capture",
                Description = UiText.T("main.cmd.capture"),
                Execute = _ => _host.CaptureCurrentWindow()
            },
            new()
            {
                Name = "refresh", Usage = ">refresh",
                Description = UiText.T("main.cmd.refresh"),
                Execute = _ => RunRefreshCommand(),
                CloseAfterExecute = false
            },
            new()
            {
                Name = "theme", Usage = ">theme dark|light|system",
                Description = UiText.T("main.cmd.theme"),
                Execute = RunThemeCommand,
                CloseAfterExecute = false
            },
            new()
            {
                Name = "backup", Usage = ">backup",
                Description = UiText.T("main.cmd.backup"),
                Execute = _ => _host.BackupNow()
            },
            new()
            {
                Name = "diag", Usage = ">diag",
                Description = UiText.T("main.cmd.diag"),
                Execute = _ => ShowDiagnostics()
            },
            new()
            {
                Name = "help", Usage = ">help",
                Description = UiText.T("main.cmd.help"),
                Execute = _ => ShowCommandHelp()
            }
        };
    }

    /// <summary>命令模式渲染：按输入过滤命令列表</summary>
    private void ApplyCommandResults(string query, int version)
    {
        var trimmed = query.Length > 1 ? query[1..].Trim() : string.Empty;
        var nameFilter = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? string.Empty;

        var matches = GetCommands()
            .Where(c => nameFilter.Length == 0 || c.Name.StartsWith(nameFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var items = new ObservableCollection<ResultDisplayItem>();
        var seq = 0;
        foreach (var command in matches)
        {
            items.Add(CreateCommandDisplayItem(command, nameFilter, seq++));
        }

        ApplyDisplay(items, null, version);

        if (matches.Count == 0)
        {
            SetHint(UiText.T("main.command.unknown"));
        }
    }

    // ───────────────────────── DeepSeek 网络搜索模式（DS + Tab） ─────────────────────────

    /// <summary>进入 DS 模式：清空输入等待查询串，结果区显示"搜索行动行"</summary>
    private void EnterDeepSeekMode()
    {
        _deepSeekMode = true;
        SearchBox.Text = string.Empty;
        SearchBox.Focus();
        SetHint(UiText.T("main.ds.hint"));
        ScheduleSearch();
    }

    /// <summary>退出 DS 模式：恢复常规搜索</summary>
    private void ExitDeepSeekMode()
    {
        if (!_deepSeekMode) return;
        _deepSeekMode = false;
        SearchBox.Text = string.Empty;
        ScheduleSearch();
    }

    /// <summary>DS 模式渲染：结果区只放一条"用 DeepSeek 搜索「查询」"行动行（复用命令行管线）</summary>
    private void ApplyDeepSeekResults(string query, int version)
    {
        var items = new ObservableCollection<ResultDisplayItem>();
        var keyword = query.Trim();
        if (keyword.Length > 0)
        {
            var name = string.Format(UiText.T("main.ds.row"), keyword);
            items.Add(new ResultDisplayItem
            {
                Kind = ResultKind.Command,
                Command = new CommandItem
                {
                    Name = "deepseek",
                    Usage = name,
                    Description = UiText.T("main.ds.rowSecondary"),
                    Execute = _ => LaunchDeepSeekSearch(keyword),
                    CloseAfterExecute = true
                },
                Id = "deepseek",
                Name = name,
                Path = "chat.deepseek.com",
                SecondaryText = UiText.T("main.ds.rowSecondary"),
                Icon = _defaultIcon ?? new BitmapImage(),
                NameSegments = BuildSegments(name, keyword),
                LaunchHint = UiText.T("main.ds.launchHint"),
                TypeLabel = "DeepSeek",
                GroupName = UiText.T("main.ds.group"),
                GroupRank = 2,
                Sequence = 0
            });
        }

        ApplyDisplay(items, null, version);
        SetHint(UiText.T("main.ds.hint"));
    }

    /// <summary>打开 DeepSeek 网络搜索页（URL 形如浏览器搜索引擎配置，自动发送搜索）</summary>
    private void LaunchDeepSeekSearch(string keyword)
    {
        var url = "https://chat.deepseek.com/?q=" + Uri.EscapeDataString(keyword) + "&autosend=1&search=1";
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            HidePalette();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "DeepSeek search launch failed");
            SetHint(string.Format(UiText.T("main.launch.failed"), url));
        }
    }

    /// <summary>回车执行命令（以输入框内容为准；"&gt;" 明确输入时执行选中行）</summary>
    private void ExecuteCommandFromInput()
    {
        var trimmed = SearchBox.Text.Length > 1 ? SearchBox.Text[1..].Trim() : string.Empty;
        var parts = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var name = parts.Length > 0 ? parts[0] : string.Empty;
        var args = parts.Length > 1 ? parts[1].Trim() : string.Empty;

        CommandItem? command = null;
        if (name.Length == 0)
        {
            command = SelectedResult()?.Command;
        }
        else
        {
            var commands = GetCommands();
            command = commands.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                      ?? commands.FirstOrDefault(c => c.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase));
        }

        if (command == null)
        {
            SetHint(UiText.T("main.command.unknown"));
            return;
        }

        ExecuteCommand(command, args);
    }

    private void ExecuteCommand(CommandItem command, string args)
    {
        try
        {
            command.Execute(args);
            if (command.CloseAfterExecute)
            {
                HidePalette();
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Command failed: {Command}", command.Name);
            SetHint(string.Format(UiText.T("main.launch.failed"), command.Name));
        }
    }

    /// <summary>&gt;refresh：重新加载白名单 + 请求重建文件索引</summary>
    private void RunRefreshCommand()
    {
        RefreshApps();
        _fileSearchManager.RequestRebuild();
        SetHint(UiText.T("main.command.refreshDone"));
    }

    /// <summary>&gt;theme dark|light|system：切换并持久化主题</summary>
    private void RunThemeCommand(string args)
    {
        var theme = args.Trim().ToLowerInvariant();
        if (theme is not ("dark" or "light" or "system"))
        {
            SetHint(UiText.T("main.command.themeInvalid"));
            return;
        }

        _host.SwitchTheme(theme);
        SetHint(string.Format(UiText.T("main.command.themeApplied"), theme));
    }

    /// <summary>&gt;help：弹命令清单</summary>
    private void ShowCommandHelp()
    {
        var text = string.Join(Environment.NewLine,
            GetCommands().Select(c => $"{c.Usage,-28}{c.Description}"));
        ShowMessage(text, UiText.T("main.command.helpTitle"));
    }

    /// <summary>&gt;diag：版本 / 数据目录 / 索引状态 / 内存</summary>
    private void ShowDiagnostics()
    {
        using var process = Process.GetCurrentProcess();
        var sb = new StringBuilder();
        sb.AppendLine($"Version / 版本: {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}");
        sb.AppendLine($"Data / 数据目录: {_repository.DataDirectory}");
        sb.AppendLine($"Index / 文件索引: {_fileSearchManager.Status}, {_fileSearchManager.IndexedCount:N0} entries");
        sb.AppendLine($"Index loaded / 索引加载: {_fileSearchManager.IndexLoadedAt?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "-"}");
        sb.AppendLine($"Memory / 内存: working set {process.WorkingSet64 / 1024.0 / 1024.0:F1} MB, GC {GC.GetTotalMemory(false) / 1024.0 / 1024.0:F1} MB");

        ShowMessage(sb.ToString(), UiText.T("main.title.diag"));
    }

    #endregion

    #region 键盘交互

    /// <summary>
    /// Tab 在 WPF 的隧道阶段就会被焦点导航消费，KeyDown 收不到——
    /// DS 触发与 DS 模式下的 Tab 必须在 PreviewKeyDown 拦截。
    /// </summary>
    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Tab) return;

        if (_deepSeekMode)
        {
            e.Handled = true; // 模式内 Tab 不做焦点导航
            return;
        }

        // "DS" + Tab 进入 DeepSeek 网络搜索模式（可在设置中关闭）
        if (_host.IsDeepSeekSearchEnabled()
            && !IsCommandMode
            && SearchBox.Text.Trim().Equals("ds", StringComparison.OrdinalIgnoreCase))
        {
            EnterDeepSeekMode();
            e.Handled = true;
        }
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+1..9 快捷启动第 N 条 / Ctrl+C 复制路径（输入框与列表通用）
        if (HandleControlShortcut(e))
            return;

        switch (e.Key)
        {
            case Key.Escape:
                if (_deepSeekMode)
                {
                    // DS 模式下 Esc 先退出模式（保留面板），再按才隐藏
                    ExitDeepSeekMode();
                }
                else
                {
                    HidePalette();
                }

                e.Handled = true;
                break;

            case Key.Back:
                // DS 模式下查询为空时退格 = 退出模式
                if (_deepSeekMode && SearchBox.Text.Length == 0)
                {
                    ExitDeepSeekMode();
                    e.Handled = true;
                }
                break;

            case Key.Enter:
                if (_deepSeekMode)
                {
                    var keyword = SearchBox.Text.Trim();
                    if (keyword.Length > 0)
                    {
                        LaunchDeepSeekSearch(keyword);
                    }
                }
                else if (IsCommandMode)
                {
                    ExecuteCommandFromInput();
                }
                else
                {
                    LaunchSelected();
                }

                e.Handled = true;
                break;

            case Key.Down:
                // ↓ 回列表（选中第一条）
                SelectFirstAndFocusList();
                e.Handled = true;
                break;

            case Key.Up:
                // ↑ 跳到列表末尾
                SelectLastAndFocusList();
                e.Handled = true;
                break;

            case Key.PageDown:
                MoveSelection(PageStep);
                e.Handled = true;
                break;

            case Key.PageUp:
                MoveSelection(-PageStep);
                e.Handled = true;
                break;
        }
    }

    private void ResultsList_KeyDown(object sender, KeyEventArgs e)
    {
        if (HandleControlShortcut(e))
            return;

        switch (e.Key)
        {
            case Key.Escape:
                if (_deepSeekMode)
                {
                    ExitDeepSeekMode();
                    SearchBox.Focus();
                }
                else
                {
                    HidePalette();
                }

                e.Handled = true;
                break;

            case Key.Enter:
                if (_deepSeekMode)
                {
                    var keyword = SearchBox.Text.Trim();
                    if (keyword.Length > 0)
                    {
                        LaunchDeepSeekSearch(keyword);
                    }
                    else
                    {
                        LaunchSelected();
                    }
                }
                else if (IsCommandMode)
                {
                    var command = SelectedResult()?.Command;
                    if (command != null)
                    {
                        ExecuteCommand(command, CurrentCommandArgs());
                    }
                    else
                    {
                        ExecuteCommandFromInput();
                    }
                }
                else
                {
                    LaunchSelected();
                }

                e.Handled = true;
                break;

            case Key.Up:
                if (ResultsList.SelectedIndex <= 0)
                {
                    // 列表顶部 ↑ 回输入框
                    SearchBox.Focus();
                    e.Handled = true;
                }
                break;

            case Key.Down:
                if (ResultsList.SelectedIndex >= ResultsList.Items.Count - 1)
                {
                    // 列表底部 ↓ 回输入框
                    SearchBox.Focus();
                    e.Handled = true;
                }
                break;

            case Key.PageUp:
                MoveSelection(-PageStep);
                e.Handled = true;
                break;

            case Key.PageDown:
                MoveSelection(PageStep);
                e.Handled = true;
                break;
        }
    }

    /// <summary>Ctrl+1..9 直接启动第 N 条；Ctrl+C 复制选中项路径</summary>
    private bool HandleControlShortcut(KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control)
            return false;

        var digit = e.Key switch
        {
            >= Key.D1 and <= Key.D9 => e.Key - Key.D1,
            >= Key.NumPad1 and <= Key.NumPad9 => e.Key - Key.NumPad1,
            _ => -1
        };

        if (digit >= 0)
        {
            LaunchByIndex((int)digit);
            e.Handled = true;
            return true;
        }

        if (e.Key == Key.C)
        {
            CopySelectedPath();
            e.Handled = true;
            return true;
        }

        return false;
    }

    /// <summary>当前输入框中命令的参数部分（"&gt;theme dark" → "dark"）</summary>
    private string CurrentCommandArgs()
    {
        var trimmed = SearchBox.Text.Length > 1 ? SearchBox.Text[1..].Trim() : string.Empty;
        var parts = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length > 1 ? parts[1].Trim() : string.Empty;
    }

    private void SelectFirstAndFocusList()
    {
        if (ResultsList.Items.Count == 0) return;
        ResultsList.SelectedIndex = 0;
        ResultsList.ScrollIntoView(ResultsList.Items[0]);
        ResultsList.Focus();
    }

    private void SelectLastAndFocusList()
    {
        if (ResultsList.Items.Count == 0) return;
        var last = ResultsList.Items.Count - 1;
        ResultsList.SelectedIndex = last;
        ResultsList.ScrollIntoView(ResultsList.Items[last]);
        ResultsList.Focus();
    }

    private void MoveSelection(int delta)
    {
        if (ResultsList.Items.Count == 0) return;
        var index = ResultsList.SelectedIndex;
        if (index < 0)
        {
            index = delta > 0 ? 0 : ResultsList.Items.Count - 1;
        }
        else
        {
            index = Math.Clamp(index + delta, 0, ResultsList.Items.Count - 1);
        }

        ResultsList.SelectedIndex = index;
        ResultsList.ScrollIntoView(ResultsList.Items[index]);
        ResultsList.Focus();
    }

    private void LaunchByIndex(int index)
    {
        if (index < 0 || index >= ResultsList.Items.Count) return;
        ResultsList.SelectedIndex = index;
        LaunchSelected();
    }

    private ResultDisplayItem? SelectedResult() => ResultsList.SelectedItem as ResultDisplayItem;

    private void ResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 提示栏随选中项动态刷新（应用/文件/命令的可用快捷键不同）
        UpdateHints();
    }

    /// <summary>双击启动（先做命中测试，点在空白处不触发）</summary>
    private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var container = ItemsControl.ContainerFromElement(ResultsList, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (container == null)
            return;

        if (ResultsList.ItemContainerGenerator.ItemFromContainer(container) is not ResultDisplayItem item)
            return;

        LaunchItem(item);
    }

    #endregion

    #region 启动

    private void LaunchSelected(bool runAsAdmin = false, bool openDirectory = false)
    {
        var item = SelectedResult();
        if (item == null) return;
        LaunchItem(item, runAsAdmin, openDirectory);
    }

    private async void LaunchItem(ResultDisplayItem item, bool runAsAdmin = false, bool openDirectory = false)
    {
        try
        {
            if (item.Kind == ResultKind.Command)
            {
                if (item.Command != null)
                {
                    ExecuteCommand(item.Command, CurrentCommandArgs());
                }

                return;
            }

            if (item.Kind == ResultKind.File)
            {
                LaunchFile(item, openDirectory);
                return;
            }

            var app = item.AppItem;
            if (app == null) return;

            _logger.Information("Launching: {Name} ({Path})", app.Name, app.Path);

            var options = new LaunchOptions
            {
                RunAsAdmin = runAsAdmin || Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift) || app.RunAsAdmin,
                OpenDirectory = openDirectory || Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl)
            };

            var result = await _launcherManager.LaunchAsync(app, options);

            if (result.Success)
            {
                await RecordUsageAsync(app);
                _eventBus.Publish(new AppLaunchedEvent(app.Id, true, null));

                if (!options.OpenDirectory)
                {
                    HidePalette();
                }
            }
            else
            {
                HandleLaunchFailure(app, result);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Launch failed unexpectedly: {Name}", item.Name);
        }
    }

    /// <summary>记录使用统计（LaunchCount/LastLaunched 写库 + usage.json）</summary>
    private async Task RecordUsageAsync(AppItem app)
    {
        app.LaunchCount++;
        app.LastLaunched = DateTime.UtcNow;
        await _repository.UpdateAsync(app);

        try
        {
            _usageStore.RecordLaunch(app.Id);
            _usageStore.Save();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Record usage failed: {Name}", app.Name);
        }
    }

    /// <summary>启动失败：NotFound 走"重新定位"引导，其余给清晰错误</summary>
    private void HandleLaunchFailure(AppItem app, LaunchResult result)
    {
        _logger.Warning("Launch failed: {Name}: {Error} ({Kind})", app.Name, result.ErrorMessage, result.ErrorKind);

        if (result.ErrorKind == LaunchErrorKind.NotFound)
        {
            ShowRelocateDialog(app);
            return;
        }

        ShowMessage(
            string.Format(UiText.T("main.launch.failed"), result.ErrorMessage ?? string.Empty),
            UiText.T("main.title.error"),
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    /// <summary>路径失效"重新定位"引导：改路径 / 打开目录 / 移除（三选一），改完写库并刷新</summary>
    private void ShowRelocateDialog(AppItem app)
    {
        RelocateDialog.RelocateChoice choice;
        using (var guard = new DialogGuard(this))
        {
            var dialog = new RelocateDialog(app, this);
            dialog.ShowDialog();
            choice = dialog.Choice;
        }

        switch (choice)
        {
            case RelocateDialog.RelocateChoice.ChangePath:
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = UiText.T("main.relocate.change"),
                    Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*"
                };
                if (dialog.ShowDialog(this) != true) return;

                app.Path = dialog.FileName;
                if (string.IsNullOrWhiteSpace(app.WorkDir))
                {
                    app.WorkDir = Path.GetDirectoryName(dialog.FileName) ?? string.Empty;
                }

                _ = SaveAppAsync(app);
                break;
            }
            case RelocateDialog.RelocateChoice.OpenDirectory:
            {
                var dir = Path.GetDirectoryName(app.Path);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                {
                    ShowMessage(UiText.T("main.relocate.dirMissing"), UiText.T("main.title.info"),
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                Process.Start(new ProcessStartInfo("explorer.exe")
                {
                    UseShellExecute = true,
                    Arguments = $"\"{dir}\""
                });
                break;
            }
            case RelocateDialog.RelocateChoice.Remove:
                _ = RemoveAppAsync(app);
                break;
        }
    }

    /// <summary>保存修改后的应用（写库 + 图标缓存失效 + 刷新）</summary>
    private async Task SaveAppAsync(AppItem app)
    {
        try
        {
            app.UpdatedAt = DateTime.UtcNow;
            await _repository.UpdateAsync(app);
            _appIconCache.Remove(app.Id); // 路径变了，图标缓存失效
            await RefreshAppsAsync();
            SetHint(UiText.T("main.relocate.updated"));
            _logger.Information("App path updated: {Name} -> {Path}", app.Name, app.Path);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Save app failed: {Name}", app.Name);
        }
    }

    /// <summary>从列表移除应用（写库 + 写时复制移出内存池）</summary>
    private async Task RemoveAppAsync(AppItem app)
    {
        try
        {
            await _repository.DeleteAsync(app.Id);
            _appPool = _appPool.Where(a => a.Id != app.Id).ToList();
            _appIconCache.Remove(app.Id);
            ScheduleSearch();
            SetHint(UiText.T("main.relocate.removed"));
            _logger.Information("App removed: {Name}", app.Name);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Remove app failed: {Name}", app.Name);
        }
    }

    private void LaunchFile(ResultDisplayItem item, bool openDirectory)
    {
        var path = item.FileEntry?.Path;
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            if (openDirectory || Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
            {
                // 打开所在目录并选中：系统 API 优先（explorer /select 在部分系统会落到默认文件夹）
                if (ExplorerHelper.RevealInExplorer(path, msg => _logger.Warning("Reveal degraded: {Msg}", msg)))
                {
                    _logger.Information("Revealed in explorer: {Path}", path);
                }
                else
                {
                    ShowMessage(UiText.T("main.relocate.dirMissing"), UiText.T("main.title.info"),
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            else
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                _logger.Information("Opened: {Path}", path);

                try
                {
                    _usageStore.RecordLaunch(path);
                    _usageStore.Save();
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Record file usage failed");
                }
            }

            HidePalette();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to open: {Path}", path);
            ShowMessage(
                string.Format(UiText.T("main.open.failed"), ex.Message),
                UiText.T("main.title.error"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    #endregion

    #region 右键菜单

    private void ResultsList_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var listBox = sender as ListBox;
        if (listBox == null) return;

        // 命中测试：右键先选中光标下的行，再弹菜单（避免对着 A 行操作 B 行）
        var container = ItemsControl.ContainerFromElement(listBox, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (container != null)
        {
            container.IsSelected = true;
        }

        if (listBox.SelectedItem is not ResultDisplayItem item)
            return;

        var contextMenu = new ContextMenu();

        if (item.Kind == ResultKind.File)
        {
            var openItem = new MenuItem
            {
                Header = item.FileEntry?.IsDirectory == true ? UiText.T("main.menu.openFolder") : UiText.T("main.menu.open")
            };
            openItem.Click += (_, _) => LaunchFile(item, openDirectory: false);
            contextMenu.Items.Add(openItem);

            var revealItem = new MenuItem { Header = UiText.T("main.menu.openDir") };
            revealItem.Click += (_, _) => LaunchFile(item, openDirectory: true);
            contextMenu.Items.Add(revealItem);

            contextMenu.Items.Add(new Separator());

            var copyPathItem = new MenuItem { Header = UiText.T("main.menu.copyPath") };
            copyPathItem.Click += (_, _) => CopySelectedPath();
            contextMenu.Items.Add(copyPathItem);
        }
        else if (item.Kind == ResultKind.Command)
        {
            var runItem = new MenuItem { Header = UiText.T("main.menu.launch") };
            runItem.Click += (_, _) => LaunchItem(item);
            contextMenu.Items.Add(runItem);
        }
        else
        {
            var launchItem = new MenuItem { Header = UiText.T("main.menu.launch") };
            launchItem.Click += (_, _) => LaunchSelected();
            contextMenu.Items.Add(launchItem);

            var adminItem = new MenuItem { Header = UiText.T("main.menu.launchAdmin") };
            adminItem.Click += (_, _) => LaunchSelected(runAsAdmin: true);
            contextMenu.Items.Add(adminItem);

            var openDirItem = new MenuItem { Header = UiText.T("main.menu.openDir") };
            openDirItem.Click += (_, _) => LaunchSelected(openDirectory: true);
            contextMenu.Items.Add(openDirItem);

            contextMenu.Items.Add(new Separator());

            var favoriteItem = new MenuItem
            {
                Header = item.IsFavorite ? UiText.T("main.menu.unfavorite") : UiText.T("main.menu.favorite"),
                Tag = item
            };
            favoriteItem.Click += (_, _) => ToggleFavorite(item);
            contextMenu.Items.Add(favoriteItem);

            var editItem = new MenuItem { Header = UiText.T("main.menu.edit"), Tag = item };
            editItem.Click += (_, _) => EditApp(item);
            contextMenu.Items.Add(editItem);

            // "隐藏"与"删除"分离：隐藏=不再出现在任何列表（防复活），删除=彻底移除记录
            var hideItem = new MenuItem { Header = UiText.T("main.menu.hide"), Tag = item };
            hideItem.Click += (_, _) => HideItem(item);
            contextMenu.Items.Add(hideItem);

            var deleteItem = new MenuItem { Header = UiText.T("main.menu.delete"), Tag = item };
            deleteItem.Click += (_, _) => DeleteApp(item);
            contextMenu.Items.Add(deleteItem);
        }

        listBox.ContextMenu = contextMenu;
    }

    #endregion

    #region 应用管理操作

    private async void ToggleFavorite(ResultDisplayItem item)
    {
        try
        {
            var app = item.AppItem;
            if (app == null) return;

            app.Favorite = !app.Favorite;
            await _repository.UpdateAsync(app);

            item.IsFavorite = app.Favorite;
            ResultsList.Items.Refresh();

            _logger.Information("Toggle favorite: {Name} -> {Favorite}", app.Name, app.Favorite);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Toggle favorite failed");
        }
    }

    private async void EditApp(ResultDisplayItem item)
    {
        try
        {
            var app = item.AppItem;
            if (app == null) return;

            bool? accepted;
            AppItem? updated;
            bool alreadySaved;
            using (var guard = new DialogGuard(this))
            {
                var editWindow = new EditAppWindow(app) { Owner = this };
                accepted = editWindow.ShowDialog();
                updated = editWindow.Result; // 编辑窗口在草稿副本上工作，保存的是 Result
                alreadySaved = editWindow.SavedToRepository; // true 表示窗口已自行写库
            }

            if (accepted == true && updated != null)
            {
                updated.UpdatedAt = DateTime.UtcNow;
                if (!alreadySaved)
                {
                    await _repository.UpdateAsync(updated);
                }

                _appIconCache.Remove(updated.Id);
                await RefreshAppsAsync(); // 重新加载应用池（旧对象不复用）
                _logger.Information("Updated app: {Name}", updated.Name);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Edit app failed");
        }
    }

    private async void DeleteApp(ResultDisplayItem item)
    {
        try
        {
            var app = item.AppItem;
            if (app == null) return;

            var result = ShowMessage(
                string.Format(UiText.T("main.delete.confirm"), app.Name),
                UiText.T("main.title.delete"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                await _repository.DeleteAsync(app.Id);
                // 写时复制：不在原列表上 RemoveAll
                _appPool = _appPool.Where(a => a.Id != app.Id).ToList();
                _appIconCache.Remove(app.Id);
                ScheduleSearch();
                _logger.Information("Deleted app: {Name}", app.Name);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Delete app failed");
        }
    }

    /// <summary>隐藏条目：写 hidden.json（防复活）并从内存池移除</summary>
    private void HideItem(ResultDisplayItem item)
    {
        try
        {
            var app = item.AppItem;
            if (app == null) return;

            _hiddenItemStore.Hide(ItemKey(app));
            _appPool = _appPool.Where(a => a.Id != app.Id).ToList();
            ScheduleSearch();
            SetHint(UiText.T("main.status.hidden"));
            _logger.Information("Hidden app: {Name}", app.Name);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Hide item failed");
        }
    }

    /// <summary>Ctrl+C：复制选中项路径</summary>
    private void CopySelectedPath()
    {
        var item = SelectedResult();
        if (item == null || string.IsNullOrEmpty(item.Path)) return;

        try
        {
            Clipboard.SetText(item.Path);
            SetHint(UiText.T("main.status.copied"));
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Copy path failed");
            SetHint(UiText.T("main.copy.failed"));
        }
    }

    #endregion

    #region 结果项构建

    private ResultDisplayItem CreateAppDisplayItem(SearchHit hit, string query, int sequence)
    {
        var item = hit.Item;
        return new ResultDisplayItem
        {
            Kind = ResultKind.App,
            AppItem = item,
            Id = item.Id,
            Name = item.Name,
            Path = item.Path,
            SecondaryText = string.IsNullOrEmpty(item.Path) ? item.Aumid : item.Path,
            Icon = GetAppIcon(item),
            NameSegments = BuildSegments(item.Name, query),
            LaunchHint = GetLaunchHint(hit),
            IsFavorite = item.Favorite,
            TypeLabel = UiText.T("main.tag.app"),
            GroupName = UiText.T("main.group.app"),
            GroupRank = 0,
            Sequence = sequence
        };
    }

    private ResultDisplayItem CreateFileDisplayItem(FileHit hit, string query, int sequence)
    {
        var entry = hit.Entry;
        var kind = ClassifyFile(entry);
        return new ResultDisplayItem
        {
            Kind = ResultKind.File,
            FileEntry = entry,
            Id = entry.Path,
            Name = entry.Name,
            Path = entry.Path,
            SecondaryText = entry.Path,
            Icon = GetFileIcon(kind),
            NameSegments = BuildSegments(entry.Name, query),
            LaunchHint = entry.IsDirectory ? UiText.T("main.menu.openFolder") : UiText.T("main.menu.open"),
            TypeLabel = GetFileTypeLabel(kind),
            ModifiedText = entry.Modified.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture),
            GroupName = UiText.T("main.group.file"),
            GroupRank = 1,
            Sequence = sequence
        };
    }

    private ResultDisplayItem CreateCommandDisplayItem(CommandItem command, string query, int sequence)
    {
        return new ResultDisplayItem
        {
            Kind = ResultKind.Command,
            Command = command,
            Id = command.Name,
            Name = command.Usage,
            Path = command.Usage,
            SecondaryText = command.Description,
            Icon = _defaultIcon ?? new BitmapImage(),
            NameSegments = BuildSegments(command.Usage, query.Length == 0 ? command.Name : query),
            LaunchHint = UiText.T("main.match.command"),
            TypeLabel = UiText.T("main.group.command"),
            GroupName = UiText.T("main.group.command"),
            GroupRank = 2,
            Sequence = sequence
        };
    }

    /// <summary>按查询串把文本切成高亮分段（命中 Run 高亮）</summary>
    private static List<TextSegment> BuildSegments(string text, string query)
    {
        var segments = new List<TextSegment>();
        if (string.IsNullOrEmpty(text))
        {
            return segments;
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            segments.Add(new TextSegment(text, false));
            return segments;
        }

        var index = 0;
        while (index < text.Length)
        {
            var match = text.IndexOf(query, index, StringComparison.OrdinalIgnoreCase);
            if (match < 0)
            {
                segments.Add(new TextSegment(text[index..], false));
                break;
            }

            if (match > index)
            {
                segments.Add(new TextSegment(text[index..match], false));
            }

            segments.Add(new TextSegment(text[match..(match + query.Length)], true));
            index = match + query.Length;
        }

        return segments;
    }

    /// <summary>结果项图标（按 appId 缓存；OnLoad + Freeze，不占住图标文件）</summary>
    private ImageSource GetAppIcon(AppItem item)
    {
        if (_appIconCache.TryGetValue(item.Id, out var cached))
        {
            return cached;
        }

        ImageSource icon;
        try
        {
            var iconPath = _iconService.GetIconPath(item.Path, item.Id);
            if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad; // 加载后立即释放文件句柄
                bitmap.UriSource = new Uri(iconPath);
                bitmap.EndInit();
                bitmap.Freeze(); // 冻结后可安全共享缓存
                icon = bitmap;
            }
            else
            {
                icon = _defaultIcon ?? new BitmapImage();
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to load icon for: {Name}", item.Name);
            icon = _defaultIcon ?? new BitmapImage();
        }

        _appIconCache[item.Id] = icon;
        return icon;
    }

    /// <summary>文件类型图标：按类型着色的圆形 + 图标字符（同类型缓存）</summary>
    private ImageSource GetFileIcon(FileKind kind)
    {
        if (_fileIconCache.TryGetValue(kind, out var cached)) return cached;

        ImageSource icon;
        try
        {
            icon = RenderFileIcon(kind);
        }
        catch
        {
            return _defaultIcon ?? new BitmapImage();
        }

        _fileIconCache[kind] = icon;
        return icon;
    }

    private ImageSource RenderFileIcon(FileKind kind)
    {
        try
        {
            var (symbol, color) = kind switch
            {
                FileKind.Folder => ("📁", Color.FromRgb(80, 140, 255)),
                FileKind.Image => ("🖼️", Color.FromRgb(80, 190, 120)),
                FileKind.Audio => ("🎵", Color.FromRgb(220, 150, 80)),
                FileKind.Video => ("🎬", Color.FromRgb(170, 100, 220)),
                FileKind.Archive => ("📦", Color.FromRgb(210, 165, 70)),
                FileKind.Document => ("📄", Color.FromRgb(90, 170, 220)),
                FileKind.Executable => ("⚙️", Color.FromRgb(100, 190, 150)),
                _ => ("📄", Color.FromRgb(130, 130, 140))
            };

            var visual = new DrawingVisual();
            var dpi = VisualTreeHelper.GetDpi(this);
            using (var dc = visual.RenderOpen())
            {
                dc.DrawEllipse(new SolidColorBrush(color), null, new System.Windows.Point(16, 16), 14, 14);

                var text = new FormattedText(
                    symbol, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI Emoji"), 15, Brushes.White, dpi.PixelsPerDip)
                {
                    TextAlignment = TextAlignment.Center
                };

                // emoji 字形在 em box 内有左侧留白（见 EmojiSideBearingRatio 常量说明），
                // 直接 width/2 居中会让符号视觉偏左，加 EmojiCenteringCompensation 补偿后居中
                dc.DrawText(text, new System.Windows.Point(
                    16 - text.Width * EmojiSideBearingRatio + EmojiCenteringCompensation,
                    16 - text.Height / 2));
            }

            var renderTarget = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
            renderTarget.Render(visual);
            renderTarget.Freeze();
            return renderTarget;
        }
        catch
        {
            return _defaultIcon ?? new BitmapImage();
        }
    }

    /// <summary>文件类型分类</summary>
    private static FileKind ClassifyFile(FileEntry entry)
    {
        if (entry.IsDirectory) return FileKind.Folder;

        var ext = Path.GetExtension(entry.Name).ToLowerInvariant();
        return ext switch
        {
            ".doc" or ".docx" or ".pdf" or ".txt" or ".md" or ".xls" or ".xlsx"
                or ".ppt" or ".pptx" or ".rtf" or ".csv" or ".one" => FileKind.Document,
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp"
                or ".svg" or ".ico" or ".heic" or ".tiff" => FileKind.Image,
            ".mp3" or ".wav" or ".flac" or ".aac" or ".m4a" or ".ogg" or ".wma" => FileKind.Audio,
            ".mp4" or ".avi" or ".mkv" or ".mov" or ".webm" or ".wmv" or ".flv" => FileKind.Video,
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2" or ".xz" or ".iso" => FileKind.Archive,
            ".exe" or ".msi" or ".lnk" or ".bat" or ".cmd" or ".com"
                or ".appx" or ".appxbundle" or ".ps1" => FileKind.Executable,
            _ => FileKind.Other
        };
    }

    private static string GetFileTypeLabel(FileKind kind) => kind switch
    {
        FileKind.Folder => UiText.T("main.tag.folder"),
        FileKind.Image => UiText.T("main.tag.image"),
        FileKind.Audio => UiText.T("main.tag.audio"),
        FileKind.Video => UiText.T("main.tag.video"),
        FileKind.Archive => UiText.T("main.tag.archive"),
        FileKind.Document => UiText.T("main.tag.document"),
        FileKind.Executable => UiText.T("main.tag.program"),
        _ => UiText.T("main.tag.file")
    };

    private static string GetLaunchHint(SearchHit hit) => hit.Dimension switch
    {
        MatchDimension.NameExact => UiText.T("main.match.exact"),
        MatchDimension.NamePrefix => UiText.T("main.match.prefix"),
        MatchDimension.PinyinFull => UiText.T("main.match.pinyin"),
        MatchDimension.PinyinInitials => UiText.T("main.match.initials"),
        MatchDimension.AliasOrTag => UiText.T("main.match.alias"),
        _ => string.Empty
    };

    #endregion

    #region Win32（多显示器定位 / DPI）

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint lpPoint);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    #endregion
}

/// <summary>结果类型</summary>
public enum ResultKind
{
    /// <summary>应用</summary>
    App,
    /// <summary>本地文件/文件夹</summary>
    File,
    /// <summary>命令模式命令</summary>
    Command
}

/// <summary>名称高亮分段（命中查询串的部分 IsMatch=true）</summary>
public sealed record TextSegment(string Text, bool IsMatch);

/// <summary>结果列表显示项</summary>
public class ResultDisplayItem
{
    public ResultKind Kind { get; set; } = ResultKind.App;
    public AppItem? AppItem { get; set; }
    public FileEntry? FileEntry { get; set; }
    public CommandItem? Command { get; set; }
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;

    /// <summary>第二行文本：应用/文件为路径，命令为说明</summary>
    public string SecondaryText { get; set; } = string.Empty;

    /// <summary>图标（已缓存/冻结）</summary>
    public ImageSource Icon { get; set; } = null!;

    /// <summary>名称高亮分段</summary>
    public List<TextSegment> NameSegments { get; set; } = new();

    /// <summary>右侧提示（匹配维度 / 操作提示）</summary>
    public string LaunchHint { get; set; } = string.Empty;

    public bool IsFavorite { get; set; }

    /// <summary>类型标签：应用 / 文档 / 图片 ...</summary>
    public string TypeLabel { get; set; } = string.Empty;

    /// <summary>修改时间（文件）</summary>
    public string ModifiedText { get; set; } = string.Empty;

    /// <summary>分组标题文本（应用 / 文件 / 命令）</summary>
    public string GroupName { get; set; } = string.Empty;

    /// <summary>分组排序权重（应用=0 文件=1 命令=2），保证分组顺序稳定</summary>
    public int GroupRank { get; set; }

    /// <summary>组内顺序号（稳定排序用）</summary>
    public int Sequence { get; set; }
}

/// <summary>文件类型分类</summary>
public enum FileKind
{
    Folder,
    Document,
    Image,
    Audio,
    Video,
    Archive,
    Executable,
    Other
}
