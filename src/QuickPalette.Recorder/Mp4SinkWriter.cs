namespace QuickPalette.Recorder;

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

        MfNative.MFStartup(MfNative.Version, 0);
        MfNative.MFCreateSinkWriterFromURL(outputPath, IntPtr.Zero, IntPtr.Zero, out _writer);

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

        // ---- 音频：输出 AAC ← 输入 PCM16（输出仅 major+subtype，多余属性会被编码器拒收）----
        if (_hasAudio)
        {
            MfNative.MFCreateMediaType(out var audioOut);
            MfNative.SetGuid(audioOut, MfGuids.MajorType, MfGuids.MediaTypeAudio);
            MfNative.SetGuid(audioOut, MfGuids.Subtype, MfGuids.AudioFormatAac);

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

    /// <summary>重封装模式：输入输出同为压缩类型（H.264/AAC 透传，仅重写时间戳）</summary>
    public static Mp4SinkWriter CreateRemux(string outputPath, IntPtr videoNative, IntPtr audioNative)
    {
        var w = new Mp4SinkWriter();
        MfNative.MFStartup(MfNative.Version, 0);
        MfNative.MFCreateSinkWriterFromURL(outputPath, IntPtr.Zero, IntPtr.Zero, out w._writer);

        MfNative.SinkAddStream(w._writer, videoNative, out w._videoStream);
        MfNative.SinkSetInputMediaType(w._writer, w._videoStream, videoNative);
        if (audioNative != IntPtr.Zero)
        {
            MfNative.SinkAddStream(w._writer, audioNative, out w._audioStream);
            MfNative.SinkSetInputMediaType(w._writer, w._audioStream, audioNative);
            w._hasAudio = true;
        }

        MfNative.SinkBeginWriting(w._writer);
        w._started = true;
        return w;
    }

    private Mp4SinkWriter()
    {
    }

    /// <summary>写一帧（BGRA/RGB32，stride = width*4）</summary>
    public void WriteFrame(byte[] bgra, long timeNs) => WriteFrame(bgra, timeNs, TicksPerSecond / _fps);

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
            Marshal.Copy(data, 0, ptr, copyLength);
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

    /// <summary>写一段音频（显式时间，导出倍速用）</summary>
    public void WriteAudioPcm16(byte[] pcm, int byteCount, long timeNs, long durationNs)
    {
        if (byteCount <= 0)
        {
            return;
        }

        WriteSampleTo(false, pcm, timeNs, durationNs);
        _audioBytesWritten += byteCount;
    }

    /// <summary>完成并封盘（必须调用，否则 MP4 索引缺失不可播）</summary>
    public void FinalizeFile()
    {
        if (!_started || _finalized || _writer == IntPtr.Zero)
        {
            return;
        }

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

            MfNative.MFShutdown();
        }
        catch
        {
            // 忽略
        }
    }
}
