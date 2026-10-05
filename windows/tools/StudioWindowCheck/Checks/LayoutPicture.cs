using System.Globalization;
using TinyClips.Core.Studio;
using TinyClips.Tools.StudioPreviewCheck.Media;
using TinyClips.Tools.StudioWindowCheck.Capture;

namespace TinyClips.Tools.StudioWindowCheck.Checks;

// Reading from a picture where a scene's layout has put the screen recording and the camera. The
// project format says, for every instant, where each of the two is on the canvas and which part
// of its recording it shows there (sections 6.2 to 6.5, and 6.9 for a scene that is entered by
// moving): StudioLayoutResolver works that out. A picture shows that layout when the edges of
// each test clip are where the layer's rectangle and its part put them, and each frame strip
// reads its frame there. A layer that was drawn where another scene has it, or as it is at
// another instant of a move, has its edges somewhere else.
internal sealed partial class WindowChecks
{
    // How far the furthest edge was from its place in every picture that was read for a scene's layout.
    private readonly List<double> _layoutErrors = [];

    /// <summary>What a picture shows of one frame of a project with scenes.</summary>
    /// <param name="Frame">The frame of the screen recording the picture should show.</param>
    /// <param name="CameraFrame">The frame of the camera recording that goes with it, or <see cref="FrameCode.Unreadable"/> when the camera does not show then.</param>
    /// <param name="Resolved">Where the format puts the two layers at the frame's instant, on a canvas of the picture's size.</param>
    /// <param name="ScreenLayer">The screen recording's rectangle in pixels of the picture, or null when the layout has no screen.</param>
    /// <param name="CameraLayer">The camera's rectangle in pixels of the picture, or null when the layout has no camera.</param>
    private sealed record LayoutReading(int Frame, int CameraFrame, StudioResolvedFrame Resolved, StudioFrameRect? ScreenLayer, PartReading? Screen, StudioFrameRect? CameraLayer, PartReading? Camera)
    {
        /// <summary>How far the furthest edge that was found, of either layer, is from its place.</summary>
        public double Worst => new[] { Screen?.Worst ?? double.NaN, Camera?.Worst ?? double.NaN }.Where(worst => !double.IsNaN(worst)).DefaultIfEmpty(double.NaN).Max();

        public int Edges => (Screen?.Edges.Count ?? 0) + (Camera?.Edges.Count ?? 0);

        public override string ToString()
        {
            var screen = ScreenLayer is { } s ? $"the screen in {R(s)}, showing {Part(Resolved.Screen!.Value.Source)}{Faded(Resolved.Screen.Value.Opacity)}: {Screen?.ToString() ?? "not read, because it fades"}" : "no screen";
            var camera = CameraLayer is { } c ? $"the camera in {R(c)}, showing {Part(Resolved.Camera!.Value.Source)}{Faded(Resolved.Camera.Value.Opacity)}: {Camera?.ToString() ?? "not read, because it fades or does not show at that time"}" : "no camera";
            return $"{StudioEditorModel.GetLayoutName(Resolved.Layout)}, scene {Resolved.SceneIndex + 1}; {screen}; {camera}";
        }

        private static string Faded(double opacity) => opacity < 1 ? string.Create(CultureInfo.InvariantCulture, $" at {opacity:0.00} of its strength") : string.Empty;
    }

    private static ScreenPart Part(StudioFrameRect source) => new(source.X, source.Y, source.Width, source.Height);

    /// <summary>
    /// The camera frame that goes with a screen frame: the one the instant the screen frame
    /// stands for, its middle, lies in, once the camera's later start is taken off. Worked out
    /// here from the project format, not asked of the editor.
    /// </summary>
    private static int CameraFrameAt(TestFolder folder, StudioProject project, int frame)
    {
        if (folder.Camera is not { } clip || project.Sources.Camera is not { } source)
        {
            return FrameCode.Unreadable;
        }

        var time = MiddleOf(frame) - source.StartOffset;
        return time < 0 || time > source.Duration ? FrameCode.Unreadable : Math.Min(clip.FrameCount - 1, (int)Math.Floor((time * Fps) + 1e-6));
    }

    /// <summary>
    /// Whether a point of a picture shows a layer: inside the layer's rounded rectangle, a margin
    /// away from its outline, and not where another layer lies over it.
    /// </summary>
    private static Func<double, double, bool> ShownIn(StudioFrameRect layer, double radius, double margin, StudioFrameRect? coveredBy = null) => (x, y) =>
    {
        var (left, top, right, bottom) = (layer.X + margin, layer.Y + margin, layer.X + layer.Width - margin, layer.Y + layer.Height - margin);
        if (x < left || x > right || y < top || y > bottom)
        {
            return false;
        }

        if (coveredBy is { } over && x >= over.X - margin && x <= over.X + over.Width + margin && y >= over.Y - margin && y <= over.Y + over.Height + margin)
        {
            return false;
        }

        // The nearest point of the rectangle that is left once the round corners are cut off it.
        var corner = Math.Max(0, Math.Min(radius, Math.Min(layer.Width, layer.Height) / 2) - margin);
        var nearX = Math.Clamp(x, Math.Min(left + corner, right - corner), Math.Max(left + corner, right - corner));
        var nearY = Math.Clamp(y, Math.Min(top + corner, bottom - corner), Math.Max(top + corner, bottom - corner));
        return ((x - nearX) * (x - nearX)) + ((y - nearY) * (y - nearY)) <= corner * corner;
    };

    /// <summary>
    /// Reads a picture against the layout of one frame: the screen recording where the format
    /// puts it at that frame's instant, showing the part it gives, and the camera likewise.
    /// </summary>
    /// <param name="canvas">Where the canvas is in the picture.</param>
    /// <param name="laidOutAs">
    /// Null to hold the picture against the layout of its own frame. Another frame to ask
    /// whether the picture shows its frame laid out as that one is, which is how a picture that
    /// is wrong is told to be a frame drawn with the layout of an earlier or a later instant.
    /// </param>
    /// <param name="cameraShows">
    /// Null to hold the camera against the frame that goes with the screen's. Another frame of
    /// the camera recording to read the camera as showing that one.
    /// </param>
    private static LayoutReading ReadLayout(Shot shot, Box canvas, TestFolder folder, StudioProject project, int frame, int? laidOutAs = null, int? cameraShows = null)
    {
        // The instant a frame stands for is its middle (section 6.5 of the project format).
        var resolved = StudioLayoutResolver.Resolve(project, MiddleOf(laidOutAs ?? frame), canvas.Width, canvas.Height);
        var cameraFrame = cameraShows ?? CameraFrameAt(folder, project, frame);
        StudioFrameRect? cameraLayer = resolved.Camera is { } placed ? Shift(Aligned(placed.Rect), canvas) : null;

        // A layer that only one of two scenes has fades while the other scene is entered by
        // moving. What shows through it is in its colours then, so it is not read.
        StudioFrameRect? screenLayer = null;
        PartReading? screen = null;
        if (resolved.Screen is { } s)
        {
            // The camera is drawn over the screen recording, which shows only where the camera is not.
            var layer = Shift(Aligned(s.Rect), canvas);
            screenLayer = layer;
            if (s.Opacity >= 0.999)
            {
                screen = ReadPart(shot, folder.Screen, ScreenLandmarks(frame), layer, Part(s.Source), mirror: false, inset: 2, ShownIn(layer, s.CornerRadius, 2, cameraLayer));
            }
        }

        PartReading? camera = null;
        if (resolved.Camera is { Opacity: >= 0.999 } c && cameraLayer is { } cameraAt && folder.Camera is { } clip && cameraFrame != FrameCode.Unreadable)
        {
            var radius = c.Shape == StudioCameraShape.Rectangle ? 0 : c.CornerRadius;
            camera = ReadPart(shot, clip, CameraLandmarks(cameraFrame), cameraAt, Part(c.Source), c.Mirror, inset: c.BorderWidth + 2, ShownIn(cameraAt, radius, c.BorderWidth + 2));
        }

        return new LayoutReading(frame, cameraFrame, resolved, screenLayer, screen, cameraLayer, camera);
    }

    /// <summary>
    /// Null when a picture shows the layout of its frame: each layer the layout has shows the
    /// part the format gives, where the format puts it, at its frame. Otherwise what is wrong.
    /// </summary>
    /// <param name="more">How much further than the tolerance an edge may be: for a picture that has been through an encoder once more.</param>
    private static string? JudgeLayout(LayoutReading reading, double more = 0)
    {
        if (reading.Screen is { } screen && Judge(screen, reading.Frame, more) is { } wrongScreen)
        {
            return $"the screen recording, which should be in {R(reading.ScreenLayer!.Value)}: {wrongScreen}";
        }

        if (reading.Camera is { } camera && Judge(camera, reading.CameraFrame, more) is { } wrongCamera)
        {
            return $"the camera, which should be in {R(reading.CameraLayer!.Value)}: {wrongCamera}";
        }

        return reading.Screen is null && reading.Camera is null ? "the layout has no layer that can be read at this frame" : null;
    }

    private sealed record LayoutSight(Shot Shot, Box Canvas, LayoutReading Reading);

    private LayoutSight? LookAtLayout(Editor editor, int frame, StudioProject? project = null)
    {
        if (editor.Camera.Take() is not { } shot || CanvasBox(editor, shot) is not { } canvas || canvas.Width < 8 || canvas.Height < 8)
        {
            return null;
        }

        return new LayoutSight(shot, canvas, ReadLayout(shot, canvas, editor.Folder, project ?? editor.Expected, frame));
    }

    /// <summary>Looks until the picture shows the layout of a frame, or the time is up. The last look is returned either way.</summary>
    private LayoutSight? LookForLayout(Editor editor, int frame, double seconds = 3, StudioProject? project = null) =>
        Until(() => LookAtLayout(editor, frame, project), sight => sight is not null && JudgeLayout(sight.Reading) is null, seconds, 30);

    /// <summary>
    /// One check that the preview shows a frame as the layout of its scene has it: both layers
    /// where the format puts them at that frame's instant, each showing its frame.
    /// </summary>
    private LayoutSight? ShowsLayout(Editor editor, string name, int frame, StudioProject? project = null)
    {
        var sight = LookForLayout(editor, frame, 3, project);
        var wrong = sight is null ? "no screenshot" : JudgeLayout(sight.Reading);
        if (sight is not null && wrong is null)
        {
            _layoutErrors.Add(sight.Reading.Worst);
        }

        _report.Check(name, wrong is null, wrong is null ? $"frame {frame}: {sight!.Reading}" : $"frame {frame}: {wrong}; read as {sight?.Reading}");
        return wrong is null ? sight : null;
    }

    /// <summary>The frame number a clip's strip reads in a picture where a layer of a layout would have it, or <see cref="FrameCode.Unreadable"/>.</summary>
    private static int StripAt(Shot shot, ClipSpec clip, StudioFrameRect layer, StudioFrameRect source, bool mirror) =>
        FrameCode.Decode(shot.Bgra, shot.Width, shot.Height, clip, ClipMap.ForLayer(clip, layer, source, mirror));
}
