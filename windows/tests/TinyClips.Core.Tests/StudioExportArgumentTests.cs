using TinyClips.Core.Models;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Rendering;

namespace TinyClips.Core.Tests;

/// <summary>
/// What an export refuses before it touches Media Foundation or a graphics device, and how it
/// finds its files. The exports themselves are checked by the StudioRenderCheck tool.
/// </summary>
public sealed class StudioExportArgumentTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "TinyClipsStudioTests-" + Guid.NewGuid().ToString("N"));
    private readonly StudioEvents _events = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public StudioExportArgumentTests()
    {
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void ExportOptions_DefaultToTheAppsExport()
    {
        var options = new StudioExportOptions();

        Assert.Equal(StudioExportLimits.LongSide, options.LongSideLimit);
        Assert.Equal(VideoCodec.H264, options.Codec);
        Assert.Equal(StudioEncoderPreference.HardwareWithSoftwareFallback, options.EncoderPreference);
        Assert.Equal(StudioRenderDevicePreference.HardwareWithWarpFallback, options.DevicePreference);

        // Not asked for: the exporter picks by the device the frames are drawn on.
        Assert.Null(options.RenderQuality);
        Assert.Equal(StudioRenderQuality.Preview, (options with { RenderQuality = StudioRenderQuality.Preview }).RenderQuality);
    }

    [Fact]
    public void ExportAsync_ChecksItsArgumentsOnTheCallingThread()
    {
        var exporter = new StudioExporter();
        var project = Project();
        var paths = Paths();
        var output = Path.Combine(_folder, "out.mp4");

        Assert.Throws<ArgumentNullException>(() => { _ = exporter.ExportAsync(null!, _events, paths, output, cancellationToken: Token); });
        Assert.Throws<ArgumentNullException>(() => { _ = exporter.ExportAsync(project, null!, paths, output, cancellationToken: Token); });
        Assert.Throws<ArgumentNullException>(() => { _ = exporter.ExportAsync(project, _events, null!, output, cancellationToken: Token); });
        Assert.Throws<ArgumentNullException>(() => { _ = exporter.ExportAsync(project, _events, paths, null!, cancellationToken: Token); });
        Assert.Throws<ArgumentException>(() => { _ = exporter.ExportAsync(project, _events, paths, "  ", cancellationToken: Token); });
    }

    [Fact]
    public void WritePosterAsync_ChecksItsArgumentsOnTheCallingThread()
    {
        var exporter = new StudioExporter();
        var project = Project();
        var paths = Paths();

        Assert.Throws<ArgumentNullException>(() => { _ = exporter.WritePosterAsync(null!, _events, paths, paths.PosterPath, 640, 0, Token); });
        Assert.Throws<ArgumentNullException>(() => { _ = exporter.WritePosterAsync(project, null!, paths, paths.PosterPath, 640, 0, Token); });
        Assert.Throws<ArgumentNullException>(() => { _ = exporter.WritePosterAsync(project, _events, null!, paths.PosterPath, 640, 0, Token); });
        Assert.Throws<ArgumentException>(() => { _ = exporter.WritePosterAsync(project, _events, paths, string.Empty, 640, 0, Token); });
        Assert.Throws<ArgumentNullException>(() => { _ = new StudioExportService().WritePosterAsync(project, _events, null!, Token); });
    }

    [Fact]
    public async Task ExportAsync_FailsWhenTheOutputFolderDoesNotExist()
    {
        CreateSources();
        var output = Path.Combine(_folder, "no-such-folder", "out.mp4");

        var ex = await Assert.ThrowsAsync<DirectoryNotFoundException>(() => new StudioExporter().ExportAsync(Project(), _events, Paths(), output, cancellationToken: Token));

        Assert.Contains("no-such-folder", ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_folder, "no-such-folder")));
    }

    [Fact]
    public async Task ExportAsync_FailsWhenTheScreenRecordingIsMissing()
    {
        var ex = await Assert.ThrowsAsync<FileNotFoundException>(() => new StudioExporter().ExportAsync(Project(), _events, Paths(), Path.Combine(_folder, "out.mp4"), cancellationToken: Token));

        Assert.Contains("screen recording", ex.Message, StringComparison.Ordinal);
        Assert.Equal(Path.Combine(_folder, "screen.mp4"), ex.FileName);
    }

    [Fact]
    public async Task ExportAsync_FailsWhenTheCameraRecordingIsMissing()
    {
        File.WriteAllBytes(Path.Combine(_folder, "screen.mp4"), []);

        var ex = await Assert.ThrowsAsync<FileNotFoundException>(() => new StudioExporter().ExportAsync(Project(), _events, Paths(), Path.Combine(_folder, "out.mp4"), cancellationToken: Token));

        Assert.Contains("camera recording", ex.Message, StringComparison.Ordinal);
        Assert.Equal(Path.Combine(_folder, "camera.mp4"), ex.FileName);
        Assert.Single(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task ExportAsync_ACancelledTokenCancelsBeforeAnythingIsLookedAt()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        // No source files exist, and still it is the cancellation that is reported.
        var task = new StudioExporter().ExportAsync(Project(), _events, Paths(), Path.Combine(_folder, "out.mp4"), null, null, cancel.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(task.IsCanceled);
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Theory]
    [InlineData(1, 1.01, "shorter than one output frame")]
    [InlineData(4, 4, "Nothing is left")]
    public async Task ExportAsync_FailsWhenTheTrimLeavesNoFrame(double start, double end, string message)
    {
        CreateSources();
        var project = Project() with { Edits = new StudioEdits { TrimStart = start, TrimEnd = end } };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new StudioExporter().ExportAsync(project, _events, Paths(), Path.Combine(_folder, "out.mp4"), cancellationToken: Token));

        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
        Assert.Equal(2, Directory.GetFiles(_folder).Length);
    }

    [Fact]
    public async Task WritePosterAsync_FailsWhenTheScreenRecordingIsMissing()
    {
        var paths = Paths();

        var ex = await Assert.ThrowsAsync<FileNotFoundException>(() => new StudioExporter().WritePosterAsync(Project(), _events, paths, paths.PosterPath, 640, 0, Token));

        Assert.Contains("screen recording", ex.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task ExportService_PassesFailuresThrough()
    {
        IStudioExportService service = new StudioExportService();

        await Assert.ThrowsAsync<FileNotFoundException>(() => service.ExportAsync(Project(), _events, Paths(), Path.Combine(_folder, "out.mp4"), VideoCodec.H264, null, Token));
        await Assert.ThrowsAsync<FileNotFoundException>(() => service.WritePosterAsync(Project(), _events, Paths(), Token));
    }

    [Fact]
    public void SourceFiles_AreWhereTheStoreSaysTheyAre()
    {
        var project = Project();
        var paths = Paths();

        Assert.Equal(paths.ScreenPath, StudioSourceFiles.Screen(project, paths));
        Assert.Equal(paths.CameraPath, StudioSourceFiles.Camera(project, paths));

        // A flat project's video stays where it is.
        var flat = project with { Sources = project.Sources with { Screen = project.Sources.Screen with { File = @"D:\Videos\clip.mp4", External = true }, Camera = null } };
        Assert.Equal(@"D:\Videos\clip.mp4", StudioSourceFiles.Screen(flat, paths));
        Assert.Null(StudioSourceFiles.Camera(flat, paths));

        // Paths built without a camera path still find the camera next to the project.
        Assert.Equal(Path.Combine(_folder, "camera.mp4"), StudioSourceFiles.Camera(project, paths with { CameraPath = null }));
    }

    private void CreateSources()
    {
        File.WriteAllBytes(Path.Combine(_folder, "screen.mp4"), []);
        File.WriteAllBytes(Path.Combine(_folder, "camera.mp4"), []);
    }

    private static StudioProject Project() =>
        new()
        {
            Id = "00000000-0000-4000-8000-000000000001",
            Sources = new StudioSources
            {
                Screen = new StudioScreenSource { File = "screen.mp4", Width = 1920, Height = 1080, Duration = 10, FrameRate = 30 },
                Camera = new StudioCameraSource { File = "camera.mp4", Width = 1280, Height = 720, Duration = 10, StartOffset = 0.2 },
            },
        };

    private StudioProjectPaths Paths() =>
        new(
            "00000000-0000-4000-8000-000000000001",
            _folder,
            Path.Combine(_folder, "project.json"),
            Path.Combine(_folder, "screen.mp4"),
            Path.Combine(_folder, "camera.mp4"),
            Path.Combine(_folder, "events.json"),
            Path.Combine(_folder, "poster.jpg"));
}
