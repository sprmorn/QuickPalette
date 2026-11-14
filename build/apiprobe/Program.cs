using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.Wave;
using QuickPalette.Recorder;
using Serilog;

class SilenceCheck
{
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(long point, uint flags);

    static void Main()
    {
        System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.ConsoleTraceListener());
        var logger = new LoggerConfiguration().MinimumLevel.Debug().CreateLogger();

        // 连续音乐感信号（连续正弦，不应有静音）+ 录 5 秒
        var tone = ContinuousTone(48000, 5.5);
        var stream = new RawSourceWaveStream(new MemoryStream(tone), new WaveFormat(48000, 16, 2));
        var player = new WaveOutEvent();
        player.Init(stream);
        player.Play();

        var out1 = Path.Combine(Path.GetTempPath(), "qp_silence.mp4");
        File.Delete(out1);
        var hmon = MonitorFromPoint(0, 0);
        var options = new RecordingOptions(hmon, 400, 300, 500, 300, 2560, 1600, 30, 2_000_000, true, true, out1);
        using (var session = new RecordingSession(options, logger))
        {
            Thread.Sleep(5200);
            session.Stop();
        }
        player.Stop();
        Console.WriteLine($"[1] 录制(连续音) ✓");

        var pcm = DecodeAudio(out1, out var sec);
        Console.WriteLine($"[2] 解码音频 {sec:0.00}s");

        // 10ms 窗 RMS → 静音段统计
        var frames = pcm.Length / 4;
        var gaps = new List<(double Start, double Len)>();
        double cur = 0;
        for (var f = 0; f < frames; f += 480)
        {
            double sum = 0;
            for (var k = 0; k < 480 && f + k < frames; k++)
            {
                var s = (short)(pcm[(f + k) * 4] | (pcm[(f + k) * 4 + 1] << 8));
                sum += s * s;
            }

            var rms = Math.Sqrt(sum / 480) / 32768.0;
            if (rms < 0.01)
            {
                cur += 0.01;
            }
            else if (cur > 0)
            {
                gaps.Add((f / 48000.0 - cur, cur));
                cur = 0;
            }
        }

        if (cur > 0) gaps.Add((frames / 48000.0 - cur, cur));
        Console.WriteLine($"[3] 静音段 {gaps.Count} 个: [{string.Join(", ", gaps.Select(g => $"{g.Start * 1000:0}ms起/{g.Len * 1000:0}ms"))}]");
        var midGaps = gaps.Count(g => g.Start > 0.3 && g.Len < 0.05);
        Console.WriteLine(midGaps == 0
            ? "[✓] 中段无短静音块 —— 声音连续（顿音修复；开头空白为播放器启动延迟）"
            : $"[✗] 中段有 {midGaps} 个 <50ms 短静音块 —— 顿音仍在");
    }

    static byte[] ContinuousTone(int rate, double sec)
    {
        var frames = (int)(rate * sec);
        var buf = new byte[frames * 4];
        for (var i = 0; i < frames; i++)
        {
            var v = (short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 8000);
            buf[i * 4] = (byte)(v & 0xFF);
            buf[i * 4 + 1] = (byte)((v >> 8) & 0xFF);
            buf[i * 4 + 2] = buf[i * 4];
            buf[i * 4 + 3] = buf[i * 4 + 1];
        }

        return buf;
    }

    static byte[] DecodeAudio(string path, out double sec)
    {
        MfNative.EnsureStarted();
        MfNative.MFCreateSourceReaderFromURL(path, IntPtr.Zero, out var reader);
        var audioIdx = -1;
        for (var i = 0; i < 4; i++)
        {
            try
            {
                MfNative.ReaderGetNativeMediaType(reader, i, 0, out var mt);
                MfNative.GetGuid(mt, MfGuids.MajorType, out var major);
                if (major == MfGuids.MediaTypeAudio && audioIdx < 0) audioIdx = i;
            }
            catch { }
        }

        MfNative.MFCreateMediaType(out var pcm);
        MfNative.SetGuid(pcm, MfGuids.MajorType, MfGuids.MediaTypeAudio);
        MfNative.SetGuid(pcm, MfGuids.Subtype, MfGuids.AudioFormatPcm);
        MfNative.SetUInt32(pcm, MfGuids.AudioBitsPerSample, 16);
        MfNative.SetUInt32(pcm, MfGuids.AudioSamplesPerSecond, 48000);
        MfNative.SetUInt32(pcm, MfGuids.AudioNumChannels, 2);
        MfNative.SetUInt32(pcm, MfGuids.AudioBlockAlignment, 4);
        MfNative.SetUInt32(pcm, MfGuids.AudioAvgBytesPerSecond, 192000);
        MfNative.ReaderSetCurrentMediaType(reader, audioIdx, pcm);

        var all = new MemoryStream();
        while (true)
        {
            MfNative.ReaderReadSample(reader, audioIdx, out _, out var flags, out _, out var sample);
            if ((flags & 2) != 0 || sample == IntPtr.Zero) break;
            MfNative.SampleToContiguousBuffer(sample, out var buffer);
            MfNative.SampleGetTotalLength(sample, out var totalLength);
            MfNative.BufferLock(buffer, out var ptr, out _, out var length);
            try
            {
                var n = (int)Math.Min(length, totalLength);
                var tmp = new byte[n];
                Marshal.Copy(ptr, tmp, 0, n);
                all.Write(tmp, 0, n);
            }
            finally { MfNative.BufferUnlock(buffer); }
        }

        sec = all.Length / 4.0 / 48000;
        return all.ToArray();
    }
}
