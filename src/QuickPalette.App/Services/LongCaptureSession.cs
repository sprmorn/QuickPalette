namespace QuickPalette.App.Services;

using System.Drawing;
using System.Drawing.Imaging;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QuickPalette.Screenshot.Capture;
using PixelFormat = System.Drawing.Imaging.PixelFormat;
using Serilog;

/// <summary>
/// 长截图采集会话：用户手动滚动，定时捕获选区并按帧对齐拼接成整页长图。
/// - 只增不减（向下滚动拼接新内容；向上滚动的帧跳过，避免拼接错乱）
/// - 每帧对齐滚动量（&lt; 阈值视为未滚动跳过，不重复拼接）
/// - Updated 事件携带实时预览（等比缩小），Finished 携带完整长图（原始分辨率）
/// </summary>
public sealed class LongCaptureSession : IDisposable
{
    /// <summary>捕获节拍（毫秒）：足够跟上手动滚动，又留足对齐计算时间</summary>
    private const int TickMs = 350;

    /// <summary>预览图宽度（DIP）</summary>
    private const int PreviewWidth = 320;

    private readonly ILogger _logger;
    private readonly Rectangle _region;
    private readonly DispatcherTimer _timer;
    private Bitmap? _accumulator;
    private Bitmap? _lastFrame;
    private bool _finished;

    /// <summary>实时预览更新（等比缩小图）</summary>
    public event Action<BitmapSource>? Updated;

    /// <summary>完成：携带完整长图（原始分辨率，冻结 BitmapSource）</summary>
    public event Action<BitmapSource>? Finished;

    /// <summary>取消</summary>
    public event Action? Cancelled;

    /// <summary>已捕获的长图高度（像素）</summary>
    public int HeightPixels => _accumulator?.Height ?? 0;

    /// <summary>已拼接帧数</summary>
    public int Frames { get; private set; }

    public LongCaptureSession(ILogger logger, Rectangle region)
    {
        _logger = logger;
        _region = region;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TickMs) };
        _timer.Tick += (_, _) => CaptureTick();
    }

    /// <summary>开始采集（立即取首帧）</summary>
    public void Start()
    {
        CaptureTick();
        _timer.Start();
    }

    /// <summary>完成：输出长图</summary>
    public void Complete()
    {
        if (_finished) return;
        _finished = true;
        _timer.Stop();

        var result = _accumulator == null ? null : ToFrozenBitmap(_accumulator);
        if (result == null)
        {
            _logger.Warning("Long capture finished with no frames");
            Cancel();
            return;
        }

        _logger.Information("Long capture finished: {W}x{H}px, {Frames} frames",
            result.PixelWidth, result.PixelHeight, Frames);
        Finished?.Invoke(result);
    }

    /// <summary>取消：丢弃已捕获内容</summary>
    public void Cancel()
    {
        if (_finished) return;
        _finished = true;
        _timer.Stop();
        _logger.Information("Long capture canceled: {Frames} frames", Frames);
        Cancelled?.Invoke();
    }

    private void CaptureTick()
    {
        try
        {
            using var frame = new Bitmap(_region.Width, _region.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(frame))
            {
                g.CopyFromScreen(_region.Location, System.Drawing.Point.Empty, _region.Size);
            }

            if (_accumulator == null)
            {
                _accumulator = new Bitmap(frame);
                _lastFrame = new Bitmap(frame);
                Frames = 1;
                PublishPreview();
                return;
            }

            var maxShift = _region.Height - 8;
            var shift = FrameAligner.DetectScroll(_lastFrame!, frame, maxShift);
            if (shift is null or 0 or < 0)
            {
                // 未滚动 / 向上滚（v1 只向下拼接）/ 无法对齐：跳过本帧
                return;
            }

            // 向下滚了 s 像素：新帧底部 s 行是新内容 → 追加
            AppendBottom(frame, shift.Value);
            _lastFrame?.Dispose();
            _lastFrame = new Bitmap(frame);
            Frames++;
            PublishPreview();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Long capture tick failed");
        }
    }

    /// <summary>把新帧底部 rows 行追加到长图</summary>
    private void AppendBottom(Bitmap frame, int rows)
    {
        var oldHeight = _accumulator!.Height;
        var grown = new Bitmap(_region.Width, oldHeight + rows, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(grown))
        {
            g.DrawImage(_accumulator, 0, 0);
            var src = new Rectangle(0, _region.Height - rows, _region.Width, rows);
            var dst = new Rectangle(0, oldHeight, _region.Width, rows);
            g.DrawImage(frame, dst, src, GraphicsUnit.Pixel);
        }

        _accumulator.Dispose();
        _accumulator = grown;
    }

    private void PublishPreview()
    {
        try
        {
            if (_accumulator == null) return;
            var full = ToFrozenBitmap(_accumulator);
            var scale = PreviewWidth / (double)full.PixelWidth;
            var preview = new TransformedBitmap(full, new ScaleTransform(scale, scale));
            preview.Freeze();
            Updated?.Invoke(preview);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Long capture preview failed");
        }
    }

    private static BitmapSource ToFrozenBitmap(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var source = BitmapSource.Create(
                bitmap.Width, bitmap.Height, 96, 96,
                PixelFormats.Bgra32, null, data.Scan0, data.Stride * bitmap.Height, data.Stride);
            source.Freeze();
            return source;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _accumulator?.Dispose();
        _lastFrame?.Dispose();
    }
}
