namespace StudioEngineSpike.Engine;

internal readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public double CenterX => X + (Width / 2);

    public double CenterY => Y + (Height / 2);

    public override string ToString() => $"({X:0.#},{Y:0.#} {Width:0.#}x{Height:0.#})";
}

internal readonly record struct ShadowSpec(double Blur, double OffsetY, double Opacity);

/// <summary>
/// The handful of <c>project.json</c> values the spike's scene needs, with the defaults from
/// docs/studio-project-format.md section 8 (gradient "ocean", padding 0.06, bubble bottom-right).
/// </summary>
internal sealed record SceneSettings
{
    public double Padding { get; init; } = 0.06;

    public double ScreenCornerRadius { get; init; } = 0.02;

    public double ScreenShadow { get; init; } = 0.5;

    public double CameraShadow { get; init; } = 0.35;

    public double BubbleSize { get; init; } = 0.24;

    /// <summary>Fractions of canvas width/height added to the anchored bubble position (dragging the camera).</summary>
    public double BubbleOffsetX { get; init; }

    public double BubbleOffsetY { get; init; }

    public bool Mirror { get; init; } = true;

    public uint BackgroundPrimary { get; init; } = 0x2687E8;

    public uint BackgroundSecondary { get; init; } = 0x2EE0BF;
}

/// <summary>
/// A resolved frame for the "bubble" layout with a circular camera anchored bottom-right: the
/// subset of section 6 of the format spec this spike draws. The real engine uses
/// <c>StudioLayoutResolver</c> from TinyClips.Core; this copy exists because the spike is standalone.
/// </summary>
internal sealed record SceneLayout(
    double Width,
    double Height,
    RectD ScreenRect,
    double ScreenRadius,
    ShadowSpec ScreenShadow,
    RectD CameraRect,
    RectD CameraSource,
    bool Mirror,
    ShadowSpec CameraShadow,
    SceneSettings Settings)
{
    public static SceneLayout Resolve(SceneSettings settings, double width, double height, int screenWidth, int screenHeight, int cameraWidth, int cameraHeight)
    {
        var m = Math.Min(width, height);
        var p = Math.Clamp(settings.Padding, 0, 0.4) * m;
        var content = new RectD(p, p, width - (2 * p), height - (2 * p));
        var screenRect = Fit((double)screenWidth / screenHeight, content);
        var radius = Math.Min(Math.Clamp(settings.ScreenCornerRadius, 0, 0.2) * m, Math.Min(screenRect.Width, screenRect.Height) / 2);

        // 6.3 bubble, circle shape, bottomRight anchor.
        var d = Math.Clamp(settings.BubbleSize, 0.08, 0.6) * m;
        var bw = d;
        var bh = d;
        if (bw > 0.9 * width)
        {
            bh *= 0.9 * width / bw;
            bw = 0.9 * width;
        }

        var g = 0.03 * m;
        var x = width - g - bw + (settings.BubbleOffsetX * width);
        var y = height - g - bh + (settings.BubbleOffsetY * height);
        x = Math.Clamp(x, 0, width - bw);
        y = Math.Clamp(y, 0, height - bh);
        var cameraRect = new RectD(x, y, bw, bh);

        // 6.4 camera content is aspect-filled into the bubble, centred.
        var ac = (double)cameraWidth / cameraHeight;
        var ad = cameraRect.Width / cameraRect.Height;
        RectD source;
        if (ac > ad)
        {
            var k = ad / ac;
            source = new RectD((1 - k) / 2, 0, k, 1);
        }
        else
        {
            var k = ac / ad;
            source = new RectD(0, (1 - k) / 2, 1, k);
        }

        return new SceneLayout(
            width,
            height,
            screenRect,
            radius,
            Shadow(settings.ScreenShadow, m),
            cameraRect,
            source,
            settings.Mirror,
            Shadow(settings.CameraShadow, m),
            settings);
    }

    /// <summary>Where a screen clip pixel lands on the canvas.</summary>
    public ClipMap ScreenMap(ClipSpec clip) => new(ScreenRect.X, ScreenRect.Y, ScreenRect.Width / clip.Width, ScreenRect.Height / clip.Height);

    /// <summary>Where a camera clip pixel lands on the canvas (mirrored when <see cref="Mirror"/> is set).</summary>
    public ClipMap CameraMap(ClipSpec clip)
    {
        var cropX = CameraSource.X * clip.Width;
        var cropY = CameraSource.Y * clip.Height;
        var scaleX = CameraRect.Width / (CameraSource.Width * clip.Width);
        var scaleY = CameraRect.Height / (CameraSource.Height * clip.Height);
        return Mirror
            ? new ClipMap(CameraRect.Right + (cropX * scaleX), CameraRect.Y - (cropY * scaleY), -scaleX, scaleY)
            : new ClipMap(CameraRect.X - (cropX * scaleX), CameraRect.Y - (cropY * scaleY), scaleX, scaleY);
    }

    private static ShadowSpec Shadow(double intensity, double m)
    {
        var s = Math.Clamp(intensity, 0, 1);
        return new ShadowSpec(s * 0.04 * m, s * 0.012 * m, s * 0.5);
    }

    private static RectD Fit(double aspect, RectD area)
    {
        double w;
        double h;
        if (area.Width / area.Height > aspect)
        {
            h = area.Height;
            w = h * aspect;
        }
        else
        {
            w = area.Width;
            h = w / aspect;
        }

        return new RectD(area.X + ((area.Width - w) / 2), area.Y + ((area.Height - h) / 2), w, h);
    }
}
