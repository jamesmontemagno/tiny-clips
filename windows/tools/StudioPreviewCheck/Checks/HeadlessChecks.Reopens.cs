using System.Diagnostics;
using TinyClips.Core.Studio.Preview;
using TinyClips.Tools.StudioPreviewCheck.Media;

namespace TinyClips.Tools.StudioPreviewCheck.Checks;

// --investigate reopens: previews opened in the circumstances in which two editors in a row did
// not open ("The screen recording could not be decoded (DecodingError)"): a few milliseconds
// after another preview was closed, while another is being closed, with another alive, and with
// more and more alive. Every preview has a surface attached. An open that fails leaves its
// trace; one that the engine opened at its second attempt is counted, with its trace, at the
// end of the run.
internal sealed partial class HeadlessChecks
{
    private void InvestigateReopens()
    {
        var count = _options.Number("count", 300);
        var scenario = _options.Text("scenario", "after-close").ToLowerInvariant();
        _dumps = int.MinValue / 2;
        switch (scenario)
        {
            case "after-close":
                ReopensAfterAClose(count, overlapping: false);
                break;
            case "while-closing":
                ReopensAfterAClose(count, overlapping: true);
                break;
            case "two-alive":
                ReopensBesideAnother(count, playing: false);
                break;
            case "two-playing":
                ReopensBesideAnother(count, playing: true);
                break;
            case "pile":
                PileOfPreviews(count);
                break;
            case "undecodable":
                OpensOfFilesThatCannotBeDecoded();
                break;
            default:
                throw new ArgumentException("--scenario takes after-close, while-closing, two-alive, two-playing, pile or undecodable.");
        }
    }

    /// <summary>A project of each kind in turn: the camera 0.2 s late, the camera from the start, no camera.</summary>
    private TestFolder FolderOfKind(int index, out string kind)
    {
        kind = (index % 3) switch { 0 => "camera late", 1 => "camera from the start", _ => "no camera" };
        return index % 3 == 2 ? TestFolder.Create(_media, camera: null) : TestFolder.Create(_media, TestMedia.Camera, index % 3 == 0 ? Late : 0);
    }

    /// <summary>
    /// Opens a preview with a surface and looks at its first picture. Null when it did not open;
    /// what went wrong is then in the report, with the trace of the open.
    /// </summary>
    private Session? OpenAndLook(int index, string circumstances, Samples times, ref int failed, ref int wrong)
    {
        var folder = FolderOfKind(index, out var kind);
        var watch = Stopwatch.StartNew();
        Session session;
        try
        {
            session = Session.Open(Muted, folder);
        }
        catch (Exception ex)
        {
            folder.Dispose();
            failed++;
            _report.Line($"  open {index + 1} ({kind}, {circumstances}): FAILED after {F(watch.Elapsed.TotalMilliseconds, "0")} ms: {Describe(ex)}{DumpFailedOpen(ex, "reopen")}");
            return null;
        }

        times.Add(session.OpenMilliseconds);
        var expected = new Shown(0, session.ExpectedCamera(0));
        var shown = session.ReadShown();
        if (shown != expected || session.Events.FailedEvents > 0)
        {
            wrong++;
            _report.Line($"  open {index + 1} ({kind}, {circumstances}): opened in {F(session.OpenMilliseconds, "0")} ms on {shown}, not on {expected}; {string.Join("; ", session.Events.Failures())}{session.Dump(_failuresDirectory, $"reopen-wrong-{index + 1}")}");
        }

        return session;
    }

    // ---------------------------------------------------------------------------------------
    // One after the other, the next a few milliseconds after the one before was closed
    // ---------------------------------------------------------------------------------------

    private void ReopensAfterAClose(int count, bool overlapping)
    {
        int[] gaps = [0, 1, 2, 3, 5, 10, 20];
        string[] doing = ["at rest", "playing", "in the middle of a seek", "just paused"];
        _report.Section(overlapping
            ? $"Investigation: {count} previews, each opened while the one before is still being closed"
            : $"Investigation: {count} previews, each opened 0 to 20 ms after the one before was closed");
        _report.Line("  Every preview has a surface attached. Before it is closed it is at rest, playing, in the middle of a seek, or just paused, in turn. Only what goes wrong is listed.");
        var times = new Samples();
        var closes = new Samples();
        var failed = 0;
        var wrong = 0;
        var opened = 0;
        Task? closing = null;
        var watch = Stopwatch.StartNew();
        for (var index = 0; index < count; index++)
        {
            var gap = gaps[index % gaps.Length];
            var before = doing[index / gaps.Length % doing.Length];
            var session = OpenAndLook(index, overlapping ? "while the one before closes" : $"{gap} ms after the one before closed", times, ref failed, ref wrong);
            if (closing is not null)
            {
                // The one before, which was still closing while this one opened.
                closing.GetAwaiter().GetResult();
                closing = null;
            }

            if (session is null)
            {
                continue;
            }

            opened++;
            switch (before)
            {
                case "playing":
                    session.Engine.Play();
                    Thread.Sleep(60);
                    break;
                case "in the middle of a seek":
                    session.Engine.Seek(TestFolder.TimeOf(90 + (index % 100), 0.5));
                    Thread.Sleep(10);
                    break;
                case "just paused":
                    session.Engine.Play();
                    Thread.Sleep(60);
                    session.Engine.Pause();
                    break;
            }

            var closeWatch = Stopwatch.StartNew();
            if (overlapping)
            {
                closing = Task.Run(session.Close);
            }
            else
            {
                session.Close();
                closes.Add(closeWatch.Elapsed.TotalMilliseconds);
                if (gap > 0)
                {
                    Thread.Sleep(gap);
                }
            }
        }

        closing?.GetAwaiter().GetResult();
        _report.Line();
        _report.Line($"  {count} opens in {F(watch.Elapsed.TotalSeconds, "0")} s: {opened} opened, {failed} FAILED, {wrong} opened on a wrong picture; open time {times.Summary()}{(closes.Count == 0 ? string.Empty : $"; close time {closes.Summary()}")}");
    }

    // ---------------------------------------------------------------------------------------
    // One after the other, with another preview alive all the while
    // ---------------------------------------------------------------------------------------

    private void ReopensBesideAnother(int count, bool playing)
    {
        _report.Section($"Investigation: {count} previews opened and closed one after the other while another preview is open{(playing ? " and playing" : string.Empty)}");
        _report.Line("  Every preview has a surface attached. Only what goes wrong is listed.");
        var other = OpenSession(TestMedia.Camera);
        try
        {
            var last = other.Folder.FrameCount - 1;
            var times = new Samples();
            var failed = 0;
            var wrong = 0;
            var opened = 0;
            var otherWrong = 0;
            var watch = Stopwatch.StartNew();
            other.SeekTo(30);
            if (playing)
            {
                other.Engine.Play();
            }

            for (var index = 0; index < count; index++)
            {
                if (playing && (other.PositionFrame > last - 60 || !other.Engine.IsPlaying))
                {
                    other.Engine.Seek(TestFolder.TimeOf(30, 0.5));
                    other.Engine.Play();
                }

                var session = OpenAndLook(index, playing ? "beside one that plays" : "beside one that is open", times, ref failed, ref wrong);
                if (session is not null)
                {
                    opened++;
                    session.Close();
                }

                if (other.Events.FailedEvents > 0)
                {
                    otherWrong++;
                    _report.Line($"  after open {index + 1}: the preview that was open all the while FAILED: {string.Join("; ", other.Events.Failures())}{other.Dump(_failuresDirectory, $"reopen-other-{index + 1}")}");
                    break;
                }
            }

            if (playing)
            {
                other.Engine.Pause();
            }

            // The one that was open all the while still does what it is asked.
            var sought = otherWrong == 0 && other.SeekTo(120) && other.ReadShown() == new Shown(120, other.ExpectedCamera(120));
            _report.Line();
            _report.Line($"  {count} opens in {F(watch.Elapsed.TotalSeconds, "0")} s: {opened} opened, {failed} FAILED, {wrong} opened on a wrong picture; open time {times.Summary()}; the preview that was open all the while {(sought ? "shows the frame it is sent to afterwards" : "DID NOT show the frame it was sent to afterwards")}");
        }
        finally
        {
            Close(other);
        }
    }

    // ---------------------------------------------------------------------------------------
    // More and more previews alive at once
    // ---------------------------------------------------------------------------------------

    private void PileOfPreviews(int most)
    {
        most = Math.Clamp(most, 2, 40);
        _report.Section($"Investigation: previews opened and kept open, up to {most} at once");
        _report.Line("  How many previews can be open at once before one does not open, and what it says then. Each has a surface attached and two players, or one for a project without a camera.");
        var alive = new List<Session>();
        var times = new Samples();
        var failed = 0;
        var wrong = 0;
        try
        {
            for (var index = 0; index < most; index++)
            {
                var session = OpenAndLook(index, $"with {alive.Count} open already", times, ref failed, ref wrong);
                if (session is null)
                {
                    // Once more, to see whether it is the number that counts or the moment.
                    Thread.Sleep(500);
                    session = OpenAndLook(index, $"again half a second later, with {alive.Count} open already", times, ref failed, ref wrong);
                    if (session is null)
                    {
                        break;
                    }
                }

                alive.Add(session);
                _report.Line($"  {alive.Count} open: the last opened in {F(session.OpenMilliseconds, "0")} ms");
            }

            // Every one of them still shows the frame it is sent to.
            var stuck = 0;
            for (var index = 0; index < alive.Count; index++)
            {
                var frame = 60 + index;
                if (!alive[index].SeekTo(frame) || alive[index].ReadShown() != new Shown(frame, alive[index].ExpectedCamera(frame)) || alive[index].Events.FailedEvents > 0)
                {
                    stuck++;
                    _report.Line($"  preview {index + 1} of {alive.Count} did not show frame {frame} when sent there: {alive[index].ReadShown()}; {string.Join("; ", alive[index].Events.Failures())}");
                }
            }

            _report.Line();
            _report.Line($"  {alive.Count} previews were open at once; {failed} opens FAILED, {wrong} opened on a wrong picture, {stuck} of those open did not show a frame they were sent to; open time {times.Summary()}");
        }
        finally
        {
            foreach (var session in alive)
            {
                session.Close();
            }
        }
    }

    // ---------------------------------------------------------------------------------------
    // Files that cannot be decoded: how long until the user is told
    // ---------------------------------------------------------------------------------------

    private void OpensOfFilesThatCannotBeDecoded()
    {
        var repeats = Math.Clamp(_options.Number("count", 5), 1, 50);
        _report.Section($"Investigation: opening a project whose screen recording cannot be decoded, {repeats} times each");
        _report.Line("  How long OpenAsync takes to say so, what it says, the error code underneath, and how many attempts it made. The last two are a player that says it cannot decode what it opened (simulated: no file made a player say so), which is opened once more after a wait.");
        var source = File.ReadAllBytes(TestMedia.PathOf(_media, TestMedia.Screen));
        var random = new Random(20261004);
        var noise = new byte[source.Length];
        random.NextBytes(noise);

        // Where the picture data begins: the box "mdat".
        var data = IndexOf(source, "mdat"u8);
        var index = IndexOf(source, "moov"u8);
        _report.Line($"  the test clip is {source.Length} bytes; its index (moov) is at {index}, its picture data (mdat) at {data}");
        (string Name, Func<byte[]> Bytes)[] files =
        [
            ("not a video at all: 4096 bytes of noise", () => noise[..4096]),
            ("an empty file", () => []),
            ("cut off after a tenth of its length", () => source[..(source.Length / 10)]),
            ("cut off at half its length", () => source[..(source.Length / 2)]),
            ("its picture data replaced by noise, the index intact", () => WithNoise(source, noise, data + 8, source.Length)),
            ("the second half of its picture data replaced by noise", () => WithNoise(source, noise, Math.Max(data + 8, source.Length / 2), source.Length)),
        ];
        (string Name, Func<byte[]> Bytes, StudioPreviewOptions Options)[] cases =
        [
            .. files.Select(file => (file.Name, file.Bytes, Muted)),
            ("the clip as it is, with a player that stops decoding at both attempts", () => source, Muted with { PlayersStopDecodingWhileOpening = 2 }),
            ("the clip as it is, with a player that stops decoding at the first attempt only", () => source, Muted with { PlayersStopDecodingWhileOpening = 1 }),
        ];
        foreach (var (name, bytes, options) in cases)
        {
            var times = new Samples();
            var said = new SortedDictionary<string, int>(StringComparer.Ordinal);
            for (var repeat = 0; repeat < repeats; repeat++)
            {
                using var folder = TestFolder.Create(_media, TestMedia.Camera, Late, writeScreen: false);
                File.WriteAllBytes(folder.Paths.ScreenPath, bytes());
                var watch = Stopwatch.StartNew();
                string outcome;
                try
                {
                    var session = Session.Open(options, folder);
                    var elapsed = watch.Elapsed.TotalMilliseconds;
                    times.Add(elapsed);

                    // It opened. What does it do when it is sent into what is not there?
                    var failures = session.Events.FailedEvents;
                    var opened = session.ReadShown();
                    var before = session.Engine.GetDiagnostics();
                    var target = folder.FrameCount - 30;
                    var sought = Stopwatch.StartNew();
                    session.Engine.Seek(TestFolder.TimeOf(target, 0.5));
                    var idle = session.WaitForIdle(12);
                    var after = session.Engine.GetDiagnostics();
                    outcome = $"opened at attempt {before.OpenAttempts} on {opened}; sent to frame {target}: "
                        + (session.Events.FailedEvents > failures ? "Failed raised: " + string.Join("; ", session.Events.Failures())
                            : !idle ? "did not come to rest in 12 s"
                            : $"at rest after {Math.Round(sought.Elapsed.TotalSeconds, 1):0.0} s on {session.ReadShown()} with Position on frame {session.PositionFrame}, no failure reported; detours to fetch the frame {after.Repairs - before.Repairs}, given up {after.RepairFailures - before.RepairFailures}, answers given up waiting for {after.AnswersGivenUp - before.AnswersGivenUp}");
                    session.Close();
                }
                catch (Exception ex)
                {
                    times.Add(watch.Elapsed.TotalMilliseconds);
                    outcome = $"{Describe(ex)}; attempts {AttemptsOf(ex)}";
                }

                said[outcome] = said.GetValueOrDefault(outcome) + 1;
            }

            _report.Line($"  {name}: {times.Summary()}");
            foreach (var (outcome, times2) in said)
            {
                _report.Line($"      {times2} x {outcome}");
            }
        }

        static byte[] WithNoise(byte[] source, byte[] noise, int from, int to)
        {
            var copy = (byte[])source.Clone();
            Array.Copy(noise, from, copy, from, to - from);
            return copy;
        }

        static int IndexOf(byte[] bytes, ReadOnlySpan<byte> name) => bytes.AsSpan().IndexOf(name) - 4;
    }
}
