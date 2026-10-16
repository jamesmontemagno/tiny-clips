using TinyClips.App.Models.Studio;
using TinyClips.Core.Studio;
using TinyClips.Tools.StudioPreviewCheck.Media;
using TinyClips.Tools.StudioWindowCheck.Host;

namespace TinyClips.Tools.StudioWindowCheck.Checks;

// 7. More than one window: two projects open at once, and a project that is already open.
internal sealed partial class WindowChecks
{
    private void SeveralWindows()
    {
        Timeline.Mark("7: two windows");
        var folderA = NewCameraProject("Window A");
        var folderB = NewScreenProject("Window B");
        if (OpenReady(folderA, "window A") is not { } a || OpenReady(folderB, "window B") is not { } b)
        {
            return;
        }

        // Each on a frame of its own.
        const int FrameA = 100;
        const int FrameB = 200;
        SetSlider(a, "StudioPlayhead", MiddleOf(FrameA));
        SetSlider(b, "StudioPlayhead", MiddleOf(FrameB));
        var sightA = LookFor(a, s => s.Shown == Both(a, FrameA), 5);
        var sightB = LookFor(b, s => s.Shown == Both(b, FrameB), 5);
        var visible = Native.VisibleWindowsOfThisProcess();
        _report.Check(
            "two projects are open in two windows at once, each with its own name, its own recording and its own playhead",
            a.Handle != b.Handle && visible.Contains(a.Handle) && visible.Contains(b.Handle)
                && NameOf(a, "StudioClipName") == "Window A" && NameOf(b, "StudioClipName") == "Window B"
                && sightA?.Shown == Both(a, FrameA) && sightB?.Shown == Both(b, FrameB)
                && a.Root.Find("StudioLayoutChoice") is not null && b.Root.Find("StudioNoCameraNote") is not null
                && _services.Tracker.IsOpen(a.Id) && _services.Tracker.IsOpen(b.Id),
            $"\"{NameOf(a, "StudioClipName")}\" shows {sightA?.Shown}; \"{NameOf(b, "StudioClipName")}\" shows {sightB?.Shown}");
        if (sightA is null || sightB is null)
        {
            return;
        }

        // An edit in one window is not an edit in the other.
        Timeline.Mark("7: an edit in one of two windows");
        var lemon = StudioSwatch.Find("lemon");
        var edited = Find(a, "StudioSwatch_lemon")?.Select() ?? false;
        Expect(a, p => p with { Canvas = p.Canvas with { Background = new StudioBackground { Style = StudioBackgroundStyle.Solid, Preset = "lemon", Primary = lemon?.PrimaryHex ?? string.Empty, Secondary = null } } });
        var editedA = LookFor(a, s => Corners(s).All(c => Near(c, Lemon)) && s.Shown == Both(a, FrameA), 3);
        var savedA = Until(() => _services.Store.Load(a.Id).Canvas.Background.Preset, preset => preset == "lemon", 3, 50);
        var otherB = Look(b);
        var changedInB = otherB is null ? -1 : DifferenceCount(sightB.Shot, otherB.Shot, sightB.Canvas, 12);
        var undoB = Find(b, "StudioUndoButton", 0.5);
        _report.Check(
            "a background chosen in one window changes that window and its project only: the other window's picture, its Undo and its project file stay as they were",
            edited && editedA is not null && Corners(editedA).All(c => Near(c, Lemon)) && savedA == "lemon"
                && changedInB == 0 && undoB is { IsEnabled: false } && Find(a, "StudioUndoButton", 0.5) is { IsEnabled: true }
                && _services.Store.Load(b.Id).Canvas.Background.Preset == "ocean",
            $"window A's corners {(editedA is null ? "?" : CornerText(editedA))}, its file says {savedA}; in window B {changedInB} pixels of the picture changed, Undo enabled {undoB?.IsEnabled}, its file says {_services.Store.Load(b.Id).Canvas.Background.Preset}");

        // One plays, the other rests.
        Timeline.Mark("7: one of two windows plays");
        Invoke(a, "StudioPlayPauseButton");
        Thread.Sleep(500);
        var restingB = Look(b);
        var playsB = NameOf(b, "StudioPlayPauseButton", 0);
        Invoke(a, "StudioPlayPauseButton");
        Until(() => NameOf(a, "StudioPlayPauseButton", 0), name => name == "Play", 2);
        var movedA = FrameOf(Playhead(a));
        var afterA = LookFor(a, s => s.Shown == Both(a, FrameOf(Playhead(a))), 3);
        _report.Check(
            "playing in one window moves that window only: the other stays on its frame and still offers Play",
            movedA > FrameA && afterA?.Shown == Both(a, FrameOf(Playhead(a))) && restingB?.Shown == Both(b, FrameB) && playsB == "Play" && FrameOf(Playhead(b)) == FrameB,
            $"window A played from frame {FrameA} to frame {movedA} and shows {afterA?.Shown}; window B shows {restingB?.Shown}, playhead on frame {FrameOf(Playhead(b))}");

        // Opening a project that is already open.
        Timeline.Mark("7: opening a project that is already open");
        var asked = _services.ActivationsOf(a.Handle);
        var windowsBefore = Native.VisibleWindowsOfThisProcess().Count;
        var again = OnUi(() => _services.Windows.Open(a.Id));
        var same = ReferenceEquals(again, a.Window);
        var askedNow = _services.ActivationsOf(a.Handle);
        Thread.Sleep(300);
        var windowsAfter = Native.VisibleWindowsOfThisProcess().Count;
        var untouched = LookFor(a, s => s.Shown == Both(a, FrameOf(Playhead(a))), 2);
        _report.Check(
            "opening a project that is already open opens no second window: the window service returns the window that has it and asks for that one to come forward, and the window keeps its state",
            same && askedNow == asked + 1 && windowsAfter == windowsBefore && FrameOf(Playhead(a)) == movedA && untouched?.Shown == Both(a, movedA) && Find(a, "StudioUndoButton", 0.5) is { IsEnabled: true },
            $"the same window: {same}; asked to come forward {askedNow - asked} time(s) (the tool counts this where the app activates the window); windows of the tool before and after: {windowsBefore} and {windowsAfter}; playhead still on frame {FrameOf(Playhead(a))}");

        // One closes, the other goes on.
        Timeline.Mark("7: one of two windows closes");
        var closed = CloseQuietly(a);
        var free = Until(() => !_services.Tracker.IsOpen(a.Id), ok => ok, 10, 50);
        Invoke(b, "StudioNextFrameButton");
        var stepped = LookFor(b, s => s.Shown == Both(b, FrameB + 1), 3);
        _report.Check(
            "closing one of the two leaves the other working, and only the closed project stops counting as open",
            closed && free && _services.Tracker.IsOpen(b.Id) && stepped?.Shown == Both(b, FrameB + 1) && Describe(_services.Store.Load(a.Id)) == Describe(a.Expected),
            $"window A closed {closed}, its project no longer open: {free}; window B stepped to {stepped?.Shown}");
        CloseQuietly(b);
    }

    /// <summary>
    /// Last of all, because the window service opens nothing afterwards: the app exits while two
    /// editors are open. Both close without a question, and the edits of both are on disk.
    /// </summary>
    private void ExitingWithWindowsOpen()
    {
        Timeline.Mark("7: the app exits with two windows open");
        var folderA = NewCameraProject("Exit A");
        var folderB = NewScreenProject("Exit B");
        if (OpenReady(folderA, "exit A") is not { } a || OpenReady(folderB, "exit B") is not { } b)
        {
            return;
        }

        // An edit in each, made right before the exit: nothing has been saved yet.
        var edited = SetSlider(a, "StudioPaddingSlider", 0.2) & SetSlider(b, "StudioPaddingSlider", 0.3);
        Expect(a, p => p with { Canvas = p.Canvas with { Padding = 0.2 } });
        Expect(b, p => p with { Canvas = p.Canvas with { Padding = 0.3 } });
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var exit = OnUi(() => _services.Windows.CloseAllForExitAsync());
        var savedA = Describe(_services.Store.Load(a.Id));
        var savedB = Describe(_services.Store.Load(b.Id));
        var ended = exit.Wait(TimeSpan.FromSeconds(10));
        var took = watch.Elapsed.TotalMilliseconds;
        var gone = WindowGone(a, 5) & WindowGone(b, 5);
        var asked = !gone && (Native.Exists(a.Handle) && HasDialog(a) || Native.Exists(b.Handle) && HasDialog(b));
        Release(a);
        Release(b);
        var refused = OnUi(() => _services.Windows.Open(a.Id)) is null;
        _report.Check(
            "when the app exits, every editor closes without a question, the edits of each are in its project file by the time the app is told so, and no project is opened after that",
            edited && ended && gone && !asked && savedA == Describe(a.Expected) && savedB == Describe(b.Expected) && refused
                && File.Exists(folderA.Paths.ScreenPath) && File.Exists(folderB.Paths.ScreenPath),
            $"both windows gone: {gone}, {F(took, "0")} ms after the app asked; edits saved: {savedA == Describe(a.Expected)} and {savedB == Describe(b.Expected)}; a project opened afterwards: {!refused}");
    }
}
