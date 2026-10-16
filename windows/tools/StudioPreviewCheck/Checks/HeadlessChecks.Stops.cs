using System.Diagnostics;
using TinyClips.Core.Studio.Preview;
using TinyClips.Tools.StudioPreviewCheck.Media;

namespace TinyClips.Tools.StudioPreviewCheck.Checks;

// Two experiments, not checks, with the whole process stopped from outside at a chosen moment.
// They came with a frame that was given the number of the frame its position named and showed
// the frame two after it, once in 500 plays while the process was stopped at random.
//
// "starts" stops the process once around the start of playback and reads every frame the
// players hand over afterwards: the first guess was that a stop on the start of the clock does
// it. It does not; what the plays that went wrong had in common is a hand-over that had set out
// when the stop began and made its copy after it.
//
// "waits" makes exactly that happen, and its opposite: a hand-over is kept waiting for the
// graphics device for a tenth of a second, with the process running all the while, and with
// the process stopped meanwhile. A player keeps the frame it has announced for a hand-over
// that waits; a player that was stopped itself does not always.
internal sealed partial class HeadlessChecks
{
    // "tails" is about the garbage collector and the one thing the engine takes on trust about
    // it: that a collection which begins after a hand-over has said it is over (the namer's
    // Exit) keeps the next hand-over waiting at the door, and not this one's thread on its way
    // out. A full collection is begun a few millionths of a second after a hand-over's copy,
    // again and again, and the hand-over that follows is read back.
    private void InvestigateTails()
    {
        var count = _options.Number("count", 300);
        var mostSpins = Math.Max(1, _options.Number("lead", 600));
        _report.Section($"A full collection begun as a hand-over gives its thread back: {count} times, each begun up to {mostSpins} spins after the hand-over's copy");

        // Kept alive so that a full collection lasts about as long as asked for. It never
        // collects by itself: the gap is an hour.
        using var heap = new HeldUp(HoldUpMilliseconds, 3_600_000, 3_600_000);
        _report.Line($"  a full collection holds every managed thread for about {F(heap.StallMilliseconds, "0")} ms ({F(heap.BallastMegabytes, "0")} MB kept alive)");

        // A thread that does nothing but wait for the word, so that it is not asleep when it comes.
        var go = 0;
        var stop = false;
        long collectFrom = 0;
        long collectTo = 0;
        var collector = new Thread(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                var spins = Volatile.Read(ref go);
                if (spins == 0)
                {
                    Thread.SpinWait(1);
                    continue;
                }

                for (var spin = 1; spin < spins; spin++)
                {
                    Thread.SpinWait(1);
                }

                var from = Stopwatch.GetTimestamp();
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
                Interlocked.Exchange(ref collectFrom, from);
                Interlocked.Exchange(ref collectTo, Stopwatch.GetTimestamp());
                Volatile.Write(ref go, 0);
            }
        })
        {
            IsBackground = true,
            Name = "StudioPreviewCheck.Tails",
        };
        collector.Start();

        var session = OpenSession(TestMedia.Camera, options: Muted);
        try
        {
            var last = session.Folder.FrameCount - 1;
            using var probe = new TruthProbe(session.Engine, session.Folder.Screen, session.Folder.Camera, capacity: 400);
            var armed = 0;
            var lead = 0;
            long armedSerial = 0;
            long hookLeftAt = 0;
            var inner = session.Engine.AfterCopy;
            session.Engine.AfterCopy = handOver =>
            {
                inner?.Invoke(handOver);
                if (handOver.Kind == StudioPreviewHandOverKind.Playback && handOver.Clip == 0 && Interlocked.CompareExchange(ref armed, 0, 1) == 1)
                {
                    Interlocked.Exchange(ref armedSerial, handOver.Serial);
                    Interlocked.Exchange(ref hookLeftAt, Stopwatch.GetTimestamp());
                    Volatile.Write(ref go, Volatile.Read(ref lead) + 1);
                }
            };

            // What became of the hand-over after the one the collection was begun behind: by
            // how the engine took it, and whether its number was right.
            var kept = new List<(double BeganAfter, double StartedAfterEnd, double Phase, bool Wrong, int Truth, long Frame, long Name)>();
            var others = new Dictionary<string, int>();
            var othersWrong = 0;
            var trials = 0;
            try
            {
                session.SeekTo(30);
                probe.Read();
                session.Engine.Play();
                Thread.Sleep(500);
                for (var trial = 0; trial < count; trial++)
                {
                    if (session.PositionFrame > last - 75)
                    {
                        session.Engine.Pause();
                        session.WaitForIdle();
                        session.SeekTo(30);
                        session.Engine.Play();
                        Thread.Sleep(500);
                    }

                    probe.Read();
                    Interlocked.Exchange(ref collectTo, 0);
                    Volatile.Write(ref lead, _random.Next(mostSpins + 1));
                    Volatile.Write(ref armed, 1);
                    var asked = Stopwatch.GetTimestamp();
                    while (Interlocked.Read(ref collectTo) == 0 && Stopwatch.GetElapsedTime(asked).TotalSeconds < 2)
                    {
                        Thread.Sleep(5);
                    }

                    Volatile.Write(ref armed, 0);
                    Thread.Sleep(350);
                    var to = Interlocked.Read(ref collectTo);
                    if (to == 0)
                    {
                        continue;
                    }

                    var from = Interlocked.Read(ref collectFrom);
                    var serial = Interlocked.Read(ref armedSerial);
                    var left = Interlocked.Read(ref hookLeftAt);
                    var frames = probe.Read().Where(f => f.Clip == 0 && f.Kind == StudioPreviewHandOverKind.Playback).OrderBy(f => f.Serial).ToList();
                    var behind = frames.FindIndex(f => f.Serial == serial);
                    if (behind < 0 || behind + 1 >= frames.Count || frames[behind + 1].Truth == FrameCode.Unreadable)
                    {
                        continue;
                    }

                    trials++;
                    var before = frames[behind];
                    var next = frames[behind + 1];
                    var wrong = next.Knowledge == StudioPreviewFrameKnowledge.Known && next.Frame != next.Truth;
                    var phase = Stopwatch.GetElapsedTime(before.StartedAt, next.StartedAt).TotalMilliseconds % 10;
                    phase = Math.Min(phase, 10 - phase);
                    if (next.Rule == "kept by the collector")
                    {
                        kept.Add(((from - left) * 1_000_000.0 / Stopwatch.Frequency, (next.StartedAt - to) * 1000.0 / Stopwatch.Frequency, phase, wrong, next.Truth, next.Frame, next.NameByPosition));
                    }
                    else
                    {
                        var how = $"{next.Knowledge} ({next.Rule})";
                        others[how] = others.GetValueOrDefault(how) + 1;
                        othersWrong += wrong ? 1 : 0;
                    }
                }

                session.Engine.Pause();
                session.WaitForIdle();
            }
            finally
            {
                session.Engine.AfterCopy = inner;
            }

            _report.Line($"  {trials} collections were begun behind a hand-over of the screen clip. The hand-over after it was taken as: kept by the collector {kept.Count} times; {string.Join("; ", others.OrderByDescending(o => o.Value).Select(o => $"{o.Key} {o.Value}"))}; {othersWrong} of those others with a wrong number");
            foreach (var isWrong in new[] { false, true })
            {
                var some = kept.Where(k => k.Wrong == isWrong).ToList();
                if (some.Count == 0)
                {
                    _report.Line($"  kept by the collector and {(isWrong ? "given a wrong number" : "given the right number")}: none");
                    continue;
                }

                var began = new Samples();
                var started = new Samples();
                var onATick = 0;
                foreach (var k in some)
                {
                    began.Add(k.BeganAfter);
                    started.Add(k.StartedAfterEnd);
                    onATick += k.Phase <= 1.5 ? 1 : 0;
                }

                _report.Line($"  kept by the collector and {(isWrong ? "given a wrong number" : "given the right number")}: {some.Count}. The collection was begun {began.Summary("millionths of a second")} after the copy of the hand-over before; the hand-over set out {started.Summary()} after the collection ended; {onATick} of {some.Count} set out a whole number of ticks after the one before, to within 1.5 ms{(isWrong ? "; for example " + string.Join(" | ", some.Take(4).Select(k => $"numbered {k.Frame}, position named {k.Name}, shows {k.Truth}, set out {F(k.StartedAfterEnd)} ms after the collection ended, {F(k.Phase)} ms from a tick")) : string.Empty)}");
            }
        }
        finally
        {
            Volatile.Write(ref stop, true);
            collector.Join(TimeSpan.FromSeconds(5));
            Close(session);
        }
    }

    private void InvestigateWaits()
    {
        var count = _options.Number("count", 60);
        var holdMilliseconds = Math.Clamp(_options.Number("stall-ms", 100), 20, 400);
        _report.Section($"A hand-over kept waiting for the graphics device for about {holdMilliseconds + 15} ms: {count} times with the process running, {count} times with it stopped for {holdMilliseconds} ms of that");

        // It never stops the process by itself: only when told to.
        using var freezer = new Freezer(1, 1, 3_600_000, 3_600_000);
        foreach (var stopped in new[] { false, true })
        {
            var session = OpenSession(TestMedia.Camera, options: Muted);
            try
            {
                var last = session.Folder.FrameCount - 1;
                using var probe = new TruthProbe(session.Engine, session.Folder.Screen, session.Folder.Camera, capacity: 400);

                // The hand-over that has the device when the experiment is armed keeps it: the
                // other clip's hand-over, which set out at the same moment, waits at the door.
                var armed = 0;
                long holdBegan = 0;
                long holdEnded = 0;
                var holder = -1;
                var inner = session.Engine.AfterCopy;
                session.Engine.AfterCopy = handOver =>
                {
                    inner?.Invoke(handOver);
                    if (handOver.Kind != StudioPreviewHandOverKind.Playback || Interlocked.CompareExchange(ref armed, 0, 1) != 1)
                    {
                        return;
                    }

                    var began = Stopwatch.GetTimestamp();
                    if (stopped)
                    {
                        freezer.Now(holdMilliseconds);
                    }

                    var until = began + ((holdMilliseconds + 15) * Stopwatch.Frequency / 1000);
                    while (Stopwatch.GetTimestamp() < until)
                    {
                        Thread.Sleep(1);
                    }

                    Volatile.Write(ref holder, handOver.Clip);
                    Interlocked.Exchange(ref holdBegan, began);
                    Interlocked.Exchange(ref holdEnded, Stopwatch.GetTimestamp());
                };

                var waited = 0;
                var same = 0;
                var laterBy = new Samples();
                var earlier = 0;
                var numbered = 0;
                var wrong = 0;
                var waitedFor = new Samples();
                var examples = new List<string>();
                try
                {
                    session.SeekTo(30);
                    probe.Read();
                    session.Engine.Play();
                    Thread.Sleep(400);
                    for (var trial = 0; trial < count * 3 && waited < count; trial++)
                    {
                        if (session.PositionFrame > last - 75)
                        {
                            session.Engine.Pause();
                            session.WaitForIdle();
                            session.SeekTo(30);
                            session.Engine.Play();
                            Thread.Sleep(400);
                        }

                        probe.Read();
                        Interlocked.Exchange(ref holdEnded, 0);
                        Volatile.Write(ref armed, 1);
                        var asked = Stopwatch.GetTimestamp();
                        while (Interlocked.Read(ref holdEnded) == 0 && Stopwatch.GetElapsedTime(asked).TotalSeconds < 2)
                        {
                            Thread.Sleep(5);
                        }

                        Volatile.Write(ref armed, 0);

                        // Long enough for the players to have caught up, and for the engine to
                        // have its numbers back.
                        Thread.Sleep(500);
                        var began = Interlocked.Read(ref holdBegan);
                        var ended = Interlocked.Read(ref holdEnded);
                        if (ended == 0)
                        {
                            continue;
                        }

                        // The other clip's hand-over that had set out before the device was kept,
                        // and made its copy after it was let go.
                        foreach (var frame in probe.Read())
                        {
                            if (frame.Clip == Volatile.Read(ref holder) || frame.Kind != StudioPreviewHandOverKind.Playback || frame.Truth == FrameCode.Unreadable
                                || frame.StartedAt > began || frame.CopyBeganAt < ended)
                            {
                                continue;
                            }

                            waited++;
                            waitedFor.Add(Stopwatch.GetElapsedTime(frame.StartedAt, frame.CopyBeganAt).TotalMilliseconds);
                            var by = frame.Truth - frame.NameByPosition;
                            same += by == 0 ? 1 : 0;
                            earlier += by < 0 ? 1 : 0;
                            if (by > 0)
                            {
                                laterBy.Add(by);
                            }

                            var known = frame.Knowledge == StudioPreviewFrameKnowledge.Known;
                            numbered += known ? 1 : 0;
                            wrong += known && frame.Frame != frame.Truth ? 1 : 0;
                            if (by != 0 && examples.Count < 4)
                            {
                                examples.Add($"{(frame.Clip == 0 ? "screen" : "camera")} copy {frame.Serial}: its position named frame {frame.NameByPosition} ({F(frame.IntoMilliseconds)} ms in) when it set out, its copy began {F(Stopwatch.GetElapsedTime(frame.StartedAt, frame.CopyBeganAt).TotalMilliseconds)} ms later and took {F(frame.CopyMilliseconds)} ms, and it shows frame {frame.Truth}; the engine: {frame.Knowledge} {frame.Frame} ({frame.Rule})");
                            }
                        }
                    }

                    session.Engine.Pause();
                    session.WaitForIdle();
                }
                finally
                {
                    session.Engine.AfterCopy = inner;
                }

                _report.Line($"  {(stopped ? $"with the process stopped for {holdMilliseconds} ms meanwhile" : "with the process running all the while")}: {waited} hand-overs waited for the device, {waitedFor.Summary()}. {same} held the frame their position had named when they set out; {laterBy.Count} held a later one{(laterBy.Count == 0 ? string.Empty : $" (later by {laterBy.Summary("frames")})")}; {earlier} an earlier one. The engine gave {numbered} of them a number for certain, {wrong} of those wrong{(examples.Count == 0 ? string.Empty : ". For example: " + string.Join(" | ", examples))}");
            }
            finally
            {
                Close(session);
            }
        }

        _report.Line($"  {freezer.Describe()}");
    }

    private void InvestigateStarts()
    {
        var count = _options.Number("count", 240);
        var holdMilliseconds = _options.Number("stall-ms", 100);

        // When the stop is asked for, in milliseconds after Play() was called: anywhere from
        // just before the call to well after the clock has started.
        var latest = Math.Max(0, _options.Number("until", 60));
        _report.Section($"The process stopped once around the start of playback: {count} plays, each stop {holdMilliseconds * 7 / 10} to {holdMilliseconds * 13 / 10} ms long and asked for between 2 ms before Play() and {latest} ms after it");

        // It never stops the process by itself: only when told to.
        using var freezer = new Freezer(1, 1, 3_600_000, 3_600_000);
        var session = OpenSession(TestMedia.Camera, options: Muted);
        try
        {
            var last = session.Folder.FrameCount - 1;
            using var probe = new TruthProbe(session.Engine, session.Folder.Screen, session.Folder.Camera, capacity: 300);
            var totals = new ProbeTotals { ProcessStops = () => freezer.Stalls };
            var latency = new Samples();
            var latencyHeldUp = new Samples();
            var odd = 0;
            var laterFrames = 0;
            var wrongNumbers = 0;
            var stopsInTheStart = 0;
            session.SeekTo(30);
            probe.Read();
            for (var cycle = 0; cycle < count; cycle++)
            {
                if (session.PositionFrame > last - 60)
                {
                    session.SeekTo(30);
                }

                // Where the players rest before the play: every other time reached with a step
                // of one frame, as after a pause in the pause checks, and otherwise with a seek.
                var byStep = cycle % 2 == 0;
                var rest = byStep ? session.PositionFrame + 1 : session.PositionFrame + 2 + _random.Next(20);
                session.Engine.Seek(TestFolder.TimeOf(rest, 0.5));
                var rested = session.WaitForIdle();
                var shown = session.ReadShown();
                probe.Read();
                var stallsBefore = freezer.Stalls.Length;
                var delay = _random.Next(-2, latest + 1);
                var hold = (holdMilliseconds * 7 / 10) + _random.Next((holdMilliseconds * 6 / 10) + 1);

                long played;
                if (delay < 0)
                {
                    // The stop is on its way when Play() is called.
                    freezer.Now(hold);
                    played = Stopwatch.GetTimestamp();
                    session.Engine.Play();
                }
                else
                {
                    played = Stopwatch.GetTimestamp();
                    session.Engine.Play();
                    var until = played + (delay * Stopwatch.Frequency / 1000);
                    while (Stopwatch.GetTimestamp() < until)
                    {
                        Thread.SpinWait(20);
                    }

                    freezer.Now(hold);
                }

                Thread.Sleep(300 + _random.Next(150));
                session.Engine.Pause();
                var position = session.PositionFrame;
                var picture = session.ReadClipFrame(0);
                session.WaitForIdle();
                var frames = probe.Read(totals);
                var stalls = freezer.Stalls;
                var stall = stalls.Length > stallsBefore ? stalls[^1] : default;
                var stallFrom = stall == default ? double.NaN : Milliseconds(played, stall.From);
                var stallTo = stall == default ? double.NaN : Milliseconds(played, stall.To);

                // When the position began to run, from the first frame of playback: while the
                // clock runs, the position is the time on the wall plus a constant.
                var playback = frames.Where(f => f.Kind == StudioPreviewHandOverKind.Playback && f.Truth != FrameCode.Unreadable).ToList();
                var first = playback.FirstOrDefault(f => f.Clip == 0);
                var began = double.NaN;
                if (playback.Any(f => f.Clip == 0))
                {
                    var restMilliseconds = TestFolder.TimeOf(rest, 0.5) * 1000;
                    began = Milliseconds(played, first.StartedAt) - ((first.PositionTicks / 10000.0) - restMilliseconds);
                    (stall != default && stallFrom < began + 5 && stallTo > began - 30 ? latencyHeldUp : latency).Add(began);
                    stopsInTheStart += stall != default && stallFrom <= began && stallTo >= began - 1 ? 1 : 0;
                }

                var later = playback.Where(f => f.Truth > f.NameByPosition).ToList();
                var wrong = playback.Where(f => f.Knowledge == StudioPreviewFrameKnowledge.Known && f.Frame != f.Truth).ToList();
                laterFrames += later.Count;
                wrongNumbers += wrong.Count;
                var pauseWrong = picture != FrameCode.Unreadable && picture != position;
                if (later.Count == 0 && wrong.Count == 0 && !pauseWrong && rested)
                {
                    continue;
                }

                odd++;
                _report.Line($"  play {cycle + 1}: rested on frame {rest} by a {(byStep ? "step" : "seek")} (picture {shown}); the stop was asked for {delay} ms after Play() and held the process from {F(stallFrom)} to {F(stallTo)} ms after it; the position began to run {F(began)} ms after Play(); Pause() returned with Position {position} and the picture on {picture}");
                foreach (var frame in playback.Take(8).Concat(later).Concat(wrong).DistinctBy(f => (f.Clip, f.Serial)).OrderBy(f => f.StartedAt))
                {
                    _report.Line($"      {(frame.Clip == 0 ? "screen" : "camera")} copy {frame.Serial}: set out {F(Milliseconds(played, frame.StartedAt))} ms after Play(), position {F(frame.PositionTicks / 10000.0)} ms = frame {frame.NameByPosition} ({F(frame.IntoMilliseconds)} ms in), the copy began {F(Milliseconds(frame.StartedAt, frame.CopyBeganAt))} ms later and took {F(frame.CopyMilliseconds)} ms, position after it frame {(long)Math.Floor(frame.PositionAfterTicks / 10000.0 * TestMedia.Fps / 1000)}; {frame.Knowledge} {frame.Frame} ({frame.Rule}); shows frame {frame.Truth}{(frame.Truth > frame.NameByPosition ? "  <-- later than its position named" : string.Empty)}{(frame.Knowledge == StudioPreviewFrameKnowledge.Known && frame.Frame != frame.Truth ? "  <-- wrong number" : string.Empty)}");
                }

                if (odd <= 6)
                {
                    // What the engine noted from its last start of the clock on.
                    var lines = session.Trace.Lines();
                    var from = Array.FindLastIndex(lines, line => line.Contains("clock started", StringComparison.Ordinal));
                    foreach (var line in lines.Skip(Math.Max(0, from - 4)).Take(26))
                    {
                        _report.Line("        | " + line);
                    }
                }
            }

            _report.Line($"  {count} plays, {stopsInTheStart} of them with the process stopped at the moment the position began to run; {odd} plays with something to show; {laterFrames} frames showed a later frame than their position named; {wrongNumbers} were given a wrong number. {totals.Describe()}");
            _report.Line($"  from Play() to the position running, plays whose start the stop did not touch: {latency.Summary()}; plays whose start it fell on: {latencyHeldUp.Summary()}");
            _report.Line($"  {freezer.Describe()}");
        }
        finally
        {
            Close(session);
        }

        static double Milliseconds(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;
    }
}
