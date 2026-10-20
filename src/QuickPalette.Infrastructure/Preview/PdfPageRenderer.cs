using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace QuickPalette.Infrastructure.Preview;

/// <summary>PDF 渲染结果：总页数 + 前若干页的 PNG 字节</summary>
public sealed record PdfRenderResult(int PageCount, IReadOnlyList<byte[]> Pages);

/// <summary>
/// PDF 逐页<b>图文渲染</b>（不再做文本抽取，避免 CID 字体乱码）：
/// 经 Windows PowerShell 的 WinRT 桥调用系统自带 PDF 引擎
/// （Windows.Data.Pdf —— 与 Edge/系统阅读器同款），把每页渲染成 PNG。
///
/// 为什么走 PowerShell 桥：渲染引擎是 WinRT 组件，.NET 9 离线环境无 WinRT 投影包；
/// Windows PowerShell 5.1 自带 WinRT 互操作且系统必装，是零依赖的稳定通道。
/// 一次调用渲染全部请求页（进程冷启动 ~1.5s），失败返回 null 由调用方降级文本模式。
/// </summary>
public static class PdfPageRenderer
{
    /// <summary>默认最多渲染页数（内存/耗时上限）</summary>
    public const int DefaultMaxPages = 8;

    /// <summary>默认渲染宽度（像素，高度按页面比例自适应）</summary>
    public const int DefaultWidthPx = 900;

    private static string PowerShellPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>渲染桥是否可用（Windows PowerShell 存在）</summary>
    public static bool IsAvailable => File.Exists(PowerShellPath);

    /// <summary>
    /// 渲染 PDF 前 N 页为 PNG 字节。失败/取消返回 null（调用方降级）。
    /// 临时文件用后即删；输出按页读入内存（每页 PNG 约 50-200KB）。
    /// </summary>
    public static PdfRenderResult? RenderPages(
        string pdfPath,
        int maxPages = DefaultMaxPages,
        int widthPx = DefaultWidthPx,
        CancellationToken ct = default)
    {
        if (!IsAvailable || !File.Exists(pdfPath)) return null;

        var outDir = Path.Combine(Path.GetTempPath(), "QuickPalette", "preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        try
        {
            var script = BuildScript(pdfPath, outDir, maxPages, widthPx);
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

            var psi = new ProcessStartInfo
            {
                FileName = PowerShellPath,
                Arguments = "-NoProfile -NonInteractive -EncodedCommand " + encoded,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process == null) return null;

            using var cancelReg = ct.Register(() =>
            {
                try { process.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
            });

            // 先等退出再读输出（输出量极小不会撑满管道缓冲；先阻塞读会与卡住的子进程互相死锁）
            if (!process.WaitForExit(60_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
                return null;
            }
            if (ct.IsCancellationRequested) return null;

            var stdout = process.StandardOutput.ReadToEnd();

            var pageCount = ParsePageCount(stdout);
            var pages = new List<byte[]>();
            for (var i = 1; i <= maxPages; i++)
            {
                var file = Path.Combine(outDir, $"page-{i}.png");
                if (!File.Exists(file)) break;
                pages.Add(File.ReadAllBytes(file));
            }

            return pages.Count == 0 ? null : new PdfRenderResult(pageCount, pages);
        }
        catch
        {
            return null; // 任何桥接失败 → 调用方降级文本模式
        }
        finally
        {
            try { Directory.Delete(outDir, recursive: true); } catch { /* 清理失败忽略 */ }
        }
    }

    /// <summary>
    /// 构造渲染桥脚本（参数以单引号字面量烘入，引号成对转义；
    /// 经 -EncodedCommand 传递无命令行解析/执行策略问题）。公开供单元测试校验转义。
    /// </summary>
    public static string BuildScript(string pdfPath, string outDir, int maxPages, int widthPx)
    {
        static string Lit(string s) => "'" + s.Replace("'", "''") + "'";

        return
            "Add-Type -AssemblyName System.Runtime.WindowsRuntime\n" +
            "$Pdf = " + Lit(pdfPath) + "\n" +
            "$OutDir = " + Lit(outDir) + "\n" +
            "$MaxPages = " + maxPages + "\n" +
            "$Width = " + widthPx + "\n" +
            "[void][Windows.Data.Pdf.PdfDocument, Windows.Data.Pdf, ContentType=WindowsRuntime]\n" +
            "$null = [Windows.Storage.StorageFile, Windows.Storage, ContentType=WindowsRuntime]\n" +
            "$methods = [System.WindowsRuntimeSystemExtensions].GetMethods()\n" +
            "$asTaskGeneric = ($methods | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]\n" +
            "$asTaskAction = ($methods | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncAction' })[0]\n" +
            "function Await($t, $rt) {\n" +
            "    $n = $asTaskGeneric.MakeGenericMethod($rt).Invoke($null, @($t))\n" +
            // 零方法绑定等待：Task.Wait(int) 在 PS 5.1 重载解析上偶发"值不在预期的范围内"，用 IsCompleted 轮询规避
            "    $sw = [System.Diagnostics.Stopwatch]::StartNew()\n" +
            "    while (-not $n.IsCompleted -and $sw.ElapsedMilliseconds -lt 30000) { Start-Sleep -Milliseconds 50 }\n" +
            "    if (-not $n.IsCompleted) { throw 'timeout' }\n" +
            "    if ($n.IsFaulted) { throw $n.Exception }\n" +
            "    $n.Result\n" +
            "}\n" +
            "$file = $null\n" +
            // 纯 .NET 读文件 + 内存流喂给 PDF 引擎（不经 WinRT 文件代理：受限令牌/网络盘场景更稳）
            "$bytes = [System.IO.File]::ReadAllBytes($Pdf)\n" +
            "$mem = New-Object Windows.Storage.Streams.InMemoryRandomAccessStream\n" +
            "$writer = New-Object Windows.Storage.Streams.DataWriter $mem\n" +
            "$null = $writer.WriteBytes($bytes)\n" +
            // StoreAsync 返回 WinRT IAsyncOperation（无 IsCompleted 属性）：先经 AsTask 包成 Task 再轮询
            "$st = $asTaskGeneric.MakeGenericMethod([UInt32]).Invoke($null, @($writer.StoreAsync()))\n" +
            "$sw3 = [System.Diagnostics.Stopwatch]::StartNew()\n" +
            "while (-not $st.IsCompleted -and $sw3.ElapsedMilliseconds -lt 15000) { Start-Sleep -Milliseconds 20 }\n" +
            "if (-not $st.IsCompleted) { throw 'stream timeout' }\n" +
            "$null = $writer.DetachStream()\n" +
            "$mem.Seek(0)\n" +
            "$doc = Await ([Windows.Data.Pdf.PdfDocument]::LoadFromStreamAsync($mem)) ([Windows.Data.Pdf.PdfDocument])\n" +
            "Write-Output ('PAGES=' + $doc.PageCount)\n" +
            "$pages = [Math]::Min($doc.PageCount, $MaxPages)\n" +
            "for ($i = 0; $i -lt $pages; $i++) {\n" +
            "    $page = $doc.GetPage($i)\n" +
            "    $size = $page.Size\n" +
            "    $opts = New-Object Windows.Data.Pdf.PdfPageRenderOptions\n" +
            "    $opts.DestinationWidth = [float]$Width\n" +
            "    $opts.DestinationHeight = [float]($Width * $size.Height / $size.Width)\n" +
            "    $stream = New-Object Windows.Storage.Streams.InMemoryRandomAccessStream\n" +
            "    $t = $asTaskAction.Invoke($null, @($page.RenderToStreamAsync($stream, $opts)))\n" +
            "    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()\n" +
            "    while (-not $t.IsCompleted -and $sw2.ElapsedMilliseconds -lt 30000) { Start-Sleep -Milliseconds 50 }\n" +
            "    if (-not $t.IsCompleted) { throw 'render timeout' }\n" +
            "    if ($t.IsFaulted) { throw $t.Exception }\n" +
            "    $stream.Seek(0)\n" +
            "    $net = [System.IO.WindowsRuntimeStreamExtensions]::AsStreamForRead($stream)\n" +
            "    $fs = [System.IO.File]::Create((Join-Path $OutDir ('page-' + ($i+1) + '.png')))\n" +
            "    $net.CopyTo($fs); $fs.Close(); $net.Close()\n" +
            "    $page.Dispose()\n" +
            "    Write-Output ('PAGE ' + ($i+1) + ' OK')\n" +
            "}\n";
    }

    /// <summary>从渲染桥输出解析总页数（PAGES=n）；解析不到返回 0</summary>
    public static int ParsePageCount(string output)
    {
        var m = Regex.Match(output ?? string.Empty, @"PAGES=(\d+)");
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : 0;
    }
}
