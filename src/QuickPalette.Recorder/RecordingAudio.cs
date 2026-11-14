namespace QuickPalette.Recorder;

using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

/// <summary>
/// 录制音频源：麦克风 + 系统声音（WASAPI 内录）→ 48kHz/16bit/立体声 PCM 混音。
/// 两路可独立开关（运行中切换安全）；任一源缺席/欠数自动补静音，保证时间轴连续。
/// </summary>
public sealed class RecordingAudio : IDisposable
{
    public const int TargetRate = 48000;

    private readonly object _sync = new();
    private readonly BufferedWaveProvider? _micBuffer;
    private readonly BufferedWaveProvider? _loopBuffer;
    private readonly ISampleProvider? _micProvider;
    private readonly ISampleProvider? _loopProvider;
    private WasapiCapture? _mic;
    private WasapiLoopbackCapture? _loop;

    public bool MicEnabled { get; set; }
    public bool LoopbackEnabled { get; set; }

    public bool MicAvailable => _micProvider != null;
    public bool LoopbackAvailable => _loopProvider != null;

    /// <summary>是否有任何可用音频源（都没有则整个不写音轨）</summary>
    public bool AnyAvailable => MicAvailable || LoopbackAvailable;

    public RecordingAudio(bool micEnabled, bool loopbackEnabled)
    {
        MicEnabled = micEnabled;
        LoopbackEnabled = loopbackEnabled;

        try
        {
            _loop = new WasapiLoopbackCapture();
            _loopBuffer = new BufferedWaveProvider(_loop.WaveFormat) { DiscardOnBufferOverflow = true };
            var capturedLoop = _loop;
            var loopBuffer = _loopBuffer;
            capturedLoop.DataAvailable += (_, e) => loopBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            capturedLoop.StartRecording();
            _loopProvider = Normalize(loopBuffer);
        }
        catch
        {
            _loop = null; // 无输出设备等环境：仅录麦克风/静音
        }

        try
        {
            _mic = new WasapiCapture();
            _micBuffer = new BufferedWaveProvider(_mic.WaveFormat) { DiscardOnBufferOverflow = true };
            var capturedMic = _mic;
            var micBuffer = _micBuffer;
            capturedMic.DataAvailable += (_, e) => micBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            capturedMic.StartRecording();
            _micProvider = Normalize(micBuffer);
        }
        catch
        {
            _mic = null; // 无麦克风/未授权：仅录内录
        }
    }

    /// <summary>统一为 48kHz/float/立体声（BufferedWaveProvider 读空补静音 → 时间轴连续）</summary>
    private static ISampleProvider Normalize(BufferedWaveProvider buffer)
    {
        ISampleProvider provider = new SampleChannel(buffer);
        if (provider.WaveFormat.Channels == 1)
        {
            provider = new MonoToStereoSampleProvider(provider);
        }

        if (provider.WaveFormat.SampleRate != TargetRate)
        {
            provider = new WdlResamplingSampleProvider(provider, TargetRate);
        }

        return provider;
    }

    /// <summary>读一段混音 PCM16 立体声（不足补静音）；返回写入字节数（恒为 maxBytes）</summary>
    public int ReadPcm16(byte[] target, int maxBytes)
    {
        var byteCount = Math.Min(maxBytes, (maxBytes / 4) * 4); // 4 字节/采样（16bit×2ch）
        var samples = byteCount / 2; // 单声道采样点数（含双声道）→ 用 float 计：float 数 = byteCount/2? 不——
        // 统一按 float 采样点处理：float 数 = byteCount / 2（16bit=2字节），即 byteCount/4 个立体声帧 ×2
        var floats = byteCount / 2;
        var mix = new float[floats];
        var tmp = new float[floats];

        lock (_sync)
        {
            if (MicEnabled && _micProvider != null)
            {
                var n = _micProvider.Read(tmp, 0, floats);
                for (var i = 0; i < n; i++)
                {
                    mix[i] += tmp[i];
                }
            }

            if (LoopbackEnabled && _loopProvider != null)
            {
                Array.Clear(tmp);
                var n = _loopProvider.Read(tmp, 0, floats);
                for (var i = 0; i < n; i++)
                {
                    mix[i] += tmp[i];
                }
            }
        }

        for (var i = 0; i < floats; i++)
        {
            var v = (short)(Math.Clamp(mix[i], -1f, 1f) * short.MaxValue);
            target[i * 2] = (byte)(v & 0xFF);
            target[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }

        return byteCount;
    }

    public void Dispose()
    {
        try
        {
            _mic?.StopRecording();
            _mic?.Dispose();
            _loop?.StopRecording();
            _loop?.Dispose();
        }
        catch
        {
            // 释放失败不影响退出
        }
    }
}
