namespace QuickPalette.App;

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using QuickPalette.App.Services;
using QuickPalette.Domain.Models;

/// <summary>
/// 热键录制控件：实时显示当前组合、录制新组合（Alt 组合走 e.SystemKey）、
/// 即时冲突探测（Win32 RegisterHotKey 试探）、支持恢复默认值。
/// 本控件只负责"录到合法组合"，不做全局注册；注册由 HotkeyService 在保存时统一执行。
/// </summary>
public partial class HotkeyRecorderControl : UserControl
{
    /// <summary>Win32 修饰键常量（与 HotkeyService 保持一致）</summary>
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;

    /// <summary>冲突试探用的临时热键 ID（注册后立即注销，不与 HotkeyService 的 ID 冲突）</summary>
    private const int ProbeHotkeyId = 0x7B31;

    private const int ErrorHotkeyAlreadyRegistered = 1409;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    private string _hotkey = new Settings().Hotkey;
    private string _registeredHotkey = new Settings().Hotkey;
    private bool _isRecording;

    /// <summary>"恢复默认"使用的目标默认值（主热键/截图热键各自设置）</summary>
    private string _defaultHotkey = new Settings().Hotkey;

    public HotkeyRecorderControl()
    {
        InitializeComponent();

        RecordButton.Content = UiText.T("Hotkey.Record");
        RefreshDefaultButton();

        Loaded += (s, e) => RefreshDisplay();
        LostKeyboardFocus += (s, e) =>
        {
            // 只有焦点真正离开本控件才取消录制。
            // 注意：点"修改"时 RecordButton_Click 里的 Keyboard.Focus(this) 会让按钮失焦，
            // 按钮的 LostKeyboardFocus 会冒泡到本控件（e.NewFocus 是控件自身，仍在内部）——
            // 若不加判断就取消，刚进入的录制会被立刻回滚，表现为"点击修改没反应"。
            if (_isRecording && !IsInsideControl(e.NewFocus))
            {
                CancelRecording();
            }
        };
    }

    /// <summary>设置"恢复默认"的目标热键并刷新按钮文案（不同用途的录制控件各自设置）</summary>
    public void SetDefaultHotkey(string hotkey)
    {
        _defaultHotkey = string.IsNullOrWhiteSpace(hotkey) ? new Settings().Hotkey : hotkey.Trim();
        RefreshDefaultButton();
    }

    private void RefreshDefaultButton()
        => DefaultButton.Content = $"{UiText.T("Hotkey.RestoreDefault")}（{_defaultHotkey}）";

    /// <summary>判断焦点目标是否位于本控件内部（含控件自身）</summary>
    private bool IsInsideControl(object? element)
    {
        for (DependencyObject? current = element as DependencyObject;
             current != null;
             current = GetParent(current))
        {
            if (ReferenceEquals(current, this))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>取视觉树父级；非视觉对象（如内容元素）退回逻辑父级</summary>
    private static DependencyObject? GetParent(DependencyObject element)
    {
        if (element is Visual or System.Windows.Media.Media3D.Visual3D)
        {
            return VisualTreeHelper.GetParent(element);
        }

        return (element as FrameworkContentElement)?.Parent;
    }

    /// <summary>
    /// 当前待保存的热键组合（设置控件的值即更新显示）
    /// </summary>
    public string Hotkey
    {
        get => _hotkey;
        set
        {
            _hotkey = string.IsNullOrWhiteSpace(value) ? new Settings().Hotkey : value.Trim();
            if (!_isRecording)
            {
                RefreshDisplay();
            }
        }
    }

    /// <summary>
    /// 当前已注册生效的热键组合（冲突探测时排除自身注册，避免误报）
    /// </summary>
    public string RegisteredHotkey
    {
        get => _registeredHotkey;
        set => _registeredHotkey = value ?? string.Empty;
    }

    /// <summary>
    /// 录制完成 / 恢复默认后触发，参数为新的组合字符串（尚未注册）
    /// </summary>
    public event EventHandler<string>? HotkeyChanged;

    #region 显示

    private void RefreshDisplay()
    {
        DisplayText.Text = _hotkey;
        DisplayText.Foreground = (Brush)Application.Current.Resources["AccentTextBrush"];
    }

    private void SetStatus(string text, bool isError, bool isSuccess = false)
    {
        StatusText.Text = text;
        var brushKey = isError ? "DangerTextBrush" : isSuccess ? "AccentTextBrush" : "SecondaryTextBrush";
        StatusText.Foreground = (Brush)Application.Current.Resources[brushKey];
    }

    #endregion

    #region 录制

    private void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRecording)
        {
            CancelRecording();
            return;
        }

        _isRecording = true;
        RecordButton.Content = UiText.T("Common.Cancel");
        DisplayText.Text = UiText.T("Hotkey.Recording");
        DisplayText.Foreground = (Brush)Application.Current.Resources["SecondaryTextBrush"];
        SetStatus(UiText.T("Hotkey.PressComboHint"), false);
        Keyboard.Focus(this);
    }

    private void DefaultButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRecording)
        {
            CancelRecording();
        }

        var defaultHotkey = _defaultHotkey;
        _hotkey = defaultHotkey;
        RefreshDisplay();
        SetStatus(string.Empty, false);
        HotkeyChanged?.Invoke(this, _hotkey);
    }

    private void CancelRecording()
    {
        _isRecording = false;
        RecordButton.Content = UiText.T("Hotkey.Record");
        RefreshDisplay();
        SetStatus(string.Empty, false);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (!_isRecording)
        {
            return;
        }

        e.Handled = true;

        // Alt 组合：WPF 中按 Alt 时 e.Key == Key.System，真实按键在 e.SystemKey
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // 单独的修饰键：实时显示已按下的修饰键前缀
        if (IsModifierKey(key))
        {
            DisplayText.Text = GetModifierPrefix() + "+…";
            DisplayText.Foreground = (Brush)Application.Current.Resources["SecondaryTextBrush"];
            return;
        }

        // Esc 取消录制
        if (key == Key.Escape)
        {
            CancelRecording();
            return;
        }

        // 组合键名校验：必须含至少一个修饰键
        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None)
        {
            SetStatus(UiText.T("Hotkey.NeedModifier"), true);
            return;
        }

        if (key == Key.None || key == Key.DeadCharProcessed || key == Key.ImeProcessed)
        {
            SetStatus(UiText.T("Hotkey.InvalidKey"), true);
            return;
        }

        // 键名格式与 HotkeyService.TryParseHotkey 对齐：Key.ToString() 同名字符串
        // （如 Space / D5 / NumPad5 / OemQuestion），修饰键顺序 Ctrl+Alt+Shift+Win
        var hotkey = string.Empty;
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            parts.Add("Win");
        }

        parts.Add(key.ToString());
        hotkey = string.Join("+", parts);

        // 即时冲突探测：与当前注册热键相同则跳过（那是本程序自己的注册）；
        // 否则用临时 ID 试探注册，成功立即注销，失败即被其他程序占用
        if (!string.Equals(hotkey, _registeredHotkey, StringComparison.OrdinalIgnoreCase)
            && !ProbeHotkeyAvailable(modifiers, key))
        {
            DisplayText.Text = hotkey;
            DisplayText.Foreground = (Brush)Application.Current.Resources["DangerTextBrush"];
            SetStatus(UiText.T("Hotkey.Conflict"), true);
            return;
        }

        _hotkey = hotkey;
        _isRecording = false;
        RecordButton.Content = UiText.T("Hotkey.Record");
        RefreshDisplay();
        SetStatus(UiText.T("Hotkey.Available"), false, true);
        HotkeyChanged?.Invoke(this, _hotkey);
    }

    private static bool IsModifierKey(Key key)
    {
        return key is Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin;
    }

    private static string GetModifierPrefix()
    {
        var parts = new List<string>();
        var modifiers = Keyboard.Modifiers;
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            parts.Add("Win");
        }

        return parts.Count > 0 ? string.Join("+", parts) : "…";
    }

    /// <summary>
    /// 用临时热键 ID 试探组合是否可用（注册成功立即注销，不留任何副作用）
    /// </summary>
    private bool ProbeHotkeyAvailable(ModifierKeys modifiers, Key key)
    {
        var mods = 0u;
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            mods |= MOD_CONTROL;
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            mods |= MOD_ALT;
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            mods |= MOD_SHIFT;
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            mods |= MOD_WIN;
        }

        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0)
        {
            return false;
        }

        if (RegisterHotKey(IntPtr.Zero, ProbeHotkeyId, mods, vk))
        {
            UnregisterHotKey(IntPtr.Zero, ProbeHotkeyId);
            return true;
        }

        // 仅"已被注册"判定为冲突；其他错误不阻断用户选择
        return Marshal.GetLastWin32Error() != ErrorHotkeyAlreadyRegistered;
    }

    #endregion
}
