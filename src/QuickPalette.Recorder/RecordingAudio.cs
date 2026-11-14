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
    private readonly SourceAdapter? _micAdapter;
    private readonly SourceAdapter? _loopAdapter;
    private WasapiCapture? _mic;
    private WasapiLoopbackCapture? _loop;

    public bool MicEnabled { get; set; }
    public bool LoopbackEnabled { get; set; }

    public bool MicAvailable => _micAdapter != null;
    public bool LoopbackAvailable => _loopAdapter != null;

    /// <summary>是否有任何可用音频源（都没有则整个不写音轨）</summary>
    public bool AnyAvailable => MicAvailable || LoopbackAvailable;

    public RecordingAudio(bool micEnabled, bool loopbackEnabled)
    {
        MicEnabled = micEnabled;
        LoopbackEnabled = loopbackEnabled;

        try
        {
            _loop = new WasapiLoopbackCapture();
            System.Diagnostics.Trace.TraceInformation($"Loopback fmt: {_loop.WaveFormat}");
            _loopBuffer = new BufferedWaveProvider(_loop.WaveFormat) { DiscardOnBufferOverflow = true };
            var capturedLoop = _loop;
            var loopBuffer = _loopBuffer;
            capturedLoop.DataAvailable += (_, e) =>
            {
                if (Environment.GetEnvironmentVariable("QP_PCM_DUMP") != null)
                {
                    System.Diagnostics.Trace.TraceInformation($"[CAP loop] +{e.BytesRecorded}B @{(long)CapWatch.Elapsed.TotalMilliseconds}ms");
                }

                loopBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            };
            capturedLoop.StartRecording();
            _loopAdapter = new SourceAdapter(loopBuffer);
        }
        catch (Exception ex)
        {
            _loop = null; // 无输出设备等环境：仅录麦克风/静音
            System.Diagnostics.Trace.TraceWarning("Loopback init failed: {0}", ex);
        }

        try
        {
            _mic = new WasapiCapture();
            _micBuffer = new BufferedWaveProvider(_mic.WaveFormat) { DiscardOnBufferOverflow = true };
            var capturedMic = _mic;
            var micBuffer = _micBuffer;
            capturedMic.DataAvailable += (_, e) => micBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            capturedMic.StartRecording();
            _micAdapter = new SourceAdapter(micBuffer);
        }
        catch (Exception ex)
        {
            _mic = null; // 无麦克风/未授权：仅录内录
            System.Diagnostics.Trace.TraceWarning("Mic init failed: {0}", ex);
        }
    }

    /// <summary>
    /// 采集流适配：字节→立体声 float@48k，自带字节对齐残余 + 线性重采样。
    /// 不用 NAudio 的 SampleChannel/WdlResampler：BufferedWaveProvider 按字节存取会返回
    /// 非 4 对齐的块，SampleChannel 转 float 时错位 → 周期性炸音（实测 160+ 毛刺）。
    /// </summary>
    private static readonly System.Diagnostics.Stopwatch CapWatch = System.Diagnostics.Stopwatch.StartNew();

    private sealed class SourceAdapter
    {
        private readonly BufferedWaveProvider _src;
        private readonly int _srcRate;
        private readonly int _channels;
        private readonly bool _isFloat;
        private readonly int _bytesPerSample;
        private readonly double _step; // 每输出帧消耗的源帧数 = srcRate/48000
        private readonly byte[] _raw = new byte[64 * 1024];
        private readonly float[] _queue = new float[TargetRate * 2]; // 源帧队列（立体声交错）
        private int _queueFrames;
        private int _queueHead;
        private double _pos; // 下一输出帧在队列中的源位置
        private readonly byte[] _residual = new byte[8];
        private int _residualLen;

        public SourceAdapter(BufferedWaveProvider src)
        {
            _src = src;
            // 关键：读空如实返回 0（默认会补静音——既造成死循环清空，又是静音填充根源）
            _src.ReadFully = false;
            _srcRate = src.WaveFormat.SampleRate;
            _channels = src.WaveFormat.Channels;
            _isFloat = src.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat;
            _bytesPerSample = src.WaveFormat.BitsPerSample / 8;
            _step = _srcRate / (double)TargetRate;
        }

        /// <summary>拉取 frames 个立体声输出帧；有多少给多少（不足如实返回）</summary>
        public int Pull(float[] dst, int frames)
        {
            var produced = 0;
            while (produced < frames)
            {
                // 需要 floor(pos)+2 个源帧做插值
                while (_queueFrames - _pos < 2)
                {
                    if (!FillQueue())
                    {
                        return produced; // 源耗尽，如实返回
                    }
                }

                var p0 = _queueHead + (int)_pos;
                var frac = (float)(_pos - (int)_pos);
                var l0 = _queue[p0 * 2];
                var r0 = _queue[p0 * 2 + 1];
                var l1 = _queue[(p0 + 1) * 2];
                var r1 = _queue[(p0 + 1) * 2 + 1];
                dst[produced * 2] = l0 + (l1 - l0) * frac;
                dst[produced * 2 + 1] = r0 + (r1 - r0) * frac;
                produced++;
                _pos += _step;
            }

            return produced;
        }

        /// <summary>退回 n 个已产出帧（双路对齐用：下圈从正确位置重新插值，零丢弃零跳变）</summary>
        public void UnPull(int frames)
        {
            _pos -= frames * _step;
            if (_pos < 0)
            {
                _pos = 0;
            }
        }

        /// <summary>从字节缓冲读一批源帧入队（字节对齐残余处理）；返回是否读到</summary>
        private bool FillQueue()
        {
            // 整帧消费后压缩队列
            var consumed = (int)_pos;
            if (consumed > 0)
            {
                _queueHead += consumed;
                _queueFrames -= consumed;
                _pos -= consumed;
            }

            if (_queueHead > 0 && _queueFrames > 0)
            {
                Array.Copy(_queue, _queueHead * 2, _queue, 0, _queueFrames * 2);
                _queueHead = 0;
            }
            else if (_queueFrames == 0)
            {
                _queueHead = 0;
            }

            var frameBytes = _bytesPerSample * _channels;
            var wantBytes = Math.Min(_raw.Length, (TargetRate / 25) * frameBytes);
            wantBytes -= wantBytes % frameBytes;

            // 先用残余字节
            var offset = 0;
            if (_residualLen > 0)
            {
                Array.Copy(_residual, 0, _raw, 0, _residualLen);
                offset = _residualLen;
                _residualLen = 0;
            }

            var read = _src.Read(_raw, offset, wantBytes);
            var total = offset + read;
            var frames = total / frameBytes;
            if (frames == 0)
            {
                // 保留不足一帧的残余
                _residualLen = Math.Min(total, _residual.Length);
                Array.Copy(_raw, 0, _residual, 0, _residualLen);
                return false;
            }

            // 残余字节存回
            var tail = total - frames * frameBytes;
            if (tail > 0)
            {
                _residualLen = Math.Min(tail, _residual.Length);
                Array.Copy(_raw, frames * frameBytes, _residual, 0, _residualLen);
            }

            // 字节 → 队列 float（立体声）
            for (var i = 0; i < frames; i++)
            {
                float l, r;
                if (_isFloat)
                {
                    l = BitConverter.ToSingle(_raw, i * frameBytes);
                    r = _channels >= 2 ? BitConverter.ToSingle(_raw, i * frameBytes + _bytesPerSample) : l;
                }
                else
                {
                    l = (short)(_raw[i * frameBytes] | (_raw[i * frameBytes + 1] << 8)) / 32768f;
                    r = _channels >= 2
                        ? (short)(_raw[i * frameBytes + 2] | (_raw[i * frameBytes + 3] << 8)) / 32768f
                        : l;
                }

                var q = (_queueFrames + i) * 2;
                _queue[q] = l;
                _queue[q + 1] = r;
            }

            _queueFrames += frames;
            return true;
        }

        public void Clear()
        {
            _queueFrames = 0;
            _queueHead = 0;
            _pos = 0;
            _residualLen = 0;
            while (_src.Read(_raw, 0, _raw.Length) > 0)
            {
                // 丢弃
            }
        }
    }

    /// <summary>读一段混音 PCM16 立体声；有多少读多少（不足等下圈，绝不掺静音）；返回实际写入字节数</summary>
    public int ReadPcm16(byte[] target, int maxBytes)
    {
        var frames = Math.Min(maxBytes / 4, TargetRate / 25);
        if (frames <= 0)
        {
            return 0;
        }

        var micBuf = new float[frames * 2];
        var loopBuf = new float[frames * 2];
        int micN = 0, loopN = 0;
        bool micActive = false, loopActive = false;

        lock (_sync)
        {
            micActive = MicEnabled && _micAdapter != null;
            loopActive = LoopbackEnabled && _loopAdapter != null;
            if (!micActive && !loopActive)
            {
                return 0;
            }

            if (micActive)
            {
                micN = _micAdapter.Pull(micBuf, frames);
            }

            if (loopActive)
            {
                loopN = _loopAdapter.Pull(loopBuf, frames);
            }
        }

        // 双路取对齐长度；多产出的 UnPull 退回（零丢弃零跳变）
        int n = micN > 0 && loopN > 0 ? Math.Min(micN, loopN) : Math.Max(micN, loopN);
        if (n <= 0)
        {
            return 0;
        }

        lock (_sync)
        {
            if (micN > n)
            {
                _micAdapter?.UnPull(micN - n);
            }

            if (loopN > n)
            {
                _loopAdapter?.UnPull(loopN - n);
            }
        }

        // 双路时各 -6dB 相加防削波炸音（叠加超满幅会硬削波）；单路保持原音量
        var scale = micActive && loopActive ? 0.5f : 1f;
        for (var i = 0; i < n * 2; i++)
        {
            var mix = Math.Clamp((micBuf[i] + loopBuf[i]) * scale, -1f, 1f);
            var v = (short)(mix * short.MaxValue);
            target[i * 2] = (byte)(v & 0xFF);
            target[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }

        return n * 4;
    }

    /// <summary>丢弃当前已缓冲的音频（暂停段不入轨用）</summary>
    public void DiscardPending(byte[] scratch)
    {
        lock (_sync)
        {
            _micAdapter?.Clear();
            _loopAdapter?.Clear();
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
