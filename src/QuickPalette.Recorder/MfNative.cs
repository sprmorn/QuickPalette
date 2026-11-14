namespace QuickPalette.Recorder;

using System.Runtime.InteropServices;

/// <summary>
/// Media Foundation 原生互操作（IntPtr + vtable 委托直调）。
/// 不经 COM RCW/ComWrappers——WPF 应用内 CsWinRT 注册的 ComWrappers 会让
/// [ComImport] 经典封送在 QI 时报 E_NOINTERFACE（实测坑），vtable 直调免疫。
/// vtable 槽位以 NAudio.MediaFoundation 的接口声明顺序为准（生产验证过）。
/// </summary>
public static class MfNative
{
    /// <summary>MF_VERSION（MFSDK 2.0）</summary>
    public const uint Version = 0x00020070;

    // ---- 工厂 ----

    private static readonly object StartupLock = new();
    private static bool _mfStarted;

    /// <summary>进程内只启动一次、永不关闭：反复 MFShutdown/MFStartup 后
    /// 编码器 MFT 协商会报 MF_E_INVALIDMEDIATYPE（实测：二次录制失败）</summary>
    public static void EnsureStarted()
    {
        lock (StartupLock)
        {
            if (!_mfStarted)
            {
                MFStartup(Version, 0);
                _mfStarted = true;
            }
        }
    }

    [DllImport("mfplat.dll")]
    public static extern int MFStartup(uint version, uint flags);

    /// <summary>释放 COM 对象（vtable 槽 2 = IUnknown::Release）。
    /// 不放 = 每帧泄漏 sample+buffer（实测 90MB/秒），录制必爆内存。</summary>
    public static void Release(IntPtr p)
    {
        if (p != IntPtr.Zero)
        {
            Fn<ReleaseFn>(p, 2)(p);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ReleaseFn(IntPtr self);

    [DllImport("mfplat.dll")]
    public static extern int MFShutdown();

    [DllImport("mfplat.dll")]
    public static extern int MFCreateMediaType(out IntPtr type);

    [DllImport("mfplat.dll")]
    public static extern int MFCreateAttributes(out IntPtr attributes, int initialSize);

    [DllImport("mfplat.dll")]
    public static extern int MFCreateSample(out IntPtr sample);

    [DllImport("mfplat.dll")]
    public static extern int MFCreateMemoryBuffer(int maxLength, out IntPtr buffer);

    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
    public static extern int MFCreateSinkWriterFromURL(string outputUrl, IntPtr byteStream, IntPtr attributes, out IntPtr sinkWriter);

    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
    public static extern int MFCreateSourceReaderFromURL(string inputUrl, IntPtr attributes, out IntPtr reader);

    // ---- vtable 取函数指针 ----

    public static T Fn<T>(IntPtr self, int slot) where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(self);
        var method = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(method);
    }

    // ---- IMFAttributes（槽位依 NAudio 声明序）----

    /// <summary>创建禁用节流的 SinkWriter 属性集（否则写样本被限速，导出奇慢）</summary>
    public static IntPtr CreateNoThrottleAttributes()
    {
        Check(MFCreateAttributes(out var attrs, 1));
        SetUInt32(attrs, MfGuids.SinkWriterDisableThrottling, 1);
        return attrs;
    }

    public static void SetUInt32(IntPtr type, in Guid key, uint value)
    {
        var k = key;
        Check(Fn<SetUInt32Fn>(type, 21)(type, ref k, value));
    }

    public static void SetUInt64(IntPtr type, in Guid key, ulong value)
    {
        var k = key;
        Check(Fn<SetUInt64Fn>(type, 22)(type, ref k, value));
    }

    public static void SetGuid(IntPtr type, in Guid key, in Guid value)
    {
        var k = key;
        var v = value;
        Check(Fn<SetGuidFn>(type, 24)(type, ref k, ref v));
    }

    public static void GetGuid(IntPtr type, in Guid key, out Guid value)
    {
        var k = key;
        Check(Fn<GetGuidFn>(type, 10)(type, ref k, out value));
    }

    public static void GetUInt64(IntPtr type, in Guid key, out ulong value)
    {
        var k = key;
        Check(Fn<GetUInt64Fn>(type, 8)(type, ref k, out value));
    }

    // ---- IMFMediaBuffer ----

    public static void BufferLock(IntPtr buffer, out IntPtr data, out int maxLength, out int currentLength)
        => Check(Fn<BufferLockFn>(buffer, 3)(buffer, out data, out maxLength, out currentLength));

    public static void BufferUnlock(IntPtr buffer) => Check(Fn<BufferUnlockFn>(buffer, 4)(buffer));

    public static void BufferSetCurrentLength(IntPtr buffer, int length)
        => Check(Fn<BufferSetLengthFn>(buffer, 6)(buffer, length));

    // ---- IMFSample（IMFAttributes 占槽 3..32 共 30 个方法，勿再少数）----

    public static void SampleSetTime(IntPtr sample, long time) => Check(Fn<SampleSetTimeFn>(sample, 36)(sample, time));

    public static void SampleSetDuration(IntPtr sample, long duration) => Check(Fn<SampleSetTimeFn>(sample, 38)(sample, duration));

    public static void SampleGetDuration(IntPtr sample, out long duration) => Check(Fn<SampleGetDurationFn>(sample, 37)(sample, out duration));

    public static void SampleAddBuffer(IntPtr sample, IntPtr buffer) => Check(Fn<SampleAddBufferFn>(sample, 42)(sample, buffer));

    public static void SampleToContiguousBuffer(IntPtr sample, out IntPtr buffer) => Check(Fn<SampleToContiguousFn>(sample, 41)(sample, out buffer));

    public static void SampleGetTotalLength(IntPtr sample, out long totalLength) => Check(Fn<SampleGetTotalLengthFn>(sample, 45)(sample, out totalLength));

    // ---- IMFSinkWriter ----

    public static void SinkAddStream(IntPtr writer, IntPtr mediaType, out int streamIndex)
        => Check(Fn<SinkAddStreamFn>(writer, 3)(writer, mediaType, out streamIndex));

    public static void SinkSetInputMediaType(IntPtr writer, int streamIndex, IntPtr mediaType)
        => Check(Fn<SinkSetInputFn>(writer, 4)(writer, streamIndex, mediaType, IntPtr.Zero));

    public static void SinkBeginWriting(IntPtr writer) => Check(Fn<SinkBeginFn>(writer, 5)(writer));

    public static void SinkWriteSample(IntPtr writer, int streamIndex, IntPtr sample)
        => Check(Fn<SinkWriteFn>(writer, 6)(writer, streamIndex, sample));

    public static void SinkDoFinalize(IntPtr writer) => Check(Fn<SinkFinalizeFn>(writer, 11)(writer, IntPtr.Zero));

    // ---- IMFSourceReader ----

    public static void ReaderGetNativeMediaType(IntPtr reader, int streamIndex, int typeIndex, out IntPtr mediaType)
        => Check(Fn<ReaderGetNativeTypeFn>(reader, 5)(reader, streamIndex, typeIndex, out mediaType));

    public static void ReaderSetStreamSelection(IntPtr reader, int streamIndex, bool selected)
        => Check(Fn<ReaderSetSelectionFn>(reader, 4)(reader, streamIndex, selected ? 1 : 0));

    public static void ReaderSetCurrentMediaType(IntPtr reader, int streamIndex, IntPtr mediaType)
        => Check(Fn<ReaderSetCurrentTypeFn>(reader, 7)(reader, streamIndex, IntPtr.Zero, mediaType));

    public static void ReaderReadSample(IntPtr reader, int streamIndex, out int actualStreamIndex,
        out int streamFlags, out long timestamp, out IntPtr sample)
        => Check(Fn<ReaderReadFn>(reader, 9)(reader, streamIndex, 0, out actualStreamIndex, out streamFlags, out timestamp, out sample));

    // ---- 委托声明（StdCall/Winapi）----

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int SetUInt32Fn(IntPtr self, ref Guid key, uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int SetUInt64Fn(IntPtr self, ref Guid key, ulong value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int SetGuidFn(IntPtr self, ref Guid key, ref Guid value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int GetGuidFn(IntPtr self, ref Guid key, out Guid value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int GetUInt64Fn(IntPtr self, ref Guid key, out ulong value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int BufferLockFn(IntPtr self, out IntPtr data, out int maxLength, out int currentLength);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int BufferUnlockFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int BufferSetLengthFn(IntPtr self, int length);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int SampleSetTimeFn(IntPtr self, long value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int SampleGetDurationFn(IntPtr self, out long duration);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int SampleAddBufferFn(IntPtr self, IntPtr buffer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int SampleToContiguousFn(IntPtr self, out IntPtr buffer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int SampleGetTotalLengthFn(IntPtr self, out long totalLength);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int SinkAddStreamFn(IntPtr self, IntPtr mediaType, out int streamIndex);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int SinkSetInputFn(IntPtr self, int streamIndex, IntPtr mediaType, IntPtr encodingParameters);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int SinkBeginFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int SinkWriteFn(IntPtr self, int streamIndex, IntPtr sample);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int SinkFinalizeFn(IntPtr self, IntPtr callback);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int ReaderGetNativeTypeFn(IntPtr self, int streamIndex, int typeIndex, out IntPtr mediaType);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int ReaderSetSelectionFn(IntPtr self, int streamIndex, int selected);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int ReaderSetCurrentTypeFn(IntPtr self, int streamIndex, IntPtr reserved, IntPtr mediaType);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int ReaderReadFn(IntPtr self, int streamIndex, uint controlFlags, out int actualStreamIndex, out int streamFlags, out long timestamp, out IntPtr sample);

    private static void Check(int hr)
    {
        if (hr < 0)
        {
            throw new COMException($"MF error 0x{hr:X8}", hr);
        }
    }
}
