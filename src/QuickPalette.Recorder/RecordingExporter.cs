namespace QuickPalette.Recorder;

using System.Runtime.InteropServices;
using NAudio.MediaFoundation;

/// <summary>
/// 录后导出：时间裁剪（掐头去尾）+ 倍速（时间戳重写，音高随速变化）。
/// 实现为**压缩流透传重封装**（H.264/AAC 原样搬运、仅重写时间戳）——无损、免解码、速度快。
/// 不裁不加速时直接复制文件。
/// </summary>
public static class RecordingExporter
{
    private const long Ticks = 10_000_000;

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

        MediaFoundationApi.Startup();
        try
        {
            MediaFoundationInterop.MFCreateSourceReaderFromURL(inputPath, null, out var reader);

            // 识别视频/音频流（压缩原生类型即输出类型）
            var videoIdx = -1;
            var audioIdx = -1;
            IMFMediaType? videoNative = null;
            IMFMediaType? audioNative = null;
            for (var i = 0; i < 4; i++)
            {
                IMFMediaType mt;
                try
                {
                    reader.GetNativeMediaType(i, 0, out mt);
                }
                catch
                {
                    continue;
                }

                var k = MfGuids.MajorType;
                mt.GetGUID(k, out var major);
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

                reader.SetStreamSelection(i, true);
            }

            if (videoIdx < 0 || videoNative == null)
            {
                throw new InvalidOperationException("导出失败：输入文件没有视频轨");
            }

            using var writer = Mp4SinkWriter.CreateRemux(outputPath, videoNative, audioNative);

            var trimStartTicks = (long)(trimStartSec * Ticks);
            var trimEndTicks = trimEndSec > 0 ? (long)(trimEndSec * Ticks) : long.MaxValue;

            // 视频、音频各自顺序搬运（SinkWriter 按时间戳对齐）
            CopyStream(reader, videoIdx, writer, trimStartTicks, trimEndTicks, speed);
            if (audioIdx >= 0)
            {
                CopyStream(reader, audioIdx, writer, trimStartTicks, trimEndTicks, speed);
            }

            writer.FinalizeFile();
        }
        finally
        {
            MediaFoundationApi.Shutdown();
        }
    }

    private static void CopyStream(IMFSourceReader reader, int streamIndex, Mp4SinkWriter writer,
        long trimStart, long trimEnd, double speed)
    {
        long fallbackDuration = Ticks / 30;
        while (true)
        {
            reader.ReadSample(streamIndex, 0, out _, out var flags, out var tsU, out var sample);
            if (((int)flags & 2) != 0 || sample == null) // MF_SOURCE_READERF_ENDOFSTREAM
            {
                break;
            }

            var ts = (long)tsU;
            if (ts < trimStart)
            {
                continue;
            }

            if (ts > trimEnd)
            {
                break;
            }

            sample.GetSampleDuration(out var duration);
            if (duration <= 0)
            {
                duration = fallbackDuration;
            }

            fallbackDuration = duration;
            var outTime = (long)((ts - trimStart) / speed);
            var outDuration = (long)(duration / speed);

            sample.ConvertToContiguousBuffer(out var buffer);
            buffer.Lock(out var ptr, out _, out var length);
            byte[] data;
            try
            {
                data = new byte[length];
                Marshal.Copy(ptr, data, 0, length);
            }
            finally
            {
                buffer.Unlock();
            }

            writer.WriteFrame(data, outTime, outDuration);
        }
    }
}
