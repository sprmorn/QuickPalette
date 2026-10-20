using System.Diagnostics;
using System.Globalization;

namespace QuickPalette.Infrastructure.Preview;

/// <summary>预览宿主进程启动参数</summary>
/// <param name="ParentWindowHandle">宿主窗口句柄：预览内容窗口的父窗口（由主程序提供槽位 HWND）</param>
/// <param name="WidthPx">预览区域宽（父窗口客户区像素）</param>
/// <param name="HeightPx">预览区域高（父窗口客户区像素）</param>
/// <param name="FilePath">要预览的文件绝对路径</param>
/// <param name="BackgroundRgb">可选背景色（RRGGBB），传给 IPreviewHandlerVisuals 以贴合主题</param>
public sealed record PreviewHandlerHostRequest(
    IntPtr ParentWindowHandle,
    int WidthPx,
    int HeightPx,
    string FilePath,
    string? BackgroundRgb = null);

/// <summary>预览宿主进程的状态</summary>
public enum PreviewHandlerHostStatus
{
    /// <summary>已就绪（处理器加载完成并开始预览）</summary>
    Ready,

    /// <summary>该文件没有可用的预览处理器</summary>
    NoHandler,

    /// <summary>处理器激活/初始化失败</summary>
    InitFailed,

    /// <summary>启动参数无效</summary>
    BadArguments,

    /// <summary>就绪超时（处理器冷启动过慢）</summary>
    TimedOut,

    /// <summary>就绪后正常退出（关闭请求：处理器内 Esc / 宿主自行收尾）</summary>
    Exited,

    /// <summary>就绪后异常退出（处理器崩溃被隔离在宿主进程内）</summary>
    Crashed
}

/// <summary>
/// 预览宿主进程（QuickPalette.PreviewHost.exe）的客户端：
/// 启动独立进程承载 IPreviewHandler，通过 stdout 行协议通信——
///   READY            已就绪（加载完成并渲染）
///   CLOSE            宿主请求关闭（处理器内 Esc 等）
///   FAIL &lt;状态&gt; &lt;消息&gt;   启动失败
/// 进程退出码：0 正常/关闭，2 无处理器，3 初始化失败，4 参数无效。
///
/// 隔离语义（对齐资源管理器的 prevhost.exe）：处理器是第三方 COM 代码，
/// 崩溃/卡死只影响本宿主进程；主进程观察到退出后回落既有预览链路。
/// </summary>
public sealed class PreviewHandlerHostClient : IDisposable
{
    /// <summary>x64 预览宿主 exe 名（承载 64 位进程内处理器；LocalServer 两种宿主均可激活）</summary>
    public const string HostExeName = "QuickPalette.PreviewHost.exe";

    /// <summary>
    /// x86 预览宿主 exe（承载 32 位进程内处理器：WPS 系插件、Foxit 等）。
    /// 位于独立子目录：其自包含运行时文件名与 x64 相同，不能与主程序平铺共存。
    /// </summary>
    public const string HostExeName32 = "previewhost32\\QuickPalette.PreviewHost32.exe";

    private readonly Process _process;
    private readonly TaskCompletionSource<PreviewHandlerHostStatus> _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    private PreviewHandlerHostClient(Process process)
    {
        _process = process;
        _process.OutputDataReceived += OnOutputDataReceived;
        _process.Exited += OnExited;
    }

    /// <summary>宿主的失败详情（FAIL 行的消息部分：失败阶段/异常/HRESULT；成功时为 null）</summary>
    public string? LastFailureDetail { get; private set; }

    /// <summary>宿主请求关闭（Esc 等；调用方应隐藏预览窗口）</summary>
    public event Action? CloseRequested;

    /// <summary>就绪之后进程退出（code 0 = 正常关闭；否则 Crashed，调用方应回落既有链路）</summary>
    public event Action<PreviewHandlerHostStatus>? Terminated;

    /// <summary>宿主 exe 路径（与主程序同目录；缺省 x64 宿主）</summary>
    public static string HostExePath => HostExePathFor(HostExeName);

    /// <summary>指定宿主 exe 的路径（与主程序同目录）</summary>
    public static string HostExePathFor(string hostExeName) =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, hostExeName);

    /// <summary>宿主 exe 是否随主程序部署（缺省查 x64 宿主）</summary>
    public static bool IsAvailable(string hostExeName = HostExeName) =>
        File.Exists(HostExePathFor(hostExeName));

    /// <summary>启动宿主进程（缺省 x64 宿主）；exe 缺失返回 null（调用方直接回落既有链路）</summary>
    public static PreviewHandlerHostClient? TryStart(PreviewHandlerHostRequest request, string hostExeName = HostExeName)
    {
        if (!IsAvailable(hostExeName)) return null;

        var startInfo = new ProcessStartInfo
        {
            FileName = HostExePathFor(hostExeName),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8
        };
        // ArgumentList 负责正确转义（含空格/结尾反斜杠的路径）
        startInfo.ArgumentList.Add("--hwnd");
        startInfo.ArgumentList.Add(((long)request.ParentWindowHandle).ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--width");
        startInfo.ArgumentList.Add(request.WidthPx.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--height");
        startInfo.ArgumentList.Add(request.HeightPx.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(request.BackgroundRgb))
        {
            startInfo.ArgumentList.Add("--bg");
            startInfo.ArgumentList.Add(request.BackgroundRgb);
        }
        startInfo.ArgumentList.Add("--file");
        startInfo.ArgumentList.Add(request.FilePath);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start()) return null;
        }
        catch
        {
            process.Dispose();
            return null;
        }

        var client = new PreviewHandlerHostClient(process);
        process.BeginOutputReadLine();
        return client;
    }

    /// <summary>等待就绪；超时返回 <see cref="PreviewHandlerHostStatus.TimedOut"/>（调用方应回落并 Dispose）</summary>
    public async Task<PreviewHandlerHostStatus> WaitReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var completed = await Task.WhenAny(_ready.Task, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);
        if (completed == _ready.Task) return await _ready.Task.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return PreviewHandlerHostStatus.TimedOut;
    }

    /// <summary>终止宿主进程（切换预览 / 隐藏预览 / 主程序退出时调用）</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CloseRequested = null;
        Terminated = null;
        _process.OutputDataReceived -= OnOutputDataReceived;
        _process.Exited -= OnExited;
        try
        {
            if (!_process.HasExited) _process.Kill();
        }
        catch
        {
            // 进程已退出或句柄失效：宿主随父窗口死亡有看门狗兜底，无须上报
        }
        _process.Dispose();
    }

    private void OnOutputDataReceived(object sender, DataReceivedEventArgs e)
    {
        var line = e.Data;
        if (string.IsNullOrWhiteSpace(line)) return;

        if (line == "READY")
        {
            _ready.TrySetResult(PreviewHandlerHostStatus.Ready);
        }
        else if (line == "CLOSE")
        {
            CloseRequested?.Invoke();
        }
        else if (line.StartsWith("FAIL ", StringComparison.Ordinal))
        {
            // FAIL <状态> <消息>
            var parts = line["FAIL ".Length..].Split(' ', 2);
            var status = parts[0] switch
            {
                "NOHANDLER" => PreviewHandlerHostStatus.NoHandler,
                "INIT" => PreviewHandlerHostStatus.InitFailed,
                _ => PreviewHandlerHostStatus.BadArguments
            };
            LastFailureDetail = parts.Length > 1 ? parts[1] : null;
            _ready.TrySetResult(status);
        }
    }

    private void OnExited(object? sender, EventArgs e)
    {
        int code;
        try
        {
            code = _process.ExitCode;
        }
        catch
        {
            code = -1;
        }

        if (!_ready.Task.IsCompleted)
        {
            // 就绪前退出：按退出码归类（宿主约定 0=正常 2=无处理器 3=初始化失败 4=参数无效）
            _ready.TrySetResult(code switch
            {
                2 => PreviewHandlerHostStatus.NoHandler,
                3 => PreviewHandlerHostStatus.InitFailed,
                4 => PreviewHandlerHostStatus.BadArguments,
                _ => PreviewHandlerHostStatus.Crashed
            });
            return;
        }

        Terminated?.Invoke(code == 0 ? PreviewHandlerHostStatus.Exited : PreviewHandlerHostStatus.Crashed);
    }
}
