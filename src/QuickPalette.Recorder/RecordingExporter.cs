namespace QuickPalette.Recorder;

using System.Diagnostics;
using System.Runtime.InteropServices;

/// <summary>
/// 录后导出：时间裁剪（掐头去尾）+ 倍速（时间戳重写，音高随速变化）。
/// 压缩流透传重封装（H.264/AAC 原样搬运）——无损、免解码、速度快。
/// 不裁不加速时直接复制文件。互操作走 vtable 直调（免疫 ComWrappers）。
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

        MfNative.EnsureStarted();
        try
        {
            MfNative.MFCreateSourceReaderFromURL(inputPath, IntPtr.Zero, out var reader);

            // 识别视频/音频流（压缩原生类型即输出类型）
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

            using var writer = Mp4SinkWriter.CreateRemux(outputPath, videoNative, audioNative);

            var trimStartTicks = (long)(trimStartSec * Ticks);
            var trimEndTicks = trimEndSec > 0 ? (long)(trimEndSec * Ticks) : long.MaxValue;

            CopyStream(reader, videoIdx, writer, true, trimStartTicks, trimEndTicks, speed);
            if (audioIdx >= 0)
            {
                CopyStream(reader, audioIdx, writer, false, trimStartTicks, trimEndTicks, speed);
            }

            writer.FinalizeFile();
        }
        finally
        {
        }
    }

    private static void CopyStream(IntPtr reader, int streamIndex, Mp4SinkWriter writer,
        bool video, long trimStart, long trimEnd, double speed)
    {
        // 前瞻一帧：透传时长用相邻时间戳差（解封装常缺 duration，兜底值会导致 MP4 轨不一致）
        long? pendingTs = null;
        byte[]? pendingData = null;
        long lastDuration = Ticks / 30;
        long readMs = 0, writeMs = 0;
        int count = 0;

        while (true)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            MfNative.ReaderReadSample(reader, streamIndex, out _, out var flags, out var ts, out var sample);
            readMs += sw.ElapsedMilliseconds;
            var end = (flags & 2) != 0 || sample == IntPtr.Zero; // MF_SOURCE_READERF_ENDOFSTREAM

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
                    }
                }
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
                var sw2 = System.Diagnostics.Stopwatch.StartNew();
                writer.WriteSampleTo(video, pendingData, outTime, (long)(duration / speed));
                writeMs += sw2.ElapsedMilliseconds;
                count++;
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

        System.Diagnostics.Trace.TraceInformation(
            $"CopyStream({(video ? "video" : "audio")}): {count} samples, read={readMs}ms, write={writeMs}ms");
    }
}
