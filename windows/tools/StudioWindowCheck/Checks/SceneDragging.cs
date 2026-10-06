using System.ComponentModel;
using Microsoft.UI.Dispatching;
using TinyClips.App.ViewModels.Studio;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;
using TinyClips.Core.Studio.Preview;
using TinyClips.Tools.StudioPreviewCheck.Media;
using TinyClips.Tools.StudioWindowCheck.Host;

namespace TinyClips.Tools.StudioWindowCheck.Checks;

// 12, continued. Something dragged while the preview plays. A drag is many changes to the scene
// the playhead is in, and while the recording plays the playhead may come into the next scene
// before the drag is over. So the first such change of a drag stops the preview where the
// picture is, and the whole drag stays in one scene. Undo in the middle of a drag takes back
// what the drag did so far and leaves the rest of it one step. And the keys that change the
// project wait until a drag is over.
//
// No pointer is held here: nothing sends input on this machine. A drag is made the way the
// overlay's handle and a slider's row make it, by the calls their pointer handlers make.
internal sealed partial class WindowChecks
{
    /// <summary>
    /// Follows a window's playhead without asking the window for it: the view model says when
    /// it has moved, on the UI thread, and the newest place is kept where any thread can read
    /// it. It can also do one thing, on the UI thread, as soon as the playhead is reported in
    /// a frame: not from inside the view model's notice, but right after it, as an input event
    /// that arrives then is handled.
    /// </summary>
    private sealed class PlayheadWatch : IDisposable
    {
        private readonly WindowChecks _checks;
        private readonly StudioViewModel _viewModel;
        private readonly PropertyChangedEventHandler _handler;
        private double _playhead;
        private int _frame = int.MaxValue;
        private Action? _then;

        public PlayheadWatch(WindowChecks checks, StudioViewModel viewModel)
        {
            _checks = checks;
            _viewModel = viewModel;
            _handler = OnRaised;
            checks.OnUi(() =>
            {
                _playhead = viewModel.Playhead;
                viewModel.PropertyChanged += _handler;
            });
        }

        /// <summary>Where the playhead was last reported, in source time.</summary>
        public double Now => Volatile.Read(ref _playhead);

        /// <summary>What to do, once, when the playhead is reported in a frame or past it. Call it on the UI thread.</summary>
        public void WhenInFrame(int frame, Action then)
        {
            _frame = frame;
            _then = then;
        }

        public void Dispose() => _checks.OnUi(() => _viewModel.PropertyChanged -= _handler);

        private void OnRaised(object? sender, PropertyChangedEventArgs e)
        {
            // The playhead's own name is the last the view model raises for a frame; no name at all is everything.
            if (e.PropertyName is { Length: > 0 } name && name != nameof(StudioViewModel.PlayheadText))
            {
                return;
            }

            var playhead = _viewModel.Playhead;
            Volatile.Write(ref _playhead, playhead);
            if (_then is { } then && FrameOf(playhead) >= _frame)
            {
                _then = null;
                _checks._dispatcher.TryEnqueue(DispatcherQueuePriority.High, () => then());
            }
        }
    }

    /// <summary>What came of changes that were made while the preview played.</summary>
    /// <param name="WasPlaying">Whether the editor was playing when the first change was made.</param>
    /// <param name="HeadBefore">The playhead just before the first change.</param>
    /// <param name="PlayingAfterFirst">Whether it was still playing right after the first change.</param>
    /// <param name="HeadAfterFirst">The playhead right after the first change.</param>
    /// <param name="PlayingAtEnd">Whether it was still playing after the last change.</param>
    /// <param name="HeadAtEnd">The playhead after the last change.</param>
    /// <param name="Before">The scenes before the first change.</param>
    /// <param name="After">The scenes after the last.</param>
    private sealed record DragOutcome(bool WasPlaying, double HeadBefore, bool PlayingAfterFirst, double HeadAfterFirst, bool PlayingAtEnd, double HeadAtEnd, StudioScene[] Before, StudioScene[] After);

    /// <summary>The scene a source time lies in: the last one that has started by then.</summary>
    private static int SceneAt(IReadOnlyList<StudioScene> scenes, double time)
    {
        var at = 0;
        for (var index = 0; index < scenes.Count; index++)
        {
            if (scenes[index].Start <= time + 1e-9)
            {
                at = index;
            }
        }

        return at;
    }

    /// <summary>Which scenes are not as they were. One entry of -1 when their number changed.</summary>
    private static int[] ChangedScenes(StudioScene[] before, StudioScene[] after) =>
        before.Length != after.Length ? [-1] : [.. Enumerable.Range(0, before.Length).Where(index => Describe(before[index]) != Describe(after[index]))];

    /// <summary>
    /// Whether a picture shows a frame of a project with scenes, by the frame strips: the
    /// screen's reads the frame where that frame's layout has the screen, and the camera's the
    /// frame that goes with it where that layout has the camera. The edges of the test picture
    /// are not asked here. These checks do not choose the frame the preview stops on, and at
    /// some frames what moves in the test clip lies on an edge.
    /// </summary>
    private static bool StripsShow(LayoutSight? sight) =>
        sight is not null
        && (sight.Reading.Screen is { StripIsInside: true } || sight.Reading.Camera is { StripIsInside: true })
        && (sight.Reading.Screen is not { StripIsInside: true } screen || screen.Strip == sight.Reading.Frame)
        && (sight.Reading.Camera is not { StripIsInside: true } camera || camera.Strip == sight.Reading.CameraFrame);

    /// <summary>What the frame strips of a picture read, in words.</summary>
    private static string StripText(LayoutSight? sight)
    {
        if (sight is null)
        {
            return "no screenshot";
        }

        static string Read(PartReading? part, int wanted) => part switch
        {
            null => "not in the layout, or not read",
            { StripIsInside: false } => "its strip is not in the picture",
            { Strip: FrameCode.Unreadable } => $"does not read (frame {wanted} is wanted)",
            _ => part.Strip == wanted ? $"reads frame {part.Strip}" : $"reads frame {part.Strip}, and frame {wanted} is wanted",
        };
        return $"the screen {Read(sight.Reading.Screen, sight.Reading.Frame)}, the camera {Read(sight.Reading.Camera, sight.Reading.CameraFrame)}";
    }

    /// <summary>Looks until both frame strips read what a frame calls for in a project's layout of it, or the time is up.</summary>
    private LayoutSight? LookForStrips(Editor editor, int frame, StudioProject project, double seconds = 3) =>
        Until(() => LookAtLayout(editor, frame, project), StripsShow, seconds, 30);

    /// <summary>Where a scene has the camera on the canvas of the video, at rest.</summary>
    private static StudioFrameRect? CameraOnTheVideo(StudioProject project, StudioScene scene)
    {
        var size = StudioExportLimits.GetExportSize(project);

        // The scene alone and from the start, so that nothing moves into it; a second in, when the camera has begun.
        var alone = project with { Scenes = [scene with { Start = 0, Transition = new StudioTransition() }] };
        return StudioLayoutResolver.Resolve(alone, 1, size.Width, size.Height).Camera?.Rect;
    }

    /// <summary>Null when a scene has the camera's top left corner at a place on the canvas of the video, otherwise where it has it.</summary>
    private static string? CameraIsAt(StudioProject project, StudioScene scene, double x, double y) =>
        CameraOnTheVideo(project, scene) is { } rect
            ? Math.Abs(rect.X - x) <= 0.5 && Math.Abs(rect.Y - y) <= 0.5 ? null : $"the camera is in {R(rect)} on the video, and was moved to ({F(x)},{F(y)})"
            : "the scene has no camera";

    /// <summary>
    /// Plays from one place, and when the playhead has come to another makes changes one after
    /// the other, a frame's time or two apart as the moves of a pointer come, inside a drag or
    /// without one. Null when the preview did not play that far.
    /// </summary>
    /// <param name="hold">
    /// How long the UI thread is kept busy right before the first change, in milliseconds, as
    /// it is on a busy PC. The preview shows two frames more in 70 ms, and the editor, which
    /// hears of each frame on that thread, still holds the playhead of the frame before them.
    /// </param>
    private DragOutcome? ChangeWhilePlaying(Editor editor, double from, double at, bool inGesture, int hold, params Action<StudioViewModel>[] changes)
    {
        var viewModel = OnUi(() => editor.Window.ViewModel);

        // From rest, whatever the check before this one left.
        OnUi(() =>
        {
            if (viewModel.IsInGesture)
            {
                viewModel.EndGesture();
            }

            if (viewModel.IsPlaying)
            {
                viewModel.Pause();
            }
        });
        SetSlider(editor, "StudioPlayhead", from);
        Until(() => Playhead(editor), value => Math.Abs(value - from) < 0.5 / Fps, 2);
        var before = ScenesOf(editor);
        using var watch = new PlayheadWatch(this, viewModel);
        Invoke(editor, "StudioPlayPauseButton");
        if (Until(() => watch.Now, value => value >= at, 6, 5) < at)
        {
            if (OnUi(() => viewModel.IsPlaying))
            {
                Invoke(editor, "StudioPlayPauseButton");
            }

            return null;
        }

        var (wasPlaying, headBefore, playingAfterFirst, headAfterFirst) = OnUi(() =>
        {
            if (hold > 0)
            {
                Thread.Sleep(hold);
            }

            var was = viewModel.IsPlaying;
            var head = viewModel.Playhead;
            if (inGesture)
            {
                viewModel.BeginGesture();
            }

            changes[0](viewModel);
            return (was, head, viewModel.IsPlaying, viewModel.Playhead);
        });
        foreach (var change in changes.Skip(1))
        {
            Thread.Sleep(40);
            OnUi(() => change(viewModel));
        }

        Thread.Sleep(40);
        var (playingAtEnd, headAtEnd) = OnUi(() =>
        {
            if (inGesture)
            {
                viewModel.EndGesture();
            }

            return (viewModel.IsPlaying, viewModel.Playhead);
        });
        return new DragOutcome(wasPlaying, headBefore, playingAfterFirst, headAfterFirst, playingAtEnd, headAtEnd, before, ScenesOf(editor));
    }

    /// <summary>Pauses a preview that is playing, by its button, and waits until it has.</summary>
    private bool PauseIfPlaying(Editor editor, StudioPreviewEngine? engine)
    {
        if (NameOf(editor, "StudioPlayPauseButton", 0) == "Pause")
        {
            Invoke(editor, "StudioPlayPauseButton");
        }

        var paused = Until(() => NameOf(editor, "StudioPlayPauseButton", 0), name => name == "Play", 2) == "Play";
        engine?.WaitForIdle(TimeSpan.FromSeconds(5));
        return paused;
    }

    private void DragsWhileThePreviewPlays()
    {
        DragsInThreeScenes();
        DragsInASideBySideScene();
        DragsAndKeysInOneScene();
    }

    /// <summary>
    /// One check that a drag which begins while the preview plays stops it: the editor is paused
    /// after the first change, on the frame the picture stays on; what the drag changed is the
    /// scene of that frame and no other; and one Undo takes the whole drag back.
    /// </summary>
    /// <param name="wanted">Given the scene before and after: null when it changed as the drag asked, otherwise what is wrong.</param>
    private void DragStops(Editor editor, StudioPreviewEngine? engine, string name, double from, double at, Func<StudioScene, StudioScene, string?> wanted, params Action<StudioViewModel>[] changes)
    {
        Timeline.Mark($"12: {name}");
        var project = editor.Expected;
        var undoWas = Find(editor, "StudioUndoButton", 0.5)?.IsEnabled;
        if (ChangeWhilePlaying(editor, from, at, inGesture: true, hold: 70, changes) is not { } drag)
        {
            _report.Check(name, false, $"the preview did not play from {Seconds(from)} s to {Seconds(at)} s");
            return;
        }

        var button = Until(() => NameOf(editor, "StudioPlayPauseButton", 0), text => text == "Play", 2);
        engine?.WaitForIdle(TimeSpan.FromSeconds(5));
        var head = Playhead(editor);
        var frame = FrameOf(head);
        var scene = SceneAt(drag.Before, head);
        var changed = ChangedScenes(drag.Before, drag.After);
        var onlyThatScene = changed.Length == 1 && changed[0] == scene;
        var problem = onlyThatScene ? wanted(drag.Before[scene], drag.After[scene]) : null;

        // The picture, in the layout the scenes now give the frame the playhead is on.
        var parked = LookForStrips(editor, frame, project with { Scenes = drag.After });

        var undoPressed = Invoke(editor, "StudioUndoButton");
        var undone = Until(() => Describe(ScenesOf(editor)), text => text == Describe(drag.Before), 1.5);
        var undoIs = Until(() => Find(editor, "StudioUndoButton", 0)?.IsEnabled, enabled => enabled == undoWas, 1);
        _report.Check(
            name,
            drag.WasPlaying && !drag.PlayingAfterFirst && !drag.PlayingAtEnd && button == "Play"
                && FrameOf(drag.HeadAfterFirst) == frame && FrameOf(drag.HeadAtEnd) == frame && StripsShow(parked)
                && onlyThatScene && problem is null && undoPressed && undone == Describe(drag.Before) && undoIs == undoWas,
            $"the first change was made with the editor's thread held up for 70 ms before it and its playhead at {Seconds(drag.HeadBefore)} s, frame {FrameOf(drag.HeadBefore)}, playing: {drag.WasPlaying}; "
                + $"right after it the editor was {(drag.PlayingAfterFirst ? "still playing" : "paused")} at {Seconds(drag.HeadAfterFirst)} s, frame {FrameOf(drag.HeadAfterFirst)}, "
                + $"and after the last {(drag.PlayingAtEnd ? "still playing" : "paused")} at {Seconds(drag.HeadAtEnd)} s; the button offers \"{button}\"; with the playhead at {Seconds(head)} s, frame {frame}, in scene {scene + 1}, the picture: {StripText(parked)}; "
                + $"{(changed.Length == 0 ? "no scene changed" : changed[0] < 0 ? "the number of scenes changed" : "changed: scene " + string.Join(" and ", changed.Select(index => index + 1)))}"
                + $"{(onlyThatScene ? $", from {Describe(drag.Before[scene])} to {Describe(drag.After[scene])}" : string.Empty)}{(problem is null ? string.Empty : "; " + problem)}; "
                + $"after one Undo {(undone == Describe(drag.Before) ? "every scene is as it was before the drag" : "the scenes are " + undone)}, and Undo is {(undoIs == true ? "enabled" : "disabled")}, as before the drag: {undoIs == undoWas}");
    }

    /// <summary>
    /// One check that changes leave the preview playing: it plays on through them and after
    /// them, and it takes as many Undo as there were steps to take them back.
    /// </summary>
    /// <param name="read">What the changes were made to, as one line, read from the editor.</param>
    private void PlaysOn(Editor editor, StudioPreviewEngine? engine, string name, double from, double at, bool inGesture, int steps, Func<string> read, params Action<StudioViewModel>[] changes)
    {
        Timeline.Mark($"12: {name}");
        var before = read();
        if (ChangeWhilePlaying(editor, from, at, inGesture, hold: 0, changes) is not { } drag)
        {
            _report.Check(name, false, $"the preview did not play from {Seconds(from)} s to {Seconds(at)} s");
            return;
        }

        // A third of a second on, it has to be further along.
        var later = Until(() => OnUi(() => editor.Window.ViewModel.Playhead), value => value >= drag.HeadAtEnd + 0.2, 2, 10);
        var stillPlaying = OnUi(() => editor.Window.ViewModel.IsPlaying);
        var paused = PauseIfPlaying(editor, engine);
        var after = read();

        // Back, one Undo at a time.
        var back = new List<string>();
        for (var step = 0; step < steps; step++)
        {
            var was = read();
            Invoke(editor, "StudioUndoButton");
            back.Add(Until(read, text => text != was, 1.5));
        }

        _report.Check(
            name,
            drag.WasPlaying && drag.PlayingAfterFirst && drag.PlayingAtEnd && stillPlaying && later >= drag.HeadAtEnd + 0.2 && paused
                && after != before && back.Count == steps && back[^1] == before && back.Take(steps - 1).All(text => text != before),
            $"playing when the first change was made at {Seconds(drag.HeadBefore)} s: {drag.WasPlaying}; still playing right after it: {drag.PlayingAfterFirst}, and after the last, at {Seconds(drag.HeadAtEnd)} s: {drag.PlayingAtEnd}; "
                + $"the playhead went on to {Seconds(later)} s; it went from {before} to {after}; after {steps} Undo: {(back.Count > 0 && back[^1] == before ? "as before" : back.Count > 0 ? back[^1] : "nothing was undone")}"
                + $"{(steps > 1 ? $", and not before the last of them: {back.Take(steps - 1).All(text => text != before)}" : string.Empty)}");
    }

    /// <summary>A recording with a camera and three scenes, each with its bubble at a place and of a size of its own.</summary>
    private TestFolder NewThreeBubbles(string name) => NewCameraProject(name, p =>
    {
        p = OnLemon(p);
        var first = p.Scenes[0] with { Bubble = new StudioBubble { Anchor = StudioAnchor.BottomRight, Size = 0.3, OffsetX = -0.02, OffsetY = -0.03 } };
        return p with
        {
            Scenes =
            [
                first,
                first with { Start = 4, Bubble = new StudioBubble { Anchor = StudioAnchor.TopLeft, Size = 0.36, OffsetX = 0.04, OffsetY = 0.05 }, Transition = new StudioTransition { Kind = StudioTransitionKind.Morph } },
                first with { Start = 8, Bubble = new StudioBubble { Anchor = StudioAnchor.BottomLeft, Size = 0.26, OffsetX = 0.06, OffsetY = -0.07 }, Transition = new StudioTransition { Kind = StudioTransitionKind.Cut } },
            ],
        };
    });

    private void DragsInThreeScenes()
    {
        Timeline.Mark("12: drags while the preview plays, in a recording with three scenes");
        var folder = NewThreeBubbles("Drags while it plays");
        if (OpenReady(folder, "drags while it plays") is not { } editor)
        {
            return;
        }

        var engine = EngineOf(editor);
        var project = editor.Expected;
        var rest = LookForStrips(editor, FrameOf(CameraOffset), project, 5);
        if (!_report.Check(
            "a recording with three scenes, each with its camera at a place and of a size of its own, opens on its first frame",
            StripsShow(rest) && Describe(ScenesOf(editor)) == Describe(project.Scenes),
            $"{StripText(rest)}; scenes: {Describe(ScenesOf(editor))}"))
        {
            CloseQuietly(editor);
            return;
        }

        // The camera's handle, as the overlay moves it: the camera's top left corner to a place
        // on the canvas of the video, which is 1920 by 1080. A second before the first scene ends.
        DragStops(
            editor,
            engine,
            "the camera dragged while the preview plays: the first move stops it, on the frame the picture stays on; both moves change the scene of that frame and no other; and one Undo takes the whole drag back",
            3.0,
            3.4,
            (_, after) => CameraIsAt(project, after, 800, 420),
            vm => vm.MoveBubbleTopLeft(600, 300),
            vm => vm.MoveBubbleTopLeft(800, 420));

        // The Size slider, in the second scene, a second before it ends.
        DragStops(
            editor,
            engine,
            "Size dragged while the preview plays in the second scene: the first step stops it, and that scene's camera alone has the new size, as one undo step",
            7.0,
            7.4,
            (before, after) => Same(after.Bubble.Size, 0.44) && after.Bubble.Anchor == before.Bubble.Anchor && after.Bubble.OffsetX == before.Bubble.OffsetX && after.Bubble.OffsetY == before.Bubble.OffsetY
                ? null
                : $"its size is {F(after.Bubble.Size)} and should be 0.44, with the camera's corner and offsets as they were",
            vm => vm.CameraBubbleSize = 0.40,
            vm => vm.CameraBubbleSize = 0.44);

        // One of the two offsets: the other is the scene's own, and stays.
        DragStops(
            editor,
            engine,
            "the horizontal offset dragged while the preview plays: the first step stops it, and the scene of that frame has the new horizontal offset and its own vertical one",
            3.0,
            3.4,
            (before, after) => Same(after.Bubble.OffsetX, -0.14) && after.Bubble.OffsetY == before.Bubble.OffsetY && after.Bubble.Anchor == before.Bubble.Anchor && after.Bubble.Size == before.Bubble.Size
                ? null
                : $"its offsets are {F(after.Bubble.OffsetX)} and {F(after.Bubble.OffsetY)}, and should be -0.14 and {F(before.Bubble.OffsetY)}",
            vm => vm.CameraOffsetX = -0.10,
            vm => vm.CameraOffsetX = -0.14);

        // What does not stop it. The same two moves without a drag around them: each is over at once.
        PlaysOn(
            editor,
            engine,
            "the same two moves of the camera without a drag around them, as two choices from a list are made, leave the preview playing, and are two undo steps",
            1.0,
            1.4,
            inGesture: false,
            steps: 2,
            () => Describe(ScenesOf(editor)),
            vm => vm.MoveBubbleTopLeft(600, 300),
            vm => vm.MoveBubbleTopLeft(800, 420));

        // A drag of something the whole recording has.
        PlaysOn(
            editor,
            engine,
            "Padding dragged while the preview plays leaves it playing: the padding is the whole recording's, and no scene's",
            1.0,
            1.4,
            inGesture: true,
            steps: 1,
            () => F(OnUi(() => editor.Window.ViewModel.CanvasPadding)),
            vm => vm.CanvasPadding = 0.10,
            vm => vm.CanvasPadding = 0.14);

        LateDrags(editor, engine);
        UndoInsideADrag(editor, engine);
        CloseQuietly(editor);
    }

    /// <summary>
    /// A drag that begins in the last frame of a scene. Stopping the preview puts the playhead
    /// on the frame the picture stays on, which can be the first frame of the next scene. The
    /// scene that changes then has to be that one, with its own other offset, and not the one
    /// the playhead was in when the pointer moved.
    /// </summary>
    private void LateDrags(Editor editor, StudioPreviewEngine? engine)
    {
        Timeline.Mark("12: drags that begin in the last frame of a scene");

        // The second scene starts at 4.0 s, which is frame 120. Each try plays from half a second before.
        const int LastFrame = 119;
        const int From = 104;

        // Every other try keeps the UI thread busy for this long before the drag, as it is on
        // a busy PC: the preview is a frame on by then, and the editor has not heard of it.
        const int Hold = 40;
        var tries = Math.Max(2, _options.Number("late-drags", 20));
        var original = ScenesOf(editor);
        var viewModel = OnUi(() => editor.Window.ViewModel);

        // Counted apart for the tries as they come and for the tries that were held up: [0] and [1].
        int[] same = [0, 0];
        int[] next = [0, 0];
        int[] already = [0, 0];
        var notMade = 0;
        var wrong = new List<string>();
        var restored = true;
        for (var attempt = 0; attempt < tries && restored; attempt++)
        {
            var heldUp = attempt % 2;
            var x = -0.10 - (0.001 * attempt);
            PauseIfPlaying(editor, engine);
            SetSlider(editor, "StudioPlayhead", MiddleOf(From));
            Until(() => FrameOf(Playhead(editor)), frame => frame == From, 2);

            // The drag: begun, one step of the slider, and let go, all as soon as the playhead is reported in the scene's last frame.
            var made = new TaskCompletionSource<(bool WasPlaying, double HeadBefore, bool PlayingAfter, double HeadAfter, int Current, StudioScene[] Scenes)>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var watch = new PlayheadWatch(this, viewModel))
            {
                OnUi(() => watch.WhenInFrame(LastFrame, () =>
                {
                    try
                    {
                        if (heldUp == 1)
                        {
                            Thread.Sleep(Hold);
                        }

                        var was = viewModel.IsPlaying;
                        var headBefore = viewModel.Playhead;
                        viewModel.BeginGesture();
                        viewModel.CameraOffsetX = x;
                        var playingAfter = viewModel.IsPlaying;
                        var headAfter = viewModel.Playhead;
                        var current = viewModel.CurrentSceneIndex;
                        viewModel.EndGesture();
                        made.TrySetResult((was, headBefore, playingAfter, headAfter, current, viewModel.Scenes.ToArray()));
                    }
                    catch (Exception ex)
                    {
                        made.TrySetException(ex);
                    }
                }));
                Invoke(editor, "StudioPlayPauseButton");
                try
                {
                    made.Task.Wait(TimeSpan.FromSeconds(4));
                }
                catch (AggregateException ex)
                {
                    wrong.Add($"try {attempt + 1}: {ex.InnerException?.Message ?? ex.Message}");
                }
            }

            PauseIfPlaying(editor, engine);
            if (!made.Task.IsCompletedSuccessfully || made.Task.Result is not { WasPlaying: true } drag)
            {
                // The playhead was never reported in that frame while the preview played: nothing was tried.
                notMade++;
            }
            else
            {
                var (before, after) = (SceneAt(original, drag.HeadBefore), SceneAt(original, drag.HeadAfter));
                var changed = ChangedScenes(original, drag.Scenes);
                string? problem =
                    drag.PlayingAfter ? "the preview played on"
                    : drag.Current != after ? $"the editor says the playhead is in scene {drag.Current + 1}"
                    : changed.Length != 1 || changed[0] != after ? $"{(changed.Length == 0 ? "no scene" : "scene " + string.Join(" and ", changed.Select(index => index + 1)))} changed"
                    : !Same(drag.Scenes[after].Bubble.OffsetX, x) ? $"scene {after + 1} has the horizontal offset {F(drag.Scenes[after].Bubble.OffsetX, "0.######")}"
                    : drag.Scenes[after].Bubble.OffsetY != original[after].Bubble.OffsetY ? $"scene {after + 1} has the vertical offset {F(drag.Scenes[after].Bubble.OffsetY, "0.######")}, and its own is {F(original[after].Bubble.OffsetY, "0.######")}"
                    : null;
                if (problem is not null && wrong.Count < 6)
                {
                    wrong.Add($"try {attempt + 1}{(heldUp == 1 ? ", held up" : string.Empty)}: begun with the playhead at {Seconds(drag.HeadBefore)} s in scene {before + 1}, stopped at {Seconds(drag.HeadAfter)} s in scene {after + 1}: {problem}, where scene {after + 1} alone should have the horizontal offset {F(x, "0.######")}");
                }

                same[heldUp] += after == before && before == 0 ? 1 : 0;
                next[heldUp] += after == before + 1 ? 1 : 0;
                already[heldUp] += after == before && before != 0 ? 1 : 0;
            }

            // Back to how it was, for the next try.
            if (Describe(ScenesOf(editor)) != Describe(original))
            {
                Invoke(editor, "StudioUndoButton");
                restored = Until(() => Describe(ScenesOf(editor)), text => text == Describe(original), 1.5) == Describe(original);
            }
        }

        var counted = same.Sum() + next.Sum() + already.Sum();
        string Counts(int which) => $"{same[which]} stopped in the first scene, {next[which]} in the second after beginning in the first, and {already[which]} began when the playhead was already reported in the second";
        _report.Check(
            "a drag that begins in the last frame of a scene changes the scene the preview stops in: where stopping puts the playhead on the first frame of the next scene, that scene gets the new horizontal offset and keeps its own vertical one, and the scene before it is left alone",
            wrong.Count == 0 && restored && counted >= Math.Max(1, tries / 2),
            $"{tries} tries, each a horizontal offset set inside a drag as soon as the playhead was reported in frame {LastFrame}, the last of the first scene. As they came: {Counts(0)}. With the UI thread held up for {Hold} ms first: {Counts(1)}. "
                + $"{notMade} were not made because the playhead was never reported there while the preview played"
                + $"{(wrong.Count == 0 ? string.Empty : "; " + string.Join(" | ", wrong))}{(restored ? string.Empty : "; one Undo did not put the scenes back, and the tries ended there")}");
        _report.Note(next.Sum() == 0
            ? $"in none of {counted} tries did stopping the preview put the playhead into the next scene: the case the editor's separate setting of one offset is for was not met, not even with the UI thread held up. More tries: --late-drags <number>"
            : $"stopping the preview put the playhead into the next scene in {next[0]} of the {same[0] + next[0] + already[0]} tries that came as they came and in {next[1]} of the {same[1] + next[1] + already[1]} that were held up for {Hold} ms first, and in each of them that scene was the one that changed");
    }

    /// <summary>
    /// Undo in the middle of a drag takes back what the drag did so far, and what the drag does
    /// after it is one undo step. The Undo is made by its button, which the window offers in a
    /// drag only when there was something to undo before it: so an edit is made first, to the
    /// sound, which no picture shows. Ctrl+Z is not tried here: the keys wait for a drag to end.
    /// </summary>
    private void UndoInsideADrag(Editor editor, StudioPreviewEngine? engine)
    {
        const string Name = "Undo in the middle of a drag takes back what the drag did so far, in the editor and in the picture, and leaves the edit made before the drag; the drag goes on, and what it does after that is one undo step, which one Redo brings again";
        Timeline.Mark("12: Undo in the middle of a drag");
        const int Frame = 180;
        var project = editor.Expected;
        var viewModel = OnUi(() => editor.Window.ViewModel);
        PauseIfPlaying(editor, engine);
        SetSlider(editor, "StudioPlayhead", MiddleOf(Frame));
        var rest = LookForStrips(editor, Frame, project, 4);
        var before = ScenesOf(editor);
        if (rest is null || !StripsShow(rest) || Describe(before) != Describe(project.Scenes))
        {
            _report.Check(Name, false, $"it was not tried: before the drag the preview should rest in the second scene, with the scenes as the recording has them, and {StripText(rest)}; scenes: {Describe(before)}");
            return;
        }

        // With nothing to undo from before it: whether the window offers Undo in the middle of a drag. Said, not judged.
        var offeredBefore = Find(editor, "StudioUndoButton", 0.5)?.IsEnabled;
        OnUi(() =>
        {
            viewModel.BeginGesture();
            viewModel.MoveBubbleTopLeft(600, 300);
        });
        var offeredInside = Until(() => Find(editor, "StudioUndoButton", 0)?.IsEnabled, enabled => enabled == true, 0.5);
        OnUi(viewModel.EndGesture);
        Until(() => Find(editor, "StudioUndoButton", 0)?.IsEnabled, enabled => enabled == true, 1);
        Invoke(editor, "StudioUndoButton");
        var probeUndone = Until(() => Describe(ScenesOf(editor)), text => text == Describe(before), 1.5) == Describe(before);
        _report.Note($"with nothing to undo from before a drag (Undo enabled: {offeredBefore}), Undo was {(offeredInside == true ? "enabled" : "disabled")} in the middle of the drag, after its first move. "
            + "The editor would take back the drag so far either way; the button offers it only when there is an earlier step, and Ctrl+Z waits for the drag to end");

        // An edit from before the drag, which the Undo in the middle of it has to leave alone.
        var muted = (Find(editor, "StudioMuteCheckBox")?.Toggle() ?? false) && Until(() => OnUi(() => viewModel.IsMuted), on => on, 1.5);
        var undoWas = Until(() => Find(editor, "StudioUndoButton", 0)?.IsEnabled, enabled => enabled == true, 1);

        // The first half of the drag.
        OnUi(() =>
        {
            viewModel.BeginGesture();
            viewModel.MoveBubbleTopLeft(600, 300);
            viewModel.MoveBubbleTopLeft(700, 360);
        });
        var half = ScenesOf(editor);
        var halfAt = half.Length > 1 ? CameraOnTheVideo(project, half[1]) : null;
        var halfShown = LookForStrips(editor, Frame, project with { Scenes = half });

        // Undo, by its button: the pointer still holds the camera.
        var undoEnabled = Find(editor, "StudioUndoButton", 0.5)?.IsEnabled == true;
        var pressed = Invoke(editor, "StudioUndoButton");
        var taken = Until(() => Describe(ScenesOf(editor)), text => text == Describe(before), 1.5);
        var limit = rest.Canvas.Width * rest.Canvas.Height / 200;
        var back = Until(() => LookAtLayout(editor, Frame, project), sight => StripsShow(sight) && DifferenceCount(rest.Shot, sight!.Shot, rest.Canvas) <= limit, 3, 30);
        var differing = back is null ? -1 : DifferenceCount(rest.Shot, back.Shot, rest.Canvas);
        var (stillDragging, mutedInside) = OnUi(() => (viewModel.IsInGesture, viewModel.IsMuted));

        // The second half, and the pointer lets go.
        OnUi(() =>
        {
            viewModel.MoveBubbleTopLeft(900, 500);
            viewModel.MoveBubbleTopLeft(1000, 560);
            viewModel.EndGesture();
        });
        var whole = ScenesOf(editor);
        var wholeAt = whole.Length > 1 ? CameraOnTheVideo(project, whole[1]) : null;
        var wholeShown = LookForStrips(editor, Frame, project with { Scenes = whole });

        // One Undo takes the rest back and no more: the edit from before the drag is still there, and still to be undone.
        Invoke(editor, "StudioUndoButton");
        var undone = Until(() => Describe(ScenesOf(editor)), text => text == Describe(before), 1.5);
        var mutedAfter = OnUi(() => viewModel.IsMuted);
        var undoIs = Until(() => Find(editor, "StudioUndoButton", 0)?.IsEnabled, enabled => enabled == undoWas, 1);
        Invoke(editor, "StudioRedoButton");
        var redone = Until(() => Describe(ScenesOf(editor)), text => text == Describe(whole), 1.5);

        // Back to how the window was: the drag, and then the edit from before it.
        Invoke(editor, "StudioUndoButton");
        Until(() => Describe(ScenesOf(editor)), text => text == Describe(before), 1.5);
        Invoke(editor, "StudioUndoButton");
        var unmuted = !Until(() => OnUi(() => viewModel.IsMuted), on => !on, 1.5);
        _report.Check(
            Name,
            probeUndone && muted && undoWas == true
                && halfAt is { } first && Math.Abs(first.X - 700) <= 0.5 && Math.Abs(first.Y - 360) <= 0.5 && StripsShow(halfShown)
                && undoEnabled && pressed && taken == Describe(before) && back is not null && StripsShow(back) && differing >= 0 && differing <= limit && stillDragging && mutedInside
                && wholeAt is { } last && Math.Abs(last.X - 1000) <= 0.5 && Math.Abs(last.Y - 560) <= 0.5 && StripsShow(wholeShown) && ChangedScenes(before, whole) is [1]
                && undone == Describe(before) && mutedAfter && undoIs == undoWas && redone == Describe(whole) && unmuted,
            $"an edit was made before the drag, to the sound: {muted}; after two moves the second scene's camera is in {(halfAt is { } a ? R(a) : "no place")} on the video, and the picture: {StripText(halfShown)}; Undo was enabled then: {undoEnabled}; "
                + $"after it {(taken == Describe(before) ? "every scene is as before the drag" : "the scenes are " + taken)}, {(differing < 0 ? "there was no screenshot" : $"{differing} pixels of the canvas differ from the picture before the drag, where {limit} may")}, "
                + $"the edit from before the drag is still there: {mutedInside}, and the drag is still open: {stillDragging}; "
                + $"after two more moves and letting go the camera is in {(wholeAt is { } b ? R(b) : "no place")}, and the picture: {StripText(wholeShown)}; "
                + $"after one Undo {(undone == Describe(before) ? "every scene is as before the drag" : "the scenes are " + undone)}, the edit from before the drag is still there: {mutedAfter}, and Undo is {(undoIs == true ? "enabled" : "disabled")}; "
                + $"after one Redo the camera is back where the drag left it: {redone == Describe(whole)}; two more Undo left the window as it was before all of it: {unmuted}");
    }

    /// <summary>Camera share and Move takes, in a scene that is side by side and entered by moving.</summary>
    private void DragsInASideBySideScene()
    {
        Timeline.Mark("12: drags while the preview plays, in a side-by-side scene");
        var folder = NewCameraProject("Drags, side by side", p =>
        {
            p = ForScenePictures(p);
            var first = p.Scenes[0];
            return p with
            {
                Scenes =
                [
                    first,
                    first with { Start = 4, Layout = StudioLayout.SideBySide, Transition = new StudioTransition { Kind = StudioTransitionKind.Morph } },
                    first with { Start = 8, Layout = StudioLayout.SideBySide, Split = new StudioSplit { CameraSide = StudioCameraSide.Leading, CameraFraction = 0.3 }, Transition = new StudioTransition { Kind = StudioTransitionKind.Morph, Duration = 0.5 } },
                ],
            };
        });
        if (OpenReady(folder, "drags, side by side") is not { } editor)
        {
            return;
        }

        var engine = EngineOf(editor);
        DragStops(
            editor,
            engine,
            "Camera share dragged while the preview plays in a side-by-side scene: the first step stops it, and that scene alone has the new share, on the side it had",
            7.0,
            7.4,
            (before, after) => Same(after.Split.CameraFraction, 0.5) && after.Split.CameraSide == before.Split.CameraSide ? null : $"its camera has {F(after.Split.CameraFraction)} of the canvas on the {after.Split.CameraSide} side, and should have 0.5 on the {before.Split.CameraSide} side",
            vm => vm.CameraShare = 0.45,
            vm => vm.CameraShare = 0.5);

        DragStops(
            editor,
            engine,
            "Move takes dragged while the preview plays: the first step stops it, and the move into the scene of that frame alone takes the new time",
            7.0,
            7.4,
            (before, after) => Same(after.Transition.Duration, 0.8) && after.Transition.Kind == before.Transition.Kind ? null : $"the move into it takes {F(after.Transition.Duration)} s and should take 0.8 s",
            vm => vm.SceneMoveDuration = 0.6,
            vm => vm.SceneMoveDuration = 0.8);
        CloseQuietly(editor);
    }

    /// <summary>
    /// A recording with one scene: a drag of its camera leaves the preview playing, because
    /// there is no other scene to come into. And, in the same window, the keys while a block
    /// of a lane is dragged.
    /// </summary>
    private void DragsAndKeysInOneScene()
    {
        Timeline.Mark("12: a drag while the preview plays, in a recording with one scene");
        var folder = NewCameraProject("One scene, dragged", p => OnLemon(p) with { Zooms = [PointZoom(2, 5, 0.5, 0.5)] });
        if (OpenReady(folder, "one scene, dragged") is not { } editor)
        {
            return;
        }

        PlaysOn(
            editor,
            EngineOf(editor),
            "the camera dragged while the preview plays in a recording with one scene leaves it playing: there is no other scene to come into, and the drag is one undo step",
            6.0,
            6.4,
            inGesture: true,
            steps: 1,
            () => Describe(ScenesOf(editor)),
            vm => vm.MoveBubbleTopLeft(300, 200),
            vm => vm.MoveBubbleTopLeft(500, 300));

        KeysWaitWhileSomethingIsDragged(editor);
        CloseQuietly(editor);
    }

    /// <summary>
    /// While a pointer holds a block of a lane, the keys that change the project do nothing,
    /// and Space and the arrow keys, which only move the playhead, still act. Once the pointer
    /// lets go, each of the others acts again.
    /// </summary>
    private void KeysWaitWhileSomethingIsDragged(Editor editor)
    {
        Timeline.Mark("12: the keys while a block is dragged");
        var viewModel = OnUi(() => editor.Window.ViewModel);
        WaitForLane(editor, "Zoom 2×, 2.0 to 5.0 seconds");

        // The lane in effective pixels, which is what its pointer handlers work in.
        var laneBounds = Find(editor, ZoomLane)?.Bounds ?? default;
        var laneWidth = laneBounds.Width / editor.Scale;
        var secondsPerPixel = RecordingLength / (laneWidth - 24);
        var middle = 12 + (3.5 / secondsPerPixel);
        var by = 60 * secondsPerPixel;

        // The pointer goes down on the zoom and moves: the zoom is selected, and a drag is open.
        OnUi(() =>
        {
            LaneOf(editor)!.PressAt(middle);
            LaneOf(editor)!.DragTo(middle + 60);
        });
        var held = ZoomsOf(editor);
        var dragging = OnUi(() => viewModel.IsInGesture);
        var selected = SelectedZoomOf(editor);
        var scenesBefore = Describe(ScenesOf(editor));

        // What each key runs while the drag is open, and what the project is after it.
        string State() => $"{string.Join(", ", ZoomsOf(editor).Select(zoom => $"{Seconds(zoom.Start)} to {Seconds(zoom.End)} s"))}; {Describe(ScenesOf(editor))}";
        var stateHeld = State();
        (string Name, StudioShortcutKey Key, bool Control)[] waiting =
        [
            ("Delete", StudioShortcutKey.Delete, false),
            ("S", StudioShortcutKey.S, false),
            ("1", StudioShortcutKey.Digit1, false),
            ("Ctrl+Z", StudioShortcutKey.Z, true),
            ("Ctrl+E", StudioShortcutKey.E, true),
        ];
        var ran = new List<string>();
        var allWaited = true;
        foreach (var (name, key, control) in waiting)
        {
            var action = Key(editor, key, control);
            var exporting = IsExporting(editor);
            var state = State();
            ran.Add($"{name} ran {action}");
            allWaited &= action == StudioShortcutAction.None && state == stateHeld && !exporting;
            if (exporting)
            {
                Key(editor, StudioShortcutKey.Escape);
                Gone(editor, "StudioCancelExportButton", 15);
            }
        }

        // Space and Right only move the playhead.
        var headBefore = OnUi(() => viewModel.Playhead);
        var play = Key(editor, StudioShortcutKey.Space);
        var headPlayed = Until(() => OnUi(() => viewModel.Playhead), value => value >= headBefore + 0.1, 3, 10);
        var pause = Key(editor, StudioShortcutKey.Space);
        var paused = Until(() => NameOf(editor, "StudioPlayPauseButton", 0), text => text == "Play", 2) == "Play";
        var headPaused = OnUi(() => viewModel.Playhead);
        var right = Key(editor, StudioShortcutKey.Right);
        var headStepped = Until(() => OnUi(() => viewModel.Playhead), value => FrameOf(value) == FrameOf(headPaused) + 1, 1);
        var stillDragging = OnUi(() => viewModel.IsInGesture);
        var stateAfterKeys = State();
        _report.Check(
            "while a zoom is dragged on its lane, what Delete, S, 1, Ctrl+Z and Ctrl+E run is nothing: the zoom is still there where the pointer has it, no scene is split, the layout stays, nothing is undone and no export starts; what Space and Right run still plays, pauses and steps a frame",
            dragging && selected == 0 && held.Length == 1 && Same(held[0].Start, 2 + by) && Same(held[0].End, 5 + by) && allWaited
                && play == StudioShortcutAction.TogglePlayback && headPlayed >= headBefore + 0.1 && pause == StudioShortcutAction.TogglePlayback && paused
                && right == StudioShortcutAction.NextFrame && FrameOf(headStepped) == FrameOf(headPaused) + 1 && stillDragging && stateAfterKeys == stateHeld,
            $"with the pointer down on the zoom and moved by 60, which is {Seconds(by)} s, a drag is open: {dragging}, {(selected is { } chosen ? $"zoom {chosen + 1} is selected" : "no zoom is selected")}, and the zoom is at {(held.Length == 1 ? $"{Seconds(held[0].Start)} to {Seconds(held[0].End)} s" : $"{held.Length} zooms")}; {string.Join(", ", ran)}; "
                + $"after all the keys {(stateAfterKeys == stateHeld ? "the zoom and the scenes are as the pointer has them" : "the editor holds " + stateAfterKeys)}; "
                + $"Space ran {play} and the playhead went from {Seconds(headBefore)} s to {Seconds(headPlayed)} s; Space again ran {pause} and it paused: {paused}; Right ran {right} and the playhead went from frame {FrameOf(headPaused)} to frame {FrameOf(headStepped)}; the drag is still open: {stillDragging}");

        // The pointer lets go.
        OnUi(() => LaneOf(editor)!.EndPress());
        var open = OnUi(() => viewModel.IsInGesture);
        var after = new List<string>();

        // Ctrl+Z takes the drag back: it was one step.
        var undo = Key(editor, StudioShortcutKey.Z, control: true);
        var undone = Until(() => ZoomsOf(editor), zooms => zooms.Length == 1 && zooms[0].Start == 2, 1.5);
        after.Add($"Ctrl+Z ran {undo}, and the zoom is at {(undone.Length == 1 ? $"{Seconds(undone[0].Start)} to {Seconds(undone[0].End)} s" : $"{undone.Length} zooms")}");

        // S splits the scene at the playhead.
        var split = Key(editor, StudioShortcutKey.S);
        var scenes = Until(() => ScenesOf(editor), now => now.Length == 2, 1.5);
        after.Add($"S ran {split}, and there are {scenes.Length} scenes");

        // 1 shows the screen alone, in the scene the playhead is in.
        var one = Key(editor, StudioShortcutKey.Digit1);
        var layout = Until(() => OnUi(() => viewModel.Scenes[viewModel.CurrentSceneIndex].Layout), now => now == StudioLayout.Screen, 1.5);
        after.Add($"1 ran {one}, and the layout is {layout}");

        // Delete removes the selected zoom.
        OnUi(() => viewModel.SelectZoom(0));
        var delete = Key(editor, StudioShortcutKey.Delete);
        var zoomsLeft = Until(() => ZoomsOf(editor).Length, count => count == 0, 1.5);
        after.Add($"Delete ran {delete}, and {zoomsLeft} zoom(s) are left");

        // Ctrl+E starts an export, which is stopped again.
        var filesBefore = ExportFiles();
        var export = Key(editor, StudioShortcutKey.E, control: true);
        var started = Find(editor, "StudioCancelExportButton", 3) is not null;

        // Only while it runs: without an export, Esc asks the window to close.
        var escape = IsExporting(editor) ? Key(editor, StudioShortcutKey.Escape) : StudioShortcutAction.None;
        var stopped = Gone(editor, "StudioCancelExportButton", 15);
        var files = Until(() => ExportFiles().Except(filesBefore).ToArray(), left => left.Length == 0, 3, 50);
        after.Add($"Ctrl+E ran {export} and an export started: {started}; Esc ran {escape} and it stopped: {stopped}, leaving {(files.Length == 0 ? "no file" : string.Join(", ", files.Select(Path.GetFileName)))}");
        _report.Check(
            "once the pointer lets go, each of those keys acts again: Ctrl+Z takes the drag back as one step, S splits the scene, 1 shows the screen alone, Delete removes the selected zoom, and Ctrl+E starts an export",
            !open && undo == StudioShortcutAction.Undo && undone.Length == 1 && undone[0].Start == 2 && undone[0].End == 5
                && split == StudioShortcutAction.SplitScene && scenes.Length == 2 && Describe(ScenesOf(editor)) != scenesBefore
                && one == StudioShortcutAction.ShowScreenLayout && layout == StudioLayout.Screen
                && delete == StudioShortcutAction.RemoveSelectedZoom && zoomsLeft == 0
                && export == StudioShortcutAction.Export && started && escape == StudioShortcutAction.CancelExport && stopped && files.Length == 0,
            $"the drag is closed: {!open}; {string.Join("; ", after)}");
    }
}
