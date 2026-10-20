namespace QuickPalette.Infrastructure.Launchers;

using QuickPalette.Domain.Contracts;
using QuickPalette.Domain.Models;
using Serilog;

/// <summary>
/// 启动器管理器 - 根据应用类型选择合适的启动器
/// </summary>
public class LauncherManager
{
    private readonly List<ILauncher> _launchers = new();
    private readonly ILogger _logger;

    public LauncherManager(ILogger logger)
    {
        _logger = logger;

        // 注册默认启动器
        RegisterLauncher(new ExeLauncher(logger));
        RegisterLauncher(new UrlLauncher(logger));
        RegisterLauncher(new UwpLauncher(logger));
        RegisterLauncher(new CommandLauncher(logger));
    }

    public void RegisterLauncher(ILauncher launcher)
    {
        _launchers.Add(launcher);
        _logger.Debug("Registered launcher: {Type}", launcher.GetType().Name);
    }

    /// <summary>
    /// 启动应用
    /// </summary>
    public async Task<LaunchResult> LaunchAsync(AppItem item, LaunchOptions? options = null)
    {
        var launcher = _launchers.FirstOrDefault(l => l.CanLaunch(item));
        if (launcher == null)
        {
            _logger.Warning("No launcher found for: {Name} (Type: {Type})", item.Name, item.Type);
            return LaunchResult.Fail($"不支持的应用类型: {item.Type}");
        }

        return await launcher.LaunchAsync(item, options);
    }
}
