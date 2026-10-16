using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Rendering;
using TinyClips.Tools.StudioPreviewCheck.Media;
using TinyClips.Tools.StudioWindowCheck.Capture;
using TinyClips.Tools.StudioWindowCheck.Host;
using Vortice.Direct3D11;

namespace TinyClips.Tools.StudioWindowCheck.Checks;

// 10, continued. A zoom that moves in while the preview plays: every scene the preview draws on
// the way is read back, and so is a screenshot of the window every fifth of a second.
internal sealed partial class WindowChecks
{
    /// <summary>One picture of the preview while it played: when, which frame, and how far its furthest edge was from its place.</summary>
    /// <param name="At">A Stopwatch timestamp.</param>
    /// <param name="Frame">The frame number read from the strip, or <see cref="FrameCode.Unreadable"/>.</param>
    /// <param name="Problem">Null when the picture shows the part of the screen its frame calls for.</param>
    /// <param name="Fits">For a picture that is wrong: the frame whose part of the screen its edges fit best, and how closely; otherwise null.</param>
    private sealed record SceneReading(long At, int Frame, double Worst, int Edges, string? Problem, (int Frame, double Worst)? Fits = null);

    private void ZoomMovesInWhilePlaying()
    {
        PlayThroughAZoomMovingIn(heldUp: false);

        // Asked for with --held-up: the same again while this process is held up, as it is on a
        // PC that is busy. It is how to see what the preview does with a frame that comes late.
        if (_options.Flag("held-up"))
        {
            PlayThroughAZoomMovingIn(heldUp: true);
        }
    }

    /// <param name="heldUp">
    /// Has the garbage collector run about twice a second while the preview plays, with enough
    /// kept in memory for it to look through that each run stops every thread of the process,
    /// the preview's among them, for about fifty milliseconds.
    /// </param>
    private void PlayThroughAZoomMovingIn(bool heldUp)
    {
        Timeline.Mark(heldUp ? "10: a zoom moves in while the preview plays and the process is held up" : "10: a zoom moves in while the preview plays");
        var when = heldUp ? "held up while it plays (--held-up): " : string.Empty;

        // One zoom that starts at 4 s and takes three seconds to move in, on the part around
        // (0.30, 0.28). The frames 120 to 209 have their middles inside the move. The 90 frames
        // before them, 30 to 119, show the whole screen, and are what the move is compared with.
        const double ZoomStart = 4;
        const double EaseIn = 3;
        const int From = 15;
        const int PlainFirst = 30;
        const int MovingFirst = 120;
        const int MovingLast = 209;
        const int Last = 216;
        var folder = NewScreenProject("A zoom that moves in", p => p with { Zooms = [PointZoom(ZoomStart, 10, PointerFirst.X, PointerFirst.Y, easeIn: EaseIn)] });
        if (OpenReady(folder, heldUp ? "zoom moving in, held up" : "zoom moving in") is not { } editor)
        {
            return;
        }

        // Section 6.8 of the project format, for a zoom that stands alone on a screen without a
        // crop. The instant a frame stands for is its middle. Before the zoom the screen's source
        // is the whole screen; while the zoom moves in it is between the whole screen and the part
        // the zoom holds, by u × u × (3 − 2u), where u is the share of the ease in that has passed.
        static ScreenPart PartAt(int frame)
        {
            var time = MiddleOf(frame);
            if (time < ZoomStart)
            {
                return ScreenPart.Whole;
            }

            if (time >= ZoomStart + EaseIn)
            {
                return HeldAtFirst;
            }

            var u = (time - ZoomStart) / EaseIn;
            return ScreenPart.Between(ScreenPart.Whole, HeldAtFirst, u * u * (3 - (2 * u)));
        }

        // Reads one picture: which frame it shows, from the strip, looked for where each of the
        // frames around the last one would put it; and then the edges, against the part that
        // frame calls for. A picture that showed a frame with the part of the frame before it
        // would have its edges up to four pixels from their places.
        var clip = TestMedia.Screen;
        var project = editor.Expected;
        SceneReading Read(Shot shot, StudioFrameRect card, double inset, long at, ref int last)
        {
            var frame = FrameCode.Unreadable;
            for (var candidate = Math.Max(0, last - 2); candidate <= last + 14 && frame == FrameCode.Unreadable; candidate++)
            {
                var read = FrameCode.Decode(shot.Bgra, shot.Width, shot.Height, clip, ClipMap.ForLayer(clip, card, PartAt(candidate).Rect, mirror: false));
                if (read != FrameCode.Unreadable && FrameCode.Decode(shot.Bgra, shot.Width, shot.Height, clip, ClipMap.ForLayer(clip, card, PartAt(read).Rect, mirror: false)) == read)
                {
                    frame = read;
                }
            }

            if (frame == FrameCode.Unreadable)
            {
                return new SceneReading(at, frame, double.NaN, 0, "the frame strip does not read where any of the frames around the last one would put it");
            }

            last = frame;
            var reading = ReadPart(shot, clip, ScreenLandmarks(frame), card, PartAt(frame), mirror: false, inset);
            var wrong = Judge(reading, frame);
            if (wrong is null)
            {
                return new SceneReading(at, frame, reading.Worst, reading.Edges.Count, null);
            }

            // A picture that is wrong: the part of which of the frames around it do its edges fit?
            (int Frame, double Worst)? fits = null;
            for (var other = Math.Max(0, frame - 8); other <= frame + 8; other++)
            {
                if (other == frame)
                {
                    continue;
                }

                var against = ReadPart(shot, clip, ScreenLandmarks(frame), card, PartAt(other), mirror: false, inset);
                if (Judge(against, null) is null && (fits is not { } best || against.Worst < best.Worst))
                {
                    fits = (other, against.Worst);
                }
            }

            return new SceneReading(at, frame, reading.Worst, reading.Edges.Count, $"frame {frame}, which should show {PartAt(frame)}: {wrong}", fits);
        }

        SetSlider(editor, "StudioPlayhead", MiddleOf(From));
        var rest = LookForPart(editor, ScreenPart.Whole, From, 5);
        var engine = EngineOf(editor);
        if (rest is null || Judge(rest.Reading, From) is not null || engine is null)
        {
            _report.Check(when + "the preview rests on the frame the playing starts from", false, rest is null ? "no screenshot" : engine is null ? "the window's preview is not the preview engine" : Judge(rest.Reading, From) + KeptPart(editor, rest, ScreenPart.Whole, From, "zoom-rest"));
            CloseQuietly(editor);
            return;
        }

        // Every scene the engine draws, through the hook it has for its check tools. The hook is
        // called on the render thread; the reading is done on a thread of the recorder's own.
        var scenes = new List<SceneReading>();
        var lastScene = From;
        using var recorder = new SceneRecorder((shot, at) =>
        {
            var canvas = new Box(0, 0, shot.Width, shot.Height);
            scenes.Add(ScreenCard(canvas, project) is { } card
                ? Read(shot, card, CardInset(canvas, project), at, ref lastScene)
                : new SceneReading(at, FrameCode.Unreadable, double.NaN, 0, "the layout has no screen"));
        });
        // And what the window shows, about every fifth of a second.
        var shots = new List<SceneReading>();
        var lastShot = From;
        Shot? halfWay = null;
        byte[]? shotBuffer = null;

        // The tool keeps out of the preview's way while it plays: the buffers it will fill are
        // made now, and what the checks before this one left behind is collected now, so that the
        // garbage collector has no reason to stop every thread in the middle of the playing.
        editor.Camera.Take(ref shotBuffer);
        recorder.Reserve(rest.Canvas.Width, rest.Canvas.Height, 8);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // A collection takes as long as what it has to look through. In this process that used
        // to be long by itself, because every window a run had closed stayed in memory; since
        // that is mended a collection takes a few milliseconds, and holds nothing up. So to
        // hold the process up, the tool keeps something for the collector to look through.
        var pauseOfOne = 0.0;
        var ballast = heldUp ? Ballast.Make(50, out pauseOfOne) : null;
        var before = engine.GetDiagnostics();
        var pausedBefore = GC.GetTotalPauseDuration();
        var collectionsBefore = (GC.CollectionCount(0), GC.CollectionCount(2));
        var watch = Stopwatch.StartNew();
        double played;
        engine.AfterRender = recorder.OnRender;
        try
        {
            Invoke(editor, "StudioPlayPauseButton");
            for (var pass = 0; watch.Elapsed.TotalSeconds < 12 && FrameOf(Playhead(editor)) < Last; pass++)
            {
                // Held up: on every third pass, about twice a second, every thread of the
                // process stands still for as long as the collector takes.
                if (heldUp && pass % 3 == 2)
                {
                    GC.Collect();
                }

                // Into the same buffer every time: a new one for each would have the garbage
                // collector run by itself, at moments of its own choosing.
                if (editor.Camera.Take(ref shotBuffer) is { } shot)
                {
                    var reading = Read(shot, rest.Card, CardInset(rest.Canvas, project), Stopwatch.GetTimestamp(), ref lastShot);
                    shots.Add(reading);
                    if (halfWay is null && reading.Frame >= (MovingFirst + MovingLast) / 2 && reading.Frame <= MovingLast)
                    {
                        halfWay = shot with { Bgra = (byte[])shot.Bgra.Clone() };
                    }
                }

                Thread.Sleep(170);
            }

            Invoke(editor, "StudioPlayPauseButton");
            played = watch.Elapsed.TotalSeconds;
            Until(() => NameOf(editor, "StudioPlayPauseButton", 0), name => name == "Play", 2);
            engine.WaitForIdle(TimeSpan.FromSeconds(5));
        }
        finally
        {
            engine.AfterRender = null;
            recorder.Finish();
        }

        var after = engine.GetDiagnostics();
        var paused = GC.GetTotalPauseDuration() - pausedBefore;
        var collections = (All: GC.CollectionCount(0) - collectionsBefore.Item1, Full: GC.CollectionCount(2) - collectionsBefore.Item2);
        var kept = ballast?.Sum(part => (long)part.Length) ?? 0;
        GC.KeepAlive(ballast);
        ballast = null;
        if (halfWay is not null && !heldUp)
        {
            var path = Path.Combine(_output, "zoom-moving-in.png");
            halfWay.Save(path);
            _report.Line($"  saved {path}");
        }

        // The picture of every scene.
        var moving = scenes.Where(scene => scene.Frame is >= MovingFirst and <= MovingLast).ToList();
        var plain = scenes.Where(scene => scene.Frame is >= PlainFirst and < MovingFirst).ToList();
        var counted = scenes.SkipWhile(scene => scene.Frame < PlainFirst).TakeWhile(scene => scene.Frame <= MovingLast).ToList();
        var unreadable = counted.Count(scene => scene.Frame == FrameCode.Unreadable);
        var backwards = counted.Where(scene => scene.Frame != FrameCode.Unreadable).Zip(counted.Where(scene => scene.Frame != FrameCode.Unreadable).Skip(1), (a, b) => b.Frame < a.Frame).Count(wentBack => wentBack);
        var wrong = moving.Where(scene => scene.Problem is not null).ToList();
        var worst = moving.Where(scene => !double.IsNaN(scene.Worst)).OrderByDescending(scene => scene.Worst).FirstOrDefault();
        var plainWorst = plain.Where(scene => !double.IsNaN(scene.Worst)).Select(scene => scene.Worst).DefaultIfEmpty(double.NaN).Max();
        var measured = moving.Where(scene => !double.IsNaN(scene.Worst)).ToList();
        if (!heldUp)
        {
            _worstMovingEdge = worst?.Worst ?? double.NaN;
        }

        // How long before each scene the one before it was drawn, to tell what a wrong scene followed.
        var waits = new Dictionary<SceneReading, double>();
        for (var index = 1; index < scenes.Count; index++)
        {
            waits[scenes[index]] = Stopwatch.GetElapsedTime(scenes[index - 1].At, scenes[index].At).TotalMilliseconds;
        }

        string Wrong(SceneReading scene) => string.Create(
            CultureInfo.InvariantCulture,
            $"frame {scene.Frame}: an edge {scene.Worst:0.00} px from its place; {(scene.Fits is { } fits ? $"the edges fit the part of frame {fits.Frame}, within {fits.Worst:0.00} px" : $"the edges fit the part of none of the eight frames before and after it ({scene.Problem})")}; drawn {(waits.TryGetValue(scene, out var wait) ? wait : double.NaN):0} ms after the scene before it");
        var pauses = string.Create(CultureInfo.InvariantCulture, $"while it played, this tool's garbage collector ran {collections.All} times, {collections.Full} of them in full, and held every thread for {paused.TotalMilliseconds:0} ms in all")
            + (heldUp ? string.Create(CultureInfo.InvariantCulture, $"; it had {kept / 1e6:0.0} million objects to look through, kept for that, with which one run of it took {pauseOfOne:0} ms before the playing started") : string.Empty);
        var summary = measured.Count == 0
            ? "no scene of the move was read"
            : string.Create(CultureInfo.InvariantCulture, $"{moving.Count} scenes of the frames {MovingFirst} to {MovingLast}, each with {measured.Min(scene => scene.Edges)} to {measured.Max(scene => scene.Edges)} edges read; the edge furthest from its place was {worst!.Worst:0.00} px from it, in frame {worst.Frame}, and the furthest edge of a scene on average {measured.Average(scene => scene.Worst):0.00} px; {unreadable} scenes did not read, {backwards} went back to an earlier frame; where no zoom is, over {plain.Count} scenes, the furthest was {plainWorst:0.00} px; {scenes.Count} scenes were drawn in all; {pauses}");
        _report.Check(
            when + "while the zoom moves in, every scene the preview draws shows the part of the screen the format gives for the frame it shows, eased by u × u × (3 − 2u)",
            recorder.Failure is null && recorder.Unread == 0 && moving.Count >= 60 && wrong.Count == 0 && unreadable == 0 && backwards == 0,
            (wrong.Count == 0 ? string.Empty : $"{wrong.Count} of {moving.Count} scenes of the move are wrong: {string.Join(" | ", wrong.Select(Wrong))}. ")
                + summary
                + (recorder.Failure is null ? string.Empty : "; reading a scene back failed: " + recorder.Failure)
                + (recorder.Unread == 0 ? string.Empty : $"; {recorder.Unread} scenes could not be kept to be read"));

        // The frames that were late, and the ones that never came.
        (int Dropped, int Late, double LongestMs) Pace(int first, int last)
        {
            var firstDrawn = new SortedDictionary<int, long>();
            foreach (var scene in scenes.Where(scene => scene.Frame >= first - 1 && scene.Frame <= last))
            {
                firstDrawn.TryAdd(scene.Frame, scene.At);
            }

            var dropped = Enumerable.Range(first, last - first + 1).Count(frame => !firstDrawn.ContainsKey(frame));
            var late = 0;
            double longest = 0;
            KeyValuePair<int, long>? previous = null;
            foreach (var drawn in firstDrawn)
            {
                if (previous is { } earlier && drawn.Key >= first)
                {
                    // More than a frame's time later than the frames between it and the one before it account for.
                    var took = Stopwatch.GetElapsedTime(earlier.Value, drawn.Value).TotalMilliseconds;
                    var due = (drawn.Key - earlier.Key) * 1000.0 / Fps;
                    late += took - due > 1000.0 / Fps ? 1 : 0;
                    longest = Math.Max(longest, took);
                }

                previous = drawn;
            }

            return (dropped, late, longest);
        }

        var movingPace = Pace(MovingFirst, MovingLast);
        var plainPace = Pace(PlainFirst, MovingFirst - 1);
        _report.Check(
            when + "playing through the move drops and delays no more frames than playing where no zoom is, allowing two",
            moving.Count >= 60 && plain.Count >= 60 && movingPace.Dropped <= plainPace.Dropped + 2 && movingPace.Late <= plainPace.Late + 2,
            string.Create(CultureInfo.InvariantCulture, $"of the 90 frames of the move, {movingPace.Dropped} were never drawn and {movingPace.Late} came more than a frame's time late, the longest wait between two frames {movingPace.LongestMs:0} ms; ")
                + string.Create(CultureInfo.InvariantCulture, $"of the 90 frames before it, {plainPace.Dropped} and {plainPace.Late}, the longest wait {plainPace.LongestMs:0} ms; ")
                + $"during the {F(played, "0.0")} s of playing the engine drew {after.FramesDrawn - before.FramesDrawn} scenes, changed the size of the texture it copies the screen's frames into {after.CopyTargetChanges - before.CopyTargetChanges} times and discarded {after.LateFramesDiscarded - before.LateFramesDiscarded} late frames");

        // The window's own picture.
        var movingShots = shots.Where(shot => shot.Frame is >= MovingFirst and <= MovingLast).ToList();
        var wrongShots = movingShots.Where(shot => shot.Problem is not null).ToList();
        _report.Check(
            when + "screenshots of the window taken while the zoom moves in show the same: each the part its frame calls for",
            movingShots.Count >= 6 && wrongShots.Count == 0 && shots.All(shot => shot.Frame != FrameCode.Unreadable),
            wrongShots.Count > 0
                ? $"{wrongShots.Count} of {movingShots.Count} screenshots are wrong, the first: {wrongShots[0].Problem}"
                : string.Create(CultureInfo.InvariantCulture, $"{movingShots.Count} screenshots, of the frames {string.Join(", ", movingShots.Select(shot => shot.Frame))}; the edge furthest from its place was {(movingShots.Count == 0 ? double.NaN : movingShots.Max(shot => shot.Worst)):0.00} px from it; {shots.Count(shot => shot.Frame == FrameCode.Unreadable)} of all {shots.Count} screenshots did not read"));

        // At rest again, on a frame inside the part that is held.
        var head = Playhead(editor);
        var parked = LookForPart(editor, PartAt(FrameOf(head)), FrameOf(head), 3);
        _report.Check(
            when + "paused after the move, the preview rests on the playhead's frame, zoomed all the way in",
            parked is not null && Judge(parked.Reading, FrameOf(head)) is null && FrameOf(head) > MovingLast,
            parked is null ? "no screenshot" : $"playhead {Seconds(head)} s (frame {FrameOf(head)}): {Judge(parked.Reading, FrameOf(head)) ?? parked.Reading.ToString()}");
        CloseQuietly(editor);
    }
}

/// <summary>What the garbage collector is given to look through when the process is to be held up.</summary>
internal static class Ballast
{
    /// <summary>
    /// Makes small objects that refer to each other, half a million at a time, until one full
    /// run of the garbage collector stops every thread for as long as wanted, or twelve million
    /// are made. They cost a collection time for as long as what is returned is kept.
    /// </summary>
    /// <param name="pause">How long one full run of the collector held every thread with them, in milliseconds.</param>
    public static List<object[]> Make(double wantedMilliseconds, out double pause)
    {
        var ballast = new List<object[]>();
        pause = 0;
        while (ballast.Count < 24)
        {
            var part = new object[500_000];
            for (var index = 0; index < part.Length; index++)
            {
                part[index] = new object[] { part };
            }

            ballast.Add(part);
            GC.Collect();
            var pausedBefore = GC.GetTotalPauseDuration();
            GC.Collect();
            pause = (GC.GetTotalPauseDuration() - pausedBefore).TotalMilliseconds;
            if (pause >= wantedMilliseconds)
            {
                break;
            }
        }

        return ballast;
    }
}

/// <summary>
/// Copies every scene a preview engine draws out of the texture it was drawn into, through the
/// engine's after-render hook, and hands each to a reader on a thread of its own. The buffers are
/// used again, so that reading a few hundred scenes does not make the garbage collector pause the
/// very playback that is being timed.
/// </summary>
internal sealed class SceneRecorder : IDisposable
{
    private readonly BlockingCollection<(long At, byte[] Pixels, int Width, int Height)> _scenes = new(96);
    private readonly ConcurrentBag<byte[]> _spare = [];
    private readonly Action<Shot, long> _read;
    private readonly Thread _reader;
    private int _unread;
    private string? _failure;

    /// <param name="read">Called for every scene, in the order they were drawn, with the scene and the Stopwatch timestamp of its drawing.</param>
    public SceneRecorder(Action<Shot, long> read)
    {
        _read = read;
        _reader = new Thread(ReadScenes) { IsBackground = true, Name = "StudioWindowCheck.Scenes" };
        _reader.Start();
    }

    /// <summary>Makes buffers for scenes of a size ahead of time, so that none has to be made while the preview plays.</summary>
    public void Reserve(int width, int height, int count)
    {
        for (var index = 0; index < count; index++)
        {
            _spare.Add(new byte[width * height * 4]);
        }
    }

    /// <summary>How many scenes were drawn while too many were still waiting to be read.</summary>
    public int Unread => Volatile.Read(ref _unread);

    /// <summary>What went wrong reading a scene back or reading it, or null.</summary>
    public string? Failure => Volatile.Read(ref _failure);

    /// <summary>The engine's after-render hook. Runs on the render thread with the device lock held.</summary>
    public void OnRender(StudioGraphicsDevice graphics, ID3D11Texture2D target, int width, int height)
    {
        var at = Stopwatch.GetTimestamp();
        var length = width * height * 4;
        if (!_spare.TryTake(out var pixels) || pixels.Length != length)
        {
            pixels = new byte[length];
        }

        try
        {
            graphics.ReadTexture(target, 0, pixels);
            if (!_scenes.TryAdd((at, pixels, width, height)))
            {
                Interlocked.Increment(ref _unread);
                _spare.Add(pixels);
            }
        }
        catch (Exception ex)
        {
            // Also a scene drawn after Finish, which is no longer wanted.
            Interlocked.CompareExchange(ref _failure, _scenes.IsAddingCompleted ? null : ex.Message, null);
        }
    }

    /// <summary>No more scenes are coming. Returns when every scene handed over has been read.</summary>
    public void Finish()
    {
        _scenes.CompleteAdding();
        _reader.Join(TimeSpan.FromSeconds(30));
    }

    public void Dispose()
    {
        if (!_scenes.IsAddingCompleted)
        {
            Finish();
        }

        _scenes.Dispose();
    }

    private void ReadScenes()
    {
        foreach (var (at, pixels, width, height) in _scenes.GetConsumingEnumerable())
        {
            try
            {
                _read(new Shot(pixels, width, height, 0, 0, 0), at);
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref _failure, ex.ToString(), null);
            }

            _spare.Add(pixels);
        }
    }
}
