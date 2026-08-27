using TinyClips.Core.Models;

namespace TinyClips.Core.Capture;

/// <summary>
/// Pure placement math for the webcam picture-in-picture overlay, shared by the CPU
/// (<see cref="WebcamOverlayCompositor"/>) and GPU (<see cref="GpuOverlayCompositor"/>)
/// compositors so both pipelines place, crop and round the webcam identically.
/// </summary>
public readonly record struct WebcamOverlayLayout(
    int OverlayX,
    int OverlayY,
    int OverlayWidth,
    int OverlayHeight,
    int CropX,
    int CropY,
    int CropWidth,
    int CropHeight,
    int CornerRadiusPx)
{
    public bool IsEmpty => OverlayWidth <= 0 || OverlayHeight <= 0;

    public static WebcamOverlayLayout Compute(
        int frameWidth,
        int frameHeight,
        int webcamWidth,
        int webcamHeight,
        WebcamCornerPosition corner,
        WebcamSizePreset sizePreset,
        WebcamShape shape,
        double? configuredCornerRadius)
    {
        if (frameWidth <= 0 || frameHeight <= 0)
        {
            return default;
        }

        double sizeFraction = sizePreset switch
        {
            WebcamSizePreset.Small => 0.18,
            WebcamSizePreset.Large => 0.30,
            _ => 0.24,
        };

        int overlayWidth;
        int overlayHeight;
        if (shape == WebcamShape.Circle)
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
        int overlayX = corner switch
        {
            WebcamCornerPosition.TopRight or WebcamCornerPosition.BottomRight => frameWidth - overlayWidth - margin,
            _ => margin,
        };
        int overlayY = corner switch
        {
            WebcamCornerPosition.BottomLeft or WebcamCornerPosition.BottomRight => frameHeight - overlayHeight - margin,
            _ => margin,
        };

        var (cropX, cropY, cropWidth, cropHeight) = ComputeSourceCrop(webcamWidth, webcamHeight, overlayWidth, overlayHeight);
        int cornerRadiusPx = ResolveCornerRadiusPx(shape, configuredCornerRadius, overlayWidth, overlayHeight);

        return new WebcamOverlayLayout(
            overlayX,
            overlayY,
            overlayWidth,
            overlayHeight,
            cropX,
            cropY,
            cropWidth,
            cropHeight,
            cornerRadiusPx);
    }

    private static (int X, int Y, int Width, int Height) ComputeSourceCrop(int webcamWidth, int webcamHeight, int overlayWidth, int overlayHeight)
    {
        if (webcamWidth <= 0 || webcamHeight <= 0 || overlayWidth <= 0 || overlayHeight <= 0)
        {
            return (0, 0, Math.Max(1, webcamWidth), Math.Max(1, webcamHeight));
        }

        double sourceAspect = webcamWidth / (double)webcamHeight;
        double destinationAspect = overlayWidth / (double)overlayHeight;

        if (sourceAspect > destinationAspect)
        {
            int cropWidth = Math.Max(1, (int)Math.Round(webcamHeight * destinationAspect));
            return (Math.Max(0, (webcamWidth - cropWidth) / 2), 0, cropWidth, webcamHeight);
        }

        int cropHeight = Math.Max(1, (int)Math.Round(webcamWidth / destinationAspect));
        return (0, Math.Max(0, (webcamHeight - cropHeight) / 2), webcamWidth, cropHeight);
    }

    private static int ResolveCornerRadiusPx(WebcamShape shape, double? configuredCornerRadius, int overlayWidth, int overlayHeight)
    {
        if (shape != WebcamShape.RoundedRectangle)
        {
            return 0;
        }

        int maxRadius = Math.Min(overlayWidth, overlayHeight) / 2;
        if (configuredCornerRadius is not > 0)
        {
            return Math.Clamp((int)Math.Round(Math.Min(overlayWidth, overlayHeight) * 0.12), 2, maxRadius);
        }

        return Math.Clamp((int)Math.Round(configuredCornerRadius.Value), 0, maxRadius);
    }
}
