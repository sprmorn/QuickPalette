namespace QuickPalette.Infrastructure.Launchers;

using System.Diagnostics;
using System.IO;
using QuickPalette.Domain.Contracts;
using QuickPalette.Domain.Models;
using Serilog;

/// <summary>
/// 命令行启动器 - 通过 cmd.exe /c 执行命令，正确处理引号/空格转义
/// </summary>
public class CommandLauncher : ILauncher
{
    private readonly ILogger _logger;

    public CommandLauncher(ILogger logger)
    {
        _logger = logger;
    }

    public bool CanLaunch(AppItem item)
    {
        return item.Type == AppType.Command;
    }

    public Task<LaunchResult> LaunchAsync(AppItem item, LaunchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        // "打开所在目录"：命令型项目没有本地目录，明确告知而不是把命令执行起来
        if (options?.OpenDirectory == true)
        {
            _logger.Information("OpenDirectory requested for command item (no local directory): {Name}", item.Name);
            return Task.FromResult(LaunchResult.Fail("命令项目没有本地目录，无法打开文件位置", LaunchErrorKind.InvalidPath));
        }
        return Task.FromResult(LaunchCore(item));
    }

    private LaunchResult LaunchCore(AppItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Path))
        {
            _logger.Warning("Command launch rejected: {Name} has empty command", item.Name);
            return LaunchResult.Fail("命令为空", LaunchErrorKind.InvalidPath);
        }

        try
        {
            // 工作目录不存在时回退，避免 Win32Exception(ERROR_DIRECTORY)
            var workDir = item.WorkDir;
            if (!string.IsNullOrWhiteSpace(workDir) && !Directory.Exists(workDir))
            {
                _logger.Warning("Working directory not found, fallback to default: {WorkDir}", workDir);
                workDir = string.Empty;
            }

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = BuildCmdArguments(item.Path, item.Args),
                UseShellExecute = true,
                WorkingDirectory = workDir ?? string.Empty
            });

            _logger.Information("Launched command: {Name}", item.Name);
            return LaunchResult.Ok(process?.Id);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            var kind = LaunchErrorMapper.FromWin32(ex.NativeErrorCode);
            _logger.Error(ex, "Win32 failure launching command: {Name}, code={Code}", item.Name, ex.NativeErrorCode);
            return LaunchResult.Fail($"命令执行失败 (0x{ex.NativeErrorCode:X}): {ex.Message}", kind);
        }
        catch (Exception ex)
        {
            var kind = LaunchErrorMapper.Classify(ex);
            _logger.Error(ex, "Failed to launch command: {Name}", item.Name);
            return LaunchResult.Fail(ex.Message, kind);
        }
    }

    /// <summary>
    /// 构造 cmd.exe 的参数串（纯函数，可独立测试）。
    ///
    /// cmd 的引号规则（见 cmd /?）：若不满足"恰好两个引号且中间是可执行文件名"的保留条件，
    /// 则剥去整条命令行的首、尾各一个引号，内部引号原样保留。
    /// 因此把整条命令包进一对引号即可让内嵌引号/空格原样传给命令：
    ///   /c "myapp --name "John Doe""  →  命令实际收到  myapp --name "John Doe"
    /// 若命令本身是带空格的路径（含 '\'），先给命令自身加引号再外包一层，避免 cmd 把路径按空格截断。
    /// </summary>
    /// <param name="command">命令（item.Path），可为可执行路径或 shell 命令行</param>
    /// <param name="args">附加参数（item.Args），原样拼接，保留用户自带的引号</param>
    public static string BuildCmdArguments(string command, string? args = null)
    {
        ArgumentNullException.ThrowIfNull(command);

        var cmd = command.Trim();
        var rest = args?.Trim() ?? string.Empty;

        // 命令是带空格的路径且未被引号包裹 → 自身为命令加引号（支持 "C:\Program Files\a.exe"）
        if (cmd.Contains(' ') && cmd.Contains('\\') && !cmd.StartsWith('"'))
        {
            cmd = "\"" + cmd + "\"";
        }

        var inner = rest.Length == 0 ? cmd : cmd + " " + rest;
        return "/c \"" + inner + "\"";
    }
}
