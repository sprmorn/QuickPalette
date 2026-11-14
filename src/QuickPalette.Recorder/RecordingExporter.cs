namespace QuickPalette.Recorder;

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

/// <summary>
/// 录后导出：时间裁剪（掐头去尾）+ 倍速。
/// 视频：压缩流透传（无损、仅重写时间戳）。
/// 音频：speed=1 时透传；speed≠1 时**解码→重采样（音高随速）→AAC 重编码**——
/// AAC 帧恒定解出 21.3ms 声音，纯透传压缩时间戳必致音画错位（实测坑）。
/// 不裁不加速时直接复制文件。
/// </summary>
public static class RecordingExporter
{
    private const long Ticks = 10_000_000;
    private const int SampleRate = 48000;
    private const int SampleBytes = 4; // 16bit × 2ch

    /// <summary>trimStartSec/trimEndSec：保留区间（end≤0 = 到结尾）；speed ∈ [0.25, 4]</summary>
    public static void Export(string inputPath, string outputPath, double trimStartSec, double trimEndSec, double speed)
    {
        speed = Math.Clamp(speed, 0.25, 4.0);
        trimStartSec = Math.Max(0, trimStartSec);

        if (trimStartSec <= 0 && trimEndSec <= 0 && Math.Abs(speed - 1) < 0.001)
        {
            System.IO.File.Copy(inputPath, outputPath, overwrite: true);
            return;
        }

        MfNative.EnsureStarted();
        MfNative.MFCreateSourceReaderFromURL(inputPath, IntPtr.Zero, out var reader);

        // 识别视频/音频流
        var videoIdx = -1;
        var audioIdx = -1;
        var videoNative = IntPtr.Zero;
        var audioNative = IntPtr.Zero;
        for (var i = 0; i < 4; i++)
        {
            IntPtr mt;
            try
            {
                MfNative.ReaderGetNativeMediaType(reader, i, 0, out mt);
            }
            catch
            {
                continue;
            }

            MfNative.GetGuid(mt, MfGuids.MajorType, out var major);
            if (major == MfGuids.MediaTypeVideo && videoIdx < 0)
            {
                videoIdx = i;
                videoNative = mt;
            }
            else if (major == MfGuids.MediaTypeAudio && audioIdx < 0)
            {
                audioIdx = i;
                audioNative = mt;
            }

            MfNative.ReaderSetStreamSelection(reader, i, true);
        }

        if (videoIdx < 0 || videoNative == IntPtr.Zero)
        {
            throw new InvalidOperationException("导出失败：输入文件没有视频轨");
        }

        var reencodeAudio = audioIdx >= 0 && Math.Abs(speed - 1) >= 0.001;
        using var writer = Mp4SinkWriter.CreateRemux(outputPath, videoNative, audioNative, reencodeAudio, SampleRate);

        var trimStartTicks = (long)(trimStartSec * Ticks);
        var trimEndTicks = trimEndSec > 0 ? (long)(trimEndSec * Ticks) : long.MaxValue;

        // 视频：透传（时间戳按 (t-trimStart)/speed 线性映射）
        CopyStream(reader, videoIdx, writer, true, trimStartTicks, trimEndTicks, speed);

        if (audioIdx >= 0 && !reencodeAudio)
        {
            // 原速裁剪：音频透传安全
            CopyStream(reader, audioIdx, writer, false, trimStartTicks, trimEndTicks, speed);
        }
        else if (audioIdx >= 0)
        {
            // 倍速：音频解码→重采样→重编码（与视频同一时间映射，保证同步）
            TranscodeAudio(reader, audioIdx, writer, trimStartTicks, trimEndTicks, speed);
        }

        writer.FinalizeFile();
    }

    /// <summary>视频/音频压缩样本透传（时间戳重写；时长用相邻样本差前瞻推断）</summary>
    private static void CopyStream(IntPtr reader, int streamIndex, Mp4SinkWriter writer,
        bool video, long trimStart, long trimEnd, double speed)
    {
        long? pendingTs = null;
        byte[]? pendingData = null;
        long lastDuration = Ticks / 30;

        while (true)
        {
            MfNative.ReaderReadSample(reader, streamIndex, out _, out var flags, out var ts, out var sample);
            var end = (flags & 2) != 0 || sample == IntPtr.Zero;

            long tsLocal = 0;
            byte[]? data = null;
            if (!end)
            {
                tsLocal = ts;
                if (tsLocal >= trimStart && tsLocal <= trimEnd)
                {
                    MfNative.SampleToContiguousBuffer(sample, out var buffer);
                    MfNative.SampleGetTotalLength(sample, out var totalLength);
                    MfNative.BufferLock(buffer, out var ptr, out _, out var length);
                    try
                    {
                        var n = (int)Math.Min(length, totalLength);
                        data = new byte[n];
                        Marshal.Copy(ptr, data, 0, n);
                    }
                    finally
                    {
                        MfNative.BufferUnlock(buffer);
                        MfNative.Release(buffer); // COM 用完即放
                    }
                }

                MfNative.Release(sample);
            }

            if (pendingTs.HasValue && pendingData != null)
            {
                long duration = end ? lastDuration : tsLocal - pendingTs.Value;
                if (duration <= 0)
                {
                    duration = lastDuration;
                }

                lastDuration = duration;
                var outTime = (long)((pendingTs.Value - trimStart) / speed);
                writer.WriteSampleTo(video, pendingData, outTime, (long)(duration / speed));
                pendingTs = null;
                pendingData = null;
            }

            if (end)
            {
                break;
            }

            if (data != null)
            {
                pendingTs = tsLocal;
                pendingData = data;
            }
        }
    }

    /// <summary>
    /// 音频变速重编码：解码 PCM16/48k/立体声 → 按时间戳铺时间线 → 按 (t-trimStart)/speed 取值重采样 → AAC。
    /// 用绝对时间定位（而非采样累计），免疫 AAC 编解码 priming/填充造成的采样膨胀（实测坑）；
    /// 与视频时间映射完全一致 → 音画同步。
    /// </summary>
    private static void TranscodeAudio(IntPtr reader, int audioIdx, Mp4SinkWriter writer,
        long trimStart, long trimEnd, double speed)
    {
        // 解码为 PCM16/48k/立体声
        MfNative.MFCreateMediaType(out var pcm);
        MfNative.SetGuid(pcm, MfGuids.MajorType, MfGuids.MediaTypeAudio);
        MfNative.SetGuid(pcm, MfGuids.Subtype, MfGuids.AudioFormatPcm);
        MfNative.SetUInt32(pcm, MfGuids.AudioBitsPerSample, 16);
        MfNative.SetUInt32(pcm, MfGuids.AudioSamplesPerSecond, SampleRate);
        MfNative.SetUInt32(pcm, MfGuids.AudioNumChannels, 2);
        MfNative.SetUInt32(pcm, MfGuids.AudioBlockAlignment, 4);
        MfNative.SetUInt32(pcm, MfGuids.AudioAvgBytesPerSecond, SampleRate * 4);
        MfNative.ReaderSetCurrentMediaType(reader, audioIdx, pcm);

        // 时间线：按每个解码样本自带 ts 落位（不依赖采样累计——编解码器填充会令累计膨胀）
        byte[] timeline = Array.Empty<byte>();
        long timelineSamples = 0;
        while (true)
        {
            MfNative.ReaderReadSample(reader, audioIdx, out _, out var flags, out var ts, out var sample);
            if ((flags & 2) != 0 || sample == IntPtr.Zero)
            {
                break;
            }

            MfNative.SampleToContiguousBuffer(sample, out var buffer);
            MfNative.SampleGetTotalLength(sample, out var totalLength);
            MfNative.BufferLock(buffer, out var ptr, out _, out var length);
            byte[] tmp;
            try
            {
                var n = (int)Math.Min(length, totalLength);
                tmp = new byte[n];
                Marshal.Copy(ptr, tmp, 0, n);
            }
            finally
            {
                MfNative.BufferUnlock(buffer);
                MfNative.Release(buffer); // COM 用完即放
            }

            MfNative.Release(sample);

            var baseSample = (long)Math.Round(ts / 1e7 * SampleRate);
            var sampleCount = tmp.Length / SampleBytes;
            var needed = (baseSample + sampleCount + 2) * SampleBytes;
            if (needed > timeline.Length)
            {
                Array.Resize(ref timeline, (int)Math.Min(int.MaxValue - 64, needed + SampleRate * SampleBytes));
            }

            Buffer.BlockCopy(tmp, 0, timeline, (int)(baseSample * SampleBytes), tmp.Length);
            timelineSamples = Math.Max(timelineSamples, baseSample + sampleCount);
        }

        if (timelineSamples < 2)
        {
            return;
        }

        var trimStartSec = trimStart / 1e7;
        var trimEndSec = trimEnd / 1e7;

        const int chunkSamples = SampleRate / 20; // 20ms 一块
        var chunk = new byte[chunkSamples * SampleBytes];
        var chunkIndex = 0;
        long outStart = 0;

        for (long k = 0; ; k++)
        {
            var tSec = trimStartSec + k * speed / SampleRate;
            if (tSec >= trimEndSec)
            {
                break;
            }

            var p = (tSec - trimStartSec * 0) * SampleRate; // 源采样位置（绝对）
            p = tSec * SampleRate;
            if (p >= timelineSamples - 1)
            {
                break;
            }

            if (p < 0)
            {
                outStart = k + 1;
                continue;
            }

            // 线性插值（立体声：L/R 各自插值）——倍速即重采样，音高随速
            var i0 = (long)p;
            var frac = (float)(p - i0);
            var o = chunkIndex * SampleBytes;
            for (var ch = 0; ch < 2; ch++)
            {
                var i0off = (int)(i0 * SampleBytes) + ch * 2;
                var i1off = (int)((i0 + 1) * SampleBytes) + ch * 2;
                var s0 = (short)(timeline[i0off] | (timeline[i0off + 1] << 8));
                var s1 = (short)(timeline[i1off] | (timeline[i1off + 1] << 8));
                var v = (short)(s0 + (s1 - s0) * frac);
                chunk[o + ch * 2] = (byte)(v & 0xFF);
                chunk[o + ch * 2 + 1] = (byte)((v >> 8) & 0xFF);
            }

            chunkIndex++;
            if (chunkIndex == chunkSamples)
            {
                FlushChunk(writer, chunk, chunkSamples, outStart);
                outStart = k + 1;
                chunkIndex = 0;
            }
        }

        if (chunkIndex > 0)
        {
            var tail = new byte[chunkIndex * SampleBytes];
            Array.Copy(chunk, tail, tail.Length);
            FlushChunk(writer, tail, chunkIndex, outStart);
        }

        Trace.TraceInformation($"TranscodeAudio: timelineSamples={timelineSamples} ({timelineSamples / (double)SampleRate:0.000}s)");
    }

    private static void FlushChunk(Mp4SinkWriter writer, byte[] data, int samples, long startSample)
    {
        var timeNs = startSample * Ticks / SampleRate;
        var durNs = samples * Ticks / SampleRate;
        writer.WriteAudioPcm16(data, data.Length, timeNs, durNs);
    }
}
