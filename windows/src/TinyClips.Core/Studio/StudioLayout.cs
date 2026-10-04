using System.Runtime.CompilerServices;

namespace TinyClips.Core.Studio;

public readonly record struct StudioSize(double Width, double Height);

public readonly record struct StudioFrameRect(double X, double Y, double Width, double Height);

public readonly record struct StudioResolvedFrame(
    int SceneIndex,
    StudioLayout Layout,
    StudioResolvedScreen? Screen,
    StudioResolvedCamera? Camera);

/// <param name="Opacity">
/// 1, except while a scene is being entered with a morph and only one of the two scenes has a
/// screen (section 6.9 of the project format). A value made with <c>default</c> has 0.
/// </param>
public readonly record struct StudioResolvedScreen(
    StudioFrameRect Rect,
    StudioFrameRect Source,
    double CornerRadius,
    StudioResolvedShadow Shadow,
    double Opacity = 1);

/// <param name="Opacity">
/// 1, except while a scene is being entered with a morph and only one of the two scenes has a
/// camera (section 6.9 of the project format). A value made with <c>default</c> has 0.
/// </param>
public readonly record struct StudioResolvedCamera(
    StudioFrameRect Rect,
    StudioFrameRect Source,
    StudioCameraShape Shape,
    double CornerRadius,
    bool Mirror,
    double BorderWidth,
    StudioResolvedShadow Shadow,
    double SourceTime,
    bool Visible,
    double Opacity = 1);

public readonly record struct StudioResolvedShadow(double Blur, double OffsetY, double Opacity);

public sealed record StudioTimeSegment(double Start, double End);

/// <summary>
/// A stretch of the recording that the video keeps and plays at one rate. <paramref name="Rate"/>
/// is how many seconds of the recording pass in one second of video.
/// </summary>
public sealed record StudioTimePiece(double Start, double End, double Rate)
{
    /// <summary>How long the piece lasts in the video.</summary>
    public double OutputDuration => (End - Start) / Rate;
}

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

public sealed class StudioLayoutPlan
{
    private readonly StudioProject _project;
    private readonly StudioScene[] _scenes;
    private readonly double[] _transitionLengths;
    private readonly StudioFrameRect _screenBaseSourceRect;
    private readonly StudioZoom[] _zooms;
    private readonly StudioPreparedCursorSamples _cursorSamples;
    private readonly StudioRect? _cameraCrop;
    private readonly double _screenAspect;
    private readonly double _cameraAspect;
    private readonly bool _hasCamera;

    private StudioLayoutPlan(StudioProject project, StudioScene[] scenes, StudioEvents? events)
    {
        _project = project;
        _scenes = scenes;
        _transitionLengths = TransitionLengths(scenes);
        var screenCrop = StudioCanvasMath.ValidCropOrNull(project.Screen.Crop);
        _screenBaseSourceRect = ToFrameRect(screenCrop ?? new StudioRect(0, 0, 1, 1));
        _zooms = NormalizeZooms(project.Zooms);
        _cursorSamples = StudioPreparedCursorSamples.For(events);
        _screenAspect = (project.Sources.Screen.Width * (screenCrop?.Width ?? 1)) / (project.Sources.Screen.Height * (screenCrop?.Height ?? 1));
        _hasCamera = project.Sources.Camera is not null;
        _cameraCrop = StudioCanvasMath.ValidCropOrNull(project.Camera.Crop);
        if (_hasCamera)
        {
            var camera = project.Sources.Camera!;
            _cameraAspect = (camera.Width * (_cameraCrop?.Width ?? 1)) / (camera.Height * (_cameraCrop?.Height ?? 1));
        }
    }

    public IReadOnlyList<StudioScene> Scenes => _scenes;

    /// <summary>A plan for a project without events: a zoom that follows the pointer looks at its own focus point.</summary>
    public static StudioLayoutPlan Create(StudioProject project) => Create(project, events: null);

    /// <summary>
    /// A plan for a project and its events. Of the events only the cursor samples are used, by
    /// zooms that follow the pointer.
    /// </summary>
    public static StudioLayoutPlan Create(StudioProject project, StudioEvents? events)
    {
        ArgumentNullException.ThrowIfNull(project);
        return new StudioLayoutPlan(project, NormalizeScenes(project.Scenes), events);
    }

    public StudioResolvedFrame Resolve(double time, double canvasWidth, double canvasHeight)
    {
        var sceneIndex = ActiveSceneIndex(_scenes, time);
        var (layout, screen, camera) = SceneAtRest(sceneIndex, time, canvasWidth, canvasHeight);

        // A scene entered with a morph: for a moment the layers are still on their way from
        // where the scene before had them (section 6.9). A scene with a length to its move is
        // never the first, and the active scene has started, so only the end needs asking.
        var length = _transitionLengths[sceneIndex];
        var start = _scenes[sceneIndex].Start;
        if (length > 0 && time < start + length)
        {
            var k = Ease((time - start) / length);
            var (_, fromScreen, fromCamera) = SceneAtRest(sceneIndex - 1, time, canvasWidth, canvasHeight);
            screen = Move(fromScreen, screen, k);
            camera = Move(fromCamera, camera, k);
        }

        return new StudioResolvedFrame(sceneIndex, layout, screen, camera);
    }

    /// <summary>
    /// How long the layers take to move into each scene: 0 for a cut and for the first scene, and
    /// never longer than the scene itself, so a move always starts from a scene at rest.
    /// </summary>
    private static double[] TransitionLengths(StudioScene[] scenes)
    {
        var lengths = new double[scenes.Length];
        for (var i = 1; i < scenes.Length; i++)
        {
            lengths[i] = TransitionLength(scenes, i);
        }

        return lengths;
    }

    /// <summary>
    /// How long the layers take to move into scene <paramref name="index"/> of a normalized
    /// scene list (section 6.9): 0 for a cut, for the first scene and where there is no such scene.
    /// </summary>
    internal static double TransitionLength(IReadOnlyList<StudioScene> scenes, int index)
    {
        if (index < 1 || index >= scenes.Count || scenes[index].Transition.Kind != StudioTransitionKind.Morph)
        {
            return 0;
        }

        var length = Clamp(scenes[index].Transition.Duration, 0, 2);
        return index + 1 < scenes.Count ? Math.Min(length, scenes[index + 1].Start - scenes[index].Start) : length;
    }

    // A layer on its way from the scene before to this one. One that only one of the two scenes
    // has stays where that scene has it and fades.
    private static StudioResolvedScreen? Move(StudioResolvedScreen? from, StudioResolvedScreen? to, double k)
    {
        if (from is not { } origin)
        {
            return to is { } appearing ? appearing with { Opacity = k } : null;
        }

        if (to is not { } target)
        {
            return origin with { Opacity = 1 - k };
        }

        return target with
        {
            Rect = Lerp(origin.Rect, target.Rect, k),
            CornerRadius = origin.CornerRadius + ((target.CornerRadius - origin.CornerRadius) * k),
        };
    }

    private StudioResolvedCamera? Move(StudioResolvedCamera? from, StudioResolvedCamera? to, double k)
    {
        if (from is not { } origin)
        {
            return to is { } appearing ? appearing with { Opacity = k } : null;
        }

        if (to is not { } target)
        {
            return origin with { Opacity = 1 - k };
        }

        // The source is worked out again for the card the camera has now, so its picture is
        // cropped to the card all the way and never stretched. Between two shapes it moves as
        // a rounded rectangle.
        var rect = Lerp(origin.Rect, target.Rect, k);
        var sameShape = origin.Shape == target.Shape;
        var fromRadius = sameShape ? origin.CornerRadius : RadiusBetweenShapes(origin);
        var toRadius = sameShape ? target.CornerRadius : RadiusBetweenShapes(target);
        return target with
        {
            Rect = rect,
            Source = CameraSourceRect(rect),
            Shape = sameShape ? target.Shape : StudioCameraShape.RoundedRectangle,
            CornerRadius = fromRadius + ((toRadius - fromRadius) * k),
        };
    }

    /// <summary>
    /// The corner radius a camera has as one end of a move between two shapes. A squircle is
    /// drawn without one: the rounded rectangle that reaches as far into the corners of its box
    /// has a radius of 0.22 of its short side.
    /// </summary>
    private static double RadiusBetweenShapes(StudioResolvedCamera camera) =>
        camera.Shape == StudioCameraShape.Squircle ? 0.22 * Math.Min(camera.Rect.Width, camera.Rect.Height) : camera.CornerRadius;

    /// <summary>Sections 6.2 to 6.5: the layout of one scene at rest.</summary>
    private (StudioLayout Layout, StudioResolvedScreen? Screen, StudioResolvedCamera? Camera) SceneAtRest(
        int sceneIndex,
        double time,
        double canvasWidth,
        double canvasHeight)
    {
        var scene = _scenes[sceneIndex];
        var layout = !_hasCamera ? StudioLayout.Screen : scene.Layout;

        var shortSide = Math.Min(canvasWidth, canvasHeight);
        var padding = Clamp(_project.Canvas.Padding, 0, 0.4) * shortSide;
        var content = new StudioFrameRect(padding, padding, canvasWidth - (2 * padding), canvasHeight - (2 * padding));
        var screenRadiusBase = Clamp(_project.Screen.CornerRadius, 0, 0.2) * shortSide;

        StudioFrameRect? screenRect = null;
        StudioFrameRect? cameraRect = null;

        switch (layout)
        {
            case StudioLayout.Camera:
                cameraRect = content;
                break;
            case StudioLayout.Bubble:
                screenRect = Fit(_screenAspect, content);
                cameraRect = BubbleRect(scene, canvasWidth, canvasHeight, shortSide);
                break;
            case StudioLayout.SideBySide:
                (screenRect, cameraRect) = SideBySideRects(scene, content, canvasWidth, canvasHeight, shortSide);
                break;
            default:
                screenRect = Fit(_screenAspect, content);
                break;
        }

        StudioResolvedScreen? screen = layout == StudioLayout.Camera
            ? null
            : new StudioResolvedScreen(
                screenRect!.Value,
                ZoomWindow(time),
                Math.Min(screenRadiusBase, Math.Min(screenRect.Value.Width, screenRect.Value.Height) / 2),
                Shadow(_project.Screen.Shadow, shortSide));

        StudioResolvedCamera? camera = cameraRect is null || !_hasCamera
            ? null
            : ResolveCamera(scene, layout, cameraRect.Value, screenRadiusBase, shortSide, time);

        return (layout, screen, camera);
    }

    internal static StudioScene[] NormalizeScenes(IReadOnlyList<StudioScene>? scenes)
    {
        if (scenes is null || scenes.Count == 0)
        {
            return [new StudioScene { Start = 0 }];
        }

        var sorted = new (StudioScene Scene, int Index)[scenes.Count];
        for (var i = 0; i < scenes.Count; i++)
        {
            sorted[i] = (scenes[i] with { Start = Math.Max(0, scenes[i].Start) }, i);
        }

        Array.Sort(sorted, static (left, right) =>
        {
            var start = left.Scene.Start.CompareTo(right.Scene.Start);
            return start != 0 ? start : left.Index.CompareTo(right.Index);
        });

        var normalized = new List<StudioScene>(sorted.Length);
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

    internal static StudioZoom[] NormalizeZooms(IReadOnlyList<StudioZoom>? zooms)
    {
        if (zooms is null || zooms.Count == 0)
        {
            return [];
        }

        var sorted = zooms
            .Where(static zoom => zoom is not null)
            .Select((zoom, index) => (Zoom: zoom with { Start = Math.Max(0, zoom.Start) }, Index: index))
            .Where(static item => item.Zoom.End > item.Zoom.Start)
            .OrderBy(static item => item.Zoom.Start)
            .ThenBy(static item => item.Index)
            .ToArray();

        var normalized = new List<StudioZoom>(sorted.Length);
        foreach (var item in sorted)
        {
            if (normalized.Count > 0 && normalized[^1].Start == item.Zoom.Start)
            {
                normalized[^1] = item.Zoom;
            }
            else
            {
                normalized.Add(item.Zoom);
            }
        }

        for (var i = 0; i < normalized.Count - 1; i++)
        {
            if (normalized[i + 1].Start < normalized[i].End)
            {
                normalized[i] = normalized[i] with { End = normalized[i + 1].Start };
            }
        }

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

    private StudioFrameRect BubbleRect(StudioScene scene, double canvasWidth, double canvasHeight, double shortSide)
    {
        var d = Clamp(scene.Bubble.Size, 0.08, 0.6) * shortSide;
        double bw;
        double bh;
        if (_project.Camera.Shape is StudioCameraShape.Circle or StudioCameraShape.Squircle)
        {
            bw = d;
            bh = d;
        }
        else
        {
            bh = d;
            bw = d * Clamp(_cameraAspect, 0.5, 2);
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
        return new StudioFrameRect(x, y, bw, bh);
    }

    private (StudioFrameRect Screen, StudioFrameRect Camera) SideBySideRects(
        StudioScene scene,
        StudioFrameRect content,
        double canvasWidth,
        double canvasHeight,
        double shortSide)
    {
        var gap = 0.02 * shortSide;
        var fraction = Clamp(scene.Split.CameraFraction, 0.15, 0.6);

        if (canvasWidth >= canvasHeight)
        {
            var cameraWidth = fraction * (content.Width - gap);
            var screen = Fit(_screenAspect, new StudioFrameRect(0, 0, content.Width - gap - cameraWidth, content.Height));
            var x0 = content.X + (content.Width - (screen.Width + gap + cameraWidth)) / 2;
            var y0 = content.Y + (content.Height - screen.Height) / 2;
            return scene.Split.CameraSide == StudioCameraSide.Trailing
                ? (new StudioFrameRect(x0, y0, screen.Width, screen.Height), new StudioFrameRect(x0 + screen.Width + gap, y0, cameraWidth, screen.Height))
                : (new StudioFrameRect(x0 + cameraWidth + gap, y0, screen.Width, screen.Height), new StudioFrameRect(x0, y0, cameraWidth, screen.Height));
        }
        else
        {
            var cameraHeight = fraction * (content.Height - gap);
            var screen = Fit(_screenAspect, new StudioFrameRect(0, 0, content.Width, content.Height - gap - cameraHeight));
            var x0 = content.X + (content.Width - screen.Width) / 2;
            var y0 = content.Y + (content.Height - (screen.Height + gap + cameraHeight)) / 2;
            return scene.Split.CameraSide == StudioCameraSide.Trailing
                ? (new StudioFrameRect(x0, y0, screen.Width, screen.Height), new StudioFrameRect(x0, y0 + screen.Height + gap, screen.Width, cameraHeight))
                : (new StudioFrameRect(x0, y0 + cameraHeight + gap, screen.Width, screen.Height), new StudioFrameRect(x0, y0, screen.Width, cameraHeight));
        }
    }

    private StudioResolvedCamera ResolveCamera(
        StudioScene scene,
        StudioLayout layout,
        StudioFrameRect rect,
        double screenRadiusBase,
        double shortSide,
        double time)
    {
        var shape = StudioCameraShape.Rectangle;
        double cornerRadius;
        if (layout == StudioLayout.Bubble)
        {
            shape = _project.Camera.Shape;
            cornerRadius = shape switch
            {
                StudioCameraShape.Circle or StudioCameraShape.Squircle => Math.Min(rect.Width, rect.Height) / 2,
                StudioCameraShape.RoundedRectangle => Clamp(_project.Camera.CornerRadius, 0, 0.5) * Math.Min(rect.Width, rect.Height),
                _ => 0,
            };
        }
        else
        {
            cornerRadius = Math.Min(screenRadiusBase, Math.Min(rect.Width, rect.Height) / 2);
            shape = cornerRadius > 0 ? StudioCameraShape.RoundedRectangle : StudioCameraShape.Rectangle;
        }

        var cameraSource = _project.Sources.Camera!;
        var sourceTime = Clamp(time - cameraSource.StartOffset, 0, cameraSource.Duration);
        return new StudioResolvedCamera(
            rect,
            CameraSourceRect(rect),
            shape,
            cornerRadius,
            _project.Camera.Mirror,
            Clamp(_project.Camera.BorderWidth, 0, 0.02) * shortSide,
            Shadow(_project.Camera.Shadow, shortSide),
            sourceTime,
            time - cameraSource.StartOffset >= 0 && time - cameraSource.StartOffset <= cameraSource.Duration);
    }

    private StudioFrameRect CameraSourceRect(StudioFrameRect cameraRect)
    {
        var crop = _cameraCrop ?? new StudioRect(0, 0, 1, 1);
        var destinationAspect = cameraRect.Width / cameraRect.Height;
        if (_cameraAspect > destinationAspect)
        {
            var k = destinationAspect / _cameraAspect;
            return new StudioFrameRect(crop.X + (crop.Width * (1 - k) / 2), crop.Y, crop.Width * k, crop.Height);
        }
        else
        {
            var k = _cameraAspect / destinationAspect;
            return new StudioFrameRect(crop.X, crop.Y + (crop.Height * (1 - k) / 2), crop.Width, crop.Height * k);
        }
    }

    private StudioFrameRect ZoomWindow(double time)
    {
        var active = ActiveZoomIndex(time);
        if (active < 0)
        {
            return _screenBaseSourceRect;
        }

        var zoom = _zooms[active];
        var chainedToPrevious = active > 0 && _zooms[active - 1].End == zoom.Start;
        var nextIsChained = active + 1 < _zooms.Length && _zooms[active + 1].Start == zoom.End;
        var easeIn = Clamp(zoom.EaseIn, 0, 3);
        var easeOut = nextIsChained ? 0 : Clamp(zoom.EaseOut, 0, 3);
        var duration = zoom.End - zoom.Start;
        if (easeIn + easeOut > duration)
        {
            var factor = duration / (easeIn + easeOut);
            easeIn *= factor;
            easeOut *= factor;
        }

        var held = HeldWindow(zoom, time);
        if (time < zoom.Start + easeIn)
        {
            var from = chainedToPrevious ? HeldWindow(_zooms[active - 1], time) : _screenBaseSourceRect;
            return Lerp(from, held, Ease((time - zoom.Start) / easeIn));
        }

        if (time > zoom.End - easeOut)
        {
            return Lerp(_screenBaseSourceRect, held, Ease((zoom.End - time) / easeOut));
        }

        return held;
    }

    private int ActiveZoomIndex(double time)
    {
        var low = 0;
        var high = _zooms.Length - 1;
        var candidate = -1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (_zooms[middle].Start <= time)
            {
                candidate = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return candidate >= 0 && time < _zooms[candidate].End ? candidate : -1;
    }

    private StudioFrameRect HeldWindow(StudioZoom zoom, double time)
    {
        var (focusX, focusY) = Focus(zoom, time);
        return StudioZoomMath.HeldWindow(_screenBaseSourceRect, zoom.Scale, focusX, focusY);
    }

    private (double X, double Y) Focus(StudioZoom zoom, double time)
    {
        if (zoom.Focus.Mode == StudioZoomFocusMode.Cursor && _cursorSamples.Count > 0)
        {
            return _cursorSamples.MeanInCenteredSecond(time);
        }

        return (Clamp(zoom.Focus.X, 0, 1), Clamp(zoom.Focus.Y, 0, 1));
    }

    private static double Ease(double value) => value * value * (3 - (2 * value));

    private static StudioFrameRect Lerp(StudioFrameRect from, StudioFrameRect to, double amount) =>
        new(
            from.X + ((to.X - from.X) * amount),
            from.Y + ((to.Y - from.Y) * amount),
            from.Width + ((to.Width - from.Width) * amount),
            from.Height + ((to.Height - from.Height) * amount));

    private static StudioFrameRect Fit(double aspect, StudioFrameRect rect)
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

        return new StudioFrameRect(rect.X + ((rect.Width - width) / 2), rect.Y + ((rect.Height - height) / 2), width, height);
    }

    private static StudioResolvedShadow Shadow(double intensity, double shortSide)
    {
        var s = Clamp(intensity, 0, 1);
        return new StudioResolvedShadow(s * 0.04 * shortSide, s * 0.012 * shortSide, s * 0.5);
    }

    private static StudioFrameRect ToFrameRect(StudioRect rect) =>
        new(rect.X, rect.Y, rect.Width, rect.Height);

    private static double Clamp(double value, double min, double max) =>
        Math.Min(max, Math.Max(min, value));
}

public static class StudioLayoutResolver
{
    /// <summary>Resolves a frame of a project without events: a zoom that follows the pointer looks at its own focus point.</summary>
    public static StudioResolvedFrame Resolve(StudioProject project, double time, double canvasWidth, double canvasHeight) =>
        StudioLayoutPlan.Create(project).Resolve(time, canvasWidth, canvasHeight);

    /// <summary>
    /// Resolves a frame of a project with its events. Whoever resolves many frames of one project
    /// keeps a <see cref="StudioLayoutPlan"/> instead, which normalizes the scenes and zooms once.
    /// </summary>
    public static StudioResolvedFrame Resolve(StudioProject project, StudioEvents? events, double time, double canvasWidth, double canvasHeight) =>
        StudioLayoutPlan.Create(project, events).Resolve(time, canvasWidth, canvasHeight);

    internal static StudioScene[] NormalizeScenes(IReadOnlyList<StudioScene>? scenes) =>
        StudioLayoutPlan.NormalizeScenes(scenes);

    internal static StudioZoom[] NormalizeZooms(IReadOnlyList<StudioZoom>? zooms) =>
        StudioLayoutPlan.NormalizeZooms(zooms);
}

internal static class StudioZoomMath
{
    /// <summary>
    /// The part of the screen a zoom shows while it is held (section 6.8 of the project format):
    /// <paramref name="baseRect"/> made smaller by the scale, centered on the focus, and pushed back
    /// inside where it would stick out. The lower bound is applied last, so it wins if rounding
    /// puts the upper bound below it.
    /// </summary>
    public static StudioFrameRect HeldWindow(StudioFrameRect baseRect, double scale, double focusX, double focusY)
    {
        var clamped = Math.Min(5, Math.Max(1, scale));
        var width = baseRect.Width / clamped;
        var height = baseRect.Height / clamped;
        var x = Math.Max(baseRect.X, Math.Min(focusX - (width / 2), baseRect.X + baseRect.Width - width));
        var y = Math.Max(baseRect.Y, Math.Min(focusY - (height / 2), baseRect.Y + baseRect.Height - height));
        return new StudioFrameRect(x, y, width, height);
    }
}

/// <summary>
/// A recording's cursor samples in time order with their points clamped to the frame, ready for
/// <see cref="MeanInCenteredSecond"/>, which a zoom that follows the pointer asks for every frame.
/// </summary>
internal sealed class StudioPreparedCursorSamples
{
    private static readonly StudioPreparedCursorSamples Empty = new([]);

    // A long recording has a hundred thousand samples, and the layout is resolved for every frame
    // of a preview and an export. So the samples are prepared once for each cursor array and kept
    // for as long as that array is alive. Nothing else refers to an entry, so it goes with its array.
    private static readonly ConditionalWeakTable<StudioCursorSample[], StudioPreparedCursorSamples> Prepared = new();

    private readonly double[] _times;
    private readonly double[] _x;
    private readonly double[] _y;

    private StudioPreparedCursorSamples(StudioCursorSample[] cursor)
    {
        var count = 0;
        var inOrder = true;
        var samples = new StudioCursorSample[cursor.Length];
        foreach (var sample in cursor)
        {
            if (sample is null)
            {
                continue;
            }

            inOrder &= count == 0 || !(sample.T < samples[count - 1].T);
            samples[count++] = sample;
        }

        if (!inOrder)
        {
            // By time, and for equal times in the order they are stored.
            var order = new int[count];
            for (var i = 0; i < count; i++)
            {
                order[i] = i;
            }

            var unsorted = samples;
            Array.Sort(order, (left, right) =>
            {
                var byTime = unsorted[left].T.CompareTo(unsorted[right].T);
                return byTime != 0 ? byTime : left.CompareTo(right);
            });
            samples = new StudioCursorSample[count];
            for (var i = 0; i < count; i++)
            {
                samples[i] = unsorted[order[i]];
            }
        }

        _times = new double[count];
        _x = new double[count];
        _y = new double[count];
        for (var i = 0; i < count; i++)
        {
            _times[i] = samples[i].T;
            _x[i] = Math.Min(1, Math.Max(0, samples[i].X));
            _y[i] = Math.Min(1, Math.Max(0, samples[i].Y));
        }
    }

    public int Count => _times.Length;

    /// <summary>
    /// The prepared samples of <paramref name="events"/>. The cursor array of an events value is
    /// never changed in place; a changed recording has a new array and is prepared again.
    /// </summary>
    public static StudioPreparedCursorSamples For(StudioEvents? events) =>
        events?.Cursor is { Length: > 0 } cursor
            ? Prepared.GetValue(cursor, static samples => new StudioPreparedCursorSamples(samples))
            : Empty;

    /// <summary>
    /// The pointer's mean position over the second centered on <paramref name="time"/>. A sample
    /// lasts until the next one; before the first sample the pointer counts as being at the first,
    /// and after the last at the last. Only meaningful when there is at least one sample.
    /// </summary>
    public (double X, double Y) MeanInCenteredSecond(double time)
    {
        var count = _times.Length;
        if (count == 0)
        {
            return (0.5, 0.5);
        }

        var start = time - 0.5;
        var end = time + 0.5;

        // The sample the pointer is at when the second begins. Every one before it has ended by then.
        var first = Math.Max(0, UpperBound(_times, start) - 1);
        double x = 0;
        double y = 0;
        for (var i = first; i < count; i++)
        {
            var from = i == 0 ? double.NegativeInfinity : _times[i];
            if (from >= end)
            {
                break;
            }

            var until = i == count - 1 ? double.PositiveInfinity : _times[i + 1];
            var length = Math.Max(0, Math.Min(end, until) - Math.Max(start, from));
            x += _x[i] * length;
            y += _y[i] * length;
            if (until >= end)
            {
                break;
            }
        }

        return (x, y);
    }

    /// <summary>The index of the first value greater than <paramref name="value"/>.</summary>
    private static int UpperBound(double[] values, double value)
    {
        var low = 0;
        var high = values.Length;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (values[middle] <= value)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }
}

/// <summary>
/// Converts between source time and output time: the trim, the cuts, and speed (section 7 of the
/// project format).
/// </summary>
public sealed class StudioTimeMap
{
    /// <summary>The slowest rate a speed entry can have. A lower one counts as this.</summary>
    public const double SlowestRate = 0.25;

    /// <summary>The fastest rate a speed entry can have. A higher one counts as this.</summary>
    public const double FastestRate = 8;

    private readonly StudioTimeSegment[] _segments;
    private readonly StudioTimePiece[] _pieces;
    private readonly double[] _cumulative;
    private readonly double _start;

    public StudioTimeMap(double sourceDuration, StudioEdits? edits = null)
    {
        edits ??= new StudioEdits();
        _start = Clamp(edits.TrimStart, 0, sourceDuration);
        var end = Clamp(edits.TrimEnd ?? sourceDuration, _start, sourceDuration);
        _segments = BuildSegments(_start, end, edits.Cuts ?? []);
        _pieces = BuildPieces(_segments, BuildSpeed(edits.Speed ?? []));
        _cumulative = new double[_pieces.Length];
        double current = 0;
        for (var i = 0; i < _pieces.Length; i++)
        {
            _cumulative[i] = current;
            current += _pieces[i].OutputDuration;
        }

        OutputDuration = current;
    }

    public static StudioTimeMap FromProject(StudioProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return new StudioTimeMap(project.Sources.Screen.Duration, project.Edits);
    }

    public double OutputDuration { get; }

    /// <summary>The stretches of the recording the video keeps: the trim without the cuts.</summary>
    public IReadOnlyList<StudioTimeSegment> Segments => _segments;

    /// <summary>
    /// The kept stretches divided where the speed changes, each with its rate. Without speed
    /// entries these are the <see cref="Segments"/>, each at rate 1.
    /// </summary>
    public IReadOnlyList<StudioTimePiece> Pieces => _pieces;

    /// <summary>
    /// How many seconds of the recording pass in one second of video at a source time. 1 where no
    /// speed entry is, and where nothing is kept.
    /// </summary>
    public double GetRate(double sourceTime)
    {
        foreach (var piece in _pieces)
        {
            if (sourceTime >= piece.Start && sourceTime < piece.End)
            {
                return piece.Rate;
            }
        }

        return 1;
    }

    public double SourceToOutput(double sourceTime)
    {
        if (_pieces.Length == 0)
        {
            return 0;
        }

        if (sourceTime < _pieces[0].Start)
        {
            return 0;
        }

        for (var i = 0; i < _pieces.Length; i++)
        {
            var piece = _pieces[i];
            if (sourceTime >= piece.Start && sourceTime < piece.End)
            {
                return _cumulative[i] + ((sourceTime - piece.Start) / piece.Rate);
            }

            if (i < _pieces.Length - 1 && sourceTime >= piece.End && sourceTime < _pieces[i + 1].Start)
            {
                return _cumulative[i + 1];
            }
        }

        return OutputDuration;
    }

    public double OutputToSource(double outputTime)
    {
        if (_pieces.Length == 0)
        {
            return _start;
        }

        var clampedOutput = Clamp(outputTime, 0, OutputDuration);
        for (var i = 0; i < _pieces.Length; i++)
        {
            var piece = _pieces[i];
            if (clampedOutput >= _cumulative[i] && clampedOutput < _cumulative[i] + piece.OutputDuration)
            {
                return piece.Start + ((clampedOutput - _cumulative[i]) * piece.Rate);
            }
        }

        return _pieces[^1].End;
    }

    private static StudioTimeSegment[] BuildSegments(double start, double end, IReadOnlyList<StudioTimeRange> cuts)
    {
        var mergedCuts = cuts
            .Where(static cut => cut is not null)
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

    /// <summary>
    /// The speed entries that count, in time order and clear of each other. The trim and the cuts
    /// play no part here, so the rate at a source time is the same wherever they are.
    /// </summary>
    private static List<StudioTimePiece> BuildSpeed(IReadOnlyList<StudioSpeedRange> speed)
    {
        // The place in the file decides between entries with the same start and end.
        var entries = speed
            .Where(static entry => entry is not null && double.IsFinite(entry.Rate) && entry.Rate > 0)
            .Select((entry, index) => (Piece: new StudioTimePiece(entry.Start, entry.End, Clamp(entry.Rate, SlowestRate, FastestRate)), Index: index))
            .Where(static entry => entry.Piece.End > entry.Piece.Start && entry.Piece.Rate != 1)
            .OrderBy(static entry => entry.Piece.Start)
            .ThenBy(static entry => entry.Piece.End)
            .ThenBy(static entry => entry.Index)
            .Select(static entry => entry.Piece);

        var result = new List<StudioTimePiece>();
        foreach (var entry in entries)
        {
            var entryStart = result.Count > 0 && entry.Start < result[^1].End ? result[^1].End : entry.Start;
            if (entry.End > entryStart)
            {
                result.Add(entry with { Start = entryStart });
            }
        }

        return result;
    }

    /// <summary>The kept segments divided where the rate changes inside them.</summary>
    private static StudioTimePiece[] BuildPieces(StudioTimeSegment[] segments, List<StudioTimePiece> speed)
    {
        var pieces = new List<StudioTimePiece>(segments.Length);
        var points = new List<double>();
        foreach (var segment in segments)
        {
            points.Clear();
            points.Add(segment.Start);
            foreach (var entry in speed)
            {
                if (entry.Start > segment.Start && entry.Start < segment.End)
                {
                    points.Add(entry.Start);
                }

                if (entry.End > segment.Start && entry.End < segment.End)
                {
                    points.Add(entry.End);
                }
            }

            points.Add(segment.End);
            points.Sort();

            var first = pieces.Count;
            for (var i = 0; i < points.Count - 1; i++)
            {
                var partStart = points[i];
                var partEnd = points[i + 1];
                if (!(partEnd > partStart))
                {
                    continue;
                }

                var rate = 1.0;
                foreach (var entry in speed)
                {
                    if (partStart >= entry.Start && partStart < entry.End)
                    {
                        rate = entry.Rate;
                        break;
                    }
                }

                if (pieces.Count > first && pieces[^1].Rate == rate)
                {
                    pieces[^1] = pieces[^1] with { End = partEnd };
                }
                else
                {
                    pieces.Add(new StudioTimePiece(partStart, partEnd, rate));
                }
            }
        }

        return pieces.ToArray();
    }

    private static double Clamp(double value, double min, double max) =>
        Math.Min(max, Math.Max(min, value));
}