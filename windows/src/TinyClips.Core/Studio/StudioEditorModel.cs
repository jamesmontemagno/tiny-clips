using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace TinyClips.Core.Studio;

/// <summary>
/// The parts of a project the Studio editor changes. Undo, redo, and "changed since export" work on
/// this, so they never touch the project's bookkeeping (exports, dates, name, sources).
/// </summary>
public sealed class StudioEditableState
{
    private static readonly StudioProject ComparisonShell = new();
    private string? _comparisonText;

    private StudioEditableState(StudioProject project)
    {
        Canvas = project.Canvas;
        Screen = project.Screen;
        Camera = project.Camera;
        Scenes = project.Scenes;
        Zooms = project.Zooms;
        Edits = project.Edits;
        Audio = project.Audio;
        Overlays = project.Overlays;
    }

    public StudioCanvas Canvas { get; }

    public StudioScreenStyle Screen { get; }

    public StudioCameraStyle Camera { get; }

    public StudioScene[] Scenes { get; }

    public StudioZoom[] Zooms { get; }

    public StudioEdits Edits { get; }

    public StudioAudio Audio { get; }

    public StudioOverlays Overlays { get; }

    public static StudioEditableState From(StudioProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return new StudioEditableState(project);
    }

    /// <summary>Returns <paramref name="project"/> with its editable parts replaced by these.</summary>
    public StudioProject ApplyTo(StudioProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return project with
        {
            Canvas = Canvas,
            Screen = Screen,
            Camera = Camera,
            Scenes = Scenes,
            Zooms = Zooms,
            Edits = Edits,
            Audio = Audio,
            Overlays = Overlays,
        };
    }

    /// <summary>
    /// True when both describe the same composition. The project records compare their arrays by
    /// reference, so equal content in different arrays needs this instead of <c>==</c>.
    /// </summary>
    public bool ContentEquals(StudioEditableState? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        // Cheap and sufficient when nothing was replaced. It is never true for different content.
        if (Canvas == other.Canvas
            && Screen == other.Screen
            && Camera == other.Camera
            && ReferenceEquals(Scenes, other.Scenes)
            && ReferenceEquals(Zooms, other.Zooms)
            && Edits == other.Edits
            && Audio == other.Audio
            && Overlays == other.Overlays)
        {
            return true;
        }

        return string.Equals(ComparisonText, other.ComparisonText, StringComparison.Ordinal);
    }

    private string ComparisonText => _comparisonText ??= StudioProjectJson.WriteProject(ApplyTo(ComparisonShell));
}

/// <summary>
/// Where the canvas sits inside the preview control, and how to convert between the two. Both
/// spaces have their origin at the top left.
/// </summary>
public readonly record struct StudioEditorCanvasGeometry(StudioSize ViewSize, StudioSize CanvasSize, StudioFrameRect CanvasRectInView)
{
    /// <summary>Canvas pixels per unit of the view.</summary>
    public double CanvasPixelsPerViewUnit => CanvasRectInView.Width > 0 ? CanvasSize.Width / CanvasRectInView.Width : 1;

    /// <summary>The canvas point under a view point. False when the view point is outside the canvas.</summary>
    public bool TryGetCanvasPoint(double viewX, double viewY, out double canvasX, out double canvasY)
    {
        canvasX = 0;
        canvasY = 0;
        if (!(CanvasRectInView.Width > 0) || !(CanvasRectInView.Height > 0))
        {
            return false;
        }

        var x = (viewX - CanvasRectInView.X) / CanvasRectInView.Width * CanvasSize.Width;
        var y = (viewY - CanvasRectInView.Y) / CanvasRectInView.Height * CanvasSize.Height;
        if (!(x >= 0 && x <= CanvasSize.Width && y >= 0 && y <= CanvasSize.Height))
        {
            return false;
        }

        canvasX = x;
        canvasY = y;
        return true;
    }

    public (double X, double Y) GetViewPoint(double canvasX, double canvasY) => (
        CanvasRectInView.X + canvasX / Math.Max(1, CanvasSize.Width) * CanvasRectInView.Width,
        CanvasRectInView.Y + canvasY / Math.Max(1, CanvasSize.Height) * CanvasRectInView.Height);
}

/// <summary>What an edit to a zoom did.</summary>
/// <param name="Changed">Whether the project changed.</param>
/// <param name="Index">
/// Where the zoom is in <see cref="StudioProject.Zooms"/> now, or null when it is gone or there is
/// none. The list is kept in time order, so an edit can move a zoom.
/// </param>
public readonly record struct StudioZoomEditResult(bool Changed, int? Index);

/// <summary>
/// What the focus pad shows for a zoom. The pad stands for the part of the screen that can be
/// zoomed into, which is the screen crop, or the whole screen without one.
/// </summary>
/// <param name="AspectRatio">The pad's width divided by its height.</param>
/// <param name="Window">The part the zoom holds, from 0 to 1 across and down the pad.</param>
/// <param name="FocusX">Where the zoom looks, from 0 at the pad's left edge to 1 at its right.</param>
/// <param name="FocusY">Where the zoom looks, from 0 at the pad's top edge to 1 at its bottom.</param>
public readonly record struct StudioZoomPad(double AspectRatio, StudioFrameRect Window, double FocusX, double FocusY);

/// <summary>
/// What a press on a block of the zoom, cut, or speed lane takes hold of: the whole block, to
/// move it, or one of its ends, to change when it starts or stops.
/// </summary>
public enum StudioLaneBlockPart
{
    Body,
    Start,
    End,
}

/// <summary>An edge of a crop.</summary>
public enum StudioCropEdge
{
    Left,
    Top,
    Right,
    Bottom,
}

/// <summary>
/// A crop as how much is cut off each edge, each a fraction of the frame. This is what the
/// inspector's four crop sliders show and change.
/// </summary>
public readonly record struct StudioCropInsets(double Left, double Top, double Right, double Bottom)
{
    /// <summary>The most two opposite edges can cut off together. A crop keeps 0.05 of the frame each way.</summary>
    public const double MaximumTotal = 0.95;

    public bool IsEmpty => Left == 0 && Top == 0 && Right == 0 && Bottom == 0;

    public double this[StudioCropEdge edge] => edge switch
    {
        StudioCropEdge.Left => Left,
        StudioCropEdge.Top => Top,
        StudioCropEdge.Right => Right,
        _ => Bottom,
    };

    /// <summary>The insets of a crop. A missing crop, or one that is not valid, cuts nothing off.</summary>
    public static StudioCropInsets From(StudioRect? crop) =>
        StudioCanvasMath.ValidCropOrNull(crop) is { } valid
            ? new StudioCropInsets(
                Tidy(valid.X),
                Tidy(valid.Y),
                Tidy(Math.Max(0, 1 - valid.X - valid.Width)),
                Tidy(Math.Max(0, 1 - valid.Y - valid.Height)))
            : default;

    /// <summary>
    /// These insets with one edge moved. The edge stops where it would leave less than 0.05 of the
    /// frame between it and the opposite edge. A value that is not a number changes nothing.
    /// </summary>
    public StudioCropInsets With(StudioCropEdge edge, double value)
    {
        if (!double.IsFinite(value))
        {
            return this;
        }

        return edge switch
        {
            StudioCropEdge.Left => this with { Left = Limit(value, Right) },
            StudioCropEdge.Top => this with { Top = Limit(value, Bottom) },
            StudioCropEdge.Right => this with { Right = Limit(value, Left) },
            _ => this with { Bottom = Limit(value, Top) },
        };
    }

    /// <summary>The crop rectangle, as x, y, width and height. Null when nothing is cut off.</summary>
    public (double X, double Y, double Width, double Height)? ToCrop() =>
        IsEmpty ? null : (Left, Top, Tidy(1 - Left - Right), Tidy(1 - Top - Bottom));

    private static double Limit(double value, double opposite) =>
        Tidy(Math.Max(0, Math.Min(value, MaximumTotal - opposite)));

    // Sums of fractions such as 1 - 0.07 - 0.2 carry binary noise. Six decimals is finer than a
    // pixel of any recording and keeps the stored numbers, and what is read back from them, plain.
    private static double Tidy(double value) => Math.Round(value, 6, MidpointRounding.AwayFromZero);
}

/// <summary>
/// The Studio editor's state and every edit it can make, with undo. It has no UI or media types in
/// it so it can be unit tested. Not thread-safe: use it from one thread.
/// <para>
/// The layout controls change the current scene, which is the one the playhead is in. The owner
/// says where the playhead is with <see cref="SceneTime"/>. The stored scenes are kept as section
/// 6.1 of the project format reads them: in time order, each with a start of its own, the first at 0.
/// </para>
/// </summary>
public sealed partial class StudioEditorModel
{
    public const double MinimumDuration = 0.1;

    /// <summary>The shortest zoom the editor makes, in seconds. It is also the shortest suggestion (section 8).</summary>
    public const double MinimumZoomDuration = 0.3;

    /// <summary>How long a zoom is when it is added, in seconds, where there is room for it.</summary>
    public const double NewZoomDuration = 3;

    public const int MaximumUndoDepth = 100;

    private readonly List<StudioEditableState> _undoStack = [];
    private readonly List<StudioEditableState> _redoStack = [];
    private StudioEditableState? _groupedSnapshot;
    private StudioEditableState? _exportedState;

    public StudioEditorModel(StudioProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Project = project;
        if (Project.Scenes is not { Length: > 0 })
        {
            Project = Project with
            {
                Scenes = [new StudioScene { Start = 0, Layout = HasCamera ? StudioLayout.Bubble : StudioLayout.Screen }],
            };
        }

        Project = Project with
        {
            Scenes = NormalizeStoredScenes(Project.Scenes, SourceDuration),
            Zooms = SortStoredZooms(Project.Zooms),
            Edits = ClampedEdits(
                Project.Edits with
                {
                    Cuts = NormalizeStoredCuts(Project.Edits.Cuts, SourceDuration),
                    Speed = NormalizeStoredSpeed(Project.Edits.Speed, SourceDuration),
                },
                Project.Edits.TrimStart,
                Project.Edits.TrimEnd),
        };
        _exportedState = project.Exports is { Length: > 0 } ? EditableState : null;
    }

    // State

    public StudioProject Project { get; private set; }

    public StudioEditableState EditableState => StudioEditableState.From(Project);

    public bool CanUndo => _undoStack.Count > 0;

    public bool CanRedo => _redoStack.Count > 0;

    public bool IsGroupingEdits => _groupedSnapshot is not null;

    public bool HasCamera => Project.Sources.Camera is not null;

    public bool HasNeverExported => Project.Exports is not { Length: > 0 };

    /// <summary>
    /// True when the composition differs from what was last exported, or nothing was exported yet.
    /// A project reopened after an export counts as unchanged until it is edited.
    /// </summary>
    public bool HasUnexportedChanges => _exportedState is null || !_exportedState.ContentEquals(EditableState);

    /// <summary>The scene the playhead is in.</summary>
    public StudioScene CurrentScene => Project.Scenes[CurrentSceneIndex];

    /// <summary>
    /// The layout the current scene is drawn with once it has been entered: a project without a
    /// camera always shows the screen alone.
    /// </summary>
    public StudioLayout EffectiveLayout => HasCamera ? CurrentScene.Layout : StudioLayout.Screen;

    /// <summary>The styling to save as a default. A look never carries a crop.</summary>
    public StudioLook CurrentLook => new(
        Project.Canvas,
        Project.Screen with { Crop = null },
        Project.Camera with { Crop = null });

    // Time

    public double SourceDuration => Math.Max(0, Project.Sources.Screen.Duration);

    public double FrameDuration
    {
        get
        {
            var frameRate = Project.Sources.Screen.FrameRate;
            return double.IsFinite(frameRate) && frameRate > 0 ? 1 / frameRate : 1.0 / 30;
        }
    }

    public StudioTimeMap TimeMap => new(SourceDuration, Project.Edits);

    public double OutputDuration => TimeMap.OutputDuration;

    public double TrimStart => Clamp(Project.Edits.TrimStart, 0, SourceDuration);

    public double TrimEnd => Clamp(Project.Edits.TrimEnd ?? SourceDuration, TrimStart, SourceDuration);

    public double GetSourceTime(double outputTime) => TimeMap.OutputToSource(outputTime);

    public double GetOutputTime(double sourceTime) => TimeMap.SourceToOutput(sourceTime);

    public double ClampSourceTime(double sourceTime) =>
        double.IsFinite(sourceTime) ? Clamp(sourceTime, 0, SourceDuration) : 0;

    /// <summary>
    /// Where playback starts when Play is pressed at <paramref name="sourceTime"/>: the same spot,
    /// the end of the cut it is in, or the start of the video when the playhead is outside what
    /// the video shows or already at its end.
    /// </summary>
    public double GetPlaybackStart(double sourceTime)
    {
        var time = ClampSourceTime(sourceTime);
        if (time < PlaybackStart || time >= PlaybackEnd - FrameDuration / 2)
        {
            return PlaybackStart;
        }

        return GetCutSkipTarget(time) ?? time;
    }

    /// <summary>Whether a playing preview has reached the end of the video and should stop.</summary>
    public bool IsAtPlaybackEnd(double sourceTime) => sourceTime >= PlaybackEnd - 1e-6;

    /// <summary>Formats seconds as minutes, seconds and tenths, such as <c>1:02.5</c>.</summary>
    public static string FormatTime(double seconds)
    {
        if (!double.IsFinite(seconds))
        {
            return "0:00.0";
        }

        var tenths = (long)Math.Floor(Math.Min(Math.Max(0, seconds), 359_999) * 10);
        return string.Create(CultureInfo.InvariantCulture, $"{tenths / 600}:{tenths / 10 % 60:00}.{tenths % 10}");
    }

    // Undo

    /// <summary>Starts a gesture. Every edit until <see cref="CommitEditingGroup"/> becomes one undo step.</summary>
    public void BeginEditingGroup() => _groupedSnapshot ??= EditableState;

    public void CommitEditingGroup()
    {
        if (_groupedSnapshot is not { } snapshot)
        {
            return;
        }

        _groupedSnapshot = null;
        PushUndo(snapshot);
    }

    public void CancelEditingGroup()
    {
        if (_groupedSnapshot is not { } snapshot)
        {
            return;
        }

        _groupedSnapshot = null;
        Project = snapshot.ApplyTo(Project);
    }

    /// <summary>
    /// Takes back the last step. In the middle of a gesture, what the gesture has done so far is
    /// the step that is taken back, and the gesture goes on: whatever it does next is one step
    /// again, and not a step for every move of the pointer.
    /// </summary>
    public void Undo()
    {
        var wasGrouping = IsGroupingEdits;
        CommitEditingGroup();
        if (_undoStack.Count > 0)
        {
            var previous = _undoStack[^1];
            _undoStack.RemoveAt(_undoStack.Count - 1);
            _redoStack.Add(EditableState);
            Project = previous.ApplyTo(Project);
        }

        if (wasGrouping)
        {
            BeginEditingGroup();
        }
    }

    /// <summary>
    /// Does the step that was last taken back again. A gesture goes on afterwards, as it does
    /// after <see cref="Undo"/>.
    /// </summary>
    public void Redo()
    {
        var wasGrouping = IsGroupingEdits;
        CommitEditingGroup();
        if (_redoStack.Count > 0)
        {
            var next = _redoStack[^1];
            _redoStack.RemoveAt(_redoStack.Count - 1);
            _undoStack.Add(EditableState);
            Project = next.ApplyTo(Project);
        }

        if (wasGrouping)
        {
            BeginEditingGroup();
        }
    }

    // Layout and canvas

    /// <summary>Sets the layout of the current scene. Layouts that need a camera are ignored without one.</summary>
    public void SetLayout(StudioLayout layout)
    {
        // Without a camera the screen is always shown alone, so no choice changes what is drawn.
        if (!HasCamera)
        {
            return;
        }

        MutateScene(scene => scene with { Layout = layout });
    }

    public void SetCanvasAspect(StudioCanvasAspect aspect) =>
        Mutate(project => project with { Canvas = project.Canvas with { Aspect = aspect } });

    public void SetCanvasPadding(double padding)
    {
        if (double.IsFinite(padding))
        {
            Mutate(project => project with { Canvas = project.Canvas with { Padding = Clamp(padding, 0, 0.4) } });
        }
    }

    /// <summary>Colors are <c>#RRGGBB</c>; anything else keeps the color that was there.</summary>
    public void SetBackground(StudioBackgroundStyle style, string? preset, string primary, string? secondary = null, string? image = null)
    {
        var current = Project.Canvas.Background;
        var newPrimary = NormalizeHex(primary) ?? current.Primary;
        var newSecondary = secondary is null ? null : NormalizeHex(secondary) ?? current.Secondary ?? newPrimary;
        var newImage = image is not null && IsPlainFileName(image) ? image : null;
        Mutate(project => project with
        {
            Canvas = project.Canvas with
            {
                Background = project.Canvas.Background with
                {
                    Style = style,
                    Preset = preset,
                    Primary = newPrimary,
                    Secondary = newSecondary,
                    Image = newImage,
                },
            },
        });
    }

    // Screen and camera

    public void SetScreenCornerRadius(double value)
    {
        if (double.IsFinite(value))
        {
            Mutate(project => project with { Screen = project.Screen with { CornerRadius = Clamp(value, 0, 0.2) } });
        }
    }

    public void SetScreenShadow(double value)
    {
        if (double.IsFinite(value))
        {
            Mutate(project => project with { Screen = project.Screen with { Shadow = Clamp(value, 0, 1) } });
        }
    }

    public void SetCameraShape(StudioCameraShape shape) =>
        Mutate(project => project with { Camera = project.Camera with { Shape = shape } });

    public void SetCameraCornerRadius(double value)
    {
        if (double.IsFinite(value))
        {
            Mutate(project => project with { Camera = project.Camera with { CornerRadius = Clamp(value, 0, 0.5) } });
        }
    }

    public void SetCameraMirror(bool isMirrored) =>
        Mutate(project => project with { Camera = project.Camera with { Mirror = isMirrored } });

    /// <summary>
    /// Sets what happens to everything in the camera picture that is not a person: kept, blurred
    /// or taken away.
    /// </summary>
    public void SetCameraCutout(StudioCameraCutout cutout) =>
        Mutate(project => project with { Camera = project.Camera with { Cutout = cutout } });

    public void SetCameraBorderWidth(double value)
    {
        if (double.IsFinite(value))
        {
            Mutate(project => project with { Camera = project.Camera with { BorderWidth = Clamp(value, 0, 0.02) } });
        }
    }

    public void SetCameraBorderColor(string value)
    {
        if (NormalizeHex(value) is { } color)
        {
            Mutate(project => project with { Camera = project.Camera with { BorderColor = color } });
        }
    }

    public void SetCameraShadow(double value)
    {
        if (double.IsFinite(value))
        {
            Mutate(project => project with { Camera = project.Camera with { Shadow = Clamp(value, 0, 1) } });
        }
    }

    /// <summary>
    /// Shows only part of the screen. A rectangle that is not a valid crop (section 3 of the project
    /// format) is made one: its size is brought to between 0.05 and 1 first, and then it is moved
    /// back inside the frame. Null removes the crop. A rectangle with a member that is not a number
    /// is ignored.
    /// </summary>
    public void SetScreenCrop(StudioRect? crop)
    {
        if (crop is null || IsFinite(crop))
        {
            Mutate(project => project with { Screen = project.Screen with { Crop = crop is null ? null : ClampCrop(crop) } });
        }
    }

    public void ClearScreenCrop() => SetScreenCrop(null);

    /// <summary>Shows only part of the camera picture. The same rules as <see cref="SetScreenCrop"/>.</summary>
    public void SetCameraCrop(StudioRect? crop)
    {
        if (crop is null || IsFinite(crop))
        {
            Mutate(project => project with { Camera = project.Camera with { Crop = crop is null ? null : ClampCrop(crop) } });
        }
    }

    public void ClearCameraCrop() => SetCameraCrop(null);

    /// <summary>How much the screen crop cuts off each edge.</summary>
    public StudioCropInsets ScreenCropInsets => StudioCropInsets.From(Project.Screen.Crop);

    /// <summary>How much the camera crop cuts off each edge.</summary>
    public StudioCropInsets CameraCropInsets => StudioCropInsets.From(Project.Camera.Crop);

    /// <summary>
    /// Moves one edge of the screen crop to cut off <paramref name="value"/> of the frame. With
    /// nothing cut off any edge, the crop is removed. An edge that is put where it is leaves the
    /// crop as it is stored: a window hands a slider's value back when it has only shown it,
    /// and a crop that was not written by this editor would be rewritten by that.
    /// </summary>
    public void SetScreenCropInset(StudioCropEdge edge, double value)
    {
        var insets = ScreenCropInsets;
        var moved = insets.With(edge, value);
        if (moved != insets)
        {
            SetScreenCrop(WithInsets(Project.Screen.Crop, moved));
        }
    }

    /// <summary>Moves one edge of the camera crop. The same rules as <see cref="SetScreenCropInset"/>.</summary>
    public void SetCameraCropInset(StudioCropEdge edge, double value)
    {
        var insets = CameraCropInsets;
        var moved = insets.With(edge, value);
        if (moved != insets)
        {
            SetCameraCrop(WithInsets(Project.Camera.Crop, moved));
        }
    }

    public void SetCameraBubbleSize(double value)
    {
        if (double.IsFinite(value))
        {
            MutateScene(scene => scene with { Bubble = scene.Bubble with { Size = Clamp(value, 0.08, 0.6) } });
        }
    }

    /// <summary>Snaps the bubble to a corner, clearing any offset from dragging.</summary>
    public void SetCameraAnchor(StudioAnchor anchor) =>
        MutateScene(scene => scene with { Bubble = scene.Bubble with { Anchor = anchor, OffsetX = 0, OffsetY = 0 } });

    public void SetCameraBubbleOffsets(double x, double y) =>
        MutateScene(scene => scene with
        {
            Bubble = scene.Bubble with
            {
                OffsetX = double.IsFinite(x) ? Clamp(x, -1, 1) : 0,
                OffsetY = double.IsFinite(y) ? Clamp(y, -1, 1) : 0,
            },
        });

    public void SetSideBySide(StudioCameraSide cameraSide, double fraction)
    {
        if (double.IsFinite(fraction))
        {
            MutateScene(scene => scene with
            {
                Split = scene.Split with { CameraSide = cameraSide, CameraFraction = Clamp(fraction, 0.15, 0.6) },
            });
        }
    }

    // Bubble dragging

    /// <summary>
    /// Moves the camera bubble so its top-left corner is at the given point in canvas pixels, kept
    /// on the canvas. This inverts section 6.3 of the format spec: the bubble is anchored to the
    /// corner nearest its center and the rest becomes the offset.
    /// </summary>
    public void MoveBubbleTopLeft(double x, double y, double canvasWidth, double canvasHeight)
    {
        if (!HasCamera || !(canvasWidth > 0) || !(canvasHeight > 0) || !double.IsFinite(x) || !double.IsFinite(y))
        {
            return;
        }

        var sceneIndex = CurrentSceneIndex;
        if (GetBubbleRect(Project, sceneIndex, canvasWidth, canvasHeight) is not { } size)
        {
            return;
        }

        var left = Clamp(x, 0, Math.Max(0, canvasWidth - size.Width));
        var top = Clamp(y, 0, Math.Max(0, canvasHeight - size.Height));
        var isLeft = left + size.Width / 2 < canvasWidth / 2;
        var isTop = top + size.Height / 2 < canvasHeight / 2;
        var anchor = isTop
            ? (isLeft ? StudioAnchor.TopLeft : StudioAnchor.TopRight)
            : (isLeft ? StudioAnchor.BottomLeft : StudioAnchor.BottomRight);

        var anchored = WithScene(Project, sceneIndex, scene => scene with
        {
            Bubble = scene.Bubble with { Anchor = anchor, OffsetX = 0, OffsetY = 0 },
        });
        if (GetBubbleRect(anchored, sceneIndex, canvasWidth, canvasHeight) is not { } origin)
        {
            return;
        }

        MutateScene(scene => scene with
        {
            Bubble = scene.Bubble with
            {
                Anchor = anchor,
                OffsetX = (left - origin.X) / canvasWidth,
                OffsetY = (top - origin.Y) / canvasHeight,
            },
        });
    }

    public void MoveBubbleCenter(double x, double y, double canvasWidth, double canvasHeight)
    {
        if (HasCamera && canvasWidth > 0 && canvasHeight > 0 && GetBubbleRect(Project, CurrentSceneIndex, canvasWidth, canvasHeight) is { } size)
        {
            MoveBubbleTopLeft(x - size.Width / 2, y - size.Height / 2, canvasWidth, canvasHeight);
        }
    }

    /// <summary>
    /// The rectangle the current scene's bubble has at rest on a canvas of the given size,
    /// whatever its layout is.
    /// </summary>
    public StudioFrameRect? GetBubbleRect(double canvasWidth, double canvasHeight) =>
        canvasWidth > 0 && canvasHeight > 0 ? GetBubbleRect(Project, CurrentSceneIndex, canvasWidth, canvasHeight) : null;

    // Zooms
    //
    // The stored list is kept in time order and is never null, so a zoom's index identifies it
    // between two edits. Every edit that can move or remove a zoom says where it is afterwards.

    /// <summary>
    /// The zoom that contains <paramref name="sourceTime"/>, or null. A zoom contains its start
    /// and not its end, as in the layout.
    /// </summary>
    public int? GetZoomIndexAt(double sourceTime)
    {
        var zooms = Project.Zooms;
        for (var i = 0; i < zooms.Length; i++)
        {
            if (zooms[i].Start <= sourceTime && sourceTime < zooms[i].End)
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>
    /// Adds a zoom that starts at <paramref name="sourceTime"/> and lasts
    /// <see cref="NewZoomDuration"/>, or until the next zoom or the end of the recording when that
    /// comes sooner. It looks at where the pointer is at that time, or at the center when the
    /// recording has no cursor samples.
    /// </summary>
    /// <returns>
    /// The new zoom's index. Unchanged with the index of the zoom that is already there, and
    /// unchanged with no index when there is no room for <see cref="MinimumZoomDuration"/>.
    /// </returns>
    public StudioZoomEditResult AddZoom(double sourceTime, StudioEvents? events)
    {
        if (!double.IsFinite(sourceTime))
        {
            return new StudioZoomEditResult(false, null);
        }

        var start = Clamp(sourceTime, 0, SourceDuration);
        if (GetZoomIndexAt(start) is { } existing)
        {
            return new StudioZoomEditResult(false, existing);
        }

        if (!TryGetNewZoomPlace(start, out var index, out var end))
        {
            return new StudioZoomEditResult(false, null);
        }

        // The same smoothing as a zoom that follows the pointer, taken once and kept as a point.
        var cursor = StudioPreparedCursorSamples.For(events);
        var (focusX, focusY) = cursor.Count > 0 ? cursor.MeanInCenteredSecond(start) : (0.5, 0.5);
        var added = new StudioZoom
        {
            Start = start,
            End = end,
            Focus = new StudioZoomFocus { X = Clamp(focusX, 0, 1), Y = Clamp(focusY, 0, 1) },
        };
        var zooms = Project.Zooms;
        StudioZoom[] updated = [.. zooms[..index], added, .. zooms[index..]];
        Mutate(project => project with { Zooms = updated });
        return new StudioZoomEditResult(true, index);
    }

    /// <summary>
    /// Whether <see cref="AddZoom"/> at this time has a zoom to answer with: a new one, or the one
    /// that is already there. False where less than <see cref="MinimumZoomDuration"/> fits.
    /// </summary>
    public bool CanAddZoom(double sourceTime)
    {
        if (!double.IsFinite(sourceTime))
        {
            return false;
        }

        var start = Clamp(sourceTime, 0, SourceDuration);
        return GetZoomIndexAt(start) is not null || TryGetNewZoomPlace(start, out _, out _);
    }

    public StudioZoomEditResult RemoveZoom(int index)
    {
        if (!TryGetZoom(index, out _))
        {
            return new StudioZoomEditResult(false, null);
        }

        var zooms = Project.Zooms;
        StudioZoom[] updated = [.. zooms[..index], .. zooms[(index + 1)..]];
        Mutate(project => project with { Zooms = updated });
        return new StudioZoomEditResult(true, null);
    }

    /// <summary>
    /// Moves a zoom's start. It stays at or after the end of the zoom before it, and at least
    /// <see cref="MinimumZoomDuration"/> before its own end. Moved up against the zoom before it,
    /// it takes exactly that zoom's end, which is what chains the two (section 6.8).
    /// </summary>
    public StudioZoomEditResult SetZoomStart(int index, double sourceTime)
    {
        if (!TryGetZoom(index, out var zoom))
        {
            return new StudioZoomEditResult(false, null);
        }

        if (!double.IsFinite(sourceTime))
        {
            return new StudioZoomEditResult(false, index);
        }

        // Zooms never overlap, so where both limits cannot be kept the zoom before decides.
        var earliest = index > 0 ? Project.Zooms[index - 1].End : 0;
        var latest = zoom.End - MinimumZoomDuration;
        return ReplaceZoom(index, zoom with { Start = Math.Max(earliest, Math.Min(sourceTime, latest)) });
    }

    /// <summary>
    /// Moves a zoom's end. It stays at least <see cref="MinimumZoomDuration"/> after its own start,
    /// and at or before the start of the next zoom and the end of the recording. Moved up against
    /// the next zoom, it takes exactly that zoom's start.
    /// </summary>
    public StudioZoomEditResult SetZoomEnd(int index, double sourceTime)
    {
        if (!TryGetZoom(index, out var zoom))
        {
            return new StudioZoomEditResult(false, null);
        }

        if (!double.IsFinite(sourceTime))
        {
            return new StudioZoomEditResult(false, index);
        }

        var zooms = Project.Zooms;
        var earliest = zoom.Start + MinimumZoomDuration;
        var latest = index + 1 < zooms.Length ? zooms[index + 1].Start : SourceDuration;
        return ReplaceZoom(index, zoom with { End = Math.Min(Math.Max(sourceTime, earliest), latest) });
    }

    public StudioZoomEditResult SetZoomScale(int index, double value) =>
        EditZoom(index, double.IsFinite(value), zoom => zoom with { Scale = Clamp(value, 1, 5) });

    public StudioZoomEditResult SetZoomFocusMode(int index, StudioZoomFocusMode mode) =>
        EditZoom(index, true, zoom => zoom with { Focus = zoom.Focus with { Mode = mode } });

    /// <summary>Where a zoom looks, as a point in the screen frame from 0 to 1.</summary>
    public StudioZoomEditResult SetZoomFocusPoint(int index, double x, double y) =>
        EditZoom(index, double.IsFinite(x) && double.IsFinite(y), zoom => zoom with
        {
            Focus = zoom.Focus with { X = Clamp(x, 0, 1), Y = Clamp(y, 0, 1) },
        });

    public StudioZoomEditResult SetZoomEaseIn(int index, double seconds) =>
        EditZoom(index, double.IsFinite(seconds), zoom => zoom with { EaseIn = Clamp(seconds, 0, 3) });

    public StudioZoomEditResult SetZoomEaseOut(int index, double seconds) =>
        EditZoom(index, double.IsFinite(seconds), zoom => zoom with { EaseOut = Clamp(seconds, 0, 3) });

    /// <summary>
    /// Moves a whole zoom so it starts at <paramref name="sourceTime"/>, keeping its length. It
    /// stays between the zoom before it and the zoom after it, or the ends of the recording. Moved
    /// up against a neighbour it takes exactly the neighbour's number, which chains the two.
    /// </summary>
    public StudioZoomEditResult MoveZoom(int index, double sourceTime)
    {
        if (!TryGetZoom(index, out var zoom))
        {
            return new StudioZoomEditResult(false, null);
        }

        if (!double.IsFinite(sourceTime))
        {
            return new StudioZoomEditResult(false, index);
        }

        var zooms = Project.Zooms;
        var length = zoom.End - zoom.Start;
        var earliest = index > 0 ? zooms[index - 1].End : 0;
        var latestEnd = index + 1 < zooms.Length ? zooms[index + 1].Start : SourceDuration;

        double start;
        double end;
        if (sourceTime <= earliest)
        {
            start = earliest;
            end = Math.Min(earliest + length, latestEnd);
        }
        else if (sourceTime + length >= latestEnd)
        {
            end = latestEnd;
            start = Math.Max(earliest, latestEnd - length);
        }
        else
        {
            start = sourceTime;
            end = sourceTime + length;
        }

        // Only a file written elsewhere can leave no room between two neighbours.
        return end > start
            ? ReplaceZoom(index, zoom with { Start = start, End = end })
            : new StudioZoomEditResult(false, index);
    }

    /// <summary>
    /// The zoom after the selected one, for stepping through the zooms. With nothing selected, the
    /// zoom at <paramref name="sourceTime"/> or the first one after it. Null when there is none.
    /// </summary>
    public int? GetZoomIndexAfter(int? selectedIndex, double sourceTime)
    {
        var zooms = Project.Zooms;
        if (selectedIndex is { } selected && selected >= 0 && selected < zooms.Length)
        {
            return selected + 1 < zooms.Length ? selected + 1 : null;
        }

        var index = Array.FindIndex(zooms, zoom => zoom.End > sourceTime);
        return index >= 0 ? index : null;
    }

    /// <summary>
    /// The zoom before the selected one. With nothing selected, the zoom at
    /// <paramref name="sourceTime"/> or the last one before it. Null when there is none.
    /// </summary>
    public int? GetZoomIndexBefore(int? selectedIndex, double sourceTime)
    {
        var zooms = Project.Zooms;
        if (selectedIndex is { } selected && selected >= 0 && selected < zooms.Length)
        {
            return selected > 0 ? selected - 1 : null;
        }

        var index = Array.FindLastIndex(zooms, zoom => zoom.Start <= sourceTime);
        return index >= 0 ? index : null;
    }

    /// <summary>
    /// Where the zoom at <paramref name="index"/> of <paramref name="before"/> is in
    /// <paramref name="after"/>, for an edit that did not say, such as undo. When the two lists
    /// differ in that one zoom at most, it is that zoom, changed: the same place. Otherwise it is
    /// the zoom that shares the most time with it, and among equals the one nearest to where it
    /// was. Null when there was no such zoom, or none shares any time with it.
    /// </summary>
    public static int? FindZoomFollowing(IReadOnlyList<StudioZoom> before, int index, IReadOnlyList<StudioZoom> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (index < 0 || index >= before.Count)
        {
            return null;
        }

        if (before.Count == after.Count)
        {
            var othersAreTheSame = true;
            for (var i = 0; i < before.Count && othersAreTheSame; i++)
            {
                othersAreTheSame = i == index || before[i] == after[i];
            }

            if (othersAreTheSame)
            {
                return index;
            }
        }

        var zoom = before[index];
        int? best = null;
        var bestShared = 0.0;
        for (var i = 0; i < after.Count; i++)
        {
            var shared = Math.Min(zoom.End, after[i].End) - Math.Max(zoom.Start, after[i].Start);
            if (!(shared > 0))
            {
                continue;
            }

            if (best is not { } current
                || shared > bestShared
                || (shared == bestShared && Math.Abs(i - index) < Math.Abs(current - index)))
            {
                best = i;
                bestShared = shared;
            }
        }

        return best;
    }

    /// <summary>
    /// A time at which a zoom has moved all the way in, to show it at: the end of its ease in. Kept
    /// a frame inside the zoom, which contains its start and not its end.
    /// </summary>
    public double? GetZoomLookTime(int index)
    {
        if (!TryGetZoom(index, out var zoom))
        {
            return null;
        }

        // The eases as the layout applies them (section 6.8 of the project format).
        var zooms = Project.Zooms;
        var nextIsChained = index + 1 < zooms.Length && zooms[index + 1].Start == zoom.End;
        var easeIn = Clamp(zoom.EaseIn, 0, 3);
        var easeOut = nextIsChained ? 0 : Clamp(zoom.EaseOut, 0, 3);
        var length = zoom.End - zoom.Start;
        if (easeIn + easeOut > length && easeIn + easeOut > 0)
        {
            easeIn *= length / (easeIn + easeOut);
        }

        var time = Math.Max(zoom.Start, Math.Min(zoom.Start + easeIn, zoom.End - FrameDuration));
        return ClampSourceTime(time);
    }

    /// <summary>What the focus pad shows for a zoom, with the point the zoom stores. Null when there is no such zoom.</summary>
    public StudioZoomPad? GetZoomPad(int index)
    {
        if (!TryGetZoom(index, out var zoom))
        {
            return null;
        }

        var area = ZoomArea;
        var focusX = Clamp(zoom.Focus.X, 0, 1);
        var focusY = Clamp(zoom.Focus.Y, 0, 1);
        var held = StudioZoomMath.HeldWindow(area, zoom.Scale, focusX, focusY);
        var screen = Project.Sources.Screen;
        var aspect = screen.Width > 0 && screen.Height > 0
            ? (screen.Width * area.Width) / (screen.Height * area.Height)
            : 16.0 / 9;
        return new StudioZoomPad(
            aspect,
            new StudioFrameRect(
                (held.X - area.X) / area.Width,
                (held.Y - area.Y) / area.Height,
                held.Width / area.Width,
                held.Height / area.Height),
            Clamp((focusX - area.X) / area.Width, 0, 1),
            Clamp((focusY - area.Y) / area.Height, 0, 1));
    }

    /// <summary>
    /// Points a zoom at a place on its focus pad, from 0 to 1 across and down the pad.
    /// </summary>
    public StudioZoomEditResult SetZoomFocusOnPad(int index, double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            return new StudioZoomEditResult(false, TryGetZoom(index, out _) ? index : null);
        }

        var area = ZoomArea;
        return SetZoomFocusPoint(
            index,
            area.X + (Clamp(x, 0, 1) * area.Width),
            area.Y + (Clamp(y, 0, 1) * area.Height));
    }

    /// <summary>How many of the zooms are suggestions that have not been changed since they were made.</summary>
    public int SuggestedZoomCount => Project.Zooms.Count(static zoom => zoom.Origin == StudioZoomOrigin.Auto);

    /// <summary>
    /// Replaces the suggested zooms (<see cref="StudioZoomOrigin.Auto"/>) with
    /// <paramref name="suggestions"/> and leaves every other zoom as it is. One undo step.
    /// </summary>
    /// <returns>Whether the project changed.</returns>
    public bool ApplyZoomSuggestions(IReadOnlyList<StudioZoom> suggestions)
    {
        ArgumentNullException.ThrowIfNull(suggestions);
        var kept = Project.Zooms.Where(static zoom => zoom.Origin != StudioZoomOrigin.Auto);
        var suggested = suggestions
            .Where(static zoom => zoom is not null)
            .Select(static zoom => zoom with { Origin = StudioZoomOrigin.Auto });
        var updated = SortStoredZooms(kept.Concat(suggested));
        var before = Project;
        Mutate(project => project with { Zooms = updated });
        return !ReferenceEquals(before, Project);
    }

    // Trim, audio, overlays

    /// <summary>
    /// Sets the trim in source time. The kept range stays inside the recording, in order, and at
    /// least <see cref="MinimumDuration"/> long (or the whole recording when it is shorter). A
    /// trim that would leave less video than that, between the cuts and at the speed of what is
    /// left, is not made.
    /// </summary>
    public void SetTrim(double start, double? end)
    {
        var edits = ClampedEdits(Project.Edits, start, end);
        if ((edits.Cuts.Length > 0 || edits.Speed.Length > 0) && !LeavesEnoughVideo(edits))
        {
            return;
        }

        Mutate(project => project with { Edits = edits });
    }

    /// <summary>Moves the trim start, leaving the end where it is.</summary>
    public void SetTrimStart(double value)
    {
        var latest = Math.Max(0, TrimEnd - Math.Min(MinimumDuration, SourceDuration));
        SetTrim(double.IsFinite(value) ? Math.Min(value, latest) : 0, Project.Edits.TrimEnd);
    }

    /// <summary>Moves the trim end, leaving the start where it is.</summary>
    public void SetTrimEnd(double value)
    {
        var start = TrimStart;
        var earliest = Math.Min(SourceDuration, start + Math.Min(MinimumDuration, SourceDuration));
        SetTrim(start, double.IsFinite(value) ? Math.Max(value, earliest) : SourceDuration);
    }

    public void SetMuted(bool isMuted) =>
        Mutate(project => project with { Audio = project.Audio with { Muted = isMuted } });

    /// <summary>
    /// How loud the video's sound is, from 0 (silent) to 1 (as recorded). Mute is left as it is,
    /// and a muted video stays silent. A value that is not a number changes nothing.
    /// </summary>
    public void SetVolume(double volume)
    {
        if (double.IsFinite(volume))
        {
            Mutate(project => project with { Audio = project.Audio with { Volume = StudioSound.Volume(volume) } });
        }
    }

    public void SetClickRingsEnabled(bool isEnabled) =>
        Mutate(project => project with
        {
            Overlays = project.Overlays with { Clicks = project.Overlays.Clicks with { Enabled = isEnabled } },
        });

    public void SetBrandingEnabled(bool isEnabled) =>
        Mutate(project => project with { Overlays = project.Overlays with { Branding = isEnabled } });

    // Looks

    /// <summary>Applies a saved look. Crops belong to one recording, so the project's own crops stay.</summary>
    public void ApplyLook(StudioLook look)
    {
        ArgumentNullException.ThrowIfNull(look);
        Mutate(project => project with
        {
            Canvas = look.Canvas ?? new StudioCanvas(),
            Screen = (look.Screen ?? new StudioScreenStyle()) with { Crop = project.Screen.Crop },
            Camera = (look.Camera ?? new StudioCameraStyle()) with { Crop = project.Camera.Crop },
        });
    }

    // Bookkeeping

    /// <summary>Records that <paramref name="exported"/> (the state that was rendered) is now saved as a video.</summary>
    public void MarkExported(StudioEditableState exported)
    {
        ArgumentNullException.ThrowIfNull(exported);
        _exportedState = exported;
    }

    /// <summary>Takes the fields the store or other windows own from the saved project, leaving edits alone.</summary>
    public void RefreshBookkeeping(StudioProject saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        Project = Project with
        {
            Exports = saved.Exports,
            ModifiedAt = saved.ModifiedAt,
            LastOpenedAt = saved.LastOpenedAt,
            KeepSources = saved.KeepSources,
            Name = saved.Name,
        };
    }

    // Geometry

    /// <summary>How wide the handle at each end of a lane block is.</summary>
    public const double LaneHandleWidth = 8;

    /// <summary>
    /// The narrowest block with room for a handle inside each end and something to move it by
    /// between them.
    /// </summary>
    public const double LaneHandleMinimumBlockWidth = 28;

    /// <summary>Whether a block has its handles inside its ends.</summary>
    public static bool LaneBlockHasInsideHandles(double blockWidth) =>
        double.IsFinite(blockWidth) && blockWidth >= LaneHandleMinimumBlockWidth;

    /// <summary>
    /// How far a block's handles stand out past each of its ends. A block with room has them
    /// inside. A narrower one gets them outside while it is selected, so a cut of one second in
    /// a long recording can still be made longer by dragging.
    /// </summary>
    public static double GetLaneHandleOutset(double blockWidth, bool isSelected) =>
        isSelected && !LaneBlockHasInsideHandles(blockWidth) ? LaneHandleWidth : 0;

    /// <summary>
    /// What a press at <paramref name="x"/> takes hold of, with x measured from the block's left
    /// edge: below 0 and past <paramref name="blockWidth"/> are the handles that stand outside a
    /// narrow selected block.
    /// </summary>
    public static StudioLaneBlockPart GetLaneBlockPart(double x, double blockWidth, bool isSelected)
    {
        if (LaneBlockHasInsideHandles(blockWidth))
        {
            if (x <= LaneHandleWidth)
            {
                return StudioLaneBlockPart.Start;
            }

            return x >= blockWidth - LaneHandleWidth ? StudioLaneBlockPart.End : StudioLaneBlockPart.Body;
        }

        if (!(GetLaneHandleOutset(blockWidth, isSelected) > 0))
        {
            return StudioLaneBlockPart.Body;
        }

        if (x < 0)
        {
            return StudioLaneBlockPart.Start;
        }

        return x > blockWidth ? StudioLaneBlockPart.End : StudioLaneBlockPart.Body;
    }

    /// <summary>The canvas aspect-fitted and centered in a view.</summary>
    public static StudioEditorCanvasGeometry GetCanvasGeometry(double viewWidth, double viewHeight, double canvasWidth, double canvasHeight)
    {
        var view = new StudioSize(viewWidth, viewHeight);
        var canvas = new StudioSize(canvasWidth, canvasHeight);
        if (!(viewWidth > 0) || !(viewHeight > 0) || !(canvasWidth > 0) || !(canvasHeight > 0))
        {
            return new StudioEditorCanvasGeometry(view, canvas, default);
        }

        var scale = Math.Min(viewWidth / canvasWidth, viewHeight / canvasHeight);
        var width = canvasWidth * scale;
        var height = canvasHeight * scale;
        return new StudioEditorCanvasGeometry(
            view,
            canvas,
            new StudioFrameRect((viewWidth - width) / 2, (viewHeight - height) / 2, width, height));
    }

    // Text for screen readers

    public static string GetSecondsText(double seconds) =>
        string.Create(CultureInfo.InvariantCulture, $"{(double.IsFinite(seconds) ? seconds : 0):0.0} seconds");

    public static string GetLayoutName(StudioLayout layout) => layout switch
    {
        StudioLayout.Screen => "Screen only",
        StudioLayout.Bubble => "Screen with camera bubble",
        StudioLayout.SideBySide => "Side by side",
        _ => "Camera only",
    };

    public static string GetAnchorName(StudioAnchor anchor) => anchor switch
    {
        StudioAnchor.TopLeft => "Top left",
        StudioAnchor.TopRight => "Top right",
        StudioAnchor.BottomLeft => "Bottom left",
        _ => "Bottom right",
    };

    public static string GetShapeName(StudioCameraShape shape) => shape switch
    {
        StudioCameraShape.Circle => "Circle",
        StudioCameraShape.RoundedRectangle => "Rounded rectangle",
        StudioCameraShape.Squircle => "Squircle",
        _ => "Rectangle",
    };

    /// <summary>What a choice for the camera's background is called.</summary>
    public static string GetCutoutName(StudioCameraCutout cutout) => cutout switch
    {
        StudioCameraCutout.Blur => "Blur",
        StudioCameraCutout.Remove => "Remove",
        _ => "Keep",
    };

    public static string GetAspectName(StudioCanvasAspect aspect) => aspect switch
    {
        StudioCanvasAspect.Square => "1:1",
        StudioCanvasAspect.Landscape4X3 => "4:3",
        StudioCanvasAspect.Landscape16X9 => "16:9",
        StudioCanvasAspect.Portrait3X4 => "3:4",
        StudioCanvasAspect.Portrait9X16 => "9:16",
        _ => "Auto",
    };

    /// <summary>For example "0:02.5 of 0:10.0".</summary>
    public string GetPlayheadText(double sourceTime) =>
        $"{FormatTime(GetOutputTime(sourceTime))} of {FormatTime(OutputDuration)}";

    // Private

    private void Mutate(Func<StudioProject, StudioProject> change)
    {
        var before = EditableState;
        var candidate = change(Project);
        if (before.ContentEquals(StudioEditableState.From(candidate)))
        {
            return;
        }

        Project = candidate;
        if (_groupedSnapshot is null)
        {
            PushUndo(before);
        }
    }

    private void MutateScene(Func<StudioScene, StudioScene> change)
    {
        var index = CurrentSceneIndex;
        Mutate(project => WithScene(project, index, change));
    }

    private StudioZoomEditResult EditZoom(int index, bool isValid, Func<StudioZoom, StudioZoom> change)
    {
        if (!TryGetZoom(index, out var zoom))
        {
            return new StudioZoomEditResult(false, null);
        }

        return isValid ? ReplaceZoom(index, change(zoom)) : new StudioZoomEditResult(false, index);
    }

    /// <summary>
    /// Puts <paramref name="edited"/> in the place of the zoom at <paramref name="index"/>. An edit
    /// that changes a suggested zoom makes it the user's own; one that changes nothing leaves it
    /// a suggestion.
    /// </summary>
    private StudioZoomEditResult ReplaceZoom(int index, StudioZoom edited)
    {
        var zooms = Project.Zooms;
        if (zooms[index] == edited)
        {
            return new StudioZoomEditResult(false, index);
        }

        var replacement = edited with { Origin = StudioZoomOrigin.Manual };
        var updated = (StudioZoom[])zooms.Clone();
        updated[index] = replacement;
        var before = Project;
        Mutate(project => project with { Zooms = SortStoredZooms(updated) });
        if (ReferenceEquals(before, Project))
        {
            return new StudioZoomEditResult(false, index);
        }

        var moved = Array.FindIndex(Project.Zooms, zoom => ReferenceEquals(zoom, replacement));
        return new StudioZoomEditResult(true, moved >= 0 ? moved : index);
    }

    private bool TryGetZoom(int index, [NotNullWhen(true)] out StudioZoom? zoom)
    {
        var zooms = Project.Zooms;
        zoom = index >= 0 && index < zooms.Length ? zooms[index] : null;
        return zoom is not null;
    }

    // The part of the screen a zoom works inside: the valid crop, or the whole frame (section 6.8).
    private StudioFrameRect ZoomArea => StudioCanvasMath.ValidCropOrNull(Project.Screen.Crop) is { } crop
        ? new StudioFrameRect(crop.X, crop.Y, crop.Width, crop.Height)
        : new StudioFrameRect(0, 0, 1, 1);

    // Where a zoom starting at a time that no zoom contains goes in the list, and where it ends:
    // after NewZoomDuration, or at the next zoom or the end of the recording when that comes
    // sooner. False when that leaves less than the shortest zoom.
    private bool TryGetNewZoomPlace(double start, out int index, out double end)
    {
        var zooms = Project.Zooms;
        index = Array.FindIndex(zooms, zoom => zoom.Start > start);
        if (index < 0)
        {
            index = zooms.Length;
        }

        var nextStart = index < zooms.Length ? zooms[index].Start : SourceDuration;
        end = Math.Min(start + NewZoomDuration, Math.Min(nextStart, SourceDuration));
        return end - start >= MinimumZoomDuration;
    }

    private static StudioProject WithScene(StudioProject project, int index, Func<StudioScene, StudioScene> change)
    {
        var scenes = (StudioScene[])project.Scenes.Clone();
        scenes[index] = change(scenes[index]);
        return project with { Scenes = scenes };
    }

    // In time order, and for equal starts in the order they were in: OrderBy is a stable sort.
    private static StudioZoom[] SortStoredZooms(IEnumerable<StudioZoom?>? zooms) =>
        (zooms ?? [])
            .OfType<StudioZoom>()
            .OrderBy(static zoom => zoom.Start)
            .ToArray();

    private static bool IsFinite(StudioRect rect) =>
        double.IsFinite(rect.X) && double.IsFinite(rect.Y) && double.IsFinite(rect.Width) && double.IsFinite(rect.Height);

    // The size first, then the position: a rectangle dragged past an edge stops there with its size.
    private static StudioRect ClampCrop(StudioRect crop)
    {
        var width = Clamp(crop.Width, 0.05, 1);
        var height = Clamp(crop.Height, 0.05, 1);
        return crop with
        {
            X = Clamp(crop.X, 0, 1 - width),
            Y = Clamp(crop.Y, 0, 1 - height),
            Width = width,
            Height = height,
        };
    }

    // The crop for a set of insets. It is made from the crop that is there, so members of it that
    // this version does not know are kept.
    private static StudioRect? WithInsets(StudioRect? current, StudioCropInsets insets) =>
        insets.ToCrop() is { } crop
            ? (current ?? new StudioRect()) with { X = crop.X, Y = crop.Y, Width = crop.Width, Height = crop.Height }
            : null;

    private void PushUndo(StudioEditableState snapshot)
    {
        if (snapshot.ContentEquals(EditableState))
        {
            return;
        }

        _undoStack.Add(snapshot);
        if (_undoStack.Count > MaximumUndoDepth)
        {
            _undoStack.RemoveRange(0, _undoStack.Count - MaximumUndoDepth);
        }

        _redoStack.Clear();
    }

    private StudioEdits ClampedEdits(StudioEdits edits, double trimStart, double? trimEnd)
    {
        var duration = SourceDuration;
        if (!(duration > 0))
        {
            return edits with { TrimStart = 0, TrimEnd = null };
        }

        var minimum = Math.Min(MinimumDuration, duration);
        var startInput = double.IsFinite(trimStart) ? trimStart : 0;
        var endInput = trimEnd is { } requested && double.IsFinite(requested) ? requested : duration;
        var start = Clamp(startInput, 0, duration - minimum);
        var end = Clamp(endInput, minimum, duration);
        if (end - start < minimum)
        {
            if (start + minimum <= duration)
            {
                end = start + minimum;
            }
            else
            {
                start = Math.Max(0, end - minimum);
            }
        }

        return edits with { TrimStart = start, TrimEnd = Math.Abs(end - duration) < 1e-9 ? null : end };
    }

    /// <summary>
    /// The bubble rectangle the layout resolver produces for one scene of <paramref name="project"/>
    /// at rest, forcing the bubble layout so the answer does not depend on the layout currently shown.
    /// </summary>
    private static StudioFrameRect? GetBubbleRect(StudioProject project, int sceneIndex, double width, double height)
    {
        if (project.Scenes is not { Length: > 0 } scenes || sceneIndex < 0 || sceneIndex >= scenes.Length)
        {
            return null;
        }

        var scene = scenes[sceneIndex] with { Start = 0, Layout = StudioLayout.Bubble };
        return StudioLayoutResolver.Resolve(project with { Scenes = [scene] }, 0, width, height).Camera?.Rect;
    }

    private static string? NormalizeHex(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToUpperInvariant();
        if (text.StartsWith('#'))
        {
            text = text[1..];
        }

        if (text.Length is not (6 or 8) || !text.All(Uri.IsHexDigit))
        {
            return null;
        }

        return "#" + text[..6];
    }

    private static bool IsPlainFileName(string value) =>
        value.Length > 0 && value != "." && value != ".." && !value.Contains('/') && !value.Contains('\\');

    private static double Clamp(double value, double min, double max) => Math.Min(Math.Max(value, min), max);
}
