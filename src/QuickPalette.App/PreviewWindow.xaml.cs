using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using QuickPalette.App.Services;
using QuickPalette.Infrastructure.Preview;

namespace QuickPalette.App;

/// <summary>
/// 文档/图片预览窗口：单击文档结果时在主面板旁弹出（设计见 docs\文档预览功能设计.md、
/// docs\系统预览处理器托管设计.md）。
///
/// 四种内容模式（文档类优先走系统，回落自有链路，保证任何机器都有得看）：
///  - Handler 模式：系统预览处理器（IPreviewHandler，真实排版）由独立宿主进程
///    QuickPalette.PreviewHost.exe 承载并挂载进内容区（处理器崩溃只杀宿主进程）；
///  - 图像模式：PDF（Windows.Data.Pdf 系统引擎逐页图文渲染，经 PdfPageRenderer 桥接）
///             与图片文件（png/jpg/bmp/gif/tiff/ico 直接位图化）；
///  - 文本模式：txt/md/docx/pptx/xlsx（DocumentTextExtractor 抽取）；
///  - 降级：处理器不可用（未注册/位数不匹配/超时/崩溃）或渲染桥失败时
///          回落文本模式并如实标注。
///
/// 生命周期与交互约束：
///  - Owner = 主面板（面板关闭即关闭）；
///  - 获得焦点时面板不得自动隐藏（预览滚动/复制不应误杀面板）；
///  - 自身失焦到其他应用时，与面板一并隐藏（由宿主 HideIfNotAppWindow 统一判断）。
/// 所有解析/渲染在后台线程，切换/隐藏即取消；位图解码宽度封顶控制内存。
/// </summary>
public partial class PreviewWindow : Window
{
    /// <summary>位图解码宽度上限（超大图片按此宽度重采样，控制解码内存）</summary>
    private const int DecodeWidthCap = 1000;

    /// <summary>Handler 模式就绪超时（Word/WPS 等 LocalServer 冷启动可达数秒）</summary>
    private static readonly TimeSpan HandlerReadyTimeout = TimeSpan.FromSeconds(12);

    private CancellationTokenSource? _loadCts;
    private Action? _onDeactivatedToOutside;

    /// <summary>Handler 模式的宿主进程客户端（null = 当前非 Handler 模式）</summary>
    private PreviewHandlerHostClient? _handlerClient;

    /// <summary>代次号：每次切换/隐藏自增，作废过期的宿主进程回调</summary>
    private int _generation;

    public PreviewWindow()
    {
        InitializeComponent();
        HintText.Text = UiText.T("preview.hint");
    }

    /// <summary>宿主挂接：预览失焦时回调（宿主判断焦点去向决定是否收起面板）</summary>
    public void SetDeactivatedHandler(Action handler) => _onDeactivatedToOutside = handler;

    /// <summary>显示并异步加载预览（相对宿主面板定位；重复调用切换内容并取消旧加载）</summary>
    public async void ShowPreview(string path, string displayName, string typeLabel, Window owner)
    {
        try
        {
            Owner = owner;
            TitleText.Text = displayName;
            TypeTagText.Text = typeLabel;
            HintText.Text = UiText.T("preview.hint");
            PositionBeside(owner);
            Show();

            _generation++;
            _handlerClient?.Dispose();
            _handlerClient = null;
            _loadCts?.Cancel();
            var cts = new CancellationTokenSource();
            _loadCts = cts;

            ShowLoading();

            var ext = System.IO.Path.GetExtension(path);
            if (DocumentTextExtractor.IsImage(ext))
            {
                await LoadImageModeAsync(path, cts);
                return;
            }

            // 文档类：系统预览处理器优先（真实排版）；不可用/失败/超时回落自有链路
            if (await TryLoadHandlerModeAsync(path, ext, cts))
            {
                return;
            }

            if (string.Equals(ext, ".pdf", StringComparison.OrdinalIgnoreCase))
            {
                await LoadPdfModeAsync(path, cts);
            }
            else
            {
                await LoadTextModeAsync(path, cts);
            }
        }
        catch (OperationCanceledException)
        {
            // 切换预览时旧任务作废，静默
        }
        catch (Exception ex)
        {
            if (_loadCts != null && !_loadCts.IsCancellationRequested)
            {
                ShowLoading();
                LoadingText.Text = string.Format(UiText.T("preview.error"), ex.Message);
            }
        }
    }

    /// <summary>隐藏预览、取消加载、终止预览宿主并释放位图引用</summary>
    public void HidePreview()
    {
        _generation++;
        _loadCts?.Cancel();
        _handlerClient?.Dispose();
        _handlerClient = null;
        HandlerHostBorder.Visibility = Visibility.Collapsed;
        SlotHost.DetachContent();
        ImagePanel.Children.Clear();
        Hide();
    }

    // ───────────────────────── 内容模式 ─────────────────────────

    private void ShowLoading()
    {
        _handlerClient?.Dispose();
        _handlerClient = null;
        HandlerHostBorder.Visibility = Visibility.Collapsed;
        SlotHost.DetachContent(); // 摘离上一条预览赖在槽位里的跨进程内容（如 WPS 服务持有的旧窗口）
        ImageScroll.Visibility = Visibility.Collapsed;
        ImagePanel.Children.Clear();
        ContentBox.Visibility = Visibility.Collapsed;
        ContentBox.Text = string.Empty;
        LoadingText.Text = UiText.T("preview.loading");
        LoadingText.Visibility = Visibility.Visible;
    }

    private void ShowTextMode()
    {
        HandlerHostBorder.Visibility = Visibility.Collapsed;
        ImageScroll.Visibility = Visibility.Collapsed;
        ImagePanel.Children.Clear();
        ContentBox.Visibility = Visibility.Visible;
        LoadingText.Visibility = Visibility.Collapsed;
    }

    private void ShowImageMode()
    {
        HandlerHostBorder.Visibility = Visibility.Collapsed;
        ContentBox.Visibility = Visibility.Collapsed;
        ContentBox.Text = string.Empty;
        ImageScroll.Visibility = Visibility.Visible;
        LoadingText.Visibility = Visibility.Collapsed;
    }

    /// <summary>Handler 模式：内容区整体交给预览宿主进程挂载的系统预览处理器</summary>
    private void ShowHandlerMode()
    {
        ImageScroll.Visibility = Visibility.Collapsed;
        ImagePanel.Children.Clear();
        ContentBox.Visibility = Visibility.Collapsed;
        ContentBox.Text = string.Empty;
        LoadingText.Visibility = Visibility.Collapsed;
        HandlerHostBorder.Visibility = Visibility.Visible;
    }

    // ───────────────────────── 系统预览处理器（Handler 模式） ─────────────────────────

    /// <summary>
    /// Handler 模式：经独立宿主进程调用系统预览处理器（IPreviewHandler，与资源管理器
    /// 预览窗格同一套能力）。返回 true = 已接管内容区；false = 不可用/失败/超时/已取消，
    /// 调用方回落自有链路（图像渲染 / PDF 渲染桥 / 文本抽取）。
    /// </summary>
    private async Task<bool> TryLoadHandlerModeAsync(string path, string ext, CancellationTokenSource cts)
    {
        var registration = PreviewHandlerResolver.Shared.Find(ext);
        if (registration is null)
        {
            Serilog.Log.Debug("Preview: no system handler registered for {Ext}, fallback to legacy pipeline", ext);
            return false;
        }

        // 按处理器位数选宿主：32 位进程内处理器（WPS 系插件/Foxit 等）走 x86 宿主，
        // 探测不出架构的两种宿主依次尝试
        var hostCandidates = registration.HostArch switch
        {
            PreviewHandlerHostArch.X64Only => new[] { PreviewHandlerHostClient.HostExeName },
            PreviewHandlerHostArch.X86Only => new[] { PreviewHandlerHostClient.HostExeName32 },
            _ => new[] { PreviewHandlerHostClient.HostExeName, PreviewHandlerHostClient.HostExeName32 }
        };

        // 槽位必须先完成布局才有有效 HWND 与客户区尺寸。
        // 两个 WPF 细节（都实测踩过）：
        //  1. Collapsed 元素被布局系统完全跳过，HwndHost 子窗口不构建（HWND 恒为 0）；
        //  2. Collapsed→Hidden 后立即量尺寸可能仍是 HwndHost 初建的 1×1（排列尚未落到子窗口）。
        // 因此临时置 Hidden（占位参与布局、不显形），按最小合理尺寸重试；
        // 就绪后 ShowHandlerMode 才真正显示。
        const int minSlotSize = 40;
        var (width, height) = (0, 0);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            HandlerHostBorder.Visibility = attempt == 0 ? Visibility.Hidden : Visibility.Visible;
            UpdateLayout();
            (width, height) = SlotHost.GetClientSizePx();
            if (SlotHost.SlotHandle != IntPtr.Zero && width >= minSlotSize && height >= minSlotSize)
            {
                break;
            }
            await Task.Delay(60, cts.Token); // 续体回 UI 线程，给排列落地留一个消息循环间隙
        }
        if (SlotHost.SlotHandle == IntPtr.Zero || width < minSlotSize || height < minSlotSize)
        {
            Serilog.Log.Warning("Preview: slot HWND/size unavailable ({Width}x{Height}), fallback to legacy pipeline", width, height);
            HandlerHostBorder.Visibility = Visibility.Collapsed;
            return false;
        }

        var generation = _generation;
        foreach (var hostName in hostCandidates)
        {
            if (!PreviewHandlerHostClient.IsAvailable(hostName))
            {
                Serilog.Log.Debug("Preview: host {Host} not deployed", hostName);
                continue;
            }

            var client = PreviewHandlerHostClient.TryStart(new PreviewHandlerHostRequest(
                SlotHost.SlotHandle, width, height, path, GetBackgroundRgb()), hostName);
            if (client is null) continue;

            client.Terminated += status => Dispatcher.BeginInvoke(() =>
            {
                if (generation != _generation) return;
                OnHandlerTerminated(path, status);
            });

            PreviewHandlerHostStatus result;
            try
            {
                result = await client.WaitReadyAsync(HandlerReadyTimeout, cts.Token);
            }
            catch (OperationCanceledException)
            {
                client.Dispose();
                HandlerHostBorder.Visibility = Visibility.Collapsed;
                throw;
            }

            // 切换/隐藏已介入：宿主作废，由最新一次调用接管
            if (cts.IsCancellationRequested || generation != _generation)
            {
                client.Dispose();
                HandlerHostBorder.Visibility = Visibility.Collapsed;
                return false;
            }

            if (result == PreviewHandlerHostStatus.Ready)
            {
                Serilog.Log.Information("Preview: {File} via system handler {Clsid} hosted by {Host}",
                    path, registration.Clsid, hostName);
                _handlerClient = client;
                ShowHandlerMode();
                return true;
            }

            Serilog.Log.Information(
                "Preview: host {Host} failed for {File} ({Clsid}, {Kind}/{Arch}): {Status} {Detail}; trying next host or falling back",
                hostName, path, registration.Clsid, registration.Kind, registration.InProcServerArch ?? "unknown",
                result, client.LastFailureDetail ?? "");
            client.Dispose();
        }

        HandlerHostBorder.Visibility = Visibility.Collapsed;
        return false;
    }

    /// <summary>
    /// 就绪后宿主进程退出：正常退出视为关闭请求（处理器内 Esc 等）；
    /// 异常退出 = 处理器崩溃（已被隔离在宿主进程），回落自有链路兜底。
    /// </summary>
    private void OnHandlerTerminated(string path, PreviewHandlerHostStatus status)
    {
        _handlerClient?.Dispose();
        _handlerClient = null;

        if (status == PreviewHandlerHostStatus.Exited)
        {
            HidePreview();
            return;
        }

        _ = FallBackToLegacyAsync(path);
    }

    /// <summary>处理器不可用/崩溃后的兜底：回落自有解析链（PDF 渲染桥 → 文本抽取）</summary>
    private async Task FallBackToLegacyAsync(string path)
    {
        try
        {
            _loadCts?.Cancel();
            var cts = new CancellationTokenSource();
            _loadCts = cts;

            ShowLoading();

            if (string.Equals(System.IO.Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
            {
                await LoadPdfModeAsync(path, cts);
            }
            else
            {
                await LoadTextModeAsync(path, cts);
            }
        }
        catch (OperationCanceledException)
        {
            // 切换预览时旧任务作废，静默
        }
        catch (Exception ex)
        {
            if (_loadCts != null && !_loadCts.IsCancellationRequested)
            {
                ShowLoading();
                LoadingText.Text = string.Format(UiText.T("preview.error"), ex.Message);
            }
        }
    }

    /// <summary>当前主题背景色（RRGGBB）：传给预览处理器贴合深浅色主题；取不到返回 null</summary>
    private string? GetBackgroundRgb()
    {
        return TryFindResource("BgWindow") is System.Windows.Media.SolidColorBrush brush
            ? $"{brush.Color.R:X2}{brush.Color.G:X2}{brush.Color.B:X2}"
            : null;
    }

    /// <summary>文本模式：Office/纯文本抽取（DocumentTextExtractor）</summary>
    private async Task LoadTextModeAsync(string path, CancellationTokenSource cts)
    {
        var content = await Task.Run(() => DocumentTextExtractor.Extract(path, cts.Token), cts.Token);
        if (cts.IsCancellationRequested) return;

        ShowTextMode();
        // 表格保持等宽不换行（列对齐），其余自动换行便于阅读
        ContentBox.TextWrapping = content.Format == PreviewFormat.Excel
            ? TextWrapping.NoWrap
            : TextWrapping.Wrap;

        var text = content.Text;
        if (content.Truncated)
        {
            text += Environment.NewLine + Environment.NewLine + "… " + UiText.T("preview.truncated");
        }
        ContentBox.Text = text;
    }

    /// <summary>图像模式：图片文件直接位图化</summary>
    private async Task LoadImageModeAsync(string path, CancellationTokenSource cts)
    {
        var bytes = await Task.Run(() => File.ReadAllBytes(path), cts.Token);
        if (cts.IsCancellationRequested) return;

        ShowImageMode();
        AddImagePage(bytes);
    }

    /// <summary>PDF 图文模式：系统 PDF 引擎逐页渲染；渲染桥失败降级文本模式</summary>
    private async Task LoadPdfModeAsync(string path, CancellationTokenSource cts)
    {
        var result = await Task.Run(
            () => PdfPageRenderer.RenderPages(path, PdfPageRenderer.DefaultMaxPages, PdfPageRenderer.DefaultWidthPx, cts.Token),
            cts.Token);
        if (cts.IsCancellationRequested) return;

        if (result != null && result.Pages.Count > 0)
        {
            ShowImageMode();
            foreach (var png in result.Pages)
            {
                if (cts.IsCancellationRequested) return;
                AddImagePage(png);
            }

            HintText.Text = result.PageCount > result.Pages.Count
                ? string.Format(UiText.T("preview.pages"), result.PageCount, result.Pages.Count)
                : UiText.T("preview.hint");
            return;
        }

        // 渲染桥不可用/失败：降级文本模式（DocumentTextExtractor 已如实标注"文本模式"）
        await LoadTextModeAsync(path, cts);
    }

    /// <summary>向图像区追加一页/一张（解码宽度封顶控制内存，冻结以便跨线程释放）</summary>
    private void AddImagePage(byte[] bytes)
    {
        var bitmap = new BitmapImage();
        using (var ms = new MemoryStream(bytes))
        {
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = DecodeWidthCap;
            bitmap.StreamSource = ms;
            bitmap.EndInit();
            bitmap.Freeze();
        }

        ImagePanel.Children.Add(new System.Windows.Controls.Image
        {
            Source = bitmap,
            Stretch = System.Windows.Media.Stretch.Uniform,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, 10)
        });
    }

    // ───────────────────────── 布局/生命周期 ─────────────────────────

    /// <summary>相对主面板定位：右缘外 12px、顶部对齐；放不下则左缘，再放不下钳入工作区</summary>
    private void PositionBeside(Window owner)
    {
        var workArea = SystemParameters.WorkArea;
        Width = 440;
        Height = Math.Max(360, Math.Min(owner.ActualHeight > 0 ? owner.ActualHeight : 520, workArea.Height - 40));

        var left = owner.Left + owner.Width + 12;
        if (left + Width > workArea.Right)
        {
            left = owner.Left - Width - 12;
        }
        if (left < workArea.Left)
        {
            left = Math.Max(workArea.Left, workArea.Right - Width - 8);
        }

        Left = left;
        Top = Math.Max(workArea.Top, Math.Min(owner.Top, workArea.Bottom - Height - 8));
    }

    private void Close_Click(object sender, RoutedEventArgs e) => HidePreview();

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (!IsActive)
        {
            _onDeactivatedToOutside?.Invoke();
        }
    }

    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HidePreview();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }
}
