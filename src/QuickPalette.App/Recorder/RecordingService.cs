namespace QuickPalette.App.Recorder;

using System.IO;
using System.Windows;
using QuickPalette.App.Services;
using QuickPalette.Domain.Models;
using QuickPalette.Recorder;
using Serilog;

/// <summary>
/// 录屏编排：选区 → RecordingSession（WGC+MF）→ 细条条（计时/音频/暂停/停止）→
/// 编辑窗（预览/倍速/裁剪 → 复制/保存）。
/// </summary>
public sealed class RecordingService
{
    private readonly ILogger _logger;
    private Settings _settings;

    private RecordingSession? _session;
    private RecordingBarWindow? _bar;
    private bool _busy;

    public RecordingService(ILogger logger, Settings settings)
    {
        _logger = logger;
        _settings = settings;
    }

    public void UpdateSettings(Settings settings) => _settings = settings;

    /// <summary>程序触发停止（测试钩子用）</summary>
    public void RequestStop() => _bar?.TriggerStop();

    /// <summary>开始录制选区（物理像素矩形）</summary>
    public void Start(System.Drawing.Rectangle region)
    {
        if (_busy)
        {
            return;
        }

        var output = string.Empty;
        try
        {
            _busy = true;

            // 监视器句柄 = 选区中心所在屏
            var center = new System.Drawing.Point(region.X + region.Width / 2, region.Y + region.Height / 2);
            var hmon = NativeMethods.MonitorFromPoint(
                new System.Drawing.Point(center.X, center.Y), 2 /* MONITOR_DEFAULTTONEAREST */);

            var dir = string.IsNullOrWhiteSpace(_settings.RecordSaveDir)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "轻启录制")
                : _settings.RecordSaveDir;
            Directory.CreateDirectory(dir);
            var name = FileNameFor(_settings.RecordFileNameTemplate) + ".mp4";
            output = Path.Combine(dir, name);

            var monitorBounds = NativeMethods.MonitorBounds(hmon);
            var options = new RecordingOptions(
                hmon,
                region.X - monitorBounds.X,
                region.Y - monitorBounds.Y,
                region.Width,
                region.Height,
                monitorBounds.Width,
                monitorBounds.Height,
                Math.Clamp(_settings.RecordFps, 15, 30),
                Math.Clamp(region.Width * region.Height * 5, 1_500_000, 12_000_000),
                _settings.RecordMicEnabled,
                _settings.RecordSystemAudioEnabled,
                output);

            _session = new RecordingSession(options, _logger);
            _bar = new RecordingBarWindow(_session, _settings.RecordMicEnabled, _settings.RecordSystemAudioEnabled);
            _bar.StopRequested += () => _ = StopAsync();
            _bar.Closed += (_, _) =>
            {
                // 关闭细条条等同停止（若仍在录）
                if (_session != null)
                {
                    _ = StopAsync();
                }
            };
            _bar.Show();

            _logger.Information("Recording service started: {Region} -> {Output}", region, output);
        }
        catch (Exception ex)
        {
            _busy = false;
            _logger.Error(ex, "Start recording failed");
            try
            {
                // 失败时清掉创建出的 0KB 残渣文件
                if (File.Exists(output) && new FileInfo(output).Length == 0)
                {
                    File.Delete(output);
                }
            }
            catch
            {
                // 忽略
            }

            MessageBox.Show($"录屏启动失败：{ex.Message}", "录屏", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task StopAsync()
    {
        try
        {
            var session = _session;
            var bar = _bar;
            _session = null;
            _bar = null;
            _busy = false;
            if (session == null)
            {
                return;
            }

            var path = session.Stop();
            session.Dispose();
            try
            {
                bar?.Close();
            }
            catch
            {
                // 忽略
            }

            // 编辑窗（预览/倍速/裁剪/复制/保存）
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var edit = new RecordingEditWindow(_logger, path, _settings);
                edit.Show();
            });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Stop recording failed");
        }
    }

    private static string FileNameFor(string template)
    {
        var name = template;
        foreach (var (token, value) in new (string, string)[]
        {
            ("yyyyMMdd", DateTime.Now.ToString("yyyyMMdd")),
            ("HHmmss", DateTime.Now.ToString("HHmmss")),
        })
        {
            name = name.Replace(token, value);
        }

        return string.IsNullOrWhiteSpace(name) ? $"录屏_{DateTime.Now:yyyyMMdd_HHmmss}" : name;
    }

    /// <summary>Win32 小工具（MonitorFromPoint / MonitorBounds）</summary>
    private static class NativeMethods
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct NativeRect
        {
            public int Left, Top, Right, Bottom;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct MonitorInfoEx
        {
            public int cbSize;
            public NativeRect rcMonitor;
            public NativeRect rcWork;
            public uint dwFlags;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(System.Drawing.Point pt, uint flags);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx info);

        public static System.Drawing.Rectangle MonitorBounds(IntPtr hMonitor)
        {
            var info = new MonitorInfoEx { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfoEx>() };
            return GetMonitorInfo(hMonitor, ref info)
                ? System.Drawing.Rectangle.FromLTRB(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom)
                : System.Drawing.Rectangle.Empty;
        }
    }
}
