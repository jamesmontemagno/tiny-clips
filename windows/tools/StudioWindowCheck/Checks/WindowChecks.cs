using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using TinyClips.App.Views.Studio;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;
using TinyClips.Core.Studio.Preview;
using TinyClips.Tools.StudioPreviewCheck;
using TinyClips.Tools.StudioPreviewCheck.Media;
using TinyClips.Tools.StudioWindowCheck.Automation;
using TinyClips.Tools.StudioWindowCheck.Capture;
using TinyClips.Tools.StudioWindowCheck.Host;
using WinRT.Interop;

namespace TinyClips.Tools.StudioWindowCheck.Checks;

/// <summary>
/// The checks. They run on a thread of their own: they drive the Studio windows through UI
/// Automation, read screenshots of them, and come to the UI thread only for what cannot be done
/// from outside (opening a window, and the calls that stand in for keys and for a pointer).
/// </summary>
internal sealed partial class WindowChecks
{
    public static readonly string[] Groups = ["open", "transport", "inspector", "trim", "export", "close", "windows", "accessibility", "themes"];

    private const int Fps = TestMedia.Fps;

    // The camera of the test projects starts this long after the screen, as a real camera does.
    private const double CameraOffset = 0.2;

    private readonly Report _report;
    private readonly CheckOptions _options;
    private readonly string _media;
    private readonly string _output;
    private readonly ToolServices _services;
    private readonly ForegroundWatch _foreground;
    private readonly DispatcherQueue _dispatcher;
    private readonly Action _finished;
    private readonly List<Editor> _editors = [];
    private Uia _uia = null!;
    private int _started;
    private bool _stopping;

    public WindowChecks(Report report, CheckOptions options, string mediaDirectory, string outputDirectory, ToolServices services, ForegroundWatch foreground, DispatcherQueue dispatcher, Action finished)
    {
        _report = report;
        _options = options;
        _media = mediaDirectory;
        _output = outputDirectory;
        _services = services;
        _foreground = foreground;
        _dispatcher = dispatcher;
        _finished = finished;
    }

    /// <summary>Starts the checks on their own thread. When they are done, <c>finished</c> runs on the UI thread.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        var thread = new Thread(Run) { IsBackground = true, Name = "StudioWindowCheck.Checks" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    private void Run()
    {
        try
        {
            _uia = new Uia();
            _report.Line($"foreground window at the start: \"{Native.TitleOf(Native.Foreground())}\"");
            Group("open", "1. Opening a project", Opening);
            Group("transport", "2. Transport", Transport);
            Group("inspector", "3. The inspector, with undo and redo", Inspector);
            Group("trim", "4. Trimming", Trimming);
            Group("export", "5. Exporting with the real exporter", Exporting);
            Group("close", "6. Closing", Closing);
            Group("windows", "7. More than one window", SeveralWindows);
            Group("accessibility", "8. Accessibility, as far as the UI Automation tree shows it", Accessibility);
            Group("themes", "9. Light and dark", Themes);

            // Last of all: after this the window service opens nothing.
            Group("windows", "7, at the end. The app exits while editors are open", ExitingWithWindowsOpen);
        }
        catch (Exception ex)
        {
            _report.Check("the checks ran to the end", false, ex.ToString());
        }
        finally
        {
            Teardown();
            if (!_dispatcher.TryEnqueue(() => _finished()))
            {
                Environment.Exit(_report.Finish());
            }
        }
    }

    private void Group(string name, string title, Action body)
    {
        if (!_options.Wants(name))
        {
            return;
        }

        if (_foreground.StoppedTheRun)
        {
            return;
        }

        _report.Section(title);
        Timeline.Mark(title);
        try
        {
            body();
        }
        catch (Exception ex)
        {
            _report.Check($"{name}: the checks ran to the end", false, _foreground.StoppedTheRun ? "stopped, because a window of the tool was in front" : ex.ToString());
        }

        Timeline.Mark($"closing what {name} left open");

        // Whatever a group left open is closed before the next one starts.
        foreach (var editor in _editors.Where(e => !e.IsReleased).ToArray())
        {
            CloseQuietly(editor);
        }
    }

    private void Teardown()
    {
        try
        {
            foreach (var editor in _editors.Where(e => !e.IsReleased).ToArray())
            {
                CloseQuietly(editor);
            }

            OnUi(() => _services.Windows.CloseAllForExitAsync()).Wait(TimeSpan.FromSeconds(10));
            var (deleted, failures) = _services.Store.CleanupOutcome();
            _report.Section("What followed from closing windows");
            _report.Check(
                "the cleanup that follows a closed window never failed, and deleted no project",
                failures.Length == 0 && deleted.Length == 0,
                $"it ran {_services.Store.Cleanups} time(s){(deleted.Length == 0 ? string.Empty : "; deleted " + string.Join(", ", deleted))}{(failures.Length == 0 ? string.Empty : "; " + failures[0])}");
            var errors = _services.Errors();
            _report.Check("the window service reported no error of a window that was gone", errors.Length == 0, errors.Length == 0 ? null : string.Join(" | ", errors));
            _report.Note($"{_editors.Count} Studio windows were opened and {_editors.Sum(e => e.Camera.Taken)} screenshots of them were read");
        }
        catch (Exception ex)
        {
            _report.Check("the windows could be closed at the end", false, ex.ToString());
        }
    }

    // ---------------------------------------------------------------------------------------
    // The UI thread
    // ---------------------------------------------------------------------------------------

    /// <summary>Runs something on the UI thread and waits for it.</summary>
    private T OnUi<T>(Func<T> read)
    {
        StopIfInFront();
        T result = default!;
        Exception? failure = null;
        using var done = new ManualResetEventSlim(false);
        var queued = _dispatcher.TryEnqueue(() =>
        {
            try
            {
                result = read();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                done.Set();
            }
        });
        if (!queued)
        {
            throw new InvalidOperationException("The UI thread is gone.");
        }

        if (!done.Wait(TimeSpan.FromSeconds(30)))
        {
            throw new TimeoutException("The UI thread did not answer within 30 s.");
        }

        return failure is null ? result : throw new InvalidOperationException(failure.Message, failure);
    }

    private void OnUi(Action act) => OnUi(() =>
    {
        act();
        return true;
    });

    /// <summary>Ends the checks when a window of the tool was in front. The windows are closed on the way out.</summary>
    private void StopIfInFront()
    {
        if (_foreground.StoppedTheRun && !_stopping)
        {
            _stopping = true;
            throw new InvalidOperationException("A window of the tool was the foreground window. The run stops here.");
        }
    }

    // ---------------------------------------------------------------------------------------
    // Projects and windows
    // ---------------------------------------------------------------------------------------

    /// <summary>One Studio window on one project folder, with what the checks know about it.</summary>
    private sealed class Editor(string label, TestFolder folder, StudioWindow window, nint handle, UiaElement root, WindowCamera camera)
    {
        public string Label { get; } = label;

        public TestFolder Folder { get; } = folder;

        /// <summary>The window, until the checks are done with it.</summary>
        public StudioWindow Window { get; private set; } = window;

        public nint Handle { get; } = handle;

        public UiaElement Root { get; } = root;

        public WindowCamera Camera { get; } = camera;

        public string Id => Folder.Paths.ProjectId;

        /// <summary>The project as it should be now: the one written to disk, with every edit the checks made applied by hand.</summary>
        public StudioProject Expected { get; set; } = folder.Project;

        public double Scale { get; set; } = 1;

        /// <summary>The screen position, in pixels, of the top left corner of the window's XAML content.</summary>
        public (int X, int Y) ContentOrigin { get; set; }

        /// <summary>The checks are done with the window, and its camera is disposed.</summary>
        public bool IsReleased { get; private set; }

        /// <summary>
        /// Lets go of the window. A closed window that is still referred to keeps everything it
        /// was built of alive, and the pauses of the garbage collector grow with all of that.
        /// </summary>
        public void Release()
        {
            if (!IsReleased)
            {
                IsReleased = true;
                Camera.Dispose();
                Window = null!;
            }
        }
    }

    /// <summary>A project with a screen and a camera recording, as a recording made for Studio leaves it.</summary>
    private TestFolder NewCameraProject(string name, Func<StudioProject, StudioProject>? edit = null) =>
        TestFolder.Create(_media, TestMedia.Camera, CameraOffset, p =>
        {
            // The store starts the video where the camera starts, and a recording comes with a name.
            p = p with { Name = name, Edits = new StudioEdits { TrimStart = CameraOffset } };
            return edit is null ? p : edit(p);
        });

    private TestFolder NewScreenProject(string name, Func<StudioProject, StudioProject>? edit = null, bool writeScreen = true) =>
        TestFolder.Create(_media, camera: null, edit: p =>
        {
            p = p with { Name = name };
            return edit is null ? p : edit(p);
        }, writeScreen: writeScreen);

    /// <summary>Opens the project through the window service, as the app does for a finished recording.</summary>
    private Editor Open(TestFolder folder, string label)
    {
        var window = OnUi(() => _services.Windows.Open(folder.Paths.ProjectId) as StudioWindow)
            ?? throw new InvalidOperationException($"The window service opened no Studio window for {label}.");
        var handle = OnUi(() => WindowNative.GetWindowHandle(window));
        var editor = new Editor(label, folder, window, handle, _uia.FromWindow(handle), new WindowCamera(handle));
        _editors.Add(editor);
        return editor;
    }

    /// <summary>Waits for the window to say that the project is open, or that it cannot be opened. Read from UI Automation.</summary>
    private string WaitLoaded(Editor editor, double seconds = 30)
    {
        var state = Until(
            () => editor.Root.Find("StudioPlayPauseButton") is { IsEnabled: true } ? "ready"
                : editor.Root.Find("StudioUnavailablePanel") is not null ? "unavailable"
                : "loading",
            s => s != "loading",
            seconds,
            40);
        editor.Scale = OnUi(() => editor.Window.Content?.XamlRoot?.RasterizationScale ?? 1.0);

        // The child window that holds the XAML content, as the system reports it.
        if (editor.Root.Children().FirstOrDefault(child => child.ClassName == "Microsoft.UI.Content.DesktopChildSiteBridge") is { } content)
        {
            var (x, y, _, _) = content.Bounds;
            editor.ContentOrigin = (x, y);
        }

        return state;
    }

    /// <summary>Opens a project and waits until its editor is ready. Null, with a failed check, when it is not.</summary>
    private Editor? OpenReady(TestFolder folder, string label)
    {
        var watch = Stopwatch.StartNew();
        var editor = Open(folder, label);
        var state = WaitLoaded(editor);
        if (state == "ready")
        {
            _report.Note($"{label}: the editor was ready {watch.Elapsed.TotalMilliseconds:0} ms after the window service was asked to open it");
            return editor;
        }

        _report.Check($"{label}: the editor becomes ready", false, $"the window is {state}: {editor.Root.Find("StudioUnavailableMessage")?.Name}");
        return null;
    }

    /// <summary>Closes a window the way the app does when it exits: without a question, with the edits saved.</summary>
    private bool CloseQuietly(Editor editor, double seconds = 15)
    {
        if (editor.IsReleased)
        {
            return true;
        }

        var ended = true;
        if (Native.Exists(editor.Handle))
        {
            try
            {
                ended = OnUi(() => editor.Window.CloseForExit()).Wait(TimeSpan.FromSeconds(seconds));
            }
            catch (Exception)
            {
                ended = false;
            }
        }

        Release(editor);
        return ended && Until(() => !Native.Exists(editor.Handle), gone => gone, 5);
    }

    /// <summary>The window is gone or about to be: no more screenshots of it.</summary>
    private static void Release(Editor editor) => editor.Release();

    private StudioPreviewEngine? EngineOf(Editor editor) => OnUi(() => editor.Window.ViewModel.Preview as StudioPreviewEngine);

    // ---------------------------------------------------------------------------------------
    // Waiting
    // ---------------------------------------------------------------------------------------

    /// <summary>Reads until the value is acceptable or the time is up, and returns the last value read.</summary>
    private static T Until<T>(Func<T> read, Func<T, bool> accept, double seconds = 3, int everyMilliseconds = 25)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var value = read();
            if (accept(value) || watch.Elapsed.TotalSeconds >= seconds)
            {
                return value;
            }

            Thread.Sleep(everyMilliseconds);
        }
    }

    /// <summary>An element by automation id, waited for briefly because the window builds its tree a moment after a change.</summary>
    private static UiaElement? Find(Editor editor, string automationId, double seconds = 2) =>
        Until(() => editor.Root.Find(automationId), found => found is not null, seconds);

    /// <summary>True once no element with the automation id is in the tree.</summary>
    private static bool Gone(Editor editor, string automationId, double seconds = 3) =>
        Until(() => editor.Root.Find(automationId) is null, gone => gone, seconds);

    // ---------------------------------------------------------------------------------------
    // The picture
    // ---------------------------------------------------------------------------------------

    /// <summary>A rectangle of a screenshot, in pixels.</summary>
    private readonly record struct Box(int X, int Y, int Width, int Height)
    {
        public int Right => X + Width;

        public int Bottom => Y + Height;

        public override string ToString() => $"{Width}x{Height} at ({X},{Y})";
    }

    /// <summary>What a screenshot shows: the screenshot, where the canvas is in it, and which frame of each clip is on it.</summary>
    private sealed record Sight(Shot Shot, Box Canvas, SceneView View, Shown Shown);

    /// <summary>
    /// A rectangle of the window's XAML content, given in effective pixels, as pixels of a
    /// screenshot.
    /// </summary>
    private static StudioFrameRect InShot(Editor editor, Shot shot, Windows.Foundation.Rect rect) => new(
        editor.ContentOrigin.X - shot.ScreenX + (rect.X * editor.Scale),
        editor.ContentOrigin.Y - shot.ScreenY + (rect.Y * editor.Scale),
        rect.Width * editor.Scale,
        rect.Height * editor.Scale);

    /// <summary>
    /// Where the window has laid the canvas out, in pixels of a screenshot: the rectangle the
    /// preview and the overlay are arranged into. That the picture is there, and nowhere else, is
    /// what the checks then read from the pixels.
    /// </summary>
    private Box? CanvasBox(Editor editor, Shot shot)
    {
        var arranged = OnUi<Windows.Foundation.Rect?>(() =>
        {
            if (Descendant<TinyClips.App.Controls.Studio.StudioCanvasHost>(editor.Window.Content, "StudioPreview") is not { } host || !host.IsLoaded)
            {
                return null;
            }

            return host.TransformToVisual(editor.Window.Content).TransformBounds(host.CanvasRect);
        });
        if (arranged is not { } rect)
        {
            return null;
        }

        var box = InShot(editor, shot, rect);
        return new Box((int)Math.Round(box.X), (int)Math.Round(box.Y), (int)Math.Round(box.Width), (int)Math.Round(box.Height));
    }

    /// <summary>Takes a screenshot and reads the frame numbers where the given project's layout puts the clips.</summary>
    private Sight? Look(Editor editor, StudioProject? project = null)
    {
        project ??= editor.Expected;
        if (editor.Camera.Take() is not { } shot || CanvasBox(editor, shot) is not { } canvas || canvas.Width < 8 || canvas.Height < 8)
        {
            return null;
        }

        var view = SceneView.Resolve(project, editor.Folder.Screen, editor.Folder.Camera, canvas.Width, canvas.Height).Offset(canvas.X, canvas.Y);
        return new Sight(shot, canvas, view, Shown.Read(shot.Bgra, shot.Width, shot.Height, view, editor.Folder.Screen, editor.Folder.Camera));
    }

    /// <summary>Looks until the picture is what is wanted, or the time is up. The last look is returned either way.</summary>
    private Sight? LookFor(Editor editor, Func<Sight, bool> wanted, double seconds = 3, StudioProject? project = null) =>
        Until(() => Look(editor, project), sight => sight is not null && wanted(sight), seconds, 30);

    /// <summary>Both clips on the frame a screen frame calls for: the screen frame itself, and the camera frame that belongs with it.</summary>
    private static Shown Both(Editor editor, int screenFrame, StudioProject? project = null)
    {
        // The camera layout hides the screen recording.
        project ??= editor.Expected;
        var screenHidden = project.Sources.Camera is not null && project.Scenes[0].Layout == StudioLayout.Camera;
        return new Shown(screenHidden ? FrameCode.Unreadable : screenFrame, editor.Folder.ExpectedCamera(screenFrame, project));
    }

    /// <summary>The frame a source time lies in.</summary>
    private static int FrameOf(double seconds) => (int)Math.Floor((seconds * Fps) + 1e-6);

    /// <summary>The middle of a frame, as a source time. Asking for the middle leaves no doubt about which frame is meant.</summary>
    private static double MiddleOf(int frame) => (frame + 0.5) / Fps;

    private static string F(double value, string format = "0.###") => value.ToString(format, CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------------------------------
    // The tree
    // ---------------------------------------------------------------------------------------

    private static List<(int Depth, UiaElement Element)> Tree(UiaElement root, bool raw = false)
    {
        var result = new List<(int, UiaElement)>();
        Walk(root, 0);
        return result;

        void Walk(UiaElement element, int depth)
        {
            result.Add((depth, element));
            if (depth < 60)
            {
                foreach (var child in element.Children(raw))
                {
                    Walk(child, depth + 1);
                }
            }
        }
    }

    private void SaveTree(string fileName, List<(int Depth, UiaElement Element)> tree)
    {
        var path = Path.Combine(_output, fileName);
        Directory.CreateDirectory(_output);
        using var writer = new StreamWriter(path, append: false);
        foreach (var (depth, element) in tree)
        {
            var (x, y, width, height) = element.Bounds;
            var range = element.Range is { } r ? $" range {F(r.Value, "0.####")} in {F(r.Minimum, "0.####")}..{F(r.Maximum, "0.####")}" : string.Empty;
            var value = element.ValueText is { } text ? $" value \"{text}\"" : string.Empty;
            var help = element.HelpText is { Length: > 0 } h ? $" help \"{h}\"" : string.Empty;
            writer.WriteLine($"{new string(' ', depth * 2)}{element.ControlTypeName} \"{element.Name}\" [{element.Id}] class {element.ClassName}{(element.IsEnabled ? string.Empty : " disabled")}{(element.IsOffscreen ? " offscreen" : string.Empty)}{(element.IsKeyboardFocusable ? " focusable" : string.Empty)} patterns({element.Patterns}){range}{value}{help} {width}x{height} at ({x},{y})");
        }
    }
}
