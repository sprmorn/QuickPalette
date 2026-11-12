namespace QuickPalette.App.Services;

using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using QuickPalette.App.Screenshot;
using QuickPalette.Domain.Links;
using Serilog;

/// <summary>
/// 链接助手提示卡：类目图标 + 目标应用/链接摘要 + 「打开 / 忽略」。
/// 右上角弹出（多卡错层向下）、可拖动、12 秒自动收起、不抢焦点、对截屏隐形。
/// </summary>
public partial class LinkPromptWindow : Window
{
    /// <summary>同屏多卡错层偏移</summary>
    private static int _openCount;

    private readonly ILogger _logger;
    private readonly LinkSuggestion _suggestion;
    private readonly Action<LinkSuggestion>? _onOpen;
    private readonly DispatcherTimer _autoClose;
    private bool _codeCopied;
    private bool _passwordCopied;

    public LinkPromptWindow(ILogger logger, LinkSuggestion suggestion, Action<LinkSuggestion>? onOpen = null)
    {
        InitializeComponent();

        _logger = logger;
        _suggestion = suggestion;
        _onOpen = onOpen;

        CategoryIconHost.Content = ToolbarIcons.Create(suggestion.IconKey, 32, Brushes.White);
        OpenIconHost.Content = ToolbarIcons.Create("link-open", 18);
        IgnoreIconHost.Content = ToolbarIcons.Create("exit", 18);

        AppText.Text = suggestion.TargetApp;
        UrlText.Text = Truncate(suggestion.DisplayText, 42);

        // 口令/密码分开显示、分开复制（对应不同输入框）
        if (!string.IsNullOrEmpty(suggestion.Code))
        {
            CodePanel.Visibility = Visibility.Visible;
            CodeText.Text = $"{suggestion.CodeLabel} {suggestion.Code}";
            CopyCodeIconHost.Content = ToolbarIcons.Create("copy", 13, Brushes.Gainsboro);
            if (!string.IsNullOrEmpty(suggestion.Password))
            {
                PasswordText.Text = $"{suggestion.PasswordLabel} {suggestion.Password}";
                CopyPasswordIconHost.Content = ToolbarIcons.Create("copy", 13, Brushes.Gainsboro);
            }
            else
            {
                PasswordChip.Visibility = Visibility.Collapsed;
            }
        }

        Loaded += (_, _) => PositionTopRight();
        SourceInitialized += (_, _) =>
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    SetWindowDisplayAffinity(hwnd, 0x11); // 对截屏隐形，不污染截图
                }
            }
            catch
            {
                // 忽略
            }
        };

        _autoClose = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
        _autoClose.Tick += (_, _) => Close();
        _autoClose.Start();

        _openCount++;
        Closed += (_, _) => _openCount = Math.Max(0, _openCount - 1);
    }

    /// <summary>屏幕右上角弹出（多卡错层向下堆叠）</summary>
    private void PositionTopRight()
    {
        var w = double.IsNaN(Width) || Width <= 0 ? ActualWidth : Width;
        var h = double.IsNaN(Height) || Height <= 0 ? ActualHeight : Height;
        var area = SystemParameters.WorkArea;
        Left = Math.Max(0, area.Right - w - 16);
        Top = Math.Max(0, area.Top + 16 + _openCount * (h + 8));
    }

    private static string Truncate(string text, int max)
        => string.IsNullOrEmpty(text) || text.Length <= max ? text : text[..max] + "…";

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标键已释放等竞态，忽略
        }
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _onOpen?.Invoke(_suggestion);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Link prompt open callback failed");
        }

        // 带口令/验证码芯片的卡（如 UU远程）：拉起客户端后仍需复制粘贴 → 保留卡片
        //（其他情况即点即关）
        if (!string.IsNullOrEmpty(_suggestion.Code))
        {
            UrlText.Text = $"已打开{_suggestion.TargetApp} · 复制下方{_suggestion.CodeLabel}/{_suggestion.PasswordLabel}粘贴";
            _autoClose.Stop();
            _autoClose.Interval = TimeSpan.FromSeconds(30); // 硬兜底
            _autoClose.Start();
            MaybeFinishAfterCopy(); // 已复制过就直接收
            return;
        }

        Close();
    }

    private void Ignore_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>复制口令/设备ID（独立复制，对应口令输入框）</summary>
    private void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_suggestion.Code))
        {
            Clipboard.SetText(_suggestion.Code);
            _codeCopied = true;
            _logger.Information("Link code copied");
            MaybeFinishAfterCopy();
        }
    }

    /// <summary>复制密码/验证码（独立复制，对应密码输入框）</summary>
    private void CopyPassword_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_suggestion.Password))
        {
            Clipboard.SetText(_suggestion.Password);
            _passwordCopied = true;
            _logger.Information("Link password copied");
            MaybeFinishAfterCopy();
        }
    }

    /// <summary>该复制的都复制完了 → 2 秒后自动收起（不劳手动关）</summary>
    private void MaybeFinishAfterCopy()
    {
        var needPassword = !string.IsNullOrEmpty(_suggestion.Password);
        if (!_codeCopied || (needPassword && !_passwordCopied))
        {
            return;
        }

        UrlText.Text = "已复制完成 ✓";
        _autoClose.Stop();
        _autoClose.Interval = TimeSpan.FromSeconds(2);
        _autoClose.Start();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
}
