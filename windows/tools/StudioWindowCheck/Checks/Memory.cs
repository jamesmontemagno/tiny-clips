using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TinyClips.App.Views.Studio;
using TinyClips.Core.Studio.Editing;
using TinyClips.Tools.StudioWindowCheck.Automation;
using TinyClips.Tools.StudioWindowCheck.Host;
using WinRT.Interop;

namespace TinyClips.Tools.StudioWindowCheck.Checks;

// Asked for with --memory, and then the only thing that is run. It is for when the check at the
// end of a run says that a closed window is still in memory: windows are opened and closed with
// one thing done to each, and the same check then names the ones that stayed, by what was done.
// A window made here with nothing but a button and a slider tells whether a window that stays
// is the editor's doing or the framework's and the tool's.
//
// This is how it was found that every closed Studio window stayed in memory, with or without a
// preview, read through UI Automation or not, while the window with a button did not. The code
// the XAML compiler writes for a window that uses x:Bind listens to the window's Activated
// event and never stops, and its handler holds the window. The window now takes that handler
// off when it closes (StudioWindow.LetGoOfBindings).
//
// One thing that is done here is the framework's and not the editor's, and is kept apart for
// that: a window that is closed while the framework waits to show a tooltip. See ToolTipWait.
internal sealed partial class WindowChecks
{
    /// <summary>A closed window that is not a Studio window, and what it showed, held on to only weakly.</summary>
    private sealed record PlainWindow(string Way, WeakReference<object> Window, WeakReference<object> Inside);

    /// <summary>What a button's Click handler belongs to, in a window that is not a Studio window.</summary>
    private sealed class Clicked
    {
        public int Times { get; private set; }

        public void OnClick(object sender, RoutedEventArgs e) => Times++;
    }

    private void MemoryProbe()
    {
        Timeline.Mark("memory: windows opened and closed");
        var plain = new List<PlainWindow>();
        foreach (var ownTitleBar in new[] { false, true })
        {
            for (var count = 0; count < 3; count++)
            {
                plain.Add(PlainWindowClosed(ownTitleBar ? "a window with a button, a slider and a title bar of its own" : "a window with a button and a slider", ownTitleBar));
            }
        }

        // One Studio window for each thing that is done to it. What stays is named at the end of the run.
        // Where the focus is put on Play it is kept there until the button's tooltip shows: a
        // window that is closed before that is what ToolTipWait is about.
        (string What, Action<Editor> Do)[] done =
        [
            ("nothing is done to", _ => { }),
            ("a screenshot is taken of", editor => editor.Camera.Take()),
            ("plays for a second", editor =>
            {
                Invoke(editor, "StudioPlayPauseButton");
                Thread.Sleep(1000);
                Invoke(editor, "StudioPlayPauseButton");
            }),
            ("one element is looked for in", editor => Find(editor, "StudioExportButton")),
            ("every element is read of", editor => Audit(Content(editor))),
            ("a button of the inspector is pressed in", editor => Invoke(editor, "StudioZoomSectionAddButton")),
            ("a slider of the inspector is set in", editor => SetSlider(editor, "StudioPaddingSlider", 0.1)),
            ("a check box of the inspector is toggled in", editor => Find(editor, "StudioClickRingsCheckBox")?.Toggle()),
            ("a layout is chosen in", editor => Find(editor, "StudioLayoutSideBySide")?.Select()),
            ("a layout is chosen by its key in", editor => Key(editor, StudioShortcutKey.Digit3)),
            ("whether a layout is chosen is read in", editor => Selected(editor, "StudioLayoutBubble")),
            ("the focus is put on Play in and kept there until its tooltip shows", editor =>
            {
                KeyboardFocusWhereTheFocusIs(editor, "StudioPlayPauseButton");
                ToolTipShown(editor, 5);
            }),
            ("the focus is put on a slider of the inspector in", editor => FocusOn(editor, "StudioPaddingSlider")),
            ("the tab stops are gone through in", editor => TabStops(editor)),
            ("is listened to while a zoom is added", editor =>
            {
                using var heard = UiaEvents.Listen(_uia, editor.Root);
                Key(editor, StudioShortcutKey.Z);
                heard.WaitFor(0, "notification", _ => true, 1);
            }),
        ];
        foreach (var (what, act) in done)
        {
            StudioWindowClosed(what, act);
        }

        var plainButtonsOwner = ToolTipWait();

        // A project whose recording is missing has a window without a preview.
        if (Open(NewScreenProject("Memory", writeScreen: false), "a Studio window without a preview") is { } unavailable)
        {
            WaitLoaded(unavailable);
            CloseQuietly(unavailable);
        }

        // So that the window closed last is none of those that are counted.
        PlainWindowClosed("the last", ownTitleBar: false);

        // A window goes in steps: see WindowsLeftInMemory.
        Collect(5);

        foreach (var group in plain.GroupBy(window => window.Way))
        {
            _report.Note($"{group.Key}: of {group.Count()} that were closed, {group.Count(window => window.Window.TryGetTarget(out _))} are still in memory, and what {group.Count(window => window.Inside.TryGetTarget(out _))} showed");
        }

        if (plainButtonsOwner is not null)
        {
            _report.Note($"the window of nothing but a button with a tooltip and a slider that was closed while its tooltip waited: at the end, what the button's Click handler belongs to is {(plainButtonsOwner.TryGetTarget(out _) ? "still in memory" : "not in memory")}");
        }
    }

    // In a method of its own, which holds nothing of the window once it has returned.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private PlainWindow PlainWindowClosed(string way, bool ownTitleBar)
    {
        var (window, inside) = OnUi(() =>
        {
            var bar = new Border { Height = 32 };
            var panel = new StackPanel();
            panel.Children.Add(bar);
            panel.Children.Add(new Button { Content = "A button" });
            panel.Children.Add(new Slider());
            var made = new Window { Content = panel };
            if (ownTitleBar)
            {
                made.ExtendsContentIntoTitleBar = true;
                made.SetTitleBar(bar);
            }

            _services.Windows.ActivateWindow(made);
            return (made, new WeakReference<object>(panel));
        });
        var handle = OnUi(() => WindowNative.GetWindowHandle(window));
        Thread.Sleep(400);
        OnUi(window.Close);
        Until(() => !Native.Exists(handle), gone => gone, 5);
        return new PlainWindow(way, new WeakReference<object>(window), inside);
    }

    // What comes back has let go of its window: it knows it only weakly from here on.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private Editor? StudioWindowClosed(string what, Action<Editor> act, Action<Editor>? last = null)
    {
        if (OpenReady(NewCameraProject("Memory"), $"a Studio window that {what}") is not { } editor)
        {
            return null;
        }

        Thread.Sleep(300);
        act(editor);
        Thread.Sleep(300);
        last?.Invoke(editor);
        CloseQuietly(editor);
        return editor;
    }

    /// <summary>
    /// Puts the focus on a control the way a program does, which is how a Studio window puts
    /// it on Play when it opens, and a tenth of a second later the way the keyboard does. The
    /// control then has the keyboard focus where the focus was already, whether or not the
    /// window had come to put it there itself.
    /// </summary>
    private void KeyboardFocusWhereTheFocusIs(Editor editor, string automationId)
    {
        OnUi(() => { Descendant<Control>(editor.Window.Content, automationId)?.Focus(FocusState.Programmatic); });
        Thread.Sleep(100);
        OnUi(() => { Descendant<Control>(editor.Window.Content, automationId)?.Focus(FocusState.Keyboard); });
    }

    /// <summary>Waits until a tooltip shows in the window. Returns after how many milliseconds it did, or null when none came in time.</summary>
    private double? ToolTipShown(Editor editor, double seconds)
    {
        var watch = Stopwatch.StartNew();
        var shows = Until(
            () => OnUi(() => editor.Window.Content?.XamlRoot is { } root && VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Any(popup => popup.Child is ToolTip)),
            yes => yes,
            seconds);
        return shows ? watch.Elapsed.TotalMilliseconds : null;
    }

    /// <summary>
    /// Shows what the framework keeps of a window that is closed while it waits to show a
    /// tooltip, and what it lets go of again. Two notes, and no check of its own: what is
    /// still there of a Studio window at the end of the run fails the check that every run
    /// ends with. Returns what the button's Click handler belongs to in the window that is
    /// not a Studio window, for a last look at the end of the run.
    /// </summary>
    /// <remarks>
    /// The framework shows the tooltip of a control that has the keyboard focus about 0.8 s
    /// after the focus came. When a button that has the focus already, not from the keyboard,
    /// is given the keyboard focus, and its window is closed before the tooltip has come, the
    /// framework goes on holding the button, and with the button what its Click handler
    /// belongs to. Windows that are opened and closed afterwards change nothing about that,
    /// nor does time. It goes when the same is done in a later window, whose button is then
    /// the one that is held, when a later window keeps the keyboard focus on such a button
    /// until its tooltip has come, and when the tooltip is taken off the button before the
    /// window closes.
    ///
    /// None of the editor's code is needed for it, which the first window here shows: it has
    /// a button with a tooltip and a slider, and nothing else. A Studio window opens with the
    /// focus on Play, put there by the window, and the Click handler of Play belongs to the
    /// timeline's compiled bindings, which know the view model: so of a Studio window it is
    /// the view model that is left, and neither the window nor the inspector.
    /// The README has what was tried, under "A closed window that stays in memory".
    /// </remarks>
    private WeakReference<object>? ToolTipWait()
    {
        var plain = PlainWindowClosedWhileItsToolTipWaits();
        Collect(12);
        var plainFirst = plain.TryGetTarget(out _);
        // On a machine that is busy, 0.3 s can be long enough for the tooltip to come after
        // all, and then nothing is left: so whether it had come is looked at before the
        // window closes, and said.
        var cameBeforeClosing = false;
        if (StudioWindowClosed(
                "is closed 0.3 s after the keyboard focus is put on Play, where the focus is already",
                editor => KeyboardFocusWhereTheFocusIs(editor, "StudioPlayPauseButton"),
                editor => cameBeforeClosing = ToolTipShown(editor, 0) is not null) is not { } quick)
        {
            return null;
        }

        var first = LeftOf(quick);
        var plainThen = plain.TryGetTarget(out _);
        double? shownAfter = null;
        StudioWindowClosed("keeps the keyboard focus on Play until its tooltip shows, after a window that did not", editor =>
        {
            KeyboardFocusWhereTheFocusIs(editor, "StudioPlayPauseButton");
            shownAfter = ToolTipShown(editor, 5);
        });
        var then = LeftOf(quick);
        var plainLast = plain.TryGetTarget(out _);
        var kept = shownAfter is { } milliseconds ? $"until its tooltip showed, {F(milliseconds, "0")} ms after the focus" : "for five seconds, in which no tooltip showed";
        _report.Note(
            "a window of nothing but a button with a tooltip and a slider, closed 0.3 s after the keyboard focus was put on the button, where the focus was already: "
            + $"a second later what the button's Click handler belongs to was {(plainFirst ? "still in memory" : "not in memory")}; "
            + $"after a Studio window to which the same was done, it was {(plainThen ? "still in memory" : "not in memory")}; "
            + $"after a Studio window that kept the keyboard focus on Play {kept}, it was {(plainLast ? "still in memory" : "not in memory")}");
        _report.Note(
            $"a Studio window closed 0.3 s after the keyboard focus was put on Play, where the focus was already{(cameBeforeClosing ? ", and its tooltip had come by then" : string.Empty)}: "
            + $"a second later {first}; after the next window had kept the keyboard focus on Play {kept}, {then}. "
            + "This is the framework waiting to show a tooltip: see \"A closed window that stays in memory\" in the README");
        return plain;
    }

    /// <summary>
    /// Opens a window of nothing but a button with a tooltip and a slider, puts the focus on the
    /// button as a program does and then as the keyboard does, and closes the window 0.3 s
    /// later. Returns what the button's Click handler belongs to, held on to only weakly.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference<object> PlainWindowClosedWhileItsToolTipWaits()
    {
        var (window, button, clicked) = OnUi(() =>
        {
            var target = new Clicked();
            var pressed = new Button { Content = "A button" };
            pressed.Click += target.OnClick;
            ToolTipService.SetToolTip(pressed, "A tooltip");
            var panel = new StackPanel();
            panel.Children.Add(pressed);
            panel.Children.Add(new Slider());
            var made = new Window { Content = panel };
            _services.Windows.ActivateWindow(made);
            return (made, new WeakReference<object>(pressed), new WeakReference<object>(target));
        });
        var handle = OnUi(() => WindowNative.GetWindowHandle(window));
        Thread.Sleep(400);
        foreach (var state in new[] { FocusState.Programmatic, FocusState.Keyboard })
        {
            OnUi(() =>
            {
                if (button.TryGetTarget(out var pressed))
                {
                    ((Button)pressed).Focus(state);
                }
            });
            Thread.Sleep(300);
        }

        OnUi(window.Close);
        Until(() => !Native.Exists(handle), gone => gone, 5);
        return clicked;
    }

    /// <summary>Has the garbage collector run a number of times, a tenth of a second apart, with the UI thread taking a turn in between.</summary>
    private void Collect(int rounds)
    {
        for (var round = 0; round < rounds; round++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            OnUi(() => { });
            Thread.Sleep(100);
        }
    }

    /// <summary>What of a closed window is in memory after the garbage collector has run for a good second.</summary>
    private string LeftOf(Editor closed)
    {
        Collect(12);
        var left = new List<string>();
        if (closed.IsStillInMemory)
        {
            left.Add("the window");
        }

        if (closed.IsContentInMemory)
        {
            left.Add("its inspector");
        }

        if (closed.IsViewModelInMemory)
        {
            left.Add("its view model");
        }

        return left.Count == 0 ? "nothing of it was in memory" : $"{string.Join(" and ", left)} {(left.Count == 1 ? "was" : "were")} still in memory";
    }

    /// <summary>
    /// The inspector of a Studio window: a control of the app's own, which stays in memory for
    /// as long as the window's tree of controls does. UI thread.
    /// </summary>
    private static object? InspectorOf(StudioWindow window) =>
        ((window.Content as FrameworkElement)?.FindName("InspectorHost") as Border)?.Child;
}
