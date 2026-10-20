namespace QuickPalette.App.Services;

using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using Serilog;

/// <summary>
/// 全局热键服务（RegisterHotKey + 隐藏消息窗口）。
/// 关键行为：
///  1. 换热键时"先注册新的、成功后再注销旧的"，失败自动保留旧热键并返回清晰错误；
///  2. WM_HOTKEY 参数校验与 RegisterHotKey 语义一致，只有真正对不上才丢弃并告警；
///  3. 消息窗口为 HWND_MESSAGE 纯消息窗口，Dispose 时完整销毁。
/// </summary>
public partial class HotkeyService : IDisposable
{
    private readonly ILogger _logger;
    private readonly HwndSource? _source;
    private readonly IntPtr _windowHandle;

    /// <summary>热键 ID（新旧热键并存瞬间各占一个，避免 id 冲突）</summary>
    private const int HotkeyIdA = 0x5101;
    private const int HotkeyIdB = 0x5102;

    private int _registeredId;
    private bool _hasRegistration;
    private Action? _hotkeyAction;
    private uint _registeredModifiers;
    private uint _registeredKey;
    private string _registeredText = string.Empty;
    private bool _disposed;

    // Win32 常量
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int WM_HOTKEY = 0x0312;

    /// <summary>参与比较的修饰键掩码（掩掉 MOD_NOREPEAT 等无关位后再比较）</summary>
    private const uint ModifierMask = MOD_ALT | MOD_CONTROL | MOD_SHIFT | MOD_WIN;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    public HotkeyService(ILogger logger)
    {
        _logger = logger;

        // 创建 HWND_MESSAGE 纯消息窗口：不可见、无 WPF Window 对象、无句柄泄漏
        var parameters = new HwndSourceParameters("QuickPaletteHotkeyMessageWindow")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,                  // 不可见
            ParentWindow = new IntPtr(-3)     // HWND_MESSAGE
        };
        _source = new HwndSource(parameters);
        _source.AddHook(HwndHook);
        _windowHandle = _source.Handle;
    }

    /// <summary>当前已注册的热键文本（未注册时为空串）</summary>
    public string RegisteredHotkey => _registeredText;

    /// <summary>
    /// 注册全局热键（兼容入口；错误原因见 <see cref="TryRegisterHotkey"/>）。
    /// </summary>
    public bool RegisterHotkey(string hotkeyString, Action action)
        => TryRegisterHotkey(hotkeyString, action, out _);

    /// <summary>
    /// 注册全局热键：先注册新热键，成功后才注销旧热键；
    /// 注册失败时旧热键原样保留（回滚），并返回可读的错误原因。
    /// </summary>
    public bool TryRegisterHotkey(string hotkeyString, Action action, out string? error)
    {
        error = null;

        if (_disposed)
        {
            error = "热键服务已释放";
            return false;
        }

        if (!TryParseHotkey(hotkeyString, out var modifiers, out var key))
        {
            error = $"无法识别的热键格式：{hotkeyString}";
            _logger.Error("Invalid hotkey format: {Hotkey}", hotkeyString);
            return false;
        }

        // WPF Key 枚举值不是 Windows 虚拟键码（如 Key.S=62 而 VK_S=83），
        // 必须用 KeyInterop.VirtualKeyFromKey 转换后再注册
        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0)
        {
            error = $"该按键不能作为热键：{hotkeyString}";
            _logger.Error("Hotkey key has no virtual key code: {Hotkey}", hotkeyString);
            return false;
        }

        // 同一组合重复注册：只替换回调
        if (_hasRegistration && _registeredModifiers == modifiers && _registeredKey == vk)
        {
            _hotkeyAction = action;
            _registeredText = hotkeyString;
            _logger.Information("Hotkey unchanged, handler replaced: {Hotkey}", hotkeyString);
            return true;
        }

        // 先立新、后拆旧：新 id 注册成功才注销旧 id；失败则旧热键仍然有效（天然回滚）
        var newId = _registeredId == HotkeyIdA ? HotkeyIdB : HotkeyIdA;
        if (!RegisterHotKey(_windowHandle, newId, modifiers | MOD_NOREPEAT, vk))
        {
            var win32Error = Marshal.GetLastWin32Error();
            error = win32Error == 1409 // ERROR_HOTKEY_ALREADY_REGISTERED
                ? $"热键 {hotkeyString} 已被其他程序占用"
                : $"热键 {hotkeyString} 注册失败（Win32 错误 {win32Error}）";
            _logger.Warning("RegisterHotKey failed: {Hotkey}, error={Error}", hotkeyString, win32Error);
            return false;
        }

        if (_hasRegistration)
        {
            UnregisterHotKey(_windowHandle, _registeredId);
        }

        _registeredId = newId;
        _hasRegistration = true;
        _registeredModifiers = modifiers;
        _registeredKey = vk;
        _registeredText = hotkeyString;
        _hotkeyAction = action;

        _logger.Information("Hotkey registered: {Hotkey} (vk={Vk}, modifiers={Mods})", hotkeyString, vk, modifiers);
        return true;
    }

    /// <summary>
    /// 解析热键字符串（如 "Ctrl+Alt+Space"）。
    /// 键名与 Key.ToString() 输出对齐（D0-D9 / NumPad0-9 / OemX / F1-F24 / Space ...），
    /// 同时兼容 esc/del/pgup 等常用别名与单字符键。
    /// </summary>
    public static bool TryParseHotkey(string? hotkeyString, out uint modifiers, out Key key)
    {
        modifiers = 0;
        key = Key.None;

        if (string.IsNullOrWhiteSpace(hotkeyString))
            return false;

        var parts = hotkeyString.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            return false;

        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= MOD_CONTROL;
                    break;
                case "alt":
                    modifiers |= MOD_ALT;
                    break;
                case "shift":
                    modifiers |= MOD_SHIFT;
                    break;
                case "win":
                case "windows":
                    modifiers |= MOD_WIN;
                    break;
                default:
                    return false;
            }
        }

        if (modifiers == 0)
            return false;

        key = ParseKey(parts[^1]);
        return key != Key.None;
    }

    /// <summary>解析单个键名</summary>
    private static Key ParseKey(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return Key.None;

        // 1) 常用别名（大小写不敏感）
        switch (token.ToLowerInvariant())
        {
            case "esc": return Key.Escape;
            case "escape": return Key.Escape;
            case "del": return Key.Delete;
            case "ins": return Key.Insert;
            case "pgup": return Key.PageUp;
            case "pgdn":
            case "pgdown": return Key.PageDown;
            case "backspace": return Key.Back;
            case "spacebar": return Key.Space;
            case "capslock": return Key.CapsLock;
            case "return": return Key.Enter;
        }

        // 2) 数字 0-9 → Key.D0-D9（Enum.TryParse 会把数字当枚举值解析，必须先挡掉）
        if (token.Length == 1 && token[0] is >= '0' and <= '9')
            return Key.D0 + (token[0] - '0');

        // 3) 单字符字母 a-z / A-Z
        if (token.Length == 1)
        {
            var c = char.ToLowerInvariant(token[0]);
            if (c is >= 'a' and <= 'z')
                return Key.A + (c - 'a');
        }

        // 4) 与 Key.ToString() 同名的键名：D0-D9、NumPad0-9、Oem*、F1-F24、Space ...
        if (!char.IsDigit(token[0]) &&
            Enum.TryParse(token, ignoreCase: true, out Key parsed) &&
            parsed != Key.None &&
            Enum.IsDefined(typeof(Key), parsed))
        {
            return parsed;
        }

        return Key.None;
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_HOTKEY)
            return IntPtr.Zero;

        // wParam 用 ToInt64：x64 下高 32 位可能非零，ToInt32 会截断/抛异常
        if (wParam.ToInt64() != _registeredId)
            return IntPtr.Zero;

        var raw = unchecked((ulong)lParam.ToInt64());
        var wordLow = (uint)(raw & 0xFFFF);
        var wordHigh = (uint)((raw >> 16) & 0xFFFF);

        // WM_HOTKEY 的 lParam 两个 16 位字段分别是虚拟键码与修饰键掩码；
        // 不同资料对高低位的描述不一致（本机实测低位=VK、高位=修饰键），且修饰键掩码
        // 可能带 MOD_NOREPEAT 等无关位。这里两种布局都校验、比较前先掩掉无关位，
        // 只有两种布局都对不上才认为是异常消息。
        var matchLowKeyHighMod = wordHigh == _registeredKey && (wordLow & ModifierMask) == _registeredModifiers;
        var matchHighKeyLowMod = wordLow == _registeredKey && (wordHigh & ModifierMask) == _registeredModifiers;

        if (matchLowKeyHighMod || matchHighKeyLowMod)
        {
            try
            {
                _hotkeyAction?.Invoke();
            }
            catch (Exception ex)
            {
                // 热键回调异常不能穿透消息钩子，否则会把整个消息循环打崩
                _logger.Error(ex, "Hotkey handler threw");
            }

            handled = true;
        }
        else
        {
            _logger.Warning(
                "Hotkey message ignored (mismatch): wordLow={Low}, wordHigh={High}, registered vk={Vk}, mods={Mods}",
                wordLow, wordHigh, _registeredKey, _registeredModifiers);
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_hasRegistration)
        {
            UnregisterHotKey(_windowHandle, _registeredId);
            _hasRegistration = false;
        }

        if (_source != null)
        {
            _source.RemoveHook(HwndHook);
            _source.Dispose(); // 销毁隐藏消息窗口，释放句柄
        }

        _logger.Information("HotkeyService disposed");
    }
}
