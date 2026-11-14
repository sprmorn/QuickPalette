namespace QuickPalette.Recorder;

using System.Runtime.InteropServices;
using NAudio.MediaFoundation;

/// <summary>
/// Media Foundation SinkWriter 封装（基于 NAudio 的官方签名互操作）：
/// H.264(视频) + AAC(音频) → MP4。视频输入 BGRA/RGB32，音频输入 PCM16/48k/立体声。
/// 失败自动抛 COMException（NAudio 接口的语义）。
/// </summary>
public sealed class Mp4SinkWriter : IDisposable
{
    private const long TicksPerSecond = 10_000_000;

    private IMFSinkWriter _writer;
    private int _videoStream;
    private int _audioStream;
    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private bool _hasAudio;
    private readonly int _videoStride;
    private long _audioBytesWritten;
    private bool _started;
    private bool _finalized;

    public Mp4SinkWriter(string outputPath, int width, int height, int fps, int videoBitrate,
        bool hasAudio, int audioSampleRate = 48000)
    {
        _width = width;
        _height = height;
        _fps = Math.Max(1, fps);
        _hasAudio = hasAudio;
        _videoStride = width * 4;

        MediaFoundationApi.Startup();

        MediaFoundationInterop.MFCreateSinkWriterFromURL(outputPath, null, null, out var sinkWriter);
        _writer = sinkWriter;

        // ---- 视频：输出 H.264 ← 输入 RGB32 ----
        var videoOut = MediaFoundationApi.CreateMediaType();
        videoOut.SetGUID(MfGuids.MajorType, MfGuids.MediaTypeVideo);
        videoOut.SetGUID(MfGuids.Subtype, MfGuids.VideoFormatH264);
        SetUInt64(videoOut, MfGuids.FrameSize, ((ulong)(uint)width << 32) | (uint)height);
        SetUInt64(videoOut, MfGuids.FrameRate, ((ulong)(uint)_fps << 32) | 1u);
        SetUInt64(videoOut, MfGuids.PixelAspectRatio, (1ul << 32) | 1u);
        videoOut.SetUINT32(MfGuids.AvgBitrate, (int)(uint)Math.Max(500_000, videoBitrate));
        videoOut.SetUINT32(MfGuids.InterlaceMode, (int)2); // Progressive

        _writer.AddStream(videoOut, out _videoStream);

        var videoIn = MediaFoundationApi.CreateMediaType();
        videoIn.SetGUID(MfGuids.MajorType, MfGuids.MediaTypeVideo);
        videoIn.SetGUID(MfGuids.Subtype, MfGuids.VideoFormatRgb32);
        SetUInt64(videoIn, MfGuids.FrameSize, ((ulong)(uint)width << 32) | (uint)height);
        SetUInt64(videoIn, MfGuids.FrameRate, ((ulong)(uint)_fps << 32) | 1u);
        videoIn.SetUINT32(MfGuids.InterlaceMode, (int)2);

        _writer.SetInputMediaType(_videoStream, videoIn, null);

        // ---- 音频：输出 AAC ← 输入 PCM16 ----
        _audioStream = -1;
        if (_hasAudio)
        {
            var audioOut = MediaFoundationApi.CreateMediaType();
            audioOut.SetGUID(MfGuids.MajorType, MfGuids.MediaTypeAudio);
            audioOut.SetGUID(MfGuids.Subtype, MfGuids.AudioFormatAac);
            audioOut.SetUINT32(MfGuids.AudioBitsPerSample, (int)16);
            audioOut.SetUINT32(MfGuids.AudioSamplesPerSecond, (int)(uint)audioSampleRate);
            audioOut.SetUINT32(MfGuids.AudioNumChannels, (int)2);
            audioOut.SetUINT32(MfGuids.AudioAvgBytesPerSecond, (int)24000); // ~192kbps
            audioOut.SetUINT32(MfGuids.AacPayloadType, (int)0);             // 原始 AAC（MP4 容器）
            audioOut.SetUINT32(MfGuids.AacProfileLevel, (int)0x29);         // AAC-LC

            _writer.AddStream(audioOut, out _audioStream);

            var audioIn = MediaFoundationApi.CreateMediaType();
            audioIn.SetGUID(MfGuids.MajorType, MfGuids.MediaTypeAudio);
            audioIn.SetGUID(MfGuids.Subtype, MfGuids.AudioFormatPcm);
            audioIn.SetUINT32(MfGuids.AudioBitsPerSample, (int)16);
            audioIn.SetUINT32(MfGuids.AudioSamplesPerSecond, (int)(uint)audioSampleRate);
            audioIn.SetUINT32(MfGuids.AudioNumChannels, (int)2);

            _writer.SetInputMediaType(_audioStream, audioIn, null);
        }

        _writer.BeginWriting();
        _started = true;
    }

    /// <summary>重封装模式：输入输出同为压缩类型（H.264/AAC 透传，仅重写时间戳）。
    /// 用于导出裁剪/倍速——无损且免解码。</summary>
    public static Mp4SinkWriter CreateRemux(string outputPath, IMFMediaType videoNative, IMFMediaType? audioNative)
    {
        var w = new Mp4SinkWriter();
        MediaFoundationApi.Startup();
        try
        {
            MediaFoundationInterop.MFCreateSinkWriterFromURL(outputPath, null, null, out var sinkWriter);
            w._writer = sinkWriter;
            w._writer.AddStream(videoNative, out w._videoStream);
            w._writer.SetInputMediaType(w._videoStream, videoNative, null);
            if (audioNative != null)
            {
                w._writer.AddStream(audioNative, out w._audioStream);
                w._writer.SetInputMediaType(w._audioStream, audioNative, null);
                w._hasAudio = true;
            }

            w._writer.BeginWriting();
            w._started = true;
            return w;
        }
        catch
        {
            MediaFoundationApi.Shutdown();
            throw;
        }
    }

    private Mp4SinkWriter()
    {
        _width = 0;
        _height = 0;
        _fps = 30;
        _videoStride = 0;
    }

    /// <summary>写一帧（BGRA/RGB32，stride = width*4）</summary>
    public void WriteFrame(byte[] bgra, long timeNs) => WriteFrame(bgra, timeNs, TicksPerSecond / _fps);

    /// <summary>写一帧（显式时长，导出倍速用）</summary>
    public void WriteFrame(byte[] bgra, long timeNs, long durationNs)
    {
        if (!_started || _finalized)
        {
            return;
        }

        var sample = MediaFoundationApi.CreateSample();
        var size = _videoStride > 0 ? _videoStride * _height : bgra.Length;
        var buffer = MediaFoundationApi.CreateMemoryBuffer(Math.Max(1, Math.Min(size, bgra.Length)));
        buffer.Lock(out var ptr, out _, out _);
        try
        {
            Marshal.Copy(bgra, 0, ptr, Math.Min(bgra.Length, size));
        }
        finally
        {
            buffer.Unlock();
        }

        buffer.SetCurrentLength(Math.Min(size, bgra.Length));
        sample.AddBuffer(buffer);
        sample.SetSampleTime(timeNs);
        sample.SetSampleDuration(durationNs);
        _writer.WriteSample(_videoStream, sample);
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
        if (!_started || _finalized || !_hasAudio || byteCount <= 0)
        {
            return;
        }

        var sample = MediaFoundationApi.CreateSample();
        var buffer = MediaFoundationApi.CreateMemoryBuffer(byteCount);
        buffer.Lock(out var ptr, out _, out _);
        try
        {
            Marshal.Copy(pcm, 0, ptr, byteCount);
        }
        finally
        {
            buffer.Unlock();
        }

        buffer.SetCurrentLength(byteCount);
        sample.AddBuffer(buffer);
        sample.SetSampleTime(timeNs);
        sample.SetSampleDuration(durationNs);
        _writer.WriteSample(_audioStream, sample);
        _audioBytesWritten += byteCount;
    }

    /// <summary>完成并封盘（必须调用，否则 MP4 索引缺失不可播）</summary>
    public void FinalizeFile()
    {
        if (!_started || _finalized)
        {
            return;
        }

        _finalized = true;
        _writer.DoFinalize();
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
            Marshal.FinalReleaseComObject(_writer);
            MediaFoundationApi.Shutdown();
        }
        catch
        {
            // 忽略
        }
    }

    private static void SetUInt64(IMFMediaType type, Guid key, ulong value)
    {
        var k = key;
        type.SetUINT64(k, (long)value);
    }
}
