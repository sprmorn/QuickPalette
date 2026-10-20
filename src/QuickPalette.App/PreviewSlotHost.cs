using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace QuickPalette.App;

/// <summary>
/// 预览槽位：一个无逻辑的纯 Win32 子窗口，作为独立预览宿主进程
/// （QuickPalette.PreviewHost.exe）承载 IPreviewHandler 内容的挂载点。
/// 宿主进程把预览内容窗口挂到本槽位 HWND 下（与资源管理器把 prevhost
/// 内容嵌进预览窗格同构）；WPF 侧只提供句柄与客户区尺寸。
/// </summary>
public sealed class PreviewSlotHost : HwndHost
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipChildren = 0x02000000;

    private IntPtr _hwnd;

    /// <summary>槽位 HWND（窗口尚未构建时为 0）</summary>
    public IntPtr SlotHandle => _hwnd;

    /// <summary>槽位客户区尺寸（物理像素；预览宿主按此给出挂载矩形）</summary>
    public (int Width, int Height) GetClientSizePx()
    {
        if (_hwnd == IntPtr.Zero) return (0, 0);
        GetClientRect(_hwnd, out var rect);
        return (rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    /// <summary>
    /// 摘离并隐藏槽位下的所有预览内容窗口：WPS 服务等外部进程持有的旧预览窗口
    /// 杀宿主进程也清不掉，会赖在预览区串台；挂为无父窗口并隐藏即可干净剥离。
    /// 注意不能销毁重建槽位本身——WPF 的 HwndHost 记录的是构建期返回的句柄，
    /// 更换后布局调整会打在已销毁的旧窗口上（槽位永远停在初建尺寸，实测踩过）。
    /// </summary>
    public void DetachContent()
    {
        if (_hwnd == IntPtr.Zero) return;
        foreach (var child in GetChildren(_hwnd))
        {
            SetParent(child, IntPtr.Zero);
            ShowWindow(child, SwHide);
        }
    }

    private List<IntPtr> GetChildren(IntPtr parent)
    {
        var result = new List<IntPtr>();
        EnumChildWindows(parent, (hwnd, _) =>
        {
            if (GetParent(hwnd) == parent) result.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        // STATIC 仅作容器：预览内容由宿主进程的子窗口绘制
        _hwnd = CreateWindowExW(
            0, "STATIC", string.Empty,
            WsChild | WsVisible | WsClipChildren,
            0, 0, Math.Max(1, (int)Width), Math.Max(1, (int)Height),
            hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        DestroyWindow(hwnd.Handle);
        _hwnd = IntPtr.Zero;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW")]
    private static extern IntPtr CreateWindowExW(
        int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetParent(IntPtr hwnd, IntPtr newParent);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hwnd, EnumWindowsProc callback, IntPtr param);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);

    private const int SwHide = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }
}
