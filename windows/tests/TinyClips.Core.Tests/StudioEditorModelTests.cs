using TinyClips.Core.Studio;

namespace TinyClips.Core.Tests;

public sealed class StudioEditorModelTests
{
    private const int Precision = 9;

    // Undo

    [Fact]
    public void Gesture_BecomesOneUndoStep()
    {
        var model = new StudioEditorModel(MakeProject());

        model.BeginEditingGroup();
        model.SetCanvasPadding(0.12);
        model.SetCanvasPadding(0.20);
        model.SetCanvasPadding(0.50);
        Assert.True(model.IsGroupingEdits);
        Assert.False(model.CanUndo);
        Assert.Equal(0.4, model.Project.Canvas.Padding, Precision);

        model.CommitEditingGroup();
        Assert.False(model.IsGroupingEdits);
        Assert.True(model.CanUndo);

        model.Undo();
        Assert.Equal(0.06, model.Project.Canvas.Padding, Precision);
        Assert.False(model.CanUndo);
        Assert.True(model.CanRedo);

        model.Redo();
        Assert.Equal(0.4, model.Project.Canvas.Padding, Precision);
        Assert.True(model.CanUndo);
        Assert.False(model.CanRedo);
    }

    [Fact]
    public void Gesture_ThatChangesNothingLeavesNoUndoStep()
    {
        var model = new StudioEditorModel(MakeProject());

        model.BeginEditingGroup();
        model.SetCanvasPadding(0.3);
        model.SetCanvasPadding(0.06);
        model.CommitEditingGroup();

        Assert.False(model.CanUndo);
    }

    [Fact]
    public void Edit_ThatSetsTheSameValueLeavesNoUndoStep()
    {
        var model = new StudioEditorModel(MakeProject());

        model.SetCanvasPadding(0.06);
        model.SetLayout(StudioLayout.Bubble);
        model.SetCameraBubbleSize(0.24);
        model.SetMuted(false);

        Assert.False(model.CanUndo);
    }

    [Fact]
    public void CancelledGesture_RestoresTheProject()
    {
        var model = new StudioEditorModel(MakeProject());
        var before = model.EditableState;

        model.BeginEditingGroup();
        model.SetScreenShadow(1);
        model.SetLayout(StudioLayout.Camera);
        model.CancelEditingGroup();

        Assert.True(before.ContentEquals(model.EditableState));
        Assert.False(model.CanUndo);
    }

    [Fact]
    public void NewEdit_ClearsRedo_AndUndoDepthIsCapped()
    {
        var model = new StudioEditorModel(MakeProject());
        model.SetScreenShadow(0.1);
        model.SetScreenShadow(0.2);
        model.Undo();
        Assert.True(model.CanRedo);
        model.SetScreenShadow(0.9);
        Assert.False(model.CanRedo);

        for (var index = 0; index < StudioEditorModel.MaximumUndoDepth + 20; index++)
        {
            model.SetCanvasPadding(index % 2 == 0 ? 0.1 : 0.2);
        }

        var undone = 0;
        while (model.CanUndo)
        {
            model.Undo();
            undone++;
        }

        Assert.Equal(StudioEditorModel.MaximumUndoDepth, undone);
    }

    [Fact]
    public void Undo_NeverTouchesBookkeeping()
    {
        var model = new StudioEditorModel(MakeProject());
        model.SetLayout(StudioLayout.SideBySide);

        var saved = model.Project with
        {
            Exports = [new StudioExport { Path = @"C:\Videos\out.mp4" }],
            Name = "Renamed",
            KeepSources = true,
        };
        model.RefreshBookkeeping(saved);

        model.Undo();

        Assert.Equal(StudioLayout.Bubble, model.Project.Scenes[0].Layout);
        Assert.Equal(new[] { @"C:\Videos\out.mp4" }, model.Project.Exports.Select(export => export.Path).ToArray());
        Assert.Equal("Renamed", model.Project.Name);
        Assert.True(model.Project.KeepSources);
    }

    // Edits

    [Fact]
    public void StyleEdits_ClampToTheFormatRanges()
    {
        var model = new StudioEditorModel(MakeProject());

        model.SetCanvasPadding(-1);
        model.SetScreenCornerRadius(3);
        model.SetScreenShadow(-1);
        model.SetCameraBubbleSize(2);
        model.SetCameraCornerRadius(9);
        model.SetCameraBorderWidth(1);
        model.SetCameraShadow(2);
        model.SetSideBySide(StudioCameraSide.Leading, 0.9);
        model.SetCameraBubbleOffsets(5, double.NegativeInfinity);

        Assert.Equal(0, model.Project.Canvas.Padding, Precision);
        Assert.Equal(0.2, model.Project.Screen.CornerRadius, Precision);
        Assert.Equal(0, model.Project.Screen.Shadow, Precision);
        Assert.Equal(0.6, model.Project.Scenes[0].Bubble.Size, Precision);
        Assert.Equal(0.5, model.Project.Camera.CornerRadius, Precision);
        Assert.Equal(0.02, model.Project.Camera.BorderWidth, Precision);
        Assert.Equal(1, model.Project.Camera.Shadow, Precision);
        Assert.Equal(0.6, model.Project.Scenes[0].Split.CameraFraction, Precision);
        Assert.Equal(StudioCameraSide.Leading, model.Project.Scenes[0].Split.CameraSide);
        Assert.Equal(1, model.Project.Scenes[0].Bubble.OffsetX, Precision);
        Assert.Equal(0, model.Project.Scenes[0].Bubble.OffsetY, Precision);
    }

    [Fact]
    public void StyleEdits_IgnoreValuesThatAreNotNumbers()
    {
        var model = new StudioEditorModel(MakeProject());

        model.SetCanvasPadding(double.NaN);
        model.SetScreenCornerRadius(double.PositiveInfinity);
        model.SetCameraBubbleSize(double.NaN);
        model.SetSideBySide(StudioCameraSide.Leading, double.NaN);

        Assert.False(model.CanUndo);
        Assert.Equal(0.06, model.Project.Canvas.Padding, Precision);
        Assert.Equal(StudioCameraSide.Trailing, model.Project.Scenes[0].Split.CameraSide);
    }

    [Fact]
    public void Colors_AreNormalized_AndBadColorsAreIgnored()
    {
        var model = new StudioEditorModel(MakeProject());

        model.SetCameraBorderColor(" #aabbccdd ");
        Assert.Equal("#AABBCC", model.Project.Camera.BorderColor);
        model.SetCameraBorderColor("teal");
        Assert.Equal("#AABBCC", model.Project.Camera.BorderColor);

        model.SetBackground(StudioBackgroundStyle.Gradient, "candy", "ff6bad", "#8cc7ff");
        var gradient = model.Project.Canvas.Background;
        Assert.Equal(StudioBackgroundStyle.Gradient, gradient.Style);
        Assert.Equal("candy", gradient.Preset);
        Assert.Equal("#FF6BAD", gradient.Primary);
        Assert.Equal("#8CC7FF", gradient.Secondary);

        model.SetBackground(StudioBackgroundStyle.Solid, null, "nope", null, @"..\secret.png");
        var solid = model.Project.Canvas.Background;
        Assert.Equal(StudioBackgroundStyle.Solid, solid.Style);
        Assert.Null(solid.Preset);
        Assert.Equal("#FF6BAD", solid.Primary);
        Assert.Null(solid.Secondary);
        Assert.Null(solid.Image);
    }

    [Fact]
    public void LayoutChoices_AreIgnoredWithoutACamera()
    {
        var model = new StudioEditorModel(MakeProject(camera: false));
        Assert.False(model.HasCamera);
        Assert.Equal(StudioLayout.Screen, model.EffectiveLayout);

        model.SetLayout(StudioLayout.SideBySide);
        Assert.False(model.CanUndo);
        Assert.Equal(StudioLayout.Screen, model.EffectiveLayout);

        // The stored layout can be one that needs a camera. Choosing Screen then changes nothing
        // that is drawn, so it is not an edit and not an undo step.
        var storedBubble = new StudioEditorModel(MakeProject(camera: false) with
        {
            Scenes = [new StudioScene { Start = 0, Layout = StudioLayout.Bubble }],
        });
        storedBubble.SetLayout(StudioLayout.Screen);
        Assert.False(storedBubble.CanUndo);
        Assert.Equal(StudioLayout.Bubble, storedBubble.Project.Scenes[0].Layout);

        var withCamera = new StudioEditorModel(MakeProject());
        withCamera.SetLayout(StudioLayout.Camera);
        Assert.Equal(StudioLayout.Camera, withCamera.EffectiveLayout);
    }

    [Fact]
    public void Opening_KeepsLaterScenesAndTheStoredLayout()
    {
        var project = MakeProject(camera: false) with
        {
            Scenes =
            [
                new StudioScene { Start = 0, Layout = StudioLayout.Bubble },
                new StudioScene { Start = 4, Layout = StudioLayout.Camera },
            ],
        };
        var model = new StudioEditorModel(project);
        model.SetCanvasPadding(0.2);

        Assert.Equal(2, model.Project.Scenes.Length);
        Assert.Equal(StudioLayout.Bubble, model.Project.Scenes[0].Layout);
        Assert.Equal(StudioLayout.Camera, model.Project.Scenes[1].Layout);

        var empty = new StudioEditorModel(MakeProject() with { Scenes = [] });
        Assert.Single(empty.Project.Scenes);
        Assert.Equal(StudioLayout.Bubble, empty.Project.Scenes[0].Layout);
    }

    // Bubble

    [Theory]
    [InlineData(40, 30, StudioAnchor.TopLeft)]
    [InlineData(700, 30, StudioAnchor.TopRight)]
    [InlineData(40, 500, StudioAnchor.BottomLeft)]
    [InlineData(700, 500, StudioAnchor.BottomRight)]
    [InlineData(-500, -500, StudioAnchor.TopLeft)]
    [InlineData(5000, 5000, StudioAnchor.BottomRight)]
    public void MovingTheBubble_AnchorsItToTheNearestCorner_AndTheResolverAgrees(double x, double y, StudioAnchor expectedAnchor)
    {
        const double width = 1000;
        const double height = 800;
        var model = new StudioEditorModel(MakeProject());
        var size = model.GetBubbleRect(width, height)!.Value;

        model.MoveBubbleTopLeft(x, y, width, height);

        var expectedX = Math.Clamp(x, 0, width - size.Width);
        var expectedY = Math.Clamp(y, 0, height - size.Height);
        Assert.Equal(expectedAnchor, model.Project.Scenes[0].Bubble.Anchor);
        var moved = model.GetBubbleRect(width, height)!.Value;
        Assert.Equal(expectedX, moved.X, 6);
        Assert.Equal(expectedY, moved.Y, 6);
        Assert.Equal(size.Width, moved.Width, 6);
        Assert.Equal(size.Height, moved.Height, 6);

        // The layout engine draws the bubble where the editor says it is.
        var resolved = StudioLayoutResolver.Resolve(model.Project, 0, width, height).Camera!.Value.Rect;
        Assert.Equal(moved, resolved);
    }

    [Fact]
    public void MovingTheBubble_ByItsCenter_AndInOneGestureIsOneUndoStep()
    {
        const double width = 1280;
        const double height = 720;
        var model = new StudioEditorModel(MakeProject());
        var size = model.GetBubbleRect(width, height)!.Value;

        model.BeginEditingGroup();
        model.MoveBubbleCenter(300, 200, width, height);
        model.MoveBubbleCenter(640, 360, width, height);
        model.CommitEditingGroup();

        var moved = model.GetBubbleRect(width, height)!.Value;
        Assert.Equal(640 - size.Width / 2, moved.X, 6);
        Assert.Equal(360 - size.Height / 2, moved.Y, 6);

        model.Undo();
        Assert.False(model.CanUndo);
        Assert.Equal(size, model.GetBubbleRect(width, height)!.Value);
    }

    [Fact]
    public void MovingTheBubble_DoesNothingWithoutACameraOrACanvas()
    {
        var withoutCamera = new StudioEditorModel(MakeProject(camera: false));
        withoutCamera.MoveBubbleTopLeft(10, 10, 1000, 800);
        Assert.False(withoutCamera.CanUndo);
        Assert.Null(withoutCamera.GetBubbleRect(1000, 800));

        var model = new StudioEditorModel(MakeProject());
        model.MoveBubbleTopLeft(10, 10, 0, 800);
        model.MoveBubbleTopLeft(double.NaN, 10, 1000, 800);
        Assert.False(model.CanUndo);
        Assert.Null(model.GetBubbleRect(0, 0));
    }

    [Fact]
    public void SettingAnAnchor_SnapsTheBubbleBackToTheCorner()
    {
        var model = new StudioEditorModel(MakeProject());
        model.MoveBubbleTopLeft(300, 200, 1000, 800);
        Assert.NotEqual(0, model.Project.Scenes[0].Bubble.OffsetX);

        model.SetCameraAnchor(StudioAnchor.TopRight);

        Assert.Equal(StudioAnchor.TopRight, model.Project.Scenes[0].Bubble.Anchor);
        Assert.Equal(0, model.Project.Scenes[0].Bubble.OffsetX);
        Assert.Equal(0, model.Project.Scenes[0].Bubble.OffsetY);
    }

    // Looks

    [Fact]
    public void Looks_NeverCarryCrops()
    {
        var screenCrop = new StudioRect(0.1, 0.2, 0.5, 0.5);
        var cameraCrop = new StudioRect(0.2, 0.1, 0.4, 0.4);
        var baseProject = MakeProject();
        var project = baseProject with
        {
            Screen = baseProject.Screen with { Crop = screenCrop },
            Camera = baseProject.Camera with { Crop = cameraCrop },
        };
        var model = new StudioEditorModel(project);

        var look = model.CurrentLook;
        Assert.Null(look.Screen.Crop);
        Assert.Null(look.Camera.Crop);

        model.ApplyLook(new StudioLook(
            look.Canvas with { Padding = 0.25 },
            look.Screen with { CornerRadius = 0.1, Crop = new StudioRect(0, 0, 0.1, 0.1) },
            look.Camera with { Shape = StudioCameraShape.Squircle, Crop = new StudioRect(0, 0, 0.1, 0.1) }));

        Assert.Equal(0.25, model.Project.Canvas.Padding, Precision);
        Assert.Equal(0.1, model.Project.Screen.CornerRadius, Precision);
        Assert.Equal(StudioCameraShape.Squircle, model.Project.Camera.Shape);
        Assert.Same(screenCrop, model.Project.Screen.Crop);
        Assert.Same(cameraCrop, model.Project.Camera.Crop);

        model.Undo();
        Assert.Equal(0.06, model.Project.Canvas.Padding, Precision);
    }

    [Fact]
    public void TheCameraBackground_IsKeptBlurredOrRemoved()
    {
        var model = new StudioEditorModel(MakeProject());
        Assert.Equal(StudioCameraCutout.None, model.Project.Camera.Cutout);

        model.SetCameraCutout(StudioCameraCutout.Blur);
        Assert.Equal(StudioCameraCutout.Blur, model.Project.Camera.Cutout);
        model.SetCameraCutout(StudioCameraCutout.Remove);
        Assert.Equal(StudioCameraCutout.Remove, model.Project.Camera.Cutout);

        // The same choice again is no edit: two steps back and the project is as it was opened.
        model.SetCameraCutout(StudioCameraCutout.Remove);
        model.Undo();
        Assert.Equal(StudioCameraCutout.Blur, model.Project.Camera.Cutout);
        model.Undo();
        Assert.Equal(StudioCameraCutout.None, model.Project.Camera.Cutout);
        Assert.False(model.CanUndo);

        // It is part of the look, so a saved look brings it to the next recording.
        model.Redo();
        var next = new StudioEditorModel(MakeProject());
        next.ApplyLook(model.CurrentLook);
        Assert.Equal(StudioCameraCutout.Blur, next.Project.Camera.Cutout);
    }

    // Trim and time

    [Fact]
    public void Trim_StaysInsideTheRecordingAndKeepsAMinimumLength()
    {
        var model = new StudioEditorModel(MakeProject(duration: 10));

        model.SetTrim(9.95, 20);
        Assert.Equal(9.9, model.Project.Edits.TrimStart, Precision);
        Assert.Null(model.Project.Edits.TrimEnd);
        Assert.Equal(0.1, model.OutputDuration, Precision);

        model.SetTrim(-3, 4);
        Assert.Equal(0, model.TrimStart, Precision);
        Assert.Equal(4, model.TrimEnd, Precision);

        model.SetTrimStart(8);
        Assert.Equal(3.9, model.TrimStart, Precision);
        Assert.Equal(4, model.TrimEnd, Precision);

        model.SetTrimStart(1);
        model.SetTrimEnd(0.2);
        Assert.Equal(1, model.TrimStart, Precision);
        Assert.Equal(1.1, model.TrimEnd, Precision);

        model.SetTrimEnd(double.NaN);
        Assert.Equal(1, model.TrimStart, Precision);
        Assert.Equal(10, model.TrimEnd, Precision);

        model.SetTrimEnd(4);
        model.SetTrimEnd(10);
        Assert.Null(model.Project.Edits.TrimEnd);
        Assert.Equal(9, model.OutputDuration, Precision);
    }

    [Fact]
    public void VeryShortAndEmptyRecordings()
    {
        var brief = new StudioEditorModel(MakeProject(duration: 0.04));
        brief.SetTrim(0.03, 0.035);
        Assert.Equal(0, brief.TrimStart, Precision);
        Assert.Equal(0.04, brief.TrimEnd, Precision);

        var empty = new StudioEditorModel(MakeProject(duration: 0));
        Assert.Equal(0, empty.OutputDuration);
        Assert.Equal(0, empty.GetPlaybackStart(3));
    }

    [Fact]
    public void Opening_ClampsADamagedTrim()
    {
        var baseProject = MakeProject(duration: 10);
        var reversed = new StudioEditorModel(baseProject with
        {
            Edits = new StudioEdits { TrimStart = 8, TrimEnd = 2 },
        });
        Assert.True(reversed.TrimEnd - reversed.TrimStart >= StudioEditorModel.MinimumDuration - 1e-9);
        Assert.InRange(reversed.TrimStart, 0, 10);
        Assert.InRange(reversed.TrimEnd, 0, 10);
        Assert.False(reversed.CanUndo);

        var beyond = new StudioEditorModel(baseProject with
        {
            Edits = new StudioEdits { TrimStart = -4, TrimEnd = 99 },
        });
        Assert.Equal(0, beyond.TrimStart, Precision);
        Assert.Equal(10, beyond.TrimEnd, Precision);
        Assert.Null(beyond.Project.Edits.TrimEnd);
    }

    [Fact]
    public void Time_ConvertsBetweenSourceAndOutput_AndPlaybackFollowsTheTrim()
    {
        var model = new StudioEditorModel(MakeProject(duration: 10));
        model.SetTrim(2, 8);

        Assert.Equal(6, model.OutputDuration, Precision);
        Assert.Equal(0, model.GetOutputTime(1), Precision);
        Assert.Equal(3, model.GetOutputTime(5), Precision);
        Assert.Equal(6, model.GetOutputTime(9.5), Precision);
        Assert.Equal(5, model.GetSourceTime(3), Precision);
        Assert.Equal(1.0 / 30, model.FrameDuration, Precision);

        // Play starts where the playhead is when that is inside the kept range, else at its start.
        Assert.Equal(5, model.GetPlaybackStart(5), Precision);
        Assert.Equal(2, model.GetPlaybackStart(0.5), Precision);
        Assert.Equal(2, model.GetPlaybackStart(8), Precision);
        Assert.Equal(2, model.GetPlaybackStart(9), Precision);
        Assert.Equal(2, model.GetPlaybackStart(double.NaN), Precision);

        Assert.False(model.IsAtPlaybackEnd(7.9));
        Assert.True(model.IsAtPlaybackEnd(8));
        Assert.Equal(10, model.ClampSourceTime(50), Precision);
        Assert.Equal(0, model.ClampSourceTime(-1), Precision);
        Assert.Equal("0:03.0 of 0:06.0", model.GetPlayheadText(5));
    }

    [Theory]
    [InlineData(0, "0:00.0")]
    [InlineData(2.56, "0:02.5")]
    [InlineData(62.5, "1:02.5")]
    [InlineData(3599.99, "59:59.9")]
    [InlineData(-4, "0:00.0")]
    [InlineData(double.NaN, "0:00.0")]
    public void FormatTime_UsesMinutesSecondsAndTenths(double seconds, string expected)
    {
        Assert.Equal(expected, StudioEditorModel.FormatTime(seconds));
    }

    // Export state

    [Fact]
    public void UnexportedChanges_TrackTheLastExportedComposition()
    {
        var draft = new StudioEditorModel(MakeProject());
        Assert.True(draft.HasNeverExported);
        Assert.True(draft.HasUnexportedChanges);

        var rendered = draft.EditableState;
        draft.RefreshBookkeeping(draft.Project with { Exports = [new StudioExport { Path = @"C:\Videos\out.mp4" }] });
        draft.MarkExported(rendered);
        Assert.False(draft.HasNeverExported);
        Assert.False(draft.HasUnexportedChanges);

        draft.SetCameraMirror(false);
        Assert.True(draft.HasUnexportedChanges);
        draft.SetCameraMirror(true);
        Assert.False(draft.HasUnexportedChanges);

        // A project reopened after an export counts as unchanged until it is edited.
        var reopened = new StudioEditorModel(draft.Project);
        Assert.False(reopened.HasUnexportedChanges);
        reopened.SetBrandingEnabled(true);
        Assert.True(reopened.HasUnexportedChanges);
    }

    [Fact]
    public void EditableState_ComparesContentAndRoundTripsThroughAProject()
    {
        var project = MakeProject();
        var first = StudioEditableState.From(project);
        var copy = StudioEditableState.From(project with
        {
            Scenes = [project.Scenes[0] with { }],
            Edits = project.Edits with { Cuts = [] },
        });
        Assert.True(first.ContentEquals(copy));
        Assert.False(first.ContentEquals(null));

        var changed = StudioEditableState.From(project with
        {
            Scenes = [project.Scenes[0] with { Layout = StudioLayout.Camera }],
        });
        Assert.False(first.ContentEquals(changed));

        var other = MakeProject(camera: false, duration: 3) with { Name = "Other" };
        var applied = changed.ApplyTo(other);
        Assert.Equal("Other", applied.Name);
        Assert.Null(applied.Sources.Camera);
        Assert.Equal(StudioLayout.Camera, applied.Scenes[0].Layout);
    }

    // Geometry and text

    [Fact]
    public void CanvasGeometry_FitsAndCentersTheCanvas()
    {
        var wide = StudioEditorModel.GetCanvasGeometry(1000, 400, 1920, 1080);
        Assert.Equal(400.0 * 1920 / 1080, wide.CanvasRectInView.Width, 6);
        Assert.Equal(400, wide.CanvasRectInView.Height, 6);
        Assert.Equal((1000 - wide.CanvasRectInView.Width) / 2, wide.CanvasRectInView.X, 6);
        Assert.Equal(0, wide.CanvasRectInView.Y, 6);
        Assert.Equal(1080.0 / 400, wide.CanvasPixelsPerViewUnit, 6);

        Assert.True(wide.TryGetCanvasPoint(500, 200, out var x, out var y));
        Assert.Equal(960, x, 6);
        Assert.Equal(540, y, 6);
        Assert.False(wide.TryGetCanvasPoint(10, 200, out _, out _));

        var (viewX, viewY) = wide.GetViewPoint(960, 540);
        Assert.Equal(500, viewX, 6);
        Assert.Equal(200, viewY, 6);

        var empty = StudioEditorModel.GetCanvasGeometry(0, 400, 1920, 1080);
        Assert.Equal(0, empty.CanvasRectInView.Width);
        Assert.Equal(1, empty.CanvasPixelsPerViewUnit);
        Assert.False(empty.TryGetCanvasPoint(0, 0, out _, out _));
    }

    [Fact]
    public void Names_ForScreenReaders()
    {
        Assert.Equal("Screen with camera bubble", StudioEditorModel.GetLayoutName(StudioLayout.Bubble));
        Assert.Equal("Camera only", StudioEditorModel.GetLayoutName(StudioLayout.Camera));
        Assert.Equal("Bottom right", StudioEditorModel.GetAnchorName(StudioAnchor.BottomRight));
        Assert.Equal("Rounded rectangle", StudioEditorModel.GetShapeName(StudioCameraShape.RoundedRectangle));
        Assert.Equal("Keep", StudioEditorModel.GetCutoutName(StudioCameraCutout.None));
        Assert.Equal("Blur", StudioEditorModel.GetCutoutName(StudioCameraCutout.Blur));
        Assert.Equal("Remove", StudioEditorModel.GetCutoutName(StudioCameraCutout.Remove));
        Assert.Equal("Auto", StudioEditorModel.GetAspectName(StudioCanvasAspect.Auto));
        Assert.Equal("9:16", StudioEditorModel.GetAspectName(StudioCanvasAspect.Portrait9X16));
        Assert.Equal("12.5 seconds", StudioEditorModel.GetSecondsText(12.5));
        Assert.Equal("0.0 seconds", StudioEditorModel.GetSecondsText(double.NaN));
    }

    private static StudioProject MakeProject(bool camera = true, double duration = 10) => new()
    {
        Id = "3f0013cf-ba10-4453-af91-792b7882dae6",
        Name = "Test",
        Sources = new StudioSources
        {
            Screen = new StudioScreenSource { Width = 1920, Height = 1080, FrameRate = 30, Duration = duration },
            Camera = camera ? new StudioCameraSource { Width = 1280, Height = 720, Duration = duration } : null,
        },
        Scenes = [new StudioScene { Start = 0, Layout = camera ? StudioLayout.Bubble : StudioLayout.Screen }],
    };
}
