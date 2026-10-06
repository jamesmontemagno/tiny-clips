using System.Collections.Concurrent;
using Microsoft.UI.Xaml;
using TinyClips.App.Services.Studio;
using TinyClips.Core.Services;
using TinyClips.Core.Studio.Editing;
using TinyClips.Core.Studio.Preview;
using TinyClips.Core.Studio.Rendering;
using WinRT.Interop;

namespace TinyClips.Tools.StudioWindowCheck.Host;

/// <summary>
/// What the app registers for Studio, built by hand on one folder in the temp directory: the real
/// project store, preview factory, preview view factory, exporter, tracker, cleanup service and
/// window service, with the settings in memory and exports going into that folder. Whether
/// people can be found in a camera picture is the tool's to say. UI thread.
/// </summary>
internal sealed class ToolServices
{
    private readonly object _gate = new();
    private readonly Dictionary<nint, int> _activations = [];
    private readonly List<string> _errors = [];

    /// <param name="root">The one folder everything is written under. It has to be inside the temp directory.</param>
    public ToolServices(string root)
    {
        Root = root;
        ExportDirectory = Path.Combine(root, "exports");
        Directory.CreateDirectory(ExportDirectory);

        // Seeded so that CaptureSettings never has to ask where the Pictures and Videos folders are.
        RawSettings = new MemorySettings();
        RawSettings.Set("saveDirectoryFoldersMigrated", true);
        RawSettings.Set("useDefaultSaveDirectories", false);
        RawSettings.Set("screenshotSaveDirectory", ExportDirectory);
        RawSettings.Set("videoSaveDirectory", ExportDirectory);
        RawSettings.Set("gifSaveDirectory", ExportDirectory);

        // The app opens a Studio window only while the preview switch is on, and the cleanup that
        // follows a closed window runs only then.
        Settings = new CaptureSettings(RawSettings) { StudioPreviewEnabled = true };
        Store = new GuardedStore(root);
        Storage = new TempClipStorage(ExportDirectory);
        Tracker = new StudioProjectTracker();
        var cleanup = new StudioProjectCleanupService(Store, Settings, Tracker, new NoRecorder());

        // Three differences from the app's preview factory: forced mute, for no sound on this
        // machine; the engines' trace kept, with every open that fails, for when one does; and,
        // for a project a check names, the requests for a playback rate written down on their
        // way to the engine, which the view factory then reaches through to.
        Windows = new StudioWindowService(
            Store,
            new RecordingPreviewFactory(
                new StudioPreviewFactory(new StudioPreviewOptions { ForceMuted = true, Trace = PreviewOpens.Trace }),
                PreviewOpens,
                PlaybackRates),
            new StudioExportService(),
            new WatchedPreviewViewFactory(new StudioPreviewViewFactory()),
            Settings,
            Storage,
            Tracker,
            cleanup)
        {
            ActivateWindow = Show,

            // The app asks whether the model that finds people is next to it. No such file is
            // next to the tool, and none is put there: the tool says what the answer is.
            CanFindPeople = () => PeopleCanBeFound,
        };
        Windows.Exported += (_, e) => Exports.Enqueue(e);
        Windows.ScreenRecordingSaved += (_, e) => ScreenRecordingsSaved.Enqueue(e);
        Windows.ErrorReported += (_, e) =>
        {
            lock (_gate)
            {
                _errors.Add($"{e.Kind}: {e.Message}");
            }
        };
    }

    public string Root { get; }

    public string ExportDirectory { get; }

    public MemorySettings RawSettings { get; }

    public CaptureSettings Settings { get; }

    public GuardedStore Store { get; }

    public TempClipStorage Storage { get; }

    public StudioProjectTracker Tracker { get; }

    public StudioWindowService Windows { get; }

    /// <summary>What the previews of the windows said, and what each open that failed failed with.</summary>
    public PreviewOpenLog PreviewOpens { get; } = new();

    /// <summary>What the editors of the projects a check has watched asked their previews to play at.</summary>
    public PlaybackRateLog PlaybackRates { get; } = new();

    /// <summary>
    /// What the window service is told when it asks, for each editor it opens, whether people
    /// can be found in a camera picture. False, as in the app today, unless a check says
    /// otherwise for the windows it opens. Nothing finds people in the tool either way: a
    /// camera's background is drawn as it was recorded.
    /// </summary>
    public bool PeopleCanBeFound { get; set; }

    /// <summary>Every export the window service reported as finished.</summary>
    public ConcurrentQueue<StudioExportedEventArgs> Exports { get; } = new();

    /// <summary>Every screen recording the window service reported as saved from a project that could not be shown.</summary>
    public ConcurrentQueue<StudioExportedEventArgs> ScreenRecordingsSaved { get; } = new();

    /// <summary>What the window service reported after the window that would have shown it was gone.</summary>
    public string[] Errors()
    {
        lock (_gate)
        {
            return [.. _errors];
        }
    }

    /// <summary>
    /// Takes one report out of <see cref="Errors"/>: one that a check brought about on purpose
    /// and has looked at, so that what is left at the end of the run is what nobody asked for.
    /// </summary>
    public bool Forget(string error)
    {
        lock (_gate)
        {
            return _errors.Remove(error);
        }
    }

    /// <summary>
    /// True to tell the next windows, right after they are told that they are active, that they
    /// no longer are: the user opened the project and went on to something else.
    /// </summary>
    public bool OpensInactive { get; set; }

    /// <summary>How often the window service asked for a window to be shown or brought to the front.</summary>
    public int ActivationsOf(nint window)
    {
        lock (_gate)
        {
            return _activations.GetValueOrDefault(window);
        }
    }

    /// <summary>The windows the service has asked to be shown.</summary>
    public nint[] ShownWindows()
    {
        lock (_gate)
        {
            return [.. _activations.Keys];
        }
    }

    /// <summary>
    /// What the tool does where the app activates a window. Someone is working on this machine:
    /// a window is shown once, without activation, behind every other window and outside the
    /// taskbar and Alt+Tab. A second request for the same window, which in the app brings it to
    /// the front, is only counted.
    /// </summary>
    private void Show(Window window)
    {
        var handle = WindowNative.GetWindowHandle(window);
        lock (_gate)
        {
            var known = _activations.TryGetValue(handle, out var count);
            _activations[handle] = count + 1;
            if (known)
            {
                return;
            }
        }

        // The app sets its theme for the whole application when it starts, and that is what
        // colours a window's caption buttons. The tool shows both themes in one run, so it gives
        // each window's caption buttons the theme the settings name.
        window.AppWindow.TitleBar.PreferredTheme = Settings.Theme switch
        {
            TinyClips.Core.Models.AppTheme.Light => Microsoft.UI.Windowing.TitleBarTheme.Light,
            TinyClips.Core.Models.AppTheme.Dark => Microsoft.UI.Windowing.TitleBarTheme.Dark,
            _ => Microsoft.UI.Windowing.TitleBarTheme.UseDefaultAppMode,
        };
        window.AppWindow.IsShownInSwitchers = false;
        Native.MakeUnactivatable(handle);
        Native.SendToBack(handle);
        window.AppWindow.Show(activateWindow: false);
        Native.SendToBack(handle);

        // Where the app activates the window, the tool only tells the window so. A WinUI window
        // starts its compiled bindings when it is first activated, and the editor puts the focus
        // on a control only while it is the active window.
        Native.TellActivation(handle, isActive: true);
        if (OpensInactive)
        {
            Native.TellActivation(handle, isActive: false);
        }
    }
}
