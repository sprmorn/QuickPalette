namespace QuickPalette.Infrastructure.Launchers;

using System.Diagnostics;
using System.IO;
using QuickPalette.Domain.Contracts;
using QuickPalette.Domain.Models;
using Serilog;

/// <summary>
/// EXE 启动器 - 启动可执行文件 / 快捷方式目标，支持提权与"打开所在目录"
/// </summary>
public class ExeLauncher : ILauncher
{
    private readonly ILogger _logger;

    public ExeLauncher(ILogger logger)
    {
        _logger = logger;
    }

    public bool CanLaunch(AppItem item)
    {
        return item.Type == AppType.Exe || item.Type == AppType.Lnk;
    }

    public Task<LaunchResult> LaunchAsync(AppItem item, LaunchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Task.FromResult(LaunchCore(item, options));
    }

    private LaunchResult LaunchCore(AppItem item, LaunchOptions? options)
    {
        // 路径为空/非法字符 → InvalidPath
        if (string.IsNullOrWhiteSpace(item.Path))
        {
            _logger.Warning("Launch rejected: {Name} has empty path", item.Name);
            return LaunchResult.Fail("启动路径为空", LaunchErrorKind.InvalidPath);
        }

        if (item.Path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            _logger.Warning("Launch rejected: {Name} path contains invalid characters: {Path}", item.Name, item.Path);
            return LaunchResult.Fail($"路径包含非法字符: {item.Path}", LaunchErrorKind.InvalidPath);
        }

        // 路径失效必须给 NotFound（供"重新定位"引导）
        if (!File.Exists(item.Path))
        {
            _logger.Warning("Launch failed: {Name} target not found: {Path}", item.Name, item.Path);
            return LaunchResult.Fail($"文件不存在: {item.Path}", LaunchErrorKind.NotFound);
        }

        try
        {
            var openDirectory = options?.OpenDirectory == true;

            ProcessStartInfo startInfo;
            if (openDirectory)
            {
                // 打开所在目录：走系统 API（explorer /select 在部分系统会错误落到默认文件夹）
                var revealed = ExplorerHelper.RevealInExplorer(item.Path,
                    msg => _logger.Warning("Reveal in explorer degraded: {Msg}", msg));
                return revealed
                    ? LaunchResult.Ok(null)
                    : LaunchResult.Fail("无法打开所在目录", LaunchErrorKind.InvalidPath);
            }
            else
            {
                var workDir = ResolveWorkingDirectory(item);
                startInfo = new ProcessStartInfo
                {
                    FileName = item.Path,
                    Arguments = item.Args ?? string.Empty,
                    WorkingDirectory = workDir,
                    UseShellExecute = true
                };

                // 仅在真正启动目标时才允许提权
                if (options?.RunAsAdmin == true || item.RunAsAdmin)
                {
                    startInfo.Verb = "runas";
                }
            }

            using var process = Process.Start(startInfo);
            _logger.Information("Launched: {Name} ({Path}){Mode}", item.Name, item.Path,
                openDirectory ? " [open-directory]" : string.Empty);

            return LaunchResult.Ok(process?.Id);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            var kind = LaunchErrorMapper.FromWin32(ex.NativeErrorCode);
            if (ex.NativeErrorCode == 1223)
            {
                _logger.Information("User cancelled UAC for: {Name}", item.Name);
                return LaunchResult.Fail("用户取消了管理员权限请求", kind);
            }

            _logger.Error(ex, "Win32 failure launching: {Name} ({Path}), code={Code}", item.Name, item.Path, ex.NativeErrorCode);
            return LaunchResult.Fail($"启动失败 (0x{ex.NativeErrorCode:X}): {ex.Message}", kind);
        }
        catch (Exception ex)
        {
            var kind = LaunchErrorMapper.Classify(ex);
            _logger.Error(ex, "Failed to launch: {Name} ({Path})", item.Name, item.Path);
            return LaunchResult.Fail(ex.Message, kind);
        }
    }

    /// <summary>
    /// 解析工作目录：显式 WorkDir 优先（存在时），否则取目标所在目录
    /// </summary>
    private static string ResolveWorkingDirectory(AppItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.WorkDir) && Directory.Exists(item.WorkDir))
            return item.WorkDir;

        try
        {
            var dir = Path.GetDirectoryName(item.Path);
            return string.IsNullOrEmpty(dir) ? string.Empty : dir;
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }
}
