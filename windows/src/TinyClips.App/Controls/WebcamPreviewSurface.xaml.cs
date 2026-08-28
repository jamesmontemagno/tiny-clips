using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using TinyClips.Core.Capture;
using TinyClips.Core.Models;
using Windows.Graphics.Imaging;

namespace TinyClips.App;

public sealed partial class WebcamPreviewSurface : UserControl
{
    private readonly DispatcherTimer _timer;
    private IWebcamCaptureService? _capture;
    private WriteableBitmap? _bitmap;
    private TimeSpan _lastTimestamp = TimeSpan.MinValue;
    private WebcamShape _shape;
    private double? _cornerRadius;

    public WebcamPreviewSurface()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += OnTick;
        SizeChanged += (_, _) => ApplyShape();
        Unloaded += (_, _) => Detach();
    }

    public void Attach(IWebcamCaptureService capture)
    {
        _capture = capture;
        _timer.Start();
    }

    public void Detach()
    {
        _timer.Stop();
        _capture = null;
        _bitmap = null;
        _lastTimestamp = TimeSpan.MinValue;
        PreviewBrush.ImageSource = null;
    }

    public void ConfigureShape(WebcamShape shape, double? cornerRadius)
    {
        _shape = shape;
        _cornerRadius = cornerRadius;
        ApplyShape();
    }

    private void ApplyShape()
    {
        var isCircle = _shape == WebcamShape.Circle;
        CircleShape.Visibility = isCircle ? Visibility.Visible : Visibility.Collapsed;
        RectangleShape.Visibility = isCircle ? Visibility.Collapsed : Visibility.Visible;
        if (isCircle)
        {
            // An Ellipse is always round; nothing to compute.
            return;
        }

        // A stroked Rectangle fits its geometry inside the layout bounds deflated by half the
        // stroke thickness on each side, so the radius must be measured against that geometry.
        var inset = RectangleShape.StrokeThickness;
        var minSide = Math.Max(0, Math.Min(ActualWidth, ActualHeight) - inset);
        var radius = _shape == WebcamShape.RoundedRectangle
            ? (_cornerRadius is { } configuredRadius
                ? Math.Min(minSide / 2, configuredRadius / Math.Max(1.0, XamlRoot?.RasterizationScale ?? 1.0))
                : minSide * 0.12)
            : 0;
        RectangleShape.RadiusX = RectangleShape.RadiusY = Math.Max(0, radius);
    }

    private ImageBrush PreviewBrush => (ImageBrush)Resources["PreviewBrush"];

    private void OnTick(object? sender, object e)
    {
        if (_capture?.TryGetLatestFrame(out var frame) != true ||
            frame is null ||
            frame.Timestamp == _lastTimestamp)
        {
            return;
        }

        if (_bitmap is null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
        {
            _bitmap = new WriteableBitmap(frame.Width, frame.Height);
            PreviewBrush.ImageSource = _bitmap;
        }

        if (frame.IsGpuFrame)
        {
            // GPU-delivered frame (recorder is on the GPU pipeline): read it back once for the
            // preview. One conversion in flight at a time; extra ticks simply show the last image.
            if (!_gpuReadbackInFlight)
            {
                _gpuReadbackInFlight = true;
                _ = ReadBackGpuFrameAsync(frame, _bitmap);
            }

            return;
        }

        var requiredLength = checked(frame.Width * frame.Height * 4);
        if (frame.BgraPixels.Length < requiredLength)
        {
            return;
        }

        using var stream = _bitmap.PixelBuffer.AsStream();
        stream.Position = 0;
        stream.Write(frame.BgraPixels.Span[..requiredLength]);
        _bitmap.Invalidate();
        _lastTimestamp = frame.Timestamp;
    }

    private bool _gpuReadbackInFlight;

    private async Task ReadBackGpuFrameAsync(WebcamFrame frame, WriteableBitmap target)
    {
        try
        {
            using var softwareBitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface!, BitmapAlphaMode.Premultiplied);
            if (ReferenceEquals(_bitmap, target) && softwareBitmap.PixelWidth == target.PixelWidth && softwareBitmap.PixelHeight == target.PixelHeight)
            {
                softwareBitmap.CopyToBuffer(target.PixelBuffer);
                target.Invalidate();
                _lastTimestamp = frame.Timestamp;
            }
        }
        catch
        {
            // Preview is best-effort; the recording itself is unaffected.
        }
        finally
        {
            _gpuReadbackInFlight = false;
        }
    }
}
