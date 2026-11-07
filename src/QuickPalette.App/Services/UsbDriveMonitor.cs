namespace QuickPalette.App.Services;

using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using Serilog;

/// <summary>
/// U盘小助手：轮询检测可移动磁盘（2 秒一次，零依赖零权限），
/// 发现新盘符即触发提示；支持"立即检测"（含已插入的盘）。
/// </summary>
public sealed class UsbDriveMonitor : IDisposable
{
    private readonly ILogger _logger;
    private readonly DispatcherTimer _timer;
    private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>检测到 U盘（根路径如 "E:\"）</summary>
    public event Action<string>? DriveArrived;

    public UsbDriveMonitor(ILogger logger)
    {
        _logger = logger;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Poll();
    }

    /// <summary>开始监听插入事件（已存在的盘静默登记，只提示之后新插入的）</summary>
    public void Start()
    {
        foreach (var drive in GetRemovableDrives())
        {
            _known.Add(drive);
        }

        _timer.Start();
        _logger.Information("USB assistant monitoring started ({Count} existing drive(s))", _known.Count);
    }

    /// <summary>立即检测：对当前插入的 U盘逐个触发提示（含已知盘）</summary>
    public void CheckNow()
    {
        foreach (var drive in GetRemovableDrives())
        {
            _logger.Information("USB assistant check now: {Drive}", drive);
            DriveArrived?.Invoke(drive);
        }
    }

    private void Poll()
    {
        try
        {
            var current = GetRemovableDrives();
            foreach (var drive in current)
            {
                if (_known.Add(drive))
                {
                    _logger.Information("USB drive arrived: {Drive}", drive);
                    DriveArrived?.Invoke(drive);
                }
            }

            // 清理已拔掉的盘符（重新插入时能再次提示）
            _known.RemoveWhere(d => !current.Contains(d, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "USB poll failed");
        }
    }

    /// <summary>当前插入的可移动磁盘（根路径列表）</summary>
    public static List<string> GetRemovableDrives()
    {
        var list = new List<string>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType == DriveType.Removable && drive.IsReady)
                {
                    list.Add(drive.Name);
                }
            }
        }
        catch
        {
            // 枚举失败按无盘处理
        }

        return list;
    }

    /// <summary>打开 U盘文件夹</summary>
    public static void OpenFolder(string driveRoot)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", driveRoot));
    }

    /// <summary>
    /// 弹出 U盘：通过资源管理器的"弹出"动词（用户态安全弹出）。
    /// 成功与否都建议关闭提示窗（弹出是异步过程）。
    /// </summary>
    public static bool TryEject(string driveRoot, ILogger logger)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null)
            {
                logger.Warning("Shell.Application unavailable for eject");
                return false;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic? drives = shell.Namespace(17); // 我的电脑（驱动器集合）
            var name = driveRoot.TrimEnd('\\') + "\\";
            dynamic? item = drives?.ParseName(name);
            if (item == null)
            {
                logger.Warning("Eject target not found: {Drive}", driveRoot);
                return false;
            }

            item.InvokeVerb("Eject");
            logger.Information("USB eject invoked: {Drive}", driveRoot);
            return true;
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "USB eject failed: {Drive}", driveRoot);
            return false;
        }
    }

    public void Dispose() => _timer.Stop();
}
