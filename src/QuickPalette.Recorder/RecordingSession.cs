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

    private void OnFrame(byte[] bgra)
    {
        if (!_running || Paused)
        {
            return;
        }

        try
        {
            _writer.WriteFrame(bgra, (long)(Elapsed.TotalMilliseconds * 10_000));
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Write frame failed");
        }
    }

    /// <summary>音频按 20ms 节奏独立推进（不受视频帧率波动影响，时间轴连续）</summary>
    private void AudioLoop()
    {
        var buf = new byte[RecordingAudio.TargetRate / 50 * 4]; // 20ms × 48k × 4字节
        while (_running)
        {
            try
            {
                if (!Paused && _audio != null)
                {
                    var n = _audio.ReadPcm16(buf, buf.Length);
                    if (n > 0)
                    {
                        _writer.WriteAudioPcm16(buf, n, RecordingAudio.TargetRate);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Audio loop error");
            }

            Thread.Sleep(20);
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

        _logger.Information("Recording stopped: {Elapsed} -> {Path}", Elapsed, OutputPath);
        return OutputPath;
    }

    public void Dispose() => Stop();
}
