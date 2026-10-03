using TinyClips.Core.Studio;

namespace TinyClips.Core.Tests;

public sealed class StudioProjectStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TinyClipsTests", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 10, 2, 22, 41, 0, TimeSpan.Zero));

    [Fact]
    public void CreateLoadSaveListAndDelete_RoundTripsProjectInTempDirectory()
    {
        var store = CreateStore();
        var creation = store.CreateProjectForRecording(CreateRequest());

        Assert.True(Directory.Exists(creation.Paths.ProjectDirectory));
        Assert.Equal(Path.Combine(creation.Paths.ProjectDirectory, "screen.mp4"), creation.Paths.ScreenPath);
        Assert.Equal(Path.Combine(creation.Paths.ProjectDirectory, "camera.mp4"), creation.Paths.CameraPath);
        Assert.Equal(StudioLayout.Bubble, creation.Project.Scenes[0].Layout);
        Assert.Equal(0.25, creation.Project.Edits.TrimStart);
        Assert.True(creation.Project.Overlays.Branding);

        var loaded = store.Load(creation.Project.Id);
        Assert.Equal(creation.Project.Id, loaded.Id);

        _time.Advance(TimeSpan.FromMinutes(1));
        store.Save(loaded with { Name = "Edited" });

        var summary = Assert.Single(store.ListSummaries());
        Assert.Equal("Edited", summary.Name);
        Assert.True(summary.IsDraft);
        Assert.False(summary.IsFlat);
        Assert.False(summary.KeepSources);
        Assert.True(summary.SizeBytes > 0);
        Assert.Equal(_time.GetUtcNow(), store.Load(loaded.Id).ModifiedAt);

        store.Delete(loaded.Id);
        Assert.Empty(store.ListSummaries());
        Assert.False(Directory.Exists(creation.Paths.ProjectDirectory));
    }

    [Fact]
    public void ExportIndex_IsCaseInsensitiveAndSupportsUpdateRemoveAndRebuild()
    {
        var store = CreateStore();
        var project = store.CreateProjectForRecording(CreateRequest()).Project;
        var firstPath = Path.Combine(_directory, "Exports", "Clip.MP4");
        var secondPath = Path.Combine(_directory, "Exports", "Moved.mp4");

        store.RecordExport(project.Id, firstPath);

        Assert.Equal(project.Id, store.FindProjectIdByExportPath(firstPath.ToLowerInvariant()));
        Assert.True(store.UpdateExportPath(firstPath.ToUpperInvariant(), secondPath));
        Assert.Null(store.FindProjectIdByExportPath(firstPath));
        Assert.Equal(project.Id, store.FindProjectIdByExportPath(secondPath.ToUpperInvariant()));

        var reopened = CreateStore();
        Assert.Equal(project.Id, reopened.FindProjectIdByExportPath(secondPath.ToLowerInvariant()));

        Assert.True(reopened.RemoveExportPath(secondPath));
        Assert.Null(reopened.FindProjectIdByExportPath(secondPath));
    }

    [Fact]
    public void GetOrCreateFlatProject_ReturnsOneProjectPerExternalVideoPath()
    {
        var store = CreateStore();
        var videoPath = Path.Combine(_directory, "external.mp4");
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(videoPath, [1, 2, 3]);

        var first = store.GetOrCreateFlatProject(videoPath.ToUpperInvariant(), "1.9.0");
        var second = store.GetOrCreateFlatProject(videoPath.ToLowerInvariant(), "1.9.0");

        Assert.Equal(first.Id, second.Id);
        Assert.True(first.Sources.Screen.External);
        Assert.Equal(Path.GetFullPath(videoPath.ToUpperInvariant()), first.Sources.Screen.File);
        Assert.Null(first.Sources.Camera);
        Assert.Null(first.Sources.Events);
        Assert.True(store.ListSummaries().Single().IsFlat);
    }

    [Fact]
    public void Events_LoadAndSaveRoundTrip()
    {
        var store = CreateStore();
        var project = store.CreateProjectForRecording(CreateRequest()).Project;
        var events = new StudioEvents
        {
            Capture = new StudioCaptureInfo { Width = 100, Height = 50, Scale = 2, Kind = StudioCaptureKind.Region },
            Clicks = [new StudioClickEvent { T = 1.2, X = 0.3, Y = 0.4, Button = StudioMouseButton.Right }],
            Cursor = [new StudioCursorSample { T = 0, X = 0.5, Y = 0.6 }],
            CameraCorners = [new StudioCameraCornerEvent { T = 0, Corner = StudioAnchor.TopLeft }],
        };

        store.SaveEvents(project.Id, events);
        var loaded = store.LoadEvents(project.Id);

        Assert.Equal(100, loaded.Capture.Width);
        Assert.Equal(StudioCaptureKind.Region, loaded.Capture.Kind);
        Assert.Equal(StudioMouseButton.Right, loaded.Clicks.Single().Button);
        Assert.Equal(StudioAnchor.TopLeft, loaded.CameraCorners.Single().Corner);
    }

    private StudioProjectStore CreateStore() => new(_directory, _time);

    private static StudioProjectCreationRequest CreateRequest() =>
        new(
            "Recording",
            new StudioRecordingSourceInfo(1920, 1080, 10, 60),
            new StudioCameraSourceInfo(1280, 720, 9, 0.25),
            StudioAnchor.TopLeft,
            new StudioClickOverlay { Enabled = true, Color = "#112233", Size = 30, StrokeWidth = 2, Opacity = 0.5, Duration = 0.3 },
            true,
            "1.9.0");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch
        {
        }
    }
}
