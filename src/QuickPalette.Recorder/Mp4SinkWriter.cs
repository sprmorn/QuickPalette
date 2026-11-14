namespace QuickPalette.Recorder;

using System.Diagnostics;
using System.Runtime.InteropServices;

/// <summary>
/// Media Foundation SinkWriter 封装（vtable 直调，免疫 ComWrappers）：
/// H.264(视频) + AAC(音频) → MP4。录制喂 BGRA/PCM16，导出透传压缩样本。
/// </summary>
public sealed class Mp4SinkWriter : IDisposable
{
    private const long TicksPerSecond = 10_000_000;

    private IntPtr _writer;
    private int _videoStream;
    private int _audioStream = -1;
    private int _width;
    private int _height;
    private int _fps;
    private bool _hasAudio;
    private bool _reencodeAudio;
    private int _videoStride;
    private long _audioBytesWritten;
    private bool _started;
    private bool _finalized;

    /// <summary>录制模式：视频 RGB32 + 音频 PCM16 输入，H.264/AAC 输出</summary>
    public Mp4SinkWriter(string outputPath, int width, int height, int fps, int videoBitrate,
        bool hasAudio, int audioSampleRate = 48000)
    {
        _width = width;
        _height = height;
        _fps = Math.Max(1, fps);
        _hasAudio = hasAudio;
        _videoStride = width * 4;

        MfNative.EnsureStarted();
        MfNative.MFCreateSinkWriterFromURL(outputPath, IntPtr.Zero, MfNative.CreateNoThrottleAttributes(), out _writer);

        // ---- 视频：输出 H.264 ← 输入 RGB32 ----
        MfNative.MFCreateMediaType(out var videoOut);
        MfNative.SetGuid(videoOut, MfGuids.MajorType, MfGuids.MediaTypeVideo);
        MfNative.SetGuid(videoOut, MfGuids.Subtype, MfGuids.VideoFormatH264);
        MfNative.SetUInt64(videoOut, MfGuids.FrameSize, ((ulong)(uint)width << 32) | (uint)height);
        MfNative.SetUInt64(videoOut, MfGuids.FrameRate, ((ulong)(uint)_fps << 32) | 1u);
        MfNative.SetUInt64(videoOut, MfGuids.PixelAspectRatio, (1ul << 32) | 1u);
        MfNative.SetUInt32(videoOut, MfGuids.AvgBitrate, (uint)Math.Max(500_000, videoBitrate));
        MfNative.SetUInt32(videoOut, MfGuids.InterlaceMode, 2u);

        MfNative.SinkAddStream(_writer, videoOut, out _videoStream);

        MfNative.MFCreateMediaType(out var videoIn);
        MfNative.SetGuid(videoIn, MfGuids.MajorType, MfGuids.MediaTypeVideo);
        MfNative.SetGuid(videoIn, MfGuids.Subtype, MfGuids.VideoFormatRgb32);
        MfNative.SetUInt64(videoIn, MfGuids.FrameSize, ((ulong)(uint)width << 32) | (uint)height);
        MfNative.SetUInt64(videoIn, MfGuids.FrameRate, ((ulong)(uint)_fps << 32) | 1u);
        MfNative.SetUInt32(videoIn, MfGuids.InterlaceMode, 2u);

        MfNative.SinkSetInputMediaType(_writer, _videoStream, videoIn);

        // ---- 音频：输出 AAC ← 输入 PCM16（配方对照 openscreen：全属性但**不设**
        // MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION（实测它是毒，设了协商必拒）；码率 192kbps ----
        if (_hasAudio)
        {
            MfNative.MFCreateMediaType(out var audioOut);
            MfNative.SetGuid(audioOut, MfGuids.MajorType, MfGuids.MediaTypeAudio);
            MfNative.SetGuid(audioOut, MfGuids.Subtype, MfGuids.AudioFormatAac);
            MfNative.SetUInt32(audioOut, MfGuids.AudioBitsPerSample, 16);
            MfNative.SetUInt32(audioOut, MfGuids.AudioSamplesPerSecond, (uint)audioSampleRate);
            MfNative.SetUInt32(audioOut, MfGuids.AudioNumChannels, 2);
            var aacBytes = int.TryParse(Environment.GetEnvironmentVariable("QP_AAC_BYTES"), out var ab) ? ab : 24000;
            var combo = Environment.GetEnvironmentVariable("QP_AAC_COMBO") ?? "none";
            if (combo is "avg" or "avgpayload" or "full")
            {
                MfNative.SetUInt32(audioOut, MfGuids.AudioAvgBytesPerSecond, (uint)aacBytes);
            }

            if (combo is "payload" or "avgpayload" or "full")
            {
                MfNative.SetUInt32(audioOut, MfGuids.AacPayloadType, 0);
            }

            if (combo == "full")
            {
                MfNative.SetUInt32(audioOut, MfGuids.AacProfileLevel, 0x29);
            }
            MfNative.SetUInt32(audioOut, MfGuids.AacPayloadType, 0);

            MfNative.SinkAddStream(_writer, audioOut, out _audioStream);

            MfNative.MFCreateMediaType(out var audioIn);
            MfNative.SetGuid(audioIn, MfGuids.MajorType, MfGuids.MediaTypeAudio);
            MfNative.SetGuid(audioIn, MfGuids.Subtype, MfGuids.AudioFormatPcm);
            MfNative.SetUInt32(audioIn, MfGuids.AudioBitsPerSample, 16);
            MfNative.SetUInt32(audioIn, MfGuids.AudioSamplesPerSecond, (uint)audioSampleRate);
            MfNative.SetUInt32(audioIn, MfGuids.AudioNumChannels, 2);
            MfNative.SetUInt32(audioIn, MfGuids.AudioBlockAlignment, 4);
            MfNative.SetUInt32(audioIn, MfGuids.AudioAvgBytesPerSecond, (uint)(audioSampleRate * 4));

            MfNative.SinkSetInputMediaType(_writer, _audioStream, audioIn);
        }

        MfNative.SinkBeginWriting(_writer);
        _started = true;
    }

    /// <summary>重封装模式：视频压缩透传（仅重写时间戳）。
    /// reencodeAudio=false 时音频也透传；=true 时音频走 PCM16→AAC 重编码——
    /// 倍速导出必须重编码音频：AAC 帧恒定解出 21.3ms 声音，纯透传压缩时间戳必致音画错位（实测坑）。</summary>
    public static Mp4SinkWriter CreateRemux(string outputPath, IntPtr videoNative, IntPtr audioNative,
        bool reencodeAudio = false, int audioSampleRate = 48000)
    {
        var w = new Mp4SinkWriter();
        MfNative.EnsureStarted();
        MfNative.MFCreateSinkWriterFromURL(outputPath, IntPtr.Zero, MfNative.CreateNoThrottleAttributes(), out w._writer);

        MfNative.SinkAddStream(w._writer, videoNative, out w._videoStream);
        MfNative.SinkSetInputMediaType(w._writer, w._videoStream, videoNative);
        if (audioNative != IntPtr.Zero && !reencodeAudio)
        {
            MfNative.SinkAddStream(w._writer, audioNative, out w._audioStream);
            MfNative.SinkSetInputMediaType(w._writer, w._audioStream, audioNative);
            w._hasAudio = true;
        }
        else if (audioNative != IntPtr.Zero)
        {
            // 音频重编码：AAC 输出（openscreen 配方，192kbps）← PCM16 输入（属性完整）
            MfNative.MFCreateMediaType(out var audioOut);
            MfNative.SetGuid(audioOut, MfGuids.MajorType, MfGuids.MediaTypeAudio);
            MfNative.SetGuid(audioOut, MfGuids.Subtype, MfGuids.AudioFormatAac);
            MfNative.SetUInt32(audioOut, MfGuids.AudioBitsPerSample, 16);
            MfNative.SetUInt32(audioOut, MfGuids.AudioSamplesPerSecond, (uint)audioSampleRate);
            MfNative.SetUInt32(audioOut, MfGuids.AudioNumChannels, 2);
            MfNative.SetUInt32(audioOut, MfGuids.AudioAvgBytesPerSecond, 24000);
            MfNative.SetUInt32(audioOut, MfGuids.AacPayloadType, 0);
            MfNative.SinkAddStream(w._writer, audioOut, out w._audioStream);

            MfNative.MFCreateMediaType(out var audioIn);
            MfNative.SetGuid(audioIn, MfGuids.MajorType, MfGuids.MediaTypeAudio);
            MfNative.SetGuid(audioIn, MfGuids.Subtype, MfGuids.AudioFormatPcm);
            MfNative.SetUInt32(audioIn, MfGuids.AudioBitsPerSample, 16);
            MfNative.SetUInt32(audioIn, MfGuids.AudioSamplesPerSecond, (uint)audioSampleRate);
            MfNative.SetUInt32(audioIn, MfGuids.AudioNumChannels, 2);
            MfNative.SetUInt32(audioIn, MfGuids.AudioBlockAlignment, 4);
            MfNative.SetUInt32(audioIn, MfGuids.AudioAvgBytesPerSecond, (uint)(audioSampleRate * 4));
            MfNative.SinkSetInputMediaType(w._writer, w._audioStream, audioIn);

            w._hasAudio = true;
            w._reencodeAudio = true;
        }

        MfNative.SinkBeginWriting(w._writer);
        w._started = true;
        return w;
    }

    private Mp4SinkWriter()
    {
    }

    /// <summary>写一帧（BGRA/RGB32，stride = width*4）。录制模式内部延迟一帧写入：
    /// 帧时长用相邻时间戳差（固定 1/fps 与真实间隔脱钩会令视频轨时长漂移 → 音画不同步，实测坑）。</summary>
    public void WriteFrame(byte[] bgra, long timeNs)
    {
        if (_pendingFrame != null)
        {
            var dur = timeNs - _pendingFrameTimeNs;
            if (dur <= 0)
            {
                dur = TicksPerSecond / _fps;
            }

            WriteSampleTo(true, _pendingFrame, _pendingFrameTimeNs, dur);
        }

        _pendingFrame = new byte[bgra.Length];
        Buffer.BlockCopy(bgra, 0, _pendingFrame, 0, bgra.Length);
        _pendingFrameTimeNs = timeNs;
    }

    private byte[]? _pendingFrame;
    private long _pendingFrameTimeNs;

    /// <summary>录制收尾：把最后一帧按兜底时长写出（Stop 时调用）</summary>
    public void FlushPendingFrame()
    {
        if (_pendingFrame != null)
        {
            WriteSampleTo(true, _pendingFrame, _pendingFrameTimeNs, TicksPerSecond / _fps);
            _pendingFrame = null;
        }
    }

    /// <summary>写一帧（显式时长，导出倍速用）</summary>
    public void WriteFrame(byte[] bgra, long timeNs, long durationNs) => WriteSampleTo(true, bgra, timeNs, durationNs);

    /// <summary>按流写样本（透传导出用：视频→视频轨，音频→音频轨）</summary>
    public void WriteSampleTo(bool video, byte[] data, long timeNs, long durationNs)
    {
        if (!_started || _finalized || _writer == IntPtr.Zero)
        {
            return;
        }

        var stream = video ? _videoStream : _audioStream;
        if (!video && !_hasAudio)
        {
            return;
        }

        var size = video && _videoStride > 0 ? _videoStride * _height : data.Length;
        var copyLength = Math.Min(size, data.Length);

        MfNative.MFCreateSample(out var sample);
        MfNative.MFCreateMemoryBuffer(Math.Max(1, copyLength), out var buffer);
        MfNative.BufferLock(buffer, out var ptr, out _, out _);
        try
        {
            if (video && _videoStride > 0)
            {
                // 录制帧：源为 top-down BGRA，MF 约定 RGB32 为 bottom-up（自底向上）——
                // 逐行倒序拷入，否则成品画面上下颠倒（实测坑）
                var stride = _videoStride;
                var rows = _height;
                for (var y = 0; y < rows; y++)
                {
                    var srcOffset = (rows - 1 - y) * stride;
                    Marshal.Copy(data, srcOffset, new IntPtr(ptr.ToInt64() + (long)y * stride), stride);
                }
            }
            else
            {
                // 透传样本：原样搬运
                Marshal.Copy(data, 0, ptr, copyLength);
            }
        }
        finally
        {
            MfNative.BufferUnlock(buffer);
        }

        MfNative.BufferSetCurrentLength(buffer, copyLength);
        MfNative.SampleAddBuffer(sample, buffer);
        MfNative.SampleSetTime(sample, timeNs);
        MfNative.SampleSetDuration(sample, durationNs);
        MfNative.SinkWriteSample(_writer, stream, sample);
    }

    /// <summary>写一段音频（PCM16 小端立体声），时间按累计样本数连续推算（录制用）</summary>
    public void WriteAudioPcm16(byte[] pcm, int byteCount, int sampleRate)
    {
        long time = _audioBytesWritten * TicksPerSecond / (sampleRate * 4L);
        long duration = byteCount * TicksPerSecond / (sampleRate * 4L);
        WriteAudioPcm16(pcm, byteCount, time, duration);
    }

    /// <summary>写一段音频（显式时间，导出倍速用）。byteCount=有效字节——
    /// 必须只写前 byteCount（曾整块缓冲喂入=重复旧数据掺入=声音发糙，实测 2 倍帧数）</summary>
    public void WriteAudioPcm16(byte[] pcm, int byteCount, long timeNs, long durationNs)
    {
        if (byteCount <= 0)
        {
            return;
        }

        if (byteCount >= pcm.Length)
        {
            WriteSampleTo(false, pcm, timeNs, durationNs);
        }
        else
        {
            var data = new byte[byteCount];
            Buffer.BlockCopy(pcm, 0, data, 0, byteCount);
            WriteSampleTo(false, data, timeNs, durationNs);
        }

        _audioBytesWritten += byteCount;
    }

    /// <summary>完成并封盘（必须调用，否则 MP4 索引缺失不可播）</summary>
    public void FinalizeFile()
    {
        if (!_started || _finalized || _writer == IntPtr.Zero)
        {
            return;
        }

        FlushPendingFrame();
        _finalized = true;
        MfNative.SinkDoFinalize(_writer);
    }

    public void Dispose()
    {
        try
        {
            FinalizeFile();
        }
        catch
        {
            // 收尾失败不阻塞释放
        }

        try
        {
            if (_writer != IntPtr.Zero)
            {
                Marshal.Release(_writer);
                _writer = IntPtr.Zero;
            }
        }
        catch
        {
            // 忽略
        }
    }
}
