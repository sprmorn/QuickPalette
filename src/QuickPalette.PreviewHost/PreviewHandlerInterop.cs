using System.Runtime.InteropServices;

namespace QuickPalette.PreviewHost;

// ───────────────────────── 结构体 ─────────────────────────

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left, Top, Right, Bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int X, Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MSG
{
    public IntPtr Hwnd;
    public uint Message;
    public IntPtr WParam;
    public IntPtr LParam;
    public uint Time;
    public POINT Pt;
}

/// <summary>IPreviewHandlerFrame::GetWindowContext 输出（加速键表；本宿主不提供）</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PreviewHandlerFrameInfo
{
    public IntPtr Accel;
    public uint AccelEntries;
}

// ───────────────────────── 消费侧 COM 接口（RCW） ─────────────────────────
// GUID 均取自 Windows SDK shobjidl_core.h / shobjidl.idl（与 Wine 头文件交叉核对）。

/// <summary>预览处理器主接口（IID 与 ShellEx 关联 GUID 相同）</summary>
[ComImport]
[Guid("8895b1c6-b41f-4c1c-a562-0d564250836f")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPreviewHandler
{
    [PreserveSig] int SetWindow(IntPtr hwnd, ref RECT rect);
    [PreserveSig] int SetRect(ref RECT rect);
    [PreserveSig] int DoPreview();
    [PreserveSig] int Unload();
    [PreserveSig] int SetFocus();
    [PreserveSig] int QueryFocus(out IntPtr phwnd);
    [PreserveSig] int TranslateAccelerator(IntPtr pmsg);
}

/// <summary>文件初始化（多数预览处理器的装入入口）</summary>
[ComImport]
[Guid("b7d14566-0509-4cce-a71f-0a554233bd9b")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInitializeWithFile
{
    [PreserveSig] int Initialize([MarshalAs(UnmanagedType.LPWStr)] string filePath, uint grfMode);
}

/// <summary>流初始化（shell32 系预览器只收流的装入入口；IID 取自 propsys.idl）</summary>
[ComImport]
[Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInitializeWithStream
{
    [PreserveSig] int Initialize(System.Runtime.InteropServices.ComTypes.IStream stream, uint grfMode);
}

/// <summary>IPersistFile：IInitializeWithFile 不可用时的兜底装入入口</summary>
[ComImport]
[Guid("0000010b-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPersistFile
{
    [PreserveSig] int GetClassID(out Guid classId);
    [PreserveSig] int IsDirty();
    [PreserveSig] int Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
    [PreserveSig] int Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);
    [PreserveSig] int SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
    [PreserveSig] int GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
}

/// <summary>站点挂接：处理器经此拿到宿主框架（IPreviewHandlerFrame）</summary>
[ComImport]
[Guid("fc4801a3-2ba9-11cf-a229-00aa003d7352")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IObjectWithSite
{
    [PreserveSig] int SetSite([MarshalAs(UnmanagedType.IUnknown)] object? site);
    [PreserveSig] int GetSite(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? site);
}

/// <summary>视觉定制（背景色/字体/文字色，仅文本类处理器支持）</summary>
[ComImport]
[Guid("196bf9a5-b346-4ef0-aa1e-5dcdb76768b1")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPreviewHandlerVisuals
{
    [PreserveSig] int SetBackgroundColor(uint colorRef);
    [PreserveSig] int SetFont(IntPtr logFont);
    [PreserveSig] int SetTextColor(uint colorRef);
}

// ───────────────────────── 实现侧 COM 接口（CCW） ─────────────────────────

/// <summary>
/// 宿主框架接口：由本进程的托管类实现、经 SetSite 交给处理器（CCW 侧，
/// 因此不带 ComImport——ComImport 仅用于消费原生对象）。
/// 处理器把未处理的按键（如 Esc）回传到 TranslateAccelerator。
/// </summary>
[Guid("fec87aaf-35f9-447a-adb7-20234491401a")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPreviewHandlerFrame
{
    [PreserveSig] int GetWindowContext(out PreviewHandlerFrameInfo info);
    [PreserveSig] int TranslateAccelerator(IntPtr pmsg);
}

// ───────────────────────── 原生方法 ─────────────────────────

internal static class Native
{
    [DllImport("user32.dll")]
    internal static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    internal static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    internal static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    internal static extern IntPtr DispatchMessage(ref MSG msg);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    /// <summary>在文件上开 IStream（装入入口走 IInitializeWithStream 的处理器用）</summary>
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    internal static extern int SHCreateStreamOnFileEx(
        string pszFile, uint dwMode, uint dwAttributes, bool fCreate,
        IntPtr pstmTemplate, out System.Runtime.InteropServices.ComTypes.IStream ppstm);
}
