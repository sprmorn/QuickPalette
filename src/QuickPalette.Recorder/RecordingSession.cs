namespace QuickPalette.Recorder;

using System.Diagnostics;
using Serilog;

/// <summary>录制参数（区域为显示器内物理像素坐标）</summary>
public sealed record RecordingOptions(
    IntPtr MonitorHandle,
    int CropX,
    int CropY,
    int CropWidth,
    int CropHeight,
    int MonitorWidth,
    int MonitorHeight,
    int Fps,
    int VideoBitrate,
    bool MicEnabled,
    bool LoopbackEnabled,
    string OutputPath);

/// <summary>
/// 录制会话：WGC 帧源 + 混音音频源 + MP4 编码器，音视频时间轴按媒体时钟对齐；
/// 支持暂停/继续（暂停段不入轨）。
/// </summary>
public sealed class RecordingSession : IDisposable
{
    private readonly ILogger _logger;
    private readonly Mp4SinkWriter _writer;
    private readonly WgcRegionSource _source;
    private readonly RecordingAudio? _audio;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _sync = new();

    private double _pausedTotalMs;
    private double _pauseStartMs;
    private volatile bool _running = true;
    private Task? _audioThread;
    private bool _stopped;

    public bool Paused { get; private set; }
    public bool HasAudioTrack { get; }
    public string OutputPath { get; }

    /// <summary>录制时长（媒体时钟，不含暂停）</summary>
    public TimeSpan Elapsed
    {
        get
        {
            lock (_sync)
            {
                var paused = Paused ? _clock.Elapsed.TotalMilliseconds - _pauseStartMs : 0;
                return TimeSpan.FromMilliseconds(_clock.Elapsed.TotalMilliseconds - _pausedTotalMs - paused);
            }
        }
    }

    public RecordingSession(RecordingOptions options, ILogger logger)
    {
        _logger = logger;

        // H.264 编码器拒收奇数宽/高（实测 MF_E_INVALIDMEDIATYPE 0xC00D36B4）——
        // 向下对齐偶数（少 1 行/列像素，肉眼不可察）
        options = options with
        {
            CropWidth = Math.Max(2, options.CropWidth & ~1),
            CropHeight = Math.Max(2, options.CropHeight & ~1),
        };

        OutputPath = options.OutputPath;

        _audio = new RecordingAudio(options.MicEnabled, options.LoopbackEnabled);
        HasAudioTrack = _audio.AnyAvailable && (options.MicEnabled || options.LoopbackEnabled);

        var fps = Math.Clamp(options.Fps, 5, 60);
        _writer = new Mp4SinkWriter(options.OutputPath, options.CropWidth, options.CropHeight, fps,
            options.VideoBitrate, HasAudioTrack, RecordingAudio.TargetRate);

        _source = new WgcRegionSource(options.MonitorHandle,
            options.CropX, options.CropY, options.CropWidth, options.CropHeight,
            options.MonitorWidth, options.MonitorHeight);
        _source.FrameArrived += OnFrame;

        if (HasAudioTrack)
        {
            _audioThread = Task.Run(AudioLoop);
        }

        _logger.Information("Recording started: {W}x{H}@{Fps}fps audio={Audio} -> {Path}",
            options.CropWidth, options.CropHeight, fps, HasAudioTrack, options.OutputPath);
    }

    private long _firstFrameNs = -1;
    private long _audioBytesWritten;
    private readonly ManualResetEventSlim _audioReady = new(false);

    private void OnFrame(byte[] bgra)
    {
        if (!_running || Paused)
        {
            return;
        }

        try
        {
            var elapsedNs = (long)(Elapsed.TotalMilliseconds * 10_000);
            if (_firstFrameNs < 0)
            {
                // 双轨统一 0 起点：首帧 = 0，音频等此信号后开写 → 音画对齐
                _firstFrameNs = elapsedNs;
                _audioReady.Set();
            }

            _writer.WriteFrame(bgra, elapsedNs - _firstFrameNs);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Write frame failed");
        }
    }

    /// <summary>
    /// 音频独立推进，但按真实时钟补足采样（每圈固定产出会欠产 → 音频轴变短 → 音画漂移，实测坑）；
    /// 暂停段排空缓冲不入轨。
    /// </summary>
    private void AudioLoop()
    {
        var buf = new byte[RecordingAudio.TargetRate / 25 * 4]; // 单次最多 40ms × 48k × 4字节
        _audioReady.Wait(5000); // 等首帧，与视频 0 起点对齐
        _audio?.DiscardPending(buf); // 丢弃首帧到达前的积压（画面起点之前的声音不入轨）
        while (_running)
        {
            try
            {
                if (_audio == null)
                {
                    break;
                }

                if (Paused)
                {
                    _audio.DiscardPending(buf);
                }
                else
                {
                    // 目标采样数 = （媒体时刻 - 首帧时刻）× 48k；缺口一次性补足
                    var sinceFirstNs = (long)(Elapsed.TotalMilliseconds * 10_000) - _firstFrameNs;
                    var targetSamples = Math.Max(0, sinceFirstNs * RecordingAudio.TargetRate / 10_000_000);
                    var writtenSamples = _audioBytesWritten / 4;
                    var missing = targetSamples - writtenSamples;
                    if (missing > 0)
                    {
                        var toRead = (int)Math.Min(missing * 4, buf.Length);
                        toRead -= toRead % 4;
                        if (toRead > 0)
                        {
                            var n = _audio.ReadPcm16(buf, toRead);
                            if (n > 0)
                            {
                                var time = _audioBytesWritten * 10_000_000L / (RecordingAudio.TargetRate * 4L);
                                var dur = n * 10_000_000L / (RecordingAudio.TargetRate * 4L);
                                _writer.WriteAudioPcm16(buf, n, time, dur);
                                _audioBytesWritten += n;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Audio loop error");
            }

            Thread.Sleep(15);
        }
    }

    public void Pause()
    {
        lock (_sync)
        {
            if (Paused)
            {
                return;
            }

            Paused = true;
            _pauseStartMs = _clock.Elapsed.TotalMilliseconds;
            _logger.Information("Recording paused");
        }
    }

    /// <summary>录制中切换麦克风开关</summary>
    public void SetMicEnabled(bool enabled)
    {
        if (_audio != null)
        {
            _audio.MicEnabled = enabled;
            _logger.Information("Mic enabled: {Enabled}", enabled);
        }
    }

    /// <summary>录制中切换系统声音开关</summary>
    public void SetLoopbackEnabled(bool enabled)
    {
        if (_audio != null)
        {
            _audio.LoopbackEnabled = enabled;
            _logger.Information("Loopback enabled: {Enabled}", enabled);
        }
    }

    public void Resume()
    {
        lock (_sync)
        {
            if (!Paused)
            {
                return;
            }

            _pausedTotalMs += _clock.Elapsed.TotalMilliseconds - _pauseStartMs;
            Paused = false;
            _logger.Information("Recording resumed");
        }
    }

    /// <summary>停止并封盘，返回输出文件路径</summary>
    public string Stop()
    {
        if (_stopped)
        {
            return OutputPath;
        }

        _stopped = true;
        _running = false;

        _source.Dispose();
        try
        {
            _audioThread?.Wait(1000);
        }
        catch
        {
            // 忽略
        }

        _writer.FinalizeFile();
        _writer.Dispose();
        _audio?.Dispose();
        _logger.Information("Audio written: {Samples} samples = {Sec:0.000}s (elapsed {Elapsed:0.000}s)",
            _audioBytesWritten / 4, _audioBytesWritten / 4.0 / RecordingAudio.TargetRate, Elapsed.TotalSeconds);

        _logger.Information("Recording stopped: {Elapsed} -> {Path}", Elapsed, OutputPath);
        return OutputPath;
    }

    public void Dispose() => Stop();
}
