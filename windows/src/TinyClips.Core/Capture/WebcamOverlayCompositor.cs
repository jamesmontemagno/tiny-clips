using System.Runtime.Versioning;
using TinyClips.Core.Models;

namespace TinyClips.Core.Capture;

/// <summary>
/// Composites webcam frames into a tightly-packed BGRA8 capture frame using
/// corner/size/shape settings. Shape alpha is precomputed per overlay size;
/// each frame then performs a CPU-side alpha blend.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WebcamOverlayCompositor
{
    private readonly object _drawGate = new();
    private WebcamCornerPosition _corner;
    private readonly WebcamSizePreset _sizePreset;
    private readonly WebcamShape _shape;
    private readonly double? _configuredCornerRadius;

    private byte[]? _alphaMask;
    private int _maskWidth;
    private int _maskHeight;
    private WebcamShape _maskShape;
    private int _maskCornerRadiusPx = -1;

    private int _overlayX;
    private int _overlayY;
    private int _overlayWidth;
    private int _overlayHeight;
    private int _cropX;
    private int _cropY;
    private int _cropWidth;
    private int _cropHeight;

    private int _builtForFrameWidth = -1;
    private int _builtForFrameHeight = -1;
    private int _builtForWebcamWidth = -1;
    private int _builtForWebcamHeight = -1;

    public WebcamOverlayCompositor(
        WebcamCornerPosition corner,
        WebcamSizePreset sizePreset,
        WebcamShape shape,
        double? cornerRadius)
    {
        _corner = corner;
        _sizePreset = sizePreset;
        _shape = shape;
        _configuredCornerRadius = cornerRadius;
    }

    /// <summary>
    /// Blends <paramref name="webcamFrame"/> onto <paramref name="bgra"/> if all dimensions are valid.
    /// </summary>
    public void Draw(byte[] bgra, int frameWidth, int frameHeight, WebcamFrame webcamFrame)
    {
        lock (_drawGate)
        {
            DrawCore(bgra, frameWidth, frameHeight, webcamFrame);
        }
    }

    private void DrawCore(byte[] bgra, int frameWidth, int frameHeight, WebcamFrame webcamFrame)
    {
        if (bgra.Length == 0 ||
            frameWidth <= 0 ||
            frameHeight <= 0 ||
            webcamFrame.Width <= 0 ||
            webcamFrame.Height <= 0)
        {
            return;
        }

        EnsureLayout(frameWidth, frameHeight, webcamFrame.Width, webcamFrame.Height);
        if (_overlayWidth <= 0 || _overlayHeight <= 0 || _alphaMask is null)
        {
            return;
        }

        var source = webcamFrame.BgraPixels.Span;
        if (source.Length < webcamFrame.Width * webcamFrame.Height * 4)
        {
            return;
        }

        int sourceStride = webcamFrame.Width * 4;
        int overlayStride = _overlayWidth * 4;
        double scaleX = _cropWidth / (double)_overlayWidth;
        double scaleY = _cropHeight / (double)_overlayHeight;

        for (int y = 0; y < _overlayHeight; y++)
        {
            int dstY = _overlayY + y;
            if (dstY < 0 || dstY >= frameHeight)
            {
                continue;
            }

            int maskRow = y * _overlayWidth;
            int dstRow = dstY * frameWidth * 4;
            int srcY = _cropY + Math.Clamp((int)(y * scaleY), 0, _cropHeight - 1);
            int srcRow = srcY * sourceStride;

            for (int x = 0; x < _overlayWidth; x++)
            {
                byte maskAlpha = _alphaMask[maskRow + x];
                if (maskAlpha == 0)
                {
                    continue;
                }

                int dstX = _overlayX + x;
                if (dstX < 0 || dstX >= frameWidth)
                {
                    continue;
                }

                int srcX = _cropX + Math.Clamp((int)(x * scaleX), 0, _cropWidth - 1);
                int sourceIndex = srcRow + (srcX * 4);
                int destIndex = dstRow + (dstX * 4);

                // Webcam frames are opaque, but camera drivers often leave BGRA alpha undefined.
                double alpha = maskAlpha / 255.0;
                if (alpha <= 0)
                {
                    continue;
                }

                bgra[destIndex] = Blend(bgra[destIndex], source[sourceIndex], alpha);
                bgra[destIndex + 1] = Blend(bgra[destIndex + 1], source[sourceIndex + 1], alpha);
                bgra[destIndex + 2] = Blend(bgra[destIndex + 2], source[sourceIndex + 2], alpha);
            }
        }
    }

    public void Draw(
        byte[] bgra,
        int frameWidth,
        int frameHeight,
        WebcamFrame webcamFrame,
        WebcamCornerPosition corner)
    {
        lock (_drawGate)
        {
            if (_corner != corner)
            {
                _corner = corner;
                _builtForFrameWidth = -1;
            }

            DrawCore(bgra, frameWidth, frameHeight, webcamFrame);
        }
    }

    private void EnsureLayout(int frameWidth, int frameHeight, int webcamWidth, int webcamHeight)
    {
        if (frameWidth == _builtForFrameWidth &&
            frameHeight == _builtForFrameHeight &&
            webcamWidth == _builtForWebcamWidth &&
            webcamHeight == _builtForWebcamHeight)
        {
            return;
        }

        _builtForFrameWidth = frameWidth;
        _builtForFrameHeight = frameHeight;
        _builtForWebcamWidth = webcamWidth;
        _builtForWebcamHeight = webcamHeight;

        double sizeFraction = _sizePreset switch
        {
            WebcamSizePreset.Small => 0.18,
            WebcamSizePreset.Large => 0.30,
            _ => 0.24,
        };

        int overlayWidth;
        int overlayHeight;
        if (_shape == WebcamShape.Circle)
        {
            int side = Math.Clamp(
                (int)Math.Round(Math.Min(frameWidth, frameHeight) * sizeFraction),
                48,
                Math.Min(frameWidth, frameHeight) - 2);
            overlayWidth = side;
            overlayHeight = side;
        }
        else
        {
            double sourceAspect = webcamHeight <= 0 ? (16.0 / 9.0) : webcamWidth / (double)webcamHeight;
            overlayWidth = Math.Clamp(
                (int)Math.Round(frameWidth * sizeFraction),
                64,
                Math.Max(64, (int)Math.Round(frameWidth * 0.45)));
            overlayHeight = Math.Max(48, (int)Math.Round(overlayWidth / sourceAspect));

            int maxHeight = Math.Max(48, (int)Math.Round(frameHeight * 0.40));
            if (overlayHeight > maxHeight)
            {
                overlayHeight = maxHeight;
                overlayWidth = Math.Max(64, (int)Math.Round(overlayHeight * sourceAspect));
            }

            overlayWidth = Math.Min(overlayWidth, Math.Max(2, frameWidth - 2));
            overlayHeight = Math.Min(overlayHeight, Math.Max(2, frameHeight - 2));
        }

        int margin = Math.Clamp((int)Math.Round(Math.Min(frameWidth, frameHeight) * 0.03), 12, 40);
        _overlayX = _corner switch
        {
            WebcamCornerPosition.TopRight or WebcamCornerPosition.BottomRight => frameWidth - overlayWidth - margin,
            _ => margin,
        };
        _overlayY = _corner switch
        {
            WebcamCornerPosition.BottomLeft or WebcamCornerPosition.BottomRight => frameHeight - overlayHeight - margin,
            _ => margin,
        };
        _overlayWidth = overlayWidth;
        _overlayHeight = overlayHeight;

        ComputeSourceCrop(webcamWidth, webcamHeight, overlayWidth, overlayHeight);

        int cornerRadiusPx = ResolveCornerRadiusPx(overlayWidth, overlayHeight);
        EnsureMask(overlayWidth, overlayHeight, cornerRadiusPx);
    }

    private void ComputeSourceCrop(int webcamWidth, int webcamHeight, int overlayWidth, int overlayHeight)
    {
        if (webcamWidth <= 0 || webcamHeight <= 0 || overlayWidth <= 0 || overlayHeight <= 0)
        {
            _cropX = 0;
            _cropY = 0;
            _cropWidth = Math.Max(1, webcamWidth);
            _cropHeight = Math.Max(1, webcamHeight);
            return;
        }

        double sourceAspect = webcamWidth / (double)webcamHeight;
        double destinationAspect = overlayWidth / (double)overlayHeight;

        if (sourceAspect > destinationAspect)
        {
            _cropHeight = webcamHeight;
            _cropWidth = Math.Max(1, (int)Math.Round(webcamHeight * destinationAspect));
            _cropX = Math.Max(0, (webcamWidth - _cropWidth) / 2);
            _cropY = 0;
        }
        else
        {
            _cropWidth = webcamWidth;
            _cropHeight = Math.Max(1, (int)Math.Round(webcamWidth / destinationAspect));
            _cropY = Math.Max(0, (webcamHeight - _cropHeight) / 2);
            _cropX = 0;
        }
    }

    private int ResolveCornerRadiusPx(int overlayWidth, int overlayHeight)
    {
        if (_shape != WebcamShape.RoundedRectangle)
        {
            return 0;
        }

        int maxRadius = Math.Min(overlayWidth, overlayHeight) / 2;
        if (_configuredCornerRadius is not > 0)
        {
            return Math.Clamp((int)Math.Round(Math.Min(overlayWidth, overlayHeight) * 0.12), 2, maxRadius);
        }

        return Math.Clamp((int)Math.Round(_configuredCornerRadius.Value), 0, maxRadius);
    }

    private void EnsureMask(int width, int height, int cornerRadiusPx)
    {
        if (_alphaMask is not null &&
            _maskWidth == width &&
            _maskHeight == height &&
            _maskShape == _shape &&
            _maskCornerRadiusPx == cornerRadiusPx)
        {
            return;
        }

        _maskWidth = width;
        _maskHeight = height;
        _maskShape = _shape;
        _maskCornerRadiusPx = cornerRadiusPx;
        _alphaMask = new byte[width * height];

        switch (_shape)
        {
            case WebcamShape.Circle:
                BuildCircleMask(_alphaMask, width, height);
                break;
            case WebcamShape.RoundedRectangle:
                BuildRoundedRectangleMask(_alphaMask, width, height, cornerRadiusPx);
                break;
            default:
                Array.Fill(_alphaMask, (byte)255);
                break;
        }
    }

    private static void BuildCircleMask(byte[] mask, int width, int height)
    {
        double rx = width / 2.0;
        double ry = height / 2.0;
        double cx = rx;
        double cy = ry;
        double minRadius = Math.Max(1.0, Math.Min(rx, ry));

        for (int y = 0; y < height; y++)
        {
            double py = (y + 0.5) - cy;
            for (int x = 0; x < width; x++)
            {
                double px = (x + 0.5) - cx;
                double normalizedDistance = Math.Sqrt((px * px) / (rx * rx) + (py * py) / (ry * ry));
                double edgeDistance = (1.0 - normalizedDistance) * minRadius;
                double coverage = Math.Clamp(edgeDistance + 0.5, 0, 1);
                mask[(y * width) + x] = (byte)Math.Round(coverage * 255);
            }
        }
    }

    private static void BuildRoundedRectangleMask(byte[] mask, int width, int height, int radiusPx)
    {
        if (radiusPx <= 0)
        {
            Array.Fill(mask, (byte)255);
            return;
        }

        double halfWidth = width / 2.0;
        double halfHeight = height / 2.0;
        double radius = Math.Min(radiusPx, Math.Min(halfWidth, halfHeight));
        double boundX = halfWidth - radius;
        double boundY = halfHeight - radius;

        for (int y = 0; y < height; y++)
        {
            double py = (y + 0.5) - halfHeight;
            for (int x = 0; x < width; x++)
            {
                double px = (x + 0.5) - halfWidth;
                double qx = Math.Abs(px) - boundX;
                double qy = Math.Abs(py) - boundY;
                double ox = Math.Max(qx, 0);
                double oy = Math.Max(qy, 0);
                double outside = Math.Sqrt((ox * ox) + (oy * oy));
                double inside = Math.Min(Math.Max(qx, qy), 0);
                double signedDistance = outside + inside - radius;
                double coverage = Math.Clamp(0.5 - signedDistance, 0, 1);
                mask[(y * width) + x] = (byte)Math.Round(coverage * 255);
            }
        }
    }

    private static byte Blend(byte destination, byte source, double alpha) =>
        (byte)Math.Clamp((source * alpha) + (destination * (1 - alpha)), 0, 255);
}
