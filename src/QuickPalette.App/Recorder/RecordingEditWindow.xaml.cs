namespace QuickPalette.App.Recorder;

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using QuickPalette.Domain.Models;
using QuickPalette.Recorder;
using QuickPalette.App.Screenshot;
using Serilog;

/// <summary>
/// 录屏编辑窗：预览播放（倍速即时生效）+ 时间裁剪（首尾把手）→ 复制 / 保存 / 另存为。
/// 导出走透传重封装（无损、快）。
/// </summary>
public partial class RecordingEditWindow : Window
{
    private readonly ILogger _logger;
    private readonly string _sourcePath;
    private readonly string _previewPath;
    private readonly Settings _settings;
    private readonly DispatcherTimer _clock;
    private TimeSpan _duration;
    private bool _playing;

    public RecordingEditWindow(ILogger logger, string sourcePath, Settings settings)
    {
        InitializeComponent();

        _logger = logger;
        _sourcePath = sourcePath;
        _settings = settings;

        // 预览用临时副本：WPF MediaElement（WMP）释放文件句柄不可靠（Source=null 后仍可能持锁），
        // 导出若读同一文件会被锁挂死（实测：0KB 目标、无任何弹窗）——预览/导出彻底隔离
        _previewPath = Path.Combine(Path.GetTempPath(), $"轻启预览_{Guid.NewGuid():N}.mp4");
        File.Copy(sourcePath, _previewPath, overwrite: true);

        TitleIconHost.Content = ToolbarIcons.Create("rec-camera", 18);
        SpeedIconHost.Content = ToolbarIcons.Create("rec-speed", 16, Brushes.Gray);
        CropIconHost.Content = ToolbarIcons.Create("rec-crop", 15, Brushes.Gray);

        Preview.Source = new Uri(_previewPath);
        Preview.MediaOpened += (_, _) =>
        {
            if (Preview.NaturalDuration.HasTimeSpan)
            {
                _duration = Preview.NaturalDuration.TimeSpan;
                TrimStartSlider.Maximum = _duration.TotalSeconds;
                TrimEndSlider.Maximum = _duration.TotalSeconds;
                TrimEndSlider.Value = _duration.TotalSeconds;
                UpdateTrimText();
            }
        };
        Preview.MediaEnded += (_, _) => { _playing = false; PlayButton.Content = "▶"; };

        _clock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _clock.Tick += (_, _) => TimeText.Text = $"{Format(Preview.Position)} / {Format(_duration)}";
        _clock.Start();

        Closed += (_, _) =>
        {
            _clock.Stop();
            try
            {
                Preview.Stop();
                Preview.Source = null;
                File.Delete(_previewPath);
            }
            catch
            {
                // 清理失败忽略（%TEMP% 会被系统回收）
            }
        };
    }

    private static string Format(TimeSpan t) => $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_playing)
        {
            Preview.Pause();
            _playing = false;
            PlayButton.Content = "▶";
        }
        else
        {
            if (Preview.Position >= _duration)
            {
                Preview.Position = TimeSpan.Zero;
            }

            Preview.Play();
            _playing = true;
            PlayButton.Content = "⏸";
        }
    }

    private void Speed_Changed(object sender, RoutedEventArgs e)
    {
        // WPF MediaElement 不支持播放倍速；倍速在导出时应用（预览按原速）
        if (!IsLoaded)
        {
            return;
        }

        UpdateTrimText();
    }

    private void Trim_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded)
        {
            return;
        }

        if (TrimStartSlider.Value > TrimEndSlider.Value - 0.2)
        {
            TrimStartSlider.Value = Math.Max(0, TrimEndSlider.Value - 0.2);
        }

        UpdateTrimText();
    }

    private void UpdateTrimText()
    {
        if (TrimText == null)
        {
            return;
        }

        TrimText.Text = $"裁剪：{TrimStartSlider.Value:0.0}s – {TrimEndSlider.Value:0.0}s（总 {_duration.TotalSeconds:0.0}s）";
    }

    private (double Start, double End, double Speed) CurrentEdit()
    {
        double.TryParse((SpeedBox.SelectedItem as ComboBoxItem)?.Tag as string, out var speed);
        return (TrimStartSlider.Value, TrimEndSlider.Value, speed <= 0 ? 1 : speed);
    }

    private bool IsEdited()
    {
        var (start, end, speed) = CurrentEdit();
        return start > 0.05 || end < _duration.TotalSeconds - 0.05 || Math.Abs(speed - 1) > 0.001;
    }

    /// <summary>导出前释放预览占用（WMP 持有文件句柄会让 SourceReader 挂起 → 界面假死）</summary>
    private void ReleasePreview()
    {
        try
        {
            Preview.Stop();
            Preview.Source = null;
        }
        catch
        {
            // 忽略
        }
    }

    private bool _exporting;

    /// <summary>后台线程导出当前编辑结果；未编辑则复制原文件。30 秒超时兜底（防止锁等待挂死无提示）</summary>
    private async Task<string?> ProduceEditedAsync(string targetPath)
    {
        ReleasePreview();
        var (start, end, speed) = CurrentEdit();
        var source = _sourcePath;
        var edited = IsEdited();
        var exportTask = Task.Run(() =>
        {
            try
            {
                if (!edited)
                {
                    File.Copy(source, targetPath, overwrite: true);
                    return targetPath;
                }

                RecordingExporter.Export(source, targetPath, start, end, speed);
                return targetPath;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Export failed");
                return null;
            }
        });

        var done = await Task.WhenAny(exportTask, Task.Delay(TimeSpan.FromSeconds(30)));
        if (done != exportTask)
        {
            _logger.Error("Export timed out after 30s");
            return null;
        }

        return await exportTask;
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_exporting)
        {
            return;
        }

        try
        {
            _exporting = true;
            var temp = Path.Combine(Path.GetTempPath(), $"轻启录屏_{DateTime.Now:HHmmss}.mp4");
            var result = await ProduceEditedAsync(temp);
            if (result == null)
            {
                MessageBox.Show("导出失败，请查看日志。", "录屏", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var data = new DataObject();
            var files = new System.Collections.Specialized.StringCollection { temp };
            data.SetFileDropList(files);
            Clipboard.SetDataObject(data, true);
            MessageBox.Show("已复制到剪贴板（可直接粘贴发送）", "录屏", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Copy failed");
        }
        finally
        {
            _exporting = false;
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_exporting)
        {
            return;
        }

        try
        {
            _exporting = true;
            var dir = string.IsNullOrWhiteSpace(_settings.RecordSaveDir)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "轻启录制")
                : _settings.RecordSaveDir;
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, $"录屏_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
            var result = await ProduceEditedAsync(target);
            if (result != null)
            {
                MessageBox.Show($"已保存：\n{target}", "录屏", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("导出失败，请查看日志。", "录屏", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Save failed");
        }
        finally
        {
            _exporting = false;
        }
    }

    private async void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (_exporting)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "MP4 视频|*.mp4",
            FileName = $"录屏_{DateTime.Now:yyyyMMdd_HHmmss}.mp4"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            _exporting = true;
            var result = await ProduceEditedAsync(dialog.FileName);
            if (result == null)
            {
                MessageBox.Show("导出失败，请查看日志。", "录屏", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "SaveAs failed");
        }
        finally
        {
            _exporting = false;
        }
    }
}
