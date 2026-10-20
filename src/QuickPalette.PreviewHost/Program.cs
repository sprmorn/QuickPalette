using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using QuickPalette.Infrastructure.Preview;

namespace QuickPalette.PreviewHost;

/// <summary>
/// 预览宿主进程：在独立进程中承载系统预览处理器（IPreviewHandler），
/// 将处理器内容挂到主进程传入的槽位 HWND 上（与资源管理器的 prevhost.exe 同构）。
/// 处理器是第三方 COM 代码，崩溃/卡死只影响本进程；主进程观察到退出后回落既有预览链路。
///
/// 命令行：--hwnd &lt;槽位HWND&gt; --width &lt;px&gt; --height &lt;px&gt; [--bg RRGGBB] --file &lt;路径&gt;
/// stdout 行协议：READY / CLOSE / FAIL &lt;NOHANDLER|INIT|ARGS&gt; &lt;消息&gt;
/// 退出码：0 正常/关闭，2 无处理器，3 初始化失败，4 参数无效。
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitNoHandler = 2;
    private const int ExitInitFailed = 3;
    private const int ExitBadArguments = 4;

    internal const uint WmQuit = 0x0012;
    internal const uint WmKeyDown = 0x0100;
    internal const uint WmSysKeyDown = 0x0104;
    internal const int VkEscape = 0x1B;
    private const uint StgmRead = 0x00000000;

    private static uint _mainThreadId;

    /// <summary>
    /// 装入用的文件流 RCW：必须保持到 Unload——处理器渲染期间还会回来读流，
    /// 若被 GC 回收（连带关闭底层流），预览内容就是空白。
    /// </summary>
    private static System.Runtime.InteropServices.ComTypes.IStream? _liveInitStream;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch
        {
            // 无控制台且未重定向输出：协议写出失败亦无害（调用方按超时回落）
        }

        IntPtr parentHwnd = IntPtr.Zero;
        var width = 0;
        var height = 0;
        string? filePath = null;
        string? backgroundRgb = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--hwnd" when i + 1 < args.Length:
                    parentHwnd = new IntPtr(long.Parse(args[++i], CultureInfo.InvariantCulture));
                    break;
                case "--width" when i + 1 < args.Length:
                    width = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--height" when i + 1 < args.Length:
                    height = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--bg" when i + 1 < args.Length:
                    backgroundRgb = args[++i];
                    break;
                case "--file" when i + 1 < args.Length:
                    filePath = args[++i];
                    break;
            }
        }

        _mainThreadId = Native.GetCurrentThreadId();

        if (parentHwnd == IntPtr.Zero || string.IsNullOrEmpty(filePath))
        {
            return Fail(ExitBadArguments, "ARGS", "missing --hwnd/--file");
        }
        if (!File.Exists(filePath))
        {
            return Fail(ExitBadArguments, "ARGS", "file not found");
        }
        if (!Native.IsWindow(parentHwnd))
        {
            return Fail(ExitBadArguments, "ARGS", "parent window is gone");
        }

        var registration = PreviewHandlerResolver.Shared.Find(Path.GetExtension(filePath));
        if (registration is null)
        {
            return Fail(ExitNoHandler, "NOHANDLER", "no preview handler for this extension");
        }
        // 按本进程位数判定（x86 宿主承载 32 位进程内处理器）
        if (!PreviewHandlerResolver.CanHost(registration, Environment.Is64BitProcess))
        {
            return Fail(ExitNoHandler, "NOHANDLER",
                $"handler not hostable in {(Environment.Is64BitProcess ? "x64" : "x86")} process ({registration.Kind}/{registration.InProcServerArch ?? "unknown-arch"})");
        }

        return RunPreview(parentHwnd, width, height, filePath, backgroundRgb, registration.Clsid);
    }

    private static int RunPreview(
        IntPtr parentHwnd, int width, int height,
        string filePath, string? backgroundRgb, Guid clsid)
    {
        object? comObject = null;
        IPreviewHandler? handler = null;
        var ready = false;
        var phase = "activate";

        try
        {
            // 1. 激活：LocalServer32 由 COM 拉起独立进程（Word/WPS 等），InprocServer32 装载 DLL。
            // WPS 更新/自修复会瞬时改写 COM 注册（症状 REGDB_E_CLASSNOTREG），失败后短暂重试一次
            var type = Type.GetTypeFromCLSID(clsid, throwOnError: false);
            if (type is null)
            {
                return Fail(ExitNoHandler, "NOHANDLER", "CLSID is not activatable");
            }

            // 0. 激活前置：WPS 系预览器只把 CLSID 注册进 WOW6432Node（32 位视图），
            // 且其后台服务会周期性改写注册（时好时坏）；LocalServer 的激活由 COM 服务端
            // 按 64 位视图解析，查不到就报 REGDB_E_CLASSNOTREG（进程内 DLL 走客户端视图
            // 故不受影响——PDF 正常而 Word/Excel 失败的原因）。把 LocalServer32 命令行
            // 镜像到 HKCU\Software\Classes\CLSID（HKCR 的每用户共享部分，双视图 + COM
            // 服务端均可见）让激活确定成功：仅补缺失项、不改已有注册、无需管理员。
            MirrorRegistrationForActivation(clsid);

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    comObject = Activator.CreateInstance(type);
                    break;
                }
                catch (COMException ex) when (ex.HResult == unchecked((int)0x80040154) && attempt < 2)
                {
                    // 仍报类未注册：补镜像后重试
                    MirrorRegistrationForActivation(clsid);
                    Thread.Sleep(400);
                }
            }
            if (comObject is not IPreviewHandler previewHandler)
            {
                return Fail(ExitInitFailed, "INIT", "IPreviewHandler is not supported");
            }
            handler = previewHandler;

            // 2. 装入文件（IInitializeWithFile → IInitializeWithStream → IPersistFile 逐级兜底）
            phase = "initialize";
            if (!Initialize(comObject, filePath, out var initDiagnostics))
            {
                return Fail(ExitInitFailed, "INIT", $"Initialize() failed [{initDiagnostics}]");
            }

            // 3. 站点：处理器经 IObjectWithSite 拿到宿主框架（Esc 等按键由此回传）。
            // 可选步骤：失败不作废预览（Explorer 之外的宿主不给站点也能预览）
            phase = "setsite";
            try
            {
                if (comObject is IObjectWithSite withSite)
                {
                    var frame = new PreviewHandlerFrame(OnCloseRequested);
                    withSite.SetSite(Marshal.GetIUnknownForObject(frame));
                }
            }
            catch (Exception)
            {
                // 站点挂接失败不作废预览（仅影响 Esc 关闭等增强交互）
            }

            // 4. 主题背景色（RRGGBB → COLORREF，仅文本类处理器响应）。可选步骤，失败不作废预览
            phase = "visuals";
            try
            {
                if (backgroundRgb != null && comObject is IPreviewHandlerVisuals visuals)
                {
                    visuals.SetBackgroundColor(ParseColorRef(backgroundRgb));
                }
            }
            catch (Exception)
            {
                // 主题着色是纯装饰步骤
            }

            // 5. 挂载到主进程槽位并开始预览（矩形为父窗口客户区坐标）
            phase = "setwindow";
            var rect = new RECT { Left = 0, Top = 0, Right = width, Bottom = height };
            Marshal.ThrowExceptionForHR(handler.SetWindow(parentHwnd, ref rect));
            phase = "dopreview";
            Marshal.ThrowExceptionForHR(handler.DoPreview());

            ready = true;
            Emit("READY");
            MessageLoop(parentHwnd);
            return ExitOk;
        }
        catch (Exception ex)
        {
            if (!ready)
            {
                return Fail(ExitInitFailed, "INIT",
                    $"[{phase}] {ex.GetType().Name}: {ex.Message}{Diagnostics(clsid)}");
            }
            // 就绪后异常（消息循环内）：按崩溃语义退出，主进程回落既有链路
            return ExitInitFailed;
        }
        finally
        {
            try
            {
                handler?.Unload();
            }
            catch
            {
                // 卸载失败不应掩盖退出语义
            }
            if (_liveInitStream is not null)
            {
                try
                {
                    Marshal.FinalReleaseComObject(_liveInitStream);
                }
                catch
                {
                    // 流已随服务器释放等情况
                }
                _liveInitStream = null;
            }
            if (comObject is not null)
            {
                try
                {
                    Marshal.FinalReleaseComObject(comObject);
                }
                catch
                {
                    // 服务器已先行退出等情况
                }
            }
        }
    }

    /// <summary>
    /// 装入文件：三种标准装入入口逐级尝试（处理器实现哪种就用哪种），
    /// 失败时输出各入口的 HRESULT 便于定位。
    /// </summary>
    private static bool Initialize(object comObject, string filePath, out string diagnostics)
    {
        var attempts = new List<string>();

        if (comObject is IInitializeWithFile initFile)
        {
            var hr = initFile.Initialize(filePath, StgmRead);
            attempts.Add($"IInitializeWithFile=0x{hr:X8}");
            if (hr >= 0)
            {
                diagnostics = string.Join(" ", attempts);
                return true;
            }
        }

        if (comObject is IInitializeWithStream initStream)
        {
            var hrCreate = Native.SHCreateStreamOnFileEx(filePath, StgmRead, 0, false, IntPtr.Zero, out var stream);
            attempts.Add($"SHCreateStreamOnFileEx=0x{hrCreate:X8}");
            if (hrCreate >= 0)
            {
                var hr = initStream.Initialize(stream, StgmRead);
                attempts.Add($"IInitializeWithStream=0x{hr:X8}");
                if (hr >= 0)
                {
                    _liveInitStream = stream; // 保持存活到 Unload，见字段说明
                    diagnostics = string.Join(" ", attempts);
                    return true;
                }
            }
        }

        if (comObject is IPersistFile persistFile)
        {
            var hr = persistFile.Load(filePath, StgmRead);
            attempts.Add($"IPersistFile=0x{hr:X8}");
            if (hr >= 0)
            {
                diagnostics = string.Join(" ", attempts);
                return true;
            }
        }

        if (attempts.Count == 0) attempts.Add("no supported initialize interface");
        diagnostics = string.Join(" ", attempts);
        return false;
    }

    /// <summary>
    /// 消息泵：处理预览内容窗口（跨进程子窗口）的消息；
    /// 看门狗监视父窗口（主进程槽位），父窗口消失即退出（主进程异常退出不留孤儿）。
    /// </summary>
    private static void MessageLoop(IntPtr parentHwnd)
    {
        using var watchdog = new Timer(_ =>
        {
            if (!Native.IsWindow(parentHwnd))
            {
                Native.PostThreadMessage(_mainThreadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
            }
        }, null, 700, 700);

        while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }
    }

    /// <summary>处理器框架请求关闭（Esc）：通知主进程并退出消息泵</summary>
    private static void OnCloseRequested()
    {
        Emit("CLOSE");
        Native.PostThreadMessage(_mainThreadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>"RRGGBB" → COLORREF（0x00BBGGRR）</summary>
    private static uint ParseColorRef(string rgb)
    {
        if (rgb.Length == 6 &&
            uint.TryParse(rgb, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            var r = (value >> 16) & 0xFF;
            var g = (value >> 8) & 0xFF;
            var b = value & 0xFF;
            return r | (g << 8) | (b << 16);
        }
        return 0xFFFFFF;
    }

    private static void Emit(string line)
    {
        try
        {
            Console.Out.WriteLine(line);
            Console.Out.Flush();
        }
        catch
        {
            // 输出未重定向或管道已断：调用方按超时/退出码兜底
        }
    }

    /// <summary>
    /// 把仅存在于 32 位视图（WOW6432Node）的 LocalServer32 注册镜像到
    /// HKCU\Software\Classes\CLSID（HKCR 每用户共享部分，任意位数进程与 COM 服务端均可见）。
    /// 仅在缺失时补写，不修改已有注册，无需管理员权限。
    /// </summary>
    private static void MirrorRegistrationForActivation(Guid clsid)
    {
        try
        {
            var key = clsid.ToString("B");
            var command = FindLocalServerCommand(key);
            if (string.IsNullOrWhiteSpace(command)) return;

            // 机器级（HKLM）优先：任何完整性级别/权限上下文的 COM 激活都必见——
            // 高完整性（管理员）进程的激活对 HKCU 每用户注册解析不可靠（实测）。
            // 仅补缺失项；无权限时退回每用户（HKCU）镜像。
            foreach (var root in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
            {
                try
                {
                    using var shared = root.CreateSubKey($"Software\\Classes\\CLSID\\{key}");
                    if (shared is null) continue;

                    // 清理历史误写：LocalServer32 必须是"子键 + 默认值"，不是 CLSID 键下的同名值
                    if (shared.GetValue("LocalServer32") is not null)
                    {
                        shared.DeleteValue("LocalServer32", throwOnMissingValue: false);
                    }

                    using (var existing = shared.OpenSubKey("LocalServer32"))
                    {
                        if (existing?.GetValue(null) is not null) return; // 已有激活注册：完成
                    }
                    using (var existingInproc = shared.OpenSubKey("InprocServer32"))
                    {
                        if (existingInproc?.GetValue(null) is not null) return;
                    }

                    using var localServer = shared.CreateSubKey("LocalServer32");
                    localServer?.SetValue(null, command);
                    return;
                }
                catch
                {
                    // 该根无写权限（如普通权限写 HKLM）：尝试下一个
                }
            }
        }
        catch
        {
        }
    }

    /// <summary>从 32 位视图/本进程视图找 LocalServer32 启动命令</summary>
    private static string? FindLocalServerCommand(string key)
    {
        foreach (var source in new[] { $"WOW6432Node\\CLSID\\{key}\\LocalServer32", $"CLSID\\{key}\\LocalServer32" })
        {
            using var k = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(source);
            var command = k?.GetValue(null) as string;
            if (!string.IsNullOrWhiteSpace(command)) return command;
        }
        return null;
    }

    /// <summary>
    /// 激活失败时的现场信息：进程完整性级别 + 本进程注册表视图里 CLSID 的可见性。
    /// 用于区分"注册表瞬时缺失/视图错配"与"运行上下文限制（低完整性等）"。
    /// </summary>
    private static string Diagnostics(Guid clsid)
    {
        try
        {
            var key = clsid.ToString("B");
            using var clsidKey = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey($"CLSID\\{key}");
            using var local = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey($"CLSID\\{key}\\LocalServer32");
            using var inproc = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey($"CLSID\\{key}\\InprocServer32");
            return $" | diag: lowIL={QuickPalette.Infrastructure.ProcessIntegrity.IsLowIntegrity()}" +
                   $" elevated={QuickPalette.Infrastructure.ProcessIntegrity.IsElevated()}" +
                   $" arch={(Environment.Is64BitProcess ? "x64" : "x86")}" +
                   $" clsidKey={(clsidKey is null ? "missing" : "found")}" +
                   $" localServer={(local is null ? "missing" : "found")}" +
                   $" inproc={(inproc is null ? "missing" : "found")}";
        }
        catch (Exception ex)
        {
            return $" | diag failed: {ex.GetType().Name}";
        }
    }

    private static int Fail(int exitCode, string status, string message)
    {
        Emit($"FAIL {status} {message.Replace('\r', ' ').Replace('\n', ' ')}");
        return exitCode;
    }
}

/// <summary>
/// 宿主框架实现（CCW）：处理器把未处理的按键回传到此，
/// Esc 即视为关闭请求（与预览窗口的 Esc 语义一致）。
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class PreviewHandlerFrame : IPreviewHandlerFrame
{
    private readonly Action _onClose;

    public PreviewHandlerFrame(Action onClose) => _onClose = onClose;

    public int GetWindowContext(out PreviewHandlerFrameInfo info)
    {
        info = default;
        return 0; // S_OK：不提供加速键表
    }

    public int TranslateAccelerator(IntPtr pmsg)
    {
        if (pmsg != IntPtr.Zero)
        {
            var msg = Marshal.PtrToStructure<MSG>(pmsg);
            if ((msg.Message is Program.WmKeyDown or Program.WmSysKeyDown)
                && msg.WParam.ToInt32() == Program.VkEscape)
            {
                _onClose();
                return 0; // S_OK：已处理
            }
        }
        return 1; // S_FALSE：未处理，交还处理器
    }
}
