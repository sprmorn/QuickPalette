namespace QuickPalette.Infrastructure.Scanners;

using System.IO;
using Microsoft.Win32;
using QuickPalette.Domain.Contracts;
using QuickPalette.Domain.Models;
using Serilog;

/// <summary>
/// 注册表扫描器 (App Paths) - 同时枚举 32/64 位注册表视图，正确拆分带引号/带参数的命令行
/// </summary>
public class RegistryScanner : IAppSource
{
    private const string AppPathsKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";

    private readonly ILogger _logger;

    public string SourceId => "Registry";

    public RegistryScanner(ILogger logger)
    {
        _logger = logger;
    }

    public Task<IEnumerable<RawApp>> ScanAsync(CancellationToken ct = default)
    {
        var apps = new List<RawApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // 同时枚举 Registry32 与 Registry64 视图（HKLM/HKCU）
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                ct.ThrowIfCancellationRequested();

                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                {
                    ScanAppPaths(hklm, view, apps, seen, ct);
                }

                using (var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view))
                {
                    ScanAppPaths(hkcu, view, apps, seen, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 取消必须向上抛，不得吞掉
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Registry scan failed");
        }

        _logger.Information("Registry scan found {Count} apps", apps.Count);
        return Task.FromResult<IEnumerable<RawApp>>(apps);
    }

    private void ScanAppPaths(RegistryKey root, RegistryView view, List<RawApp> apps,
        HashSet<string> seen, CancellationToken ct)
    {
        using var key = root.OpenSubKey(AppPathsKey);
        if (key == null)
            return;

        foreach (var subKeyName in key.GetSubKeyNames())
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                using var subKey = key.OpenSubKey(subKeyName);
                if (subKey == null)
                    continue;

                var defaultValue = subKey.GetValue(null)?.ToString();
                if (string.IsNullOrWhiteSpace(defaultValue))
                    continue;

                // 处理带引号的默认值 / 带参数命令行：拆出可执行路径与参数
                var (executable, arguments) = SplitCommandLine(defaultValue);
                if (string.IsNullOrWhiteSpace(executable))
                    continue;

                var fullPath = Environment.ExpandEnvironmentVariables(executable.Trim());

                // 路径失效的条目直接跳过（不产出假条目）
                if (!File.Exists(fullPath))
                {
                    _logger.Debug("App Paths entry target missing, skipped: {Key} -> {Path}", subKeyName, fullPath);
                    continue;
                }

                // 32/64 视图可能给出同一目标，按路径去重
                if (!seen.Add(fullPath))
                    continue;

                apps.Add(new RawApp
                {
                    Name = Path.GetFileNameWithoutExtension(subKeyName),
                    Path = fullPath,
                    Args = arguments,
                    Type = AppType.Exe,
                    SourceId = SourceId
                });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to read registry key: {Key} (view {View})", subKeyName, view);
            }
        }
    }

    /// <summary>
    /// 拆分命令行为（可执行路径, 参数）：
    /// 1. 带引号："C:\Program Files\a.exe" --x → 取引号内为路径；
    /// 2. 不带引号：按空格逐步回退，找到第一个存在的文件即为路径（兼容带空格未加引号的旧数据）；
    /// 3. 均不匹配：整体视为路径（File.Exists 校验交由调用方）。
    /// </summary>
    public static (string Path, string Args) SplitCommandLine(string commandLine)
    {
        var s = commandLine.Trim();
        if (s.Length == 0)
            return (string.Empty, string.Empty);

        if (s.StartsWith('"'))
        {
            var closing = s.IndexOf('"', 1);
            if (closing > 1)
            {
                var path = s[1..closing];
                var args = s[(closing + 1)..].Trim();
                return (path, args);
            }

            // 引号不闭合：去掉首引号按整体处理
            s = s.TrimStart('"');
        }

        // 无引号：先按整体判断，再按空格回退寻找存在的文件
        if (File.Exists(s))
            return (s, string.Empty);

        for (var i = s.IndexOf(' '); i > 0; i = s.IndexOf(' ', i + 1))
        {
            var candidate = s[..i];
            if (File.Exists(candidate))
                return (candidate, s[i..].Trim());
        }

        return (s, string.Empty);
    }
}
