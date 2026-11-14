namespace QuickPalette.App.Recorder;

using System.IO;
using System.Windows;
using QuickPalette.App.Services;
using QuickPalette.Domain.Models;
using QuickPalette.Recorder;
using Serilog;

/// <summary>
/// 录屏编排：选区 → ①3/2/1 倒数 + ③区域高亮遮罩 → RecordingSession（WGC+MF）→
/// 细条条（计时/音频/暂停/停止）→ ②录制写入临时缓存，编辑窗点“保存”才落正式目录 →
/// 编辑窗（预览/倍速/裁剪 → 复制/保存）。
/// </summary>
public sealed class RecordingService
{
    private readonly ILogger _logger;
    private Settings _settings;

    private RecordingSession? _session;
    private RecordingBarWindow? _bar;
    private CountdownOverlayWindow? _overlay;
    private bool _busy;

    public RecordingService(ILogger logger, Settings settings)
    {
        _logger = logger;
        _settings = settings;
    }

    public void UpdateSettings(Settings settings) => _settings = settings;

    /// <summary>程序触发停止（测试钩子用）</summary>
    public void RequestStop() => _bar?.TriggerStop();

    /// <summary>开始录制选区（物理像素矩形）：先倒数遮罩，倒数毕开录</summary>
    public void Start(System.Drawing.Rectangle region)
    {
        if (_busy)
        {
            return;
        }

        try
        {
            _busy = true;

            // 监视器句柄 = 选区中心所在屏
            var center = new System.Drawing.Point(region.X + region.Width / 2, region.Y + region.Height / 2);
            var hmon = NativeMethods.MonitorFromPoint(center, 2 /* MONITOR_DEFAULTTONEAREST */);
            var monitorBounds = NativeMethods.MonitorBounds(hmon);

            // ② 录制写入临时缓存——用户点“保存”才落正式目录（避免未保存就出现成品文件）
            var dir = Path.Combine(Path.GetTempPath(), "轻启录制缓存");
            Directory.CreateDirectory(dir);
            var name = FileNameFor(_settings.RecordFileNameTemplate) + ".mp4";
            var output = Path.Combine(dir, name);

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

            // ①③ 倒数 + 区域高亮遮罩（对录屏隐形）
            _overlay = new CountdownOverlayWindow(monitorBounds, region);
            _overlay.CountdownCompleted += () => BeginRecording(options, output);
            _overlay.Show();

            _logger.Information("Countdown started: {Region} -> {Output}", region, output);
        }
        catch (Exception ex)
        {
            _busy = false;
            _logger.Error(ex, "Start recording failed");
            MessageBox.Show($"录屏启动失败：{ex.Message}", "录屏", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>倒数结束：开录 + 显示细条条（遮罩保留至停止）</summary>
    private void BeginRecording(RecordingOptions options, string output)
    {
        try
        {
            _session = new RecordingSession(options, _logger);
            _bar = new RecordingBarWindow(_session, _settings.RecordMicEnabled, _settings.RecordSystemAudioEnabled);
            _bar.StopRequested += () => _ = StopAsync();
            _bar.Closed += (_, _) =>
            {
                if (_session != null)
                {
                    _ = StopAsync();
                }
            };
            _bar.Show();

            _logger.Information("Recording service started: {Output}", output);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Begin recording failed");
            try
            {
                _overlay?.Close();
            }
            catch
            {
            }

            _overlay = null;
            _busy = false;
            MessageBox.Show($"录屏启动失败：{ex.Message}", "录屏", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task StopAsync()
    {
        try
        {
            var session = _session;
            var bar = _bar;
            var overlay = _overlay;
            _session = null;
            _bar = null;
            _overlay = null;
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
            }

            try
            {
                overlay?.Close();
            }
            catch
            {
            }

            // 编辑窗（预览/倍速/裁剪/复制/保存）——保存时才落正式目录
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
