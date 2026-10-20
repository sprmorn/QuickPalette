namespace QuickPalette.Infrastructure.Scanners;

using System.Reflection;
using System.Runtime.InteropServices;
using QuickPalette.Domain.Contracts;
using QuickPalette.Domain.Models;
using Serilog;

/// <summary>
/// UWP 扫描器 - 枚举 shell:AppsFolder（Shell.Application）产出 UWP 应用条目。
/// COM 全量释放；任何失败降级返回空列表并记日志，绝不抛出到调用方。
/// </summary>
public class UwpScanner : IAppSource
{
    /// <summary>CSIDL_APPS = 0x22，即 shell:AppsFolder 虚拟目录</summary>
    private const int CsidlAppsFolder = 0x22;

    private readonly ILogger _logger;

    public string SourceId => "Uwp";

    public UwpScanner(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 扫描 shell:AppsFolder。Shell COM 为套间线程模型，放入后台 STA 线程执行并包装为 Task。
    /// </summary>
    public Task<IEnumerable<RawApp>> ScanAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            _logger.Warning("UWP 扫描仅支持 Windows，降级返回空列表");
            return Task.FromResult<IEnumerable<RawApp>>(Array.Empty<RawApp>());
        }

        var tcs = new TaskCompletionSource<IEnumerable<RawApp>>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                tcs.SetResult(ScanCore(ct));
            }
            catch (OperationCanceledException oce)
            {
                tcs.TrySetCanceled(oce.CancellationToken);
            }
            catch (Exception ex)
            {
                // 兜底：任何异常降级为空列表（ScanCore 内部已尽量处理）
                _logger.Error(ex, "UWP 扫描异常，降级返回空列表");
                tcs.TrySetResult(Array.Empty<RawApp>());
            }
        })
        {
            IsBackground = true,
            Name = "UwpScanner-STA"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private List<RawApp> ScanCore(CancellationToken ct)
    {
        var apps = new List<RawApp>();
        using var com = new ComScope();

        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null)
            {
                _logger.Warning("Shell.Application 不可用，UWP 扫描降级返回空列表");
                return apps;
            }

            dynamic shell = com.Track((object)Activator.CreateInstance(shellType)!);

            // Namespace(0x22) → shell:AppsFolder
            dynamic folder = com.Track((object)shell.Namespace(CsidlAppsFolder));
            dynamic items = com.Track((object)folder.Items());

            var count = (int)items.Count;
            for (var i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    dynamic item = com.Track((object)items.Item(i));

                    object? nameObj = item.Name;
                    object? pathObj = item.Path;
                    var name = nameObj as string ?? string.Empty;
                    var path = pathObj as string ?? string.Empty;

                    // AppsFolder 下 Path 即 AUMID；取不到则跳过（不产出假条目）
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        _logger.Debug("AppsFolder item has no AUMID, skipped: {Name}", name);
                        continue;
                    }

                    apps.Add(new RawApp
                    {
                        Name = string.IsNullOrWhiteSpace(name) ? path : name,
                        Path = path,
                        Aumid = path,
                        IconPath = TryGetIconLocation(com, (object)item),
                        Type = AppType.Uwp,
                        SourceId = SourceId
                    });
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "AppsFolder item parse failed, skipped at index {Index}", i);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 失败降级：返回已收集部分之前整体清空并返回空列表（语义：失败=空）
            _logger.Warning(ex, "UWP 扫描失败，降级返回空列表");
            apps.Clear();
            return new List<RawApp>();
        }

        _logger.Information("UWP scan found {Count} apps", apps.Count);
        return apps;
    }

    /// <summary>
    /// 尝试取图标位置：AppsFolder 下 Win32 注册应用表现为链接项，可从 ShellLinkObject.GetIconLocation 取；
    /// 纯 UWP 应用无链接图标信息，返回空串（图标由 IconService 侧兜底）。
    /// </summary>
    private string TryGetIconLocation(ComScope com, dynamic item)
    {
        try
        {
            if (!(bool)item.IsLink)
                return string.Empty;

            dynamic? linkDyn = item.GetLink;
            if (linkDyn == null)
                return string.Empty;

            object link = com.Track((object)linkDyn);

            // ShellLinkObject.GetIconLocation([out] BSTR* path, [out] int* index)：
            // 用反射 InvokeMember 调 IDispatch，out 参数通过 args 数组回传
            object[] args = { string.Empty, 0 };
            link.GetType().InvokeMember(
                "GetIconLocation",
                BindingFlags.InvokeMethod,
                binder: null,
                target: link,
                args: args);

            var iconPath = args[0] as string;
            return string.IsNullOrWhiteSpace(iconPath) ? string.Empty : iconPath;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "取 UWP/链接项图标位置失败（忽略）");
            return string.Empty;
        }
    }

    #region COM 资源释放

    /// <summary>
    /// COM 作用域：统一跟踪并以 Marshal.FinalReleaseComObject 释放（同一 RCW 只释放一次）
    /// </summary>
    private sealed class ComScope : IDisposable
    {
        private readonly List<object> _tracked = new();

        public T Track<T>(T comObject) where T : class
        {
            ArgumentNullException.ThrowIfNull(comObject);

            if (Marshal.IsComObject(comObject))
            {
                foreach (var existing in _tracked)
                {
                    if (ReferenceEquals(existing, comObject))
                        return comObject;
                }

                _tracked.Add(comObject);
            }

            return comObject;
        }

        public void Dispose()
        {
            for (var i = _tracked.Count - 1; i >= 0; i--)
            {
                var comObject = _tracked[i];
                try
                {
                    if (Marshal.IsComObject(comObject))
                        Marshal.FinalReleaseComObject(comObject);
                }
                catch (Exception)
                {
                    // 释放失败不影响业务
                }
            }

            _tracked.Clear();
        }
    }

    #endregion
}
