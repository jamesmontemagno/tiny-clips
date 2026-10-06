using TinyClips.Core.Studio;

namespace TinyClips.Tools.StudioPreviewCheck.Media;

/// <summary>
/// One Studio project folder in the temp directory: project.json, events.json, screen.mp4 and,
/// when the project has a camera, camera.mp4. Deleted when disposed.
/// </summary>
internal sealed class TestFolder : IDisposable
{
    private TestFolder(StudioProject project, StudioEvents events, StudioProjectPaths paths, ClipSpec screen, ClipSpec? camera)
    {
        Project = project;
        Events = events;
        Paths = paths;
        Screen = screen;
        Camera = camera;
    }

    /// <summary>Every folder of this run is made under here, so one delete at the end leaves nothing behind.</summary>
    public static string Root { get; } = Path.Combine(Path.GetTempPath(), $"TinyClipsStudioPreviewCheck-{Environment.ProcessId}");

    /// <summary>
    /// Made to every project of the run after the change its check asks for: null but for a
    /// run that looks at all the checks with something about the project changed (<c>--people</c>).
    /// </summary>
    public static Func<StudioProject, StudioProject>? EditEvery { get; set; }

    public StudioProject Project { get; }

    public StudioEvents Events { get; }

    public StudioProjectPaths Paths { get; }

    public ClipSpec Screen { get; }

    public ClipSpec? Camera { get; }

    public double CameraOffset => Project.Sources.Camera?.StartOffset ?? 0;

    public int FrameCount => Screen.FrameCount;

    /// <summary>Start of a frame in source seconds.</summary>
    public static double TimeOf(int frame, double fraction = 0) => (frame + fraction) / TestMedia.Fps;

    /// <summary>
    /// Makes a project folder. <paramref name="camera"/> null gives a screen-only project;
    /// <paramref name="writeCamera"/> false leaves the camera file out although the project names one.
    /// <paramref name="screen"/> is the clip used as the screen recording; the usual one when null.
    /// </summary>
    public static TestFolder Create(
        string mediaDirectory,
        ClipSpec? camera,
        double cameraOffset = 0,
        Func<StudioProject, StudioProject>? edit = null,
        bool writeScreen = true,
        bool writeCamera = true,
        ClipSpec? screen = null)
    {
        var id = Guid.NewGuid().ToString("D");
        var directory = Path.Combine(Root, id);
        Directory.CreateDirectory(directory);
        screen ??= TestMedia.Screen;
        var paths = new StudioProjectPaths(
            id,
            directory,
            Path.Combine(directory, StudioProjectStore.ProjectFileName),
            Path.Combine(directory, StudioProjectStore.ScreenFileName),
            camera is null ? null : Path.Combine(directory, StudioProjectStore.CameraFileName),
            Path.Combine(directory, StudioProjectStore.EventsFileName),
            Path.Combine(directory, StudioProjectStore.PosterFileName));

        var project = new StudioProject
        {
            Id = id,
            Name = "Preview check",
            Sources = new StudioSources
            {
                Screen = new StudioScreenSource { Width = screen.Width, Height = screen.Height, FrameRate = screen.Fps, Duration = screen.DurationSeconds },
                Camera = camera is null ? null : new StudioCameraSource { Width = camera.Width, Height = camera.Height, Duration = camera.DurationSeconds, StartOffset = cameraOffset },
                Events = StudioProjectStore.EventsFileName,
            },

            // A large bubble, so the camera's strip is easy to read at any surface size the checks use.
            Scenes = [new StudioScene { Layout = StudioLayout.Bubble, Bubble = new StudioBubble { Size = 0.4 } }],
            Overlays = new StudioOverlays { Clicks = new StudioClickOverlay { Enabled = false }, Branding = false },
        };
        if (edit is not null)
        {
            project = edit(project);
        }

        if (EditEvery is { } every)
        {
            project = every(project);
        }

        var events = new StudioEvents { Capture = new StudioCaptureInfo { Width = screen.Width, Height = screen.Height, Scale = 1 } };
        File.WriteAllText(paths.ProjectJsonPath, StudioProjectJson.WriteProject(project));
        File.WriteAllText(paths.EventsPath, StudioProjectJson.WriteEvents(events));
        if (writeScreen)
        {
            File.Copy(TestMedia.PathOf(mediaDirectory, screen), paths.ScreenPath);
        }

        if (camera is not null && writeCamera)
        {
            File.Copy(TestMedia.PathOf(mediaDirectory, camera), paths.CameraPath!);
        }

        return new TestFolder(project, events, paths, screen, camera);
    }

    /// <summary>
    /// The camera frame that belongs with a screen frame, or <see cref="FrameCode.Unreadable"/>
    /// when the camera is hidden then. Worked out here from the project format, not asked of the engine.
    /// </summary>
    public int ExpectedCamera(int screenFrame, StudioProject? project = null)
    {
        project ??= Project;
        var source = project.Sources.Camera;
        if (Camera is null || source is null)
        {
            return FrameCode.Unreadable;
        }

        var layout = project.Scenes.Length == 0 ? StudioLayout.Bubble : project.Scenes[0].Layout;
        if (layout == StudioLayout.Screen)
        {
            return FrameCode.Unreadable;
        }

        // The instant a frame stands for is its middle (docs\studio-project-format.md, section 6.5).
        var cameraTime = TimeOf(screenFrame, 0.5) - source.StartOffset;
        if (cameraTime < 0 || cameraTime > source.Duration)
        {
            return FrameCode.Unreadable;
        }

        return Math.Min(Camera.FrameCount - 1, (int)Math.Floor((cameraTime * TestMedia.Fps) + 1e-6));
    }

    /// <summary>Deletes the folder. Throws when something in it is still open.</summary>
    public void Delete()
    {
        if (Directory.Exists(Paths.ProjectDirectory))
        {
            Directory.Delete(Paths.ProjectDirectory, recursive: true);
        }
    }

    public void Dispose()
    {
        try
        {
            Delete();
        }
        catch (Exception)
        {
            // Swept up with the root folder at the end of the run.
        }
    }

    /// <summary>Removes everything this run created in the temp directory.</summary>
    public static bool DeleteRoot()
    {
        for (var attempt = 0; attempt < 20 && Directory.Exists(Root); attempt++)
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception)
            {
                Thread.Sleep(100);
            }
        }

        return !Directory.Exists(Root);
    }
}
