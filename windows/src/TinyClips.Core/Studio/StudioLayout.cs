namespace TinyClips.Core.Studio;

public readonly record struct StudioSize(double Width, double Height);

public sealed record StudioResolvedFrame(
    int SceneIndex,
    StudioLayout Layout,
    StudioResolvedScreen? Screen,
    StudioResolvedCamera? Camera);

public sealed record StudioResolvedScreen(
    StudioRect Rect,
    StudioRect Source,
    double CornerRadius,
    StudioResolvedShadow Shadow);

public sealed record StudioResolvedCamera(
    StudioRect Rect,
    StudioRect Source,
    StudioCameraShape Shape,
    double CornerRadius,
    bool Mirror,
    double BorderWidth,
    StudioResolvedShadow Shadow,
    double SourceTime,
    bool Visible);

public sealed record StudioResolvedShadow(double Blur, double OffsetY, double Opacity);

public sealed record StudioTimeSegment(double Start, double End);

public static class StudioCanvasMath
{
    public static StudioSize NaturalSize(StudioProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var screenCrop = ValidCropOrNull(project.Screen.Crop);
        var sw = project.Sources.Screen.Width * (screenCrop?.Width ?? 1);
        var sh = project.Sources.Screen.Height * (screenCrop?.Height ?? 1);
        var aspect = AspectRatio(project.Canvas.Aspect, sw, sh);

        double width;
        double height;
        if (project.Canvas.Aspect == StudioCanvasAspect.Auto)
        {
            width = sw;
            height = sh;
        }
        else if (sw / sh >= aspect)
        {
            width = sw;
            height = sw / aspect;
        }
        else
        {
            width = sh * aspect;
            height = sh;
        }

        return new StudioSize(Even(width), Even(height));
    }

    public static StudioSize ExportSize(StudioSize naturalSize, double longSideLimit)
    {
        var scale = longSideLimit <= 0 ? 1 : Math.Min(1, longSideLimit / Math.Max(naturalSize.Width, naturalSize.Height));
        return new StudioSize(Even(naturalSize.Width * scale), Even(naturalSize.Height * scale));
    }

    internal static double Even(double value) =>
        Math.Max(2, 2 * Math.Round(value / 2, MidpointRounding.AwayFromZero));

    internal static StudioRect? ValidCropOrNull(StudioRect? crop)
    {
        if (crop is null)
        {
            return null;
        }

        return crop.X >= 0
            && crop.Y >= 0
            && crop.Width >= 0.05
            && crop.Height >= 0.05
            && crop.X + crop.Width <= 1 + 1e-9
            && crop.Y + crop.Height <= 1 + 1e-9
                ? crop
                : null;
    }

    private static double AspectRatio(StudioCanvasAspect aspect, double screenWidth, double screenHeight) =>
        aspect switch
        {
            StudioCanvasAspect.Square => 1,
            StudioCanvasAspect.Landscape4X3 => 4.0 / 3.0,
            StudioCanvasAspect.Landscape16X9 => 16.0 / 9.0,
            StudioCanvasAspect.Portrait3X4 => 3.0 / 4.0,
            StudioCanvasAspect.Portrait9X16 => 9.0 / 16.0,
            _ => screenWidth / screenHeight,
        };
}

public static class StudioLayoutResolver
{
    public static StudioResolvedFrame Resolve(StudioProject project, double time, double canvasWidth, double canvasHeight)
    {
        ArgumentNullException.ThrowIfNull(project);

        var scenes = NormalizeScenes(project.Scenes);
        var sceneIndex = ActiveSceneIndex(scenes, time);
        var scene = scenes[sceneIndex];
        var layout = project.Sources.Camera is null ? StudioLayout.Screen : scene.Layout;

        var shortSide = Math.Min(canvasWidth, canvasHeight);
        var padding = Clamp(project.Canvas.Padding, 0, 0.4) * shortSide;
        var content = new StudioRect(padding, padding, canvasWidth - (2 * padding), canvasHeight - (2 * padding));
        var screenAspect = ScreenAspect(project);
        var screenRadiusBase = Clamp(project.Screen.CornerRadius, 0, 0.2) * shortSide;

        StudioRect? screenRect = null;
        StudioRect? cameraRect = null;

        switch (layout)
        {
            case StudioLayout.Camera:
                cameraRect = content;
                break;
            case StudioLayout.Bubble:
                screenRect = Fit(screenAspect, content);
                cameraRect = BubbleRect(project, scene, canvasWidth, canvasHeight, shortSide);
                break;
            case StudioLayout.SideBySide:
                (screenRect, cameraRect) = SideBySideRects(project, scene, content, canvasWidth, canvasHeight, shortSide, screenAspect);
                break;
            default:
                screenRect = Fit(screenAspect, content);
                break;
        }

        var screen = layout == StudioLayout.Camera
            ? null
            : new StudioResolvedScreen(
                screenRect!,
                StudioCanvasMath.ValidCropOrNull(project.Screen.Crop) ?? new StudioRect(0, 0, 1, 1),
                Math.Min(screenRadiusBase, Math.Min(screenRect!.Width, screenRect.Height) / 2),
                Shadow(project.Screen.Shadow, shortSide));

        var camera = cameraRect is null || project.Sources.Camera is null
            ? null
            : ResolveCamera(project, scene, layout, cameraRect, screenRadiusBase, shortSide, time);

        return new StudioResolvedFrame(sceneIndex, layout, screen, camera);
    }

    internal static StudioScene[] NormalizeScenes(IReadOnlyList<StudioScene>? scenes)
    {
        if (scenes is null || scenes.Count == 0)
        {
            return [new StudioScene { Start = 0 }];
        }

        var sorted = scenes
            .Select((scene, index) => (Scene: scene with { Start = Math.Max(0, scene.Start) }, Index: index))
            .OrderBy(item => item.Scene.Start)
            .ThenBy(item => item.Index)
            .ToArray();

        var normalized = new List<StudioScene>();
        foreach (var item in sorted)
        {
            if (normalized.Count > 0 && normalized[^1].Start == item.Scene.Start)
            {
                normalized[^1] = item.Scene;
            }
            else
            {
                normalized.Add(item.Scene);
            }
        }

        normalized[0] = normalized[0] with { Start = 0 };
        return normalized.ToArray();
    }

    private static int ActiveSceneIndex(StudioScene[] scenes, double time)
    {
        if (time < 0)
        {
            return 0;
        }

        var active = 0;
        for (var i = 0; i < scenes.Length; i++)
        {
            if (scenes[i].Start <= time)
            {
                active = i;
            }
        }

        return active;
    }

    private static double ScreenAspect(StudioProject project)
    {
        var crop = StudioCanvasMath.ValidCropOrNull(project.Screen.Crop);
        var width = project.Sources.Screen.Width * (crop?.Width ?? 1);
        var height = project.Sources.Screen.Height * (crop?.Height ?? 1);
        return width / height;
    }

    private static double CameraAspect(StudioProject project)
    {
        var camera = project.Sources.Camera!;
        var crop = StudioCanvasMath.ValidCropOrNull(project.Camera.Crop);
        var width = camera.Width * (crop?.Width ?? 1);
        var height = camera.Height * (crop?.Height ?? 1);
        return width / height;
    }

    private static StudioRect BubbleRect(StudioProject project, StudioScene scene, double canvasWidth, double canvasHeight, double shortSide)
    {
        var d = Clamp(scene.Bubble.Size, 0.08, 0.6) * shortSide;
        double bw;
        double bh;
        if (project.Camera.Shape is StudioCameraShape.Circle or StudioCameraShape.Squircle)
        {
            bw = d;
            bh = d;
        }
        else
        {
            bh = d;
            bw = d * Clamp(CameraAspect(project), 0.5, 2);
        }

        if (bw > 0.9 * canvasWidth)
        {
            var scale = 0.9 * canvasWidth / bw;
            bh *= scale;
            bw *= scale;
        }

        var gap = 0.03 * shortSide;
        var x = scene.Bubble.Anchor is StudioAnchor.TopLeft or StudioAnchor.BottomLeft ? gap : canvasWidth - gap - bw;
        var y = scene.Bubble.Anchor is StudioAnchor.TopLeft or StudioAnchor.TopRight ? gap : canvasHeight - gap - bh;
        x = Clamp(x + (scene.Bubble.OffsetX * canvasWidth), 0, canvasWidth - bw);
        y = Clamp(y + (scene.Bubble.OffsetY * canvasHeight), 0, canvasHeight - bh);
        return new StudioRect(x, y, bw, bh);
    }

    private static (StudioRect Screen, StudioRect Camera) SideBySideRects(
        StudioProject project,
        StudioScene scene,
        StudioRect content,
        double canvasWidth,
        double canvasHeight,
        double shortSide,
        double screenAspect)
    {
        var gap = 0.02 * shortSide;
        var fraction = Clamp(scene.Split.CameraFraction, 0.15, 0.6);

        if (canvasWidth >= canvasHeight)
        {
            var cameraWidth = fraction * (content.Width - gap);
            var screen = Fit(screenAspect, new StudioRect(0, 0, content.Width - gap - cameraWidth, content.Height));
            var x0 = content.X + (content.Width - (screen.Width + gap + cameraWidth)) / 2;
            var y0 = content.Y + (content.Height - screen.Height) / 2;
            return scene.Split.CameraSide == StudioCameraSide.Trailing
                ? (new StudioRect(x0, y0, screen.Width, screen.Height), new StudioRect(x0 + screen.Width + gap, y0, cameraWidth, screen.Height))
                : (new StudioRect(x0 + cameraWidth + gap, y0, screen.Width, screen.Height), new StudioRect(x0, y0, cameraWidth, screen.Height));
        }
        else
        {
            var cameraHeight = fraction * (content.Height - gap);
            var screen = Fit(screenAspect, new StudioRect(0, 0, content.Width, content.Height - gap - cameraHeight));
            var x0 = content.X + (content.Width - screen.Width) / 2;
            var y0 = content.Y + (content.Height - (screen.Height + gap + cameraHeight)) / 2;
            return scene.Split.CameraSide == StudioCameraSide.Trailing
                ? (new StudioRect(x0, y0, screen.Width, screen.Height), new StudioRect(x0, y0 + screen.Height + gap, screen.Width, cameraHeight))
                : (new StudioRect(x0, y0 + cameraHeight + gap, screen.Width, screen.Height), new StudioRect(x0, y0, screen.Width, cameraHeight));
        }
    }

    private static StudioResolvedCamera ResolveCamera(
        StudioProject project,
        StudioScene scene,
        StudioLayout layout,
        StudioRect rect,
        double screenRadiusBase,
        double shortSide,
        double time)
    {
        var shape = StudioCameraShape.Rectangle;
        double cornerRadius;
        if (layout == StudioLayout.Bubble)
        {
            shape = project.Camera.Shape;
            cornerRadius = shape switch
            {
                StudioCameraShape.Circle or StudioCameraShape.Squircle => Math.Min(rect.Width, rect.Height) / 2,
                StudioCameraShape.RoundedRectangle => Clamp(project.Camera.CornerRadius, 0, 0.5) * Math.Min(rect.Width, rect.Height),
                _ => 0,
            };
        }
        else
        {
            cornerRadius = Math.Min(screenRadiusBase, Math.Min(rect.Width, rect.Height) / 2);
            shape = cornerRadius > 0 ? StudioCameraShape.RoundedRectangle : StudioCameraShape.Rectangle;
        }

        var cameraSource = project.Sources.Camera!;
        var sourceTime = Clamp(time - cameraSource.StartOffset, 0, cameraSource.Duration);
        return new StudioResolvedCamera(
            rect,
            CameraSourceRect(project, rect),
            shape,
            cornerRadius,
            project.Camera.Mirror,
            Clamp(project.Camera.BorderWidth, 0, 0.02) * shortSide,
            Shadow(project.Camera.Shadow, shortSide),
            sourceTime,
            time - cameraSource.StartOffset >= 0 && time - cameraSource.StartOffset <= cameraSource.Duration);
    }

    private static StudioRect CameraSourceRect(StudioProject project, StudioRect cameraRect)
    {
        var crop = StudioCanvasMath.ValidCropOrNull(project.Camera.Crop) ?? new StudioRect(0, 0, 1, 1);
        var cameraAspect = CameraAspect(project);
        var destinationAspect = cameraRect.Width / cameraRect.Height;
        if (cameraAspect > destinationAspect)
        {
            var k = destinationAspect / cameraAspect;
            return new StudioRect(crop.X + (crop.Width * (1 - k) / 2), crop.Y, crop.Width * k, crop.Height);
        }
        else
        {
            var k = cameraAspect / destinationAspect;
            return new StudioRect(crop.X, crop.Y + (crop.Height * (1 - k) / 2), crop.Width, crop.Height * k);
        }
    }

    private static StudioRect Fit(double aspect, StudioRect rect)
    {
        double width;
        double height;
        if (rect.Width / rect.Height > aspect)
        {
            height = rect.Height;
            width = height * aspect;
        }
        else
        {
            width = rect.Width;
            height = width / aspect;
        }

        return new StudioRect(rect.X + ((rect.Width - width) / 2), rect.Y + ((rect.Height - height) / 2), width, height);
    }

    private static StudioResolvedShadow Shadow(double intensity, double shortSide)
    {
        var s = Clamp(intensity, 0, 1);
        return new StudioResolvedShadow(s * 0.04 * shortSide, s * 0.012 * shortSide, s * 0.5);
    }

    private static double Clamp(double value, double min, double max) =>
        Math.Min(max, Math.Max(min, value));
}

public sealed class StudioTimeMap
{
    private readonly StudioTimeSegment[] _segments;
    private readonly double[] _cumulative;
    private readonly double _start;

    public StudioTimeMap(double sourceDuration, StudioEdits? edits = null)
    {
        edits ??= new StudioEdits();
        _start = Clamp(edits.TrimStart, 0, sourceDuration);
        var end = Clamp(edits.TrimEnd ?? sourceDuration, _start, sourceDuration);
        _segments = BuildSegments(_start, end, edits.Cuts);
        _cumulative = new double[_segments.Length];
        double current = 0;
        for (var i = 0; i < _segments.Length; i++)
        {
            _cumulative[i] = current;
            current += _segments[i].End - _segments[i].Start;
        }

        OutputDuration = current;
    }

    public static StudioTimeMap FromProject(StudioProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return new StudioTimeMap(project.Sources.Screen.Duration, project.Edits);
    }

    public double OutputDuration { get; }

    public IReadOnlyList<StudioTimeSegment> Segments => _segments;

    public double SourceToOutput(double sourceTime)
    {
        if (_segments.Length == 0)
        {
            return 0;
        }

        if (sourceTime < _segments[0].Start)
        {
            return 0;
        }

        for (var i = 0; i < _segments.Length; i++)
        {
            var segment = _segments[i];
            if (sourceTime >= segment.Start && sourceTime < segment.End)
            {
                return _cumulative[i] + (sourceTime - segment.Start);
            }

            if (i < _segments.Length - 1 && sourceTime >= segment.End && sourceTime < _segments[i + 1].Start)
            {
                return _cumulative[i + 1];
            }
        }

        return OutputDuration;
    }

    public double OutputToSource(double outputTime)
    {
        if (_segments.Length == 0)
        {
            return _start;
        }

        var clampedOutput = Clamp(outputTime, 0, OutputDuration);
        for (var i = 0; i < _segments.Length; i++)
        {
            var segmentLength = _segments[i].End - _segments[i].Start;
            if (clampedOutput >= _cumulative[i] && clampedOutput < _cumulative[i] + segmentLength)
            {
                return _segments[i].Start + (clampedOutput - _cumulative[i]);
            }
        }

        return _segments[^1].End;
    }

    private static StudioTimeSegment[] BuildSegments(double start, double end, IReadOnlyList<StudioTimeRange> cuts)
    {
        var mergedCuts = cuts
            .Select(cut => new StudioTimeRange { Start = Clamp(cut.Start, start, end), End = Clamp(cut.End, start, end) })
            .Where(cut => cut.End > cut.Start)
            .OrderBy(cut => cut.Start)
            .ThenBy(cut => cut.End)
            .ToList();

        for (var i = 0; i < mergedCuts.Count - 1;)
        {
            if (mergedCuts[i + 1].Start <= mergedCuts[i].End)
            {
                mergedCuts[i] = new StudioTimeRange { Start = mergedCuts[i].Start, End = Math.Max(mergedCuts[i].End, mergedCuts[i + 1].End) };
                mergedCuts.RemoveAt(i + 1);
            }
            else
            {
                i++;
            }
        }

        var segments = new List<StudioTimeSegment>();
        var cursor = start;
        foreach (var cut in mergedCuts)
        {
            if (cut.Start > cursor)
            {
                segments.Add(new StudioTimeSegment(cursor, cut.Start));
            }

            cursor = Math.Max(cursor, cut.End);
        }

        if (cursor < end)
        {
            segments.Add(new StudioTimeSegment(cursor, end));
        }

        return segments.ToArray();
    }

    private static double Clamp(double value, double min, double max) =>
        Math.Min(max, Math.Max(min, value));
}
