namespace QuickPalette.App.Services;

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using QuickPalette.Domain.Links;
using Serilog;

/// <summary>
/// 剪贴板链接监听：AddClipboardFormatListener 事件驱动——
/// 复制即感知（~百毫秒），不复制时零消息零开销（结构性保证不误弹）。
/// 检出链接触发 <see cref="LinkFound"/>；含序列号去重与"本程序自己写剪贴板"的抑制标记。
/// </summary>
public sealed class ClipboardLinkMonitor : IDisposable
{
    private const int WmClipboardUpdate = 0x031D;
    private const int RetryCount = 5;

    private readonly ILogger _logger;
    private readonly Dispatcher _dispatcher;
    private HwndSource? _source;
    private uint _lastSequence;
    private bool _suppressNext;

    /// <summary>检出可识别链接（UI 线程回调）</summary>
    public event Action<LinkSuggestion>? LinkFound;

    public ClipboardLinkMonitor(ILogger logger)
    {
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    /// <summary>开始监听（创建隐藏消息窗并挂系统剪贴板监听）</summary>
    public void Start()
    {
        if (_source != null)
        {
            return;
        }

        var parameters = new HwndSourceParameters("QuickPaletteClipboardMonitor")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
        AddClipboardFormatListener(_source.Handle);
        _logger.Information("Clipboard link monitor started");
    }

    /// <summary>本程序即将写剪贴板时调用：紧随其后的一次变化不识别（自排除）</summary>
    public void SuppressNext() => _suppressNext = true;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmClipboardUpdate)
        {
            // 防抖：剪贴板写入可能分多步，静默 120ms 再读
            _ = _dispatcher.InvokeAsync(ScanClipboardAsync, DispatcherPriority.Background, default);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private async void ScanClipboardAsync()
    {
        try
        {
            await Task.Delay(120).ConfigureAwait(true);

            var sequence = GetClipboardSequenceNumber();
            if (sequence == 0 || sequence == _lastSequence)
            {
                return;
            }

            _lastSequence = sequence;

            if (_suppressNext)
            {
                _suppressNext = false;
                _logger.Debug("Clipboard change suppressed (self write)");
                return;
            }

            var text = ReadClipboardText();
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            var suggestion = LinkClassifier.Classify(text);
            if (suggestion == null)
            {
                return;
            }

            _logger.Information("Clipboard link detected: {Category} -> {App} ({Uri})",
                suggestion.Category, suggestion.TargetApp, suggestion.LaunchUri);
            LinkFound?.Invoke(suggestion);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Clipboard link scan failed");
        }
    }

    /// <summary>读取剪贴板文本（占用时短暂重试）</summary>
    private static string? ReadClipboardText()
    {
        for (var i = 0; i < RetryCount; i++)
        {
            try
            {
                return Clipboard.ContainsText() ? Clipboard.GetText() : null;
            }
            catch
            {
                Thread.Sleep(30);
            }
        }

        return null;
    }

    public void Dispose()
    {
        try
        {
            if (_source != null)
            {
                RemoveClipboardFormatListener(_source.Handle);
                _source.RemoveHook(WndProc);
                _source.Dispose();
                _source = null;
            }
        }
        catch
        {
            // 释放失败不影响退出
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
