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

    /// <summary>统一为 48kHz/float/立体声。ReadFully=false：读空如实返回（不足不补静音——
    /// 静音填充会造成周期性断声=顿音，实测坑）</summary>
    private static ISampleProvider Normalize(BufferedWaveProvider buffer)
    {
        buffer.ReadFully = false;
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

    // 每路未用完的余量（本圈多读的留到下圈先用，数据零丢弃）
    private readonly float[] _micCarry = new float[TargetRate];
    private int _micCarryLen;
    private readonly float[] _loopCarry = new float[TargetRate];
    private int _loopCarryLen;

    /// <summary>读一段混音 PCM16 立体声；有多少读多少（不足等下圈，绝不掺静音）；返回实际写入字节数</summary>
    public int ReadPcm16(byte[] target, int maxBytes)
    {
        var frames = Math.Min(maxBytes / 4, TargetRate / 25); // 立体声 16bit = 4B/帧
        if (frames <= 0)
        {
            return 0;
        }

        var micBuf = new float[frames * 2];
        var loopBuf = new float[frames * 2];
        int micN = 0, loopN = 0;

        lock (_sync)
        {
            var micActive = MicEnabled && _micProvider != null;
            var loopActive = LoopbackEnabled && _loopProvider != null;
            if (!micActive && !loopActive)
            {
                return 0;
            }

            if (micActive)
            {
                micN = Pull(_micProvider, _micCarry, ref _micCarryLen, micBuf, frames);
            }

            if (loopActive)
            {
                loopN = Pull(_loopProvider, _loopCarry, ref _loopCarryLen, loopBuf, frames);
            }
        }

        // 双路取对齐长度；多余部分退回 carry（零丢弃）
        int n = micN > 0 && loopN > 0 ? Math.Min(micN, loopN) : Math.Max(micN, loopN);
        if (n <= 0)
        {
            return 0;
        }

        lock (_sync)
        {
            if (micN > n)
            {
                PushBack(_micCarry, ref _micCarryLen, micBuf, n * 2, (micN - n) * 2);
            }

            if (loopN > n)
            {
                PushBack(_loopCarry, ref _loopCarryLen, loopBuf, n * 2, (loopN - n) * 2);
            }
        }

        for (var i = 0; i < n * 2; i++)
        {
            var v = (short)(Math.Clamp(micBuf[i] + loopBuf[i], -1f, 1f) * short.MaxValue);
            target[i * 2] = (byte)(v & 0xFF);
            target[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }

        return n * 4;
    }

    /// <summary>把未消费的余量插回 carry 头部</summary>
    private static void PushBack(float[] carry, ref int carryLen, float[] src, int srcOffset, int countFloats)
    {
        if (carryLen + countFloats > carry.Length)
        {
            countFloats = carry.Length - carryLen;
            if (countFloats <= 0)
            {
                return;
            }
        }

        Array.Copy(carry, 0, carry, countFloats, carryLen);
        Array.Copy(src, srcOffset, carry, 0, countFloats);
        carryLen += countFloats;
    }

    /// <summary>从 carry+采集缓冲拉取 frames 个立体声帧（不足如实返回；多读存 carry）</summary>
    private static int Pull(ISampleProvider provider, float[] carry, ref int carryLen, float[] dst, int frames)
    {
        var need = frames * 2;
        var got = 0;

        // 先用余量
        var fromCarry = Math.Min(carryLen, need);
        Array.Copy(carry, 0, dst, 0, fromCarry);
        got += fromCarry;
        carryLen -= fromCarry;
        if (carryLen > 0)
        {
            Array.Copy(carry, fromCarry, carry, 0, carryLen);
        }

        // 再读新数据（如实：读到多少算多少）
        var read = provider.Read(dst, got, need - got);
        got += read;

        // 末尾这段若被两路 min 裁掉，由调用方把差量退回——此处多读的天然留在 dst，调用方裁剪时按帧对齐丢回 carry
        return got / 2;
    }

    private readonly float[] _scratchFloats = new float[RecordingAudio.TargetRate / 25 * 2];

    /// <summary>丢弃当前已缓冲的音频（暂停段不入轨用）</summary>
    public void DiscardPending(byte[] scratch)
    {
        lock (_sync)
        {
            _micCarryLen = 0;
            _loopCarryLen = 0;
            _micProvider?.Read(_scratchFloats, 0, _scratchFloats.Length);
            _loopProvider?.Read(_scratchFloats, 0, _scratchFloats.Length);
        }
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
