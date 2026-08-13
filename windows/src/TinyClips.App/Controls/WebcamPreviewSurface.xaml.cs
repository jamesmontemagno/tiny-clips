using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using TinyClips.Core.Capture;
using TinyClips.Core.Models;

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
        PreviewImage.Source = null;
    }

    public void ConfigureShape(WebcamShape shape, double? cornerRadius)
    {
        _shape = shape;
        _cornerRadius = cornerRadius;
        ApplyShape();
    }

    private void ApplyShape()
    {
        var radius = _shape switch
        {
            WebcamShape.Circle => Math.Min(ActualWidth, ActualHeight) / 2,
            WebcamShape.RoundedRectangle => _cornerRadius is { } configuredRadius
                ? configuredRadius / Math.Max(1.0, XamlRoot?.RasterizationScale ?? 1.0)
                : Math.Min(ActualWidth, ActualHeight) * 0.12,
            _ => 0,
        };
        PreviewBorder.CornerRadius = new CornerRadius(Math.Max(0, radius));
    }

    private void OnTick(object? sender, object e)
    {
        if (_capture?.TryGetLatestFrame(out var frame) != true ||
            frame is null ||
            frame.Timestamp == _lastTimestamp)
        {
            return;
        }

        var requiredLength = checked(frame.Width * frame.Height * 4);
        if (frame.BgraPixels.Length < requiredLength)
        {
            return;
        }

        if (_bitmap is null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
        {
            _bitmap = new WriteableBitmap(frame.Width, frame.Height);
            PreviewImage.Source = _bitmap;
        }

        using var stream = _bitmap.PixelBuffer.AsStream();
        stream.Position = 0;
        stream.Write(frame.BgraPixels.Span[..requiredLength]);
        _bitmap.Invalidate();
        _lastTimestamp = frame.Timestamp;
    }
}
