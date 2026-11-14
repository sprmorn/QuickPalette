using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.Wave;
using QuickPalette.Recorder;
using Serilog;

class SyncVerify
{
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(long point, uint flags);

    static void Main()
    {
        var logger = new LoggerConfiguration().MinimumLevel.Debug().CreateLogger();

        // 节拍音：每 0.5s 一声 1kHz 短哔（12 声）经扬声器播放（内录捕获）
        var beeps = GenerateBeeps(48000, 12, 0.5, 0.08);
        var stream = new RawSourceWaveStream(new MemoryStream(beeps), new WaveFormat(48000, 16, 2));
        var player = new WaveOutEvent();
        player.Init(stream);
        player.Play();

        var out1 = Path.Combine(Path.GetTempPath(), "qp_sync.mp4");
        File.Delete(out1);
        var hmon = MonitorFromPoint(0, 0);
        var options = new RecordingOptions(hmon, 400, 300, 500, 300, 2560, 1600, 30, 2_000_000, true, true, out1);
        using (var session = new RecordingSession(options, logger))
        {
            Thread.Sleep(6200);
            session.Stop();
        }
        player.Stop();
        Console.WriteLine($"[1] 录制(带节拍声) ✓ {new FileInfo(out1).Length} 字节");

        var out2 = Path.Combine(Path.GetTempPath(), "qp_sync_2x.mp4");
        File.Delete(out2);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        RecordingExporter.Export(out1, out2, 0.3, 0, 2.0);
        Console.WriteLine($"[2] 导出 2x ✓ {new FileInfo(out2).Length} 字节，耗时 {sw.ElapsedMilliseconds}ms");

        var intervals = DetectBeepIntervals(out2, out var audioSec);
        Console.WriteLine($"[3] 导出音频 {audioSec:0.00}s，间隔样本 {intervals.Count} 个");
        foreach (var it in intervals.Take(8)) Console.WriteLine($"      间隔 {it * 1000:0}ms");
        var avg = intervals.Count > 0 ? intervals.Average() : 0;
        if (intervals.Count >= 3 && avg > 0.20 && avg < 0.30)
            Console.WriteLine($"[✓] 哔声间隔均值 ≈{avg * 1000:0}ms（期望≈250ms=0.5s×½）——音频变速正确、与视频同时间映射");
        else
            Console.WriteLine($"[✗] 间隔异常：avg={avg * 1000:0}ms（期望≈250ms）");
    }

    static byte[] GenerateBeeps(int rate, int count, double intervalSec, double beepSec)
    {
        var total = (int)(rate * (count * intervalSec + 0.5)) * 4;
        var buf = new byte[total];
        for (var b = 0; b < count; b++)
        {
            var start = (int)(b * intervalSec * rate);
            var len = (int)(beepSec * rate);
            for (var i = 0; i < len; i++)
            {
                var v = (short)(Math.Sin(2 * Math.PI * 1000 * i / rate) * 12000 * Math.Min(1, i / 200.0));
                var off = (start + i) * 4;
                if (off + 3 < buf.Length)
                {
                    buf[off] = (byte)(v & 0xFF);
                    buf[off + 1] = (byte)((v >> 8) & 0xFF);
                    buf[off + 2] = buf[off];
                    buf[off + 3] = buf[off + 1];
                }
            }
        }

        return buf;
    }

    static List<double> DetectBeepIntervals(string path, out double audioSec)
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

        var result = new List<double>();
        audioSec = 0;
        if (audioIdx < 0) return result;

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
            MfNative.BufferLock(buffer, out var ptr, out _, out var length);
            try
            {
                var tmp = new byte[length];
                Marshal.Copy(ptr, tmp, 0, length);
                all.Write(tmp, 0, length);
            }
            finally { MfNative.BufferUnlock(buffer); }
        }

        var data = all.ToArray();
        var frames = data.Length / 4;
        audioSec = frames / 48000.0;

        var inBeep = false;
        double lastRise = -1;
        for (var f = 0; f < frames; f += 480) // 10ms 窗
        {
            double sum = 0;
            for (var k = 0; k < 480 && f + k < frames; k++)
            {
                var s = (short)(data[(f + k) * 4] | (data[(f + k) * 4 + 1] << 8));
                sum += s * s;
            }

            var rms = Math.Sqrt(sum / 480) / 32768.0;
            var loud = rms > 0.02;
            if (loud && !inBeep)
            {
                var t = f / 48000.0;
                if (lastRise >= 0) result.Add(t - lastRise);
                lastRise = t;
                inBeep = true;
            }
            else if (!loud && inBeep)
            {
                inBeep = false;
            }
        }

        return result;
    }
}
