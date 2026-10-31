namespace QuickPalette.App.Services;

using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using QuickPalette.App.Screenshot;
using QuickPalette.Domain.Models;
using QuickPalette.Screenshot.Capture;
using QuickPalette.Screenshot.Model;
using QuickPalette.Screenshot.Output;
using Serilog;

/// <summary>
/// 截图模块编排：捕获 → 覆盖层（缓存复用）→ 复制 / 保存 / 钉图。
/// 独立于搜索核心：任何异常只影响截图流程，不拖垮主面板。
/// </summary>
public sealed class ScreenshotService : IDisposable
{
    private readonly ILogger _logger;
    private readonly List<OverlayWindow> _overlays = new();
    private readonly List<PinWindow> _pins = new();
    private Settings _settings;
    private bool _active;

    public ScreenshotService(ILogger logger, Settings settings)
    {
        _logger = logger;
        _settings = settings ?? new Settings();
    }

    /// <summary>设置保存后热更新</summary>
    public void UpdateSettings(Settings settings) => _settings = settings ?? _settings;

    /// <summary>开始截图（Alt+D / &gt;snap / 托盘入口）</summary>
    public void StartCapture()
    {
        try
        {
            if (_active)
            {
                return;
            }

            var monitors = ScreenCaptureService.GetMonitors();
            if (monitors.Count == 0)
            {
                _logger.Warning("No monitor found for capture");
                return;
            }

            var captures = ScreenCaptureService.CaptureAll();
            EnsureOverlays(monitors.Count);

            var config = new OverlayWindow.OverlayConfig(
                SmartSelectEnabled: _settings.ScreenshotSmartSelect,
                DefaultTool: ParseTool(_settings.ScreenshotDefaultTool),
                DefaultColor: RgbaColor.Default,
                DefaultWidth: 3,
                MosaicGranularity: Math.Clamp(_settings.ScreenshotMosaicGranularity, 2, 64),
                ColorFormat: ParseColorFormat(_settings.ScreenshotColorFormat));

            for (var i = 0; i < monitors.Count && i < captures.Count; i++)
            {
                _overlays[i].Prepare(captures[i].Bitmap, monitors[i], config);
                _overlays[i].Show();
            }

            // 焦点给光标所在屏的覆盖层
            var cursorMonitor = ScreenCaptureService.MonitorIndexFromPixelPoint(monitors, GetCursorPixel());
            _overlays[Math.Clamp(cursorMonitor, 0, _overlays.Count - 1)].Activate();

            foreach (var capture in captures)
            {
                capture.Dispose(); // Prepare 已复制为冻结位图
            }

            _active = true;
            _logger.Information("Screenshot capture started on {Count} monitor(s)", monitors.Count);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Start screenshot capture failed");
        }
    }

    private void EnsureOverlays(int count)
    {
        while (_overlays.Count < count)
        {
            var overlay = new OverlayWindow(_logger);
            overlay.Copied += OnCopied;
            overlay.Saved += OnSaved;
            overlay.Pinned += OnPinned;
            overlay.Exited += HideAll;
            _overlays.Add(overlay);
        }
    }

    private void OnCopied(BitmapSource image)
    {
        _ = image;
        _logger.Information("Screenshot copied to clipboard");
        HideAll();
    }

    private void OnSaved(BitmapSource image)
    {
        SaveToFile(image);
        HideAll();
    }

    private void OnPinned(BitmapSource image)
    {
        CreatePin(image);
        HideAll();
    }

    private void HideAll()
    {
        foreach (var overlay in _overlays)
        {
            overlay.Hide();
        }

        _active = false;
    }

    private void SaveToFile(BitmapSource image)
    {
        try
        {
            var dir = string.IsNullOrWhiteSpace(_settings.ScreenshotSaveDir)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "轻启截图")
                : _settings.ScreenshotSaveDir;
            Directory.CreateDirectory(dir);

            var stamp = DateTime.Now;
            var path = Path.Combine(dir, FileNameTemplate.Build(_settings.ScreenshotFileNameTemplate, stamp) + ".png");
            if (File.Exists(path))
            {
                path = Path.Combine(dir,
                    FileNameTemplate.Build(_settings.ScreenshotFileNameTemplate, stamp) + "_" + Guid.NewGuid().ToString("N")[..4] + ".png");
            }

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var fs = new FileStream(path, FileMode.CreateNew);
            encoder.Save(fs);

            _logger.Information("Screenshot saved: {Path}", path);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Screenshot save failed");
        }
    }

    private void CreatePin(BitmapSource image)
    {
        try
        {
            var pin = new PinWindow(image, _logger);
            pin.Closed += (_, _) => _pins.Remove(pin);
            _pins.Add(pin);
            pin.Show();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Create pin failed");
        }
    }

    private static AnnotationKind ParseTool(string? tool) => tool switch
    {
        "ellipse" => AnnotationKind.Ellipse,
        "arrow" => AnnotationKind.Arrow,
        "brush" => AnnotationKind.Brush,
        "highlight" => AnnotationKind.Highlight,
        "text" => AnnotationKind.Text,
        "mosaic" => AnnotationKind.Mosaic,
        _ => AnnotationKind.Rectangle
    };

    private static ColorFormatKind ParseColorFormat(string? format) => format switch
    {
        "rgb" => ColorFormatKind.Rgb,
        "hsl" => ColorFormatKind.Hsl,
        _ => ColorFormatKind.Hex
    };

    private static System.Drawing.Point GetCursorPixel()
    {
        return GetCursorPos(out var p) ? new System.Drawing.Point(p.X, p.Y) : new System.Drawing.Point(0, 0);
    }

    public void Dispose()
    {
        foreach (var overlay in _overlays)
        {
            try
            {
                overlay.Close();
            }
            catch
            {
                // 关闭失败不阻塞退出
            }
        }

        _overlays.Clear();

        foreach (var pin in _pins.ToArray())
        {
            try
            {
                pin.Close();
            }
            catch
            {
                // 同上
            }
        }

        _pins.Clear();
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint lpPoint);
}
