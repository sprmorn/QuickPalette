namespace QuickPalette.Recorder;

/// <summary>
/// Media Foundation 常用 GUID（取自 mingw-w64 mfapi.h 权威定义，勿凭记忆改动）。
/// COM 接口一律使用 NAudio.MediaFoundation 的公开签名（避免手写 vtable 出错）。
/// </summary>
public static class MfGuids
{
    // 媒体大类 / 子类型
    public static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");
    public static readonly Guid MediaTypeAudio = new("73647561-0000-0010-8000-00AA00389B71");
    public static readonly Guid VideoFormatRgb32 = new("00000016-0000-0010-8000-00AA00389B71");
    public static readonly Guid VideoFormatH264 = new("34363248-0000-0010-8000-00AA00389B71");
    public static readonly Guid AudioFormatPcm = new("00000001-0000-0010-8000-00AA00389B71");
    public static readonly Guid AudioFormatFloat = new("00000003-0000-0010-8000-00AA00389B71");
    public static readonly Guid AudioFormatAac = new("00001610-0000-0010-8000-00AA00389B71"); // MFAudioFormat_AAC（NAudio 权威常量）

    // 媒体类型属性
    public static readonly Guid MajorType = new("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F");
    public static readonly Guid Subtype = new("F7E34C9A-42E8-4714-B74B-CB29D72C35E5");
    public static readonly Guid FrameSize = new("1652C33D-D6B2-4012-B834-72030849A37D");
    public static readonly Guid FrameRate = new("C459A2E8-3D2C-4E44-B132-FEE5156C7BB0");
    public static readonly Guid PixelAspectRatio = new("C6376A1E-8D0A-4027-BE45-6D9A0AD39BB6");
    public static readonly Guid InterlaceMode = new("E2724BB8-E676-4806-B4B2-A8D6EFB44CCD");
    public static readonly Guid AvgBitrate = new("20332624-FB0D-4D9E-BD0D-CBF6786C102E");
    public static readonly Guid AudioBitsPerSample = new("F2DEB57F-40FA-4764-AA33-ED4F2D1FF669");
    public static readonly Guid AudioSamplesPerSecond = new("5FAEEAE7-0290-4C31-9E8A-C534F68D9DBA");
    public static readonly Guid AudioNumChannels = new("37E48BF5-645E-4C5B-89DE-ADA9E29B696A");
    public static readonly Guid AudioAvgBytesPerSecond = new("1AAB75C8-CFEF-451C-AB95-AC034B8E1731");
    public static readonly Guid AudioBlockAlignment = new("322DE230-9EEB-43BD-AB7A-FF412251541D");
    public static readonly Guid DefaultStride = new("644B4E48-1E02-4516-B0EB-C01CA9D49AC6");
    public static readonly Guid AacPayloadType = new("BFBABE79-7434-4D1C-94F0-72A3B9E17188");
    public static readonly Guid AacProfileLevel = new("7632F0E6-9538-4D61-ACDA-EA29C8C14456");
}
