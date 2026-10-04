using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.Core.Tests;

/// <summary>Every edit the session offers reaches the project, and so the preview and the disk.</summary>
public sealed class StudioEditorSessionEditCoverageTests : StudioEditorSessionTestBase
{
    public static TheoryData<string, Action<StudioEditorSession>, Func<StudioProject, bool>> Edits => new()
    {
        { "layout", s => s.SetLayout(StudioLayout.SideBySide), p => p.Scenes[0].Layout == StudioLayout.SideBySide },
        { "canvas aspect", s => s.SetCanvasAspect(StudioCanvasAspect.Square), p => p.Canvas.Aspect == StudioCanvasAspect.Square },
        { "padding", s => s.SetCanvasPadding(0.25), p => Near(p.Canvas.Padding, 0.25) },
        { "screen radius", s => s.SetScreenCornerRadius(0.1), p => Near(p.Screen.CornerRadius, 0.1) },
        { "screen shadow", s => s.SetScreenShadow(0.8), p => Near(p.Screen.Shadow, 0.8) },
        { "screen crop", s => s.SetScreenCrop(new StudioRect(0.1, 0.2, 0.5, 0.6)), p => p.Screen.Crop is { X: 0.1, Y: 0.2, Width: 0.5, Height: 0.6 } },
        { "screen crop edge", s => s.SetScreenCropInset(StudioCropEdge.Left, 0.25), p => p.Screen.Crop is { X: 0.25, Y: 0, Width: 0.75, Height: 1 } },
        { "camera shape", s => s.SetCameraShape(StudioCameraShape.RoundedRectangle), p => p.Camera.Shape == StudioCameraShape.RoundedRectangle },
        { "camera radius", s => s.SetCameraCornerRadius(0.3), p => Near(p.Camera.CornerRadius, 0.3) },
        { "camera crop", s => s.SetCameraCrop(new StudioRect(0.2, 0.1, 0.6, 0.7)), p => p.Camera.Crop is { X: 0.2, Y: 0.1, Width: 0.6, Height: 0.7 } },
        { "camera crop edge", s => s.SetCameraCropInset(StudioCropEdge.Bottom, 0.4), p => p.Camera.Crop is { X: 0, Y: 0, Width: 1, Height: 0.6 } },
        { "bubble size", s => s.SetCameraBubbleSize(0.4), p => Near(p.Scenes[0].Bubble.Size, 0.4) },
        { "anchor", s => s.SetCameraAnchor(StudioAnchor.TopLeft), p => p.Scenes[0].Bubble.Anchor == StudioAnchor.TopLeft },
        {
            "offsets",
            s => s.SetCameraBubbleOffsets(-0.1, 0.05),
            p => Near(p.Scenes[0].Bubble.OffsetX, -0.1) && Near(p.Scenes[0].Bubble.OffsetY, 0.05)
        },
        { "mirror", s => s.SetCameraMirror(false), p => !p.Camera.Mirror },
        { "border", s => s.SetCameraBorderWidth(0.01), p => Near(p.Camera.BorderWidth, 0.01) },
        { "camera shadow", s => s.SetCameraShadow(0.9), p => Near(p.Camera.Shadow, 0.9) },
        {
            "side by side",
            s => s.SetSideBySide(StudioCameraSide.Leading, 0.45),
            p => p.Scenes[0].Split.CameraSide == StudioCameraSide.Leading && Near(p.Scenes[0].Split.CameraFraction, 0.45)
        },
        { "mute", s => s.SetMuted(true), p => p.Audio.Muted },
        { "click rings", s => s.SetClickRingsEnabled(false), p => !p.Overlays.Clicks.Enabled },
        { "branding", s => s.SetBrandingEnabled(true), p => p.Overlays.Branding },
        { "trim start", s => s.SetTrimStart(2), p => Near(p.Edits.TrimStart, 2) },
        { "trim end", s => s.SetTrimEnd(8), p => p.Edits.TrimEnd is { } end && Near(end, 8) },
        { "add zoom", s => s.AddZoom(2), p => p.Zooms.Length == 1 && Near(p.Zooms[0].Start, 2) },
        { "add zoom at the playhead", s => s.AddZoomAtPlayhead(), p => p.Zooms.Length == 1 && Near(p.Zooms[0].Start, 0) },
    };

    [Theory]
    [MemberData(nameof(Edits))]
    public async Task Edit_ReachesTheProjectThePreviewAndTheDisk(
        string name,
        Action<StudioEditorSession> edit,
        Func<StudioProject, bool> isApplied)
    {
        var id = CreateProject(camera: true);
        var session = await OpenAsync(id);
        Assert.False(isApplied(session.Project!), $"{name}: the project already has the edited value");

        edit(session);

        Assert.True(isApplied(session.Project!), $"{name}: the project");
        Assert.True(isApplied(Preview.LastProject!), $"{name}: the preview");
        Assert.True(session.CanUndo, $"{name}: undo");
        Advance(600);
        Assert.True(isApplied(Projects.Load(id)), $"{name}: the disk");

        session.Undo();
        Assert.False(isApplied(session.Project!), $"{name}: after undo");
    }

    [Fact]
    public async Task SolidPreset_StoresThePresetAndOneColor()
    {
        var session = await OpenAsync(CreateProject());

        session.SetBackgroundPreset(StudioBackgroundStyle.Solid, "coral", "#ff7a6b", "#123456");

        var background = session.Project!.Canvas.Background;
        Assert.Equal(StudioBackgroundStyle.Solid, background.Style);
        Assert.Equal("coral", background.Preset);
        Assert.Equal("#FF7A6B", background.Primary);
        Assert.Null(background.Secondary);
    }

    [Fact]
    public async Task GradientPreset_StoresThePresetAndBothColors()
    {
        var session = await OpenAsync(CreateProject());

        session.SetBackgroundPreset(StudioBackgroundStyle.Gradient, "sunset", "#FF7A5E", "#FFDB4F");

        var background = session.Project!.Canvas.Background;
        Assert.Equal(StudioBackgroundStyle.Gradient, background.Style);
        Assert.Equal("sunset", background.Preset);
        Assert.Equal("#FF7A5E", background.Primary);
        Assert.Equal("#FFDB4F", background.Secondary);
    }

    [Theory]
    [InlineData(StudioBackgroundStyle.None)]
    [InlineData(StudioBackgroundStyle.Image)]
    public async Task BackgroundPreset_OfAnotherStyle_IsIgnored(StudioBackgroundStyle style)
    {
        var session = await OpenAsync(CreateProject());

        session.SetBackgroundPreset(style, "coral", "#FF7A6B");

        Assert.Equal(StudioBackgroundStyle.Gradient, session.Project!.Canvas.Background.Style);
        Assert.False(session.HasUnsavedEdits);
    }

    [Fact]
    public async Task RemoveBackground_KeepsTheColors_AndTheDefaultCanComeBack()
    {
        var session = await OpenAsync(CreateProject());

        session.RemoveBackground();

        var background = session.Project!.Canvas.Background;
        Assert.Equal(StudioBackgroundStyle.None, background.Style);
        Assert.Null(background.Preset);
        Assert.Equal("#2687E8", background.Primary);
        Assert.Equal("#2EE0BF", background.Secondary);

        session.SetBackgroundPreset(StudioBackgroundStyle.Gradient, "ocean", "#2687E8", "#2EE0BF");

        Assert.Equal(StudioBackgroundStyle.Gradient, session.Project.Canvas.Background.Style);
        Assert.Equal("ocean", session.Project.Canvas.Background.Preset);
        session.Undo();
        Assert.Equal(StudioBackgroundStyle.None, session.Project.Canvas.Background.Style);
        session.Undo();
        Assert.Equal(StudioBackgroundStyle.Gradient, session.Project.Canvas.Background.Style);
        Assert.False(session.CanUndo);
    }

    [Fact]
    public async Task MoveBubble_PutsItsTopLeftCornerWhereItWasDragged()
    {
        var session = await OpenAsync(CreateProject(camera: true));
        var before = Assert.NotNull(session.GetBubbleRect(1920, 1080));

        session.BeginGesture();
        session.MoveBubbleTopLeft(100, 60, 1920, 1080);
        session.MoveBubbleTopLeft(200, 120, 1920, 1080);
        session.EndGesture();

        var after = Assert.NotNull(session.GetBubbleRect(1920, 1080));
        Assert.Equal(200, after.X, 6);
        Assert.Equal(120, after.Y, 6);
        Assert.Equal(before.Width, after.Width, 6);
        Assert.Equal(StudioAnchor.TopLeft, session.Project!.Scenes[0].Bubble.Anchor);

        session.Undo();
        Assert.Equal(before.X, Assert.NotNull(session.GetBubbleRect(1920, 1080)).X, 6);
        Assert.False(session.CanUndo);
    }

    [Fact]
    public async Task BubbleRect_IsOnlyOfferedInTheBubbleLayout()
    {
        var session = await OpenAsync(CreateProject(camera: true));
        Assert.NotNull(session.GetBubbleRect(1920, 1080));

        session.SetLayout(StudioLayout.SideBySide);
        Assert.Null(session.GetBubbleRect(1920, 1080));

        var withoutCamera = await OpenAsync(CreateProject());
        Assert.Null(withoutCamera.GetBubbleRect(1920, 1080));
    }

    private static bool Near(double actual, double expected) => Math.Abs(actual - expected) < 1e-9;
}
