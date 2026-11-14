namespace QuickPalette.Recorder;

using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using WinRT;

/// <summary>
/// WGC 区域帧源（V2 方案，管线已端到端验证）：
/// IGraphicsCaptureItemInterop.CreateForMonitor(HMONITOR) 建捕获项 →
/// D3D11 设备 → Direct3D11CaptureFramePool → CreateCaptureSession 采集；
/// 逐帧 SoftwareBitmap 读像素并裁出选区（BGRA）。硬件加速内容（游戏/视频）都能拍。
/// </summary>
public sealed class WgcRegionSource : IDisposable
{
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _framePool;
    private readonly GraphicsCaptureSession _session;
    private readonly int _cropX;
    private readonly int _cropY;
    private readonly int _cropW;
    private readonly int _cropH;
    private readonly byte[] _frameBuffer;
    private bool _closed;

    /// <summary>裁剪后的 BGRA 帧（长度 = 宽*高*4；复用同一缓冲，消费方需立即使用）</summary>
    public event Action<byte[]>? FrameArrived;

    public int Width => _cropW;
    public int Height => _cropH;

    /// <summary>monitorHandle: HMONITOR；cropX/Y/W/H: 显示器内物理像素选区</summary>
    public WgcRegionSource(IntPtr monitorHandle, int cropX, int cropY, int cropW, int cropH,
        int monitorPixelWidth, int monitorPixelHeight)
    {
        _cropX = cropX;
        _cropY = cropY;
        _cropW = cropW;
        _cropH = cropH;
        _frameBuffer = new byte[cropW * cropH * 4];

        _item = CreateItemForMonitor(monitorHandle);

        var device = CreateD3DDevice();

        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2,
            new SizeInt32(monitorPixelWidth, monitorPixelHeight));
        _framePool.FrameArrived += OnFrameArrived;

        _session = _framePool.CreateCaptureSession(_item);
        try
        {
            _session.IsBorderRequired = false; // 去掉系统捕获边框
            _session.IsCursorCaptureEnabled = true;
        }
        catch
        {
            // 旧系统属性缺失时保留默认
        }

        _session.StartCapture();
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (_closed)
        {
            return;
        }

        using var frame = sender.TryGetNextFrame();
        if (frame == null)
        {
            return;
        }

        try
        {
            var task = SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface).AsTask();
            using var bitmap = task.GetAwaiter().GetResult();
            using var bgra = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            CopyRegion(bgra);
        }
        catch (Exception ex)
        {
            // 单帧失败跳过（瞬态）；异常走 Trace 便于诊断
            System.Diagnostics.Trace.TraceWarning("WGC frame skipped: {0}", ex);
            return;
        }

        // 订阅者异常不在此吞掉
        FrameArrived?.Invoke(_frameBuffer);
    }

    private void CopyRegion(SoftwareBitmap bgra)
    {
        using var buffer = bgra.LockBuffer(BitmapBufferAccessMode.Read);
        using var reference = buffer.CreateReference();

        // CsWinRT 的 RCW 不能强转 ComImport 接口（Marshal 也拿不到 IUnknown）→
        // 走 IWinRTObject.NativeObject 拿指针，QI 后按 vtable 直调 IMemoryBufferByteAccess.GetBuffer
        var pUnk = ((WinRT.IWinRTObject)reference).NativeObject.GetRef();
        var iid = new Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D"); // IMemoryBufferByteAccess
        Marshal.QueryInterface(pUnk, ref iid, out var pAccess);
        Marshal.Release(pUnk);
        try
        {
            var vtable = Marshal.ReadIntPtr(pAccess);
            var method = Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size); // IUnknown(3) + GetBuffer
            var getBuffer = (GetBufferDelegate)Marshal.GetDelegateForFunctionPointer(method, typeof(GetBufferDelegate));
            getBuffer(pAccess, out var data, out var capacity);

            var stride = (int)(capacity / (uint)Math.Max(1, bgra.PixelHeight));
            if (stride <= 0)
            {
                stride = bgra.PixelWidth * 4;
            }

            for (var y = 0; y < _cropH; y++)
            {
                var srcRow = new IntPtr(data.ToInt64() + (long)(_cropY + y) * stride + _cropX * 4L);
                Marshal.Copy(srcRow, _frameBuffer, y * _cropW * 4, _cropW * 4);
            }
        }
        finally
        {
            Marshal.Release(pAccess);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetBufferDelegate(IntPtr self, out IntPtr buffer, out uint capacity);

    /// <summary>IGraphicsCaptureItemInterop.CreateForMonitor（验证过的配方）</summary>
    private static GraphicsCaptureItem CreateItemForMonitor(IntPtr monitorHandle)
    {
        var className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        WindowsCreateString(className, className.Length, out var hstring);
        try
        {
            var interopIid = new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"); // IGraphicsCaptureItemInterop
            var hr = RoGetActivationFactory(hstring, ref interopIid, out var factory);
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factory);
            var inspectable = new Guid("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90"); // IInspectable
            interop.CreateForMonitor(monitorHandle, ref inspectable, out var itemPtr);
            return MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPtr);
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }

    private static IDirect3DDevice CreateD3DDevice()
    {
        var dxgiGuid = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c"); // IDXGIDevice

        foreach (int driverType in new[] { 1 /* HARDWARE */, 0 /* WARP */ })
        {
            var hr = D3D11CreateDevice(IntPtr.Zero, driverType, IntPtr.Zero, 0, IntPtr.Zero, 0, 7,
                out var devicePtr, out _, out _);
            if (hr < 0 || devicePtr == IntPtr.Zero)
            {
                continue;
            }

            Marshal.QueryInterface(devicePtr, ref dxgiGuid, out var dxgiPtr);
            try
            {
                var hr2 = CreateDirect3D11DeviceFromDXGIDevice(dxgiPtr, out var wrapped);
                if (hr2 < 0)
                {
                    Marshal.ThrowExceptionForHR(hr2);
                }

                return MarshalInterface<IDirect3DDevice>.FromAbi(wrapped);
            }
            finally
            {
                Marshal.Release(dxgiPtr);
                Marshal.Release(devicePtr);
            }
        }

        throw new InvalidOperationException("WGC: 创建 D3D11 设备失败");
    }

    public void Dispose()
    {
        _closed = true;
        try
        {
            _session.Dispose();
            _framePool.FrameArrived -= OnFrameArrived;
            _framePool.Dispose();
        }
        catch
        {
            // 释放失败不影响退出
        }
    }

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        void CreateForWindow(IntPtr hwnd, ref Guid riid, out IntPtr ppv);
        void CreateForMonitor(IntPtr hmon, ref Guid riid, out IntPtr ppv);
    }

    [ComImport, Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMemoryBufferByteAccess
    {
        void GetBuffer(out IntPtr buffer, out uint capacity);
    }

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string s, int len, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr hstring, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
        IntPtr featureLevels, int featureLevelCount, uint sdkVersion, out IntPtr device, out IntPtr featureLevel,
        out IntPtr context);

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
}
