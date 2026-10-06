using System.Diagnostics;
using TinyClips.Core.Studio.Preview;
using TinyClips.Tools.StudioPreviewCheck.Media;

namespace TinyClips.Tools.StudioPreviewCheck.Checks;

// "exits" is the other half of "tails". The engine tells a clip's namer that a hand-over is over
// while its thread is still inside the player's callback: it has yet to give the device back,
// leave the callback and return to the player through the event's stub. A collection that
// begins there keeps the player's thread for as long as it lasts. The namer read the
// collector's total a moment before, so it takes the collection for something that kept the
// next hand-over waiting at the door, with the player free to announce its frames on time, and
// that is what its rule 3 needs to be true.
//
// "tails" begins its collections from another thread, a few millionths of a second after a
// copy: most of them land before the namer is told. Here the player's own thread begins the
// collection, right after the namer was told, so every one of them is in that tail. For
// comparison, every other collection is begun by another thread a little later, when the
// player has had its thread back for longer than it takes to look for a frame: that is the
// case rule 3 was made for.
internal sealed partial class HeadlessChecks
{
    private void InvestigateExits()
    {
        var count = _options.Number("count", 300);
        var laterBy = Math.Clamp(_options.Number("lead", 15), 1, 25);
        var guarded = _options.Flag("guard-rule-3");
        _report.Section($"A full collection begun as a hand-over ends, after the namer was told that it is over: {count} times by the player's own thread at once, {count} times by another thread {laterBy} ms later{(guarded ? "; rule 3 asks for an idle look (--guard-rule-3)" : string.Empty)}");

        // Kept alive so that a full collection lasts about as long as asked for. It never
        // collects by itself: the gap is an hour.
        using var heap = new HeldUp(HoldUpMilliseconds, 3_600_000, 3_600_000);
        _report.Line($"  a full collection holds every managed thread for about {F(heap.StallMilliseconds, "0")} ms ({F(heap.BallastMegabytes, "0")} MB kept alive)");

        // The thread that begins the later collections. It sleeps until it is given a time, and
        // then waits for that time without sleeping.
        long collectAt = 0;
        long collectFrom = 0;
        long collectTo = 0;
        var stop = false;
        using var word = new AutoResetEvent(false);
        var collector = new Thread(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                word.WaitOne(200);
                var at = Interlocked.Exchange(ref collectAt, 0);
                if (at == 0)
                {
                    continue;
                }

                while (Stopwatch.GetTimestamp() < at)
                {
                    Thread.SpinWait(20);
                }

                var from = Stopwatch.GetTimestamp();
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
                Interlocked.Exchange(ref collectFrom, from);
                Interlocked.Exchange(ref collectTo, Stopwatch.GetTimestamp());
            }
        })
        {
            IsBackground = true,
            Name = "StudioPreviewCheck.Exits",
        };
        collector.Start();

        var session = OpenSession(TestMedia.Camera, options: Muted);
        try
        {
            var last = session.Folder.FrameCount - 1;
            using var probe = new TruthProbe(session.Engine, session.Folder.Screen, session.Folder.Camera, capacity: 400);

            // 1: the player's own thread collects before it leaves the callback. 2: another
            // thread is given the word and collects a little later.
            var armed = 0;
            long armedSerial = 0;
            long exitedAt = 0;
            session.Engine.AfterExit = (clip, serial) =>
            {
                if (clip != 0)
                {
                    return;
                }

                var mode = Interlocked.Exchange(ref armed, 0);
                if (mode == 0)
                {
                    return;
                }

                var now = Stopwatch.GetTimestamp();
                Interlocked.Exchange(ref armedSerial, serial);
                Interlocked.Exchange(ref exitedAt, now);
                if (mode == 1)
                {
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
                    Interlocked.Exchange(ref collectFrom, now);
                    Interlocked.Exchange(ref collectTo, Stopwatch.GetTimestamp());
                }
                else
                {
                    Interlocked.Exchange(ref collectAt, now + (laterBy * Stopwatch.Frequency / 1000));
                    word.Set();
                }
            };

            var trials = new List<Trial>();
            var cameraTrials = new List<Trial>();
            try
            {
                session.SeekTo(30);
                probe.Read();
                session.Engine.Play();
                Thread.Sleep(500);
                for (var trial = 0; trial < count * 2; trial++)
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
                    var atOnce = trial % 2 == 0;
                    Interlocked.Exchange(ref collectTo, 0);
                    Volatile.Write(ref armed, atOnce ? 1 : 2);
                    var asked = Stopwatch.GetTimestamp();
                    while (Interlocked.Read(ref collectTo) == 0 && Stopwatch.GetElapsedTime(asked).TotalSeconds < 2)
                    {
                        Thread.Sleep(5);
                    }

                    Volatile.Write(ref armed, 0);

                    // Long enough for the players to have caught up, and for the engine to have
                    // its numbers back.
                    Thread.Sleep(400);
                    var to = Interlocked.Read(ref collectTo);
                    if (to == 0)
                    {
                        continue;
                    }

                    var from = Interlocked.Read(ref collectFrom);
                    var exited = Interlocked.Read(ref exitedAt);
                    var serial = Interlocked.Read(ref armedSerial);
                    var all = probe.Read();
                    var frames = all.Where(f => f.Clip == 0 && f.Kind == StudioPreviewHandOverKind.Playback).OrderBy(f => f.Serial).ToList();
                    var behind = frames.FindIndex(f => f.Serial == serial);
                    if (behind < 0 || behind + 1 >= frames.Count || frames[behind].Truth == FrameCode.Unreadable || frames[behind + 1].Truth == FrameCode.Unreadable)
                    {
                        continue;
                    }

                    var before = frames[behind];
                    var next = frames[behind + 1];
                    trials.Add(new Trial(atOnce, before, next, exited, from, to));

                    // The camera's hand-over that the same collection kept: the first that set
                    // out after the collection began, with the one before it.
                    var camera = all.Where(f => f.Clip == 1 && f.Kind == StudioPreviewHandOverKind.Playback).OrderBy(f => f.Serial).ToList();
                    var kept = camera.FindIndex(f => f.StartedAt > from);
                    if (kept > 0 && camera[kept].Truth != FrameCode.Unreadable && camera[kept - 1].Truth != FrameCode.Unreadable)
                    {
                        cameraTrials.Add(new Trial(atOnce, camera[kept - 1], camera[kept], camera[kept - 1].CopiedAt, from, to));
                    }
                }

                session.Engine.Pause();
                session.WaitForIdle();
            }
            finally
            {
                session.Engine.AfterExit = null;
            }

            foreach (var atOnce in new[] { true, false })
            {
                var how = atOnce
                    ? "begun by the player's own thread as its hand-over ended"
                    : $"begun by another thread {laterBy} ms after a hand-over had ended";
                ReportExits($"the screen clip, collections {how}", trials.Where(t => t.AtOnce == atOnce).ToList());
            }

            foreach (var atOnce in new[] { true, false })
            {
                ReportExits($"the camera clip in the same collections ({(atOnce ? "the screen's thread collecting as its hand-over ended" : $"another thread, {laterBy} ms after the screen's hand-over had ended")})", cameraTrials.Where(t => t.AtOnce == atOnce).ToList());
            }
        }
        finally
        {
            Volatile.Write(ref stop, true);
            word.Set();
            collector.Join(TimeSpan.FromSeconds(5));
            Close(session);
        }
    }

    /// <summary>One collection, the hand-over before it and the one after.</summary>
    private readonly record struct Trial(bool AtOnce, ProbedFrame Before, ProbedFrame Next, long ExitedAt, long CollectFrom, long CollectTo)
    {
        /// <summary>How long the collection held the process, in milliseconds.</summary>
        public double Collected => Stopwatch.GetElapsedTime(CollectFrom, CollectTo).TotalMilliseconds;

        /// <summary>The time between the end of the hand-over before and the beginning of the one after that the collection does not account for.</summary>
        public double Idle => Stopwatch.GetElapsedTime(ExitedAt, Next.StartedAt).TotalMilliseconds - Collected;

        /// <summary>How many frames after the one handed over before the next one really is.</summary>
        public int Step => Next.Truth - Before.Truth;

        public bool Numbered => Next.Knowledge == StudioPreviewFrameKnowledge.Known;

        public bool Wrong => Numbered && Next.Frame != Next.Truth;
    }

    private void ReportExits(string what, List<Trial> trials)
    {
        if (trials.Count == 0)
        {
            _report.Line($"  {what}: none could be read");
            return;
        }

        var collected = new Samples();
        var idle = new Samples();
        var steps = new SortedDictionary<int, int>();
        var byRule = new SortedDictionary<string, (int Count, int Wrong)>(StringComparer.Ordinal);
        foreach (var trial in trials)
        {
            collected.Add(trial.Collected);
            idle.Add(trial.Idle);
            steps[trial.Step] = steps.GetValueOrDefault(trial.Step) + 1;
            var rule = $"{trial.Next.Knowledge} ({trial.Next.Rule})";
            var (number, wrong) = byRule.GetValueOrDefault(rule);
            byRule[rule] = (number + 1, wrong + (trial.Wrong ? 1 : 0));
        }

        _report.Line($"  {what}: {trials.Count}. The collection took {collected.Summary()}; between the hand-over before and the one after, the time it does not account for was {idle.Summary()}");
        _report.Line($"    the hand-over after it held: {string.Join("; ", steps.Select(s => $"{(s.Key == 1 ? "the next frame" : s.Key == 2 ? "the frame after the next" : $"the frame {s.Key} on")} {s.Value} times"))}");
        _report.Line($"    the engine took it as: {string.Join("; ", byRule.OrderByDescending(r => r.Value.Count).Select(r => $"{r.Key} {r.Value.Count} times, {r.Value.Wrong} of them with a wrong number"))}");
        var wrongs = trials.Where(t => t.Wrong).Take(4).ToList();
        if (wrongs.Count > 0)
        {
            _report.Line($"    for example: {string.Join(" | ", wrongs.Select(t => $"after frame {t.Before.Truth}: numbered {t.Next.Frame} ({t.Next.Rule}), position named {t.Next.NameByPosition}, shows {t.Next.Truth}; the collection took {F(t.Collected)} ms, {F(t.Idle)} ms of the wait were not its"))}");
        }

        // What asking rule 3 for an idle look would do with these: it speaks only when the time
        // the collector does not account for is a look and its margin.
        var byTheRule = trials.Where(t => t.Next.Rule == "kept by the collector").ToList();
        if (byTheRule.Count > 0)
        {
            var look = new StudioPreviewNamingSettings().IdleGapMilliseconds;
            var silenced = byTheRule.Where(t => t.Idle < look).ToList();
            _report.Line($"    of the {byTheRule.Count} that rule 3 numbered ({byTheRule.Count(t => t.Wrong)} wrongly), {silenced.Count} came with less than {F(look, "0")} ms that were not the collector's ({silenced.Count(t => t.Wrong)} of them wrongly numbered): those are the ones rule 3 would say nothing about if it asked for an idle look");
        }
    }
}
