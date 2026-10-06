using System.Diagnostics;
using System.Globalization;
using TinyClips.Core.Studio.Preview;
using TinyClips.Core.Studio.Rendering;
using TinyClips.Tools.StudioPreviewCheck.Media;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace TinyClips.Tools.StudioPreviewCheck.Checks;

/// <summary>One frame a player handed over: what the engine took it for, and which frame its pixels show.</summary>
/// <param name="Truth">The frame number read from the strip in the copy, or <see cref="FrameCode.Unreadable"/>.</param>
/// <param name="TimelineFrame">For the screen: the frame of the timeline a frame of that number is shown under, which is the number itself unless the engine counts in frames of the file. -1 for the camera.</param>
/// <param name="EarlierTimelineFrame">The same for <paramref name="EarlierFrame"/>.</param>
internal readonly record struct ProbedFrame(
    int Clip,
    long Serial,
    StudioPreviewHandOverKind Kind,
    long StartedAt,
    long PositionTicks,
    double IntoMilliseconds,
    long CopyBeganAt,
    long CopiedAt,
    long PositionAfterTicks,
    double CollectorBefore,
    double CollectorAfter,
    long NameByPosition,
    StudioPreviewFrameKnowledge Knowledge,
    long Frame,
    string? Rule,
    long EarlierSerial,
    long EarlierFrame,
    int Truth,
    long TimelineFrame = -1,
    long EarlierTimelineFrame = -1)
{
    public double CopyMilliseconds => Stopwatch.GetElapsedTime(CopyBeganAt, CopiedAt).TotalMilliseconds;
}

/// <summary>What a stretch of probed frames comes to.</summary>
internal sealed class ProbeTotals
{
    private readonly List<string> _wrong = [];

    /// <summary>Frames handed over while the clock ran.</summary>
    public int Playback { get; private set; }

    /// <summary>Of those, the ones the engine gave a number, certain or inferred: not the ones it showed under the number of their position and called unsure.</summary>
    public int Numbered { get; private set; }

    /// <summary>Of those, the ones that got their number from the frame after them.</summary>
    public int NumberedLate { get; private set; }

    /// <summary>Frames given a number that their pixels do not show, less the two kinds that are counted apart.</summary>
    public int WrongNumbers { get; private set; }

    /// <summary>
    /// The times the whole process was stopped from outside, when a check does that
    /// (<c>HoldUps.ProcessStops</c>). A player that is stopped in the instant it announces a
    /// frame can put a later frame in its place when it runs again, and nothing the engine can
    /// observe tells: a limit of the engine that the doc describes. A wrong number whose
    /// hand-over such a stop began in is counted apart (<see cref="StoppedInTheMiddle"/>) and
    /// not among <see cref="WrongNumbers"/>, unless <see cref="JudgeLimits"/> says otherwise.
    /// </summary>
    public Func<(long From, long To)[]>? ProcessStops { get; init; }

    /// <summary>
    /// Counts every wrong number as one, also the two kinds the engine says it cannot rule out
    /// (<see cref="StoppedInTheMiddle"/>, <see cref="InferredWrong"/>): <c>--judge-limits</c>.
    /// </summary>
    public bool JudgeLimits { get; init; }

    /// <summary>Frames given a wrong number after the process had been stopped in the middle of their hand-over.</summary>
    public int StoppedInTheMiddle { get; private set; }

    /// <summary>The first few of those, in words.</summary>
    public List<string> StoppedExamples { get; } = [];

    /// <summary>
    /// Frames whose number came from the namer's rule 3 ("kept by the collector": a hand-over
    /// the garbage collector kept waiting is taken for the frame next in line) and was wrong.
    /// Seen about once in 100,000 frames handed over with the collector at work, the cause not
    /// found. The engine calls such a number inferred: it is drawn by, and the frame is fetched
    /// anew when the clock stops on it. Counted apart from <see cref="WrongNumbers"/> and held
    /// against <see cref="InferredAllowed"/>. A wrong number from any other rule is judged.
    /// </summary>
    public int InferredWrong { get; private set; }

    /// <summary>The first few of those, in words.</summary>
    public List<string> InferredExamples { get; } = [];

    /// <summary>How many wrong numbers by rule 3 a check of this size may show: one in 20,000 frames given a number, and one in any case.</summary>
    public int InferredAllowed => Math.Max(1, Numbered / 20000);

    /// <summary>No number was wrong but by rule 3, and no more by rule 3 than <see cref="InferredAllowed"/>.</summary>
    public bool Hold => WrongNumbers == 0 && InferredWrong <= InferredAllowed;

    /// <summary>
    /// Which frames the copies of both kinds show, by clip: a scene drawn with one of them has
    /// the layout of the frame it was taken for, and is counted with them.
    /// </summary>
    public HashSet<(int Clip, int Truth)> LimitFrames { get; } = [];

    /// <summary>Frames the engine could not tell apart.</summary>
    public int WithoutNumber { get; private set; }

    /// <summary>Frames shown under the number their position gave because none had been certain for too long.</summary>
    public int Unsure { get; private set; }

    /// <summary>Of those, the ones that number was wrong for.</summary>
    public int UnsureWrong { get; private set; }

    /// <summary>Frames of playback whose position named another frame than their pixels show: what believing the position would have got wrong.</summary>
    public int PositionsWrong { get; private set; }

    /// <summary>Frames of playback that show a later frame than their position named. Measured: next to never.</summary>
    public int LaterThanPosition { get; private set; }

    /// <summary>
    /// Frames that got no number because their copy took long while a frame came due: such a
    /// copy may hold that frame and not the one the player had announced.
    /// </summary>
    public int CopiesHeldUp { get; private set; }

    /// <summary>Of those, the ones that do hold a later frame than their position named when the hand-over began.</summary>
    public int CopiesHeldUpLater { get; private set; }

    /// <summary>How long those copies took, in milliseconds.</summary>
    public Samples CopiesHeldUpTook { get; } = new();

    /// <summary>Frames handed over while the clock stood, in answer to a position.</summary>
    public int Answers { get; private set; }

    /// <summary>Of those, the ones that do not show the frame of the position they came with.</summary>
    public int AnswersWrong { get; private set; }

    /// <summary>Frames handed over after Pause had stopped the clock, which are left out.</summary>
    public int Late { get; private set; }

    public int Unreadable { get; private set; }

    /// <summary>Copies there was no room to keep the strip of.</summary>
    public int Lost { get; set; }

    /// <summary>The first few frames that were given a wrong number, in words.</summary>
    public IReadOnlyList<string> Wrong => _wrong;

    /// <summary>The first few answers that did not show the frame of their position, in words.</summary>
    public List<string> WrongAnswers { get; } = [];

    public void Add(IReadOnlyList<ProbedFrame> frames)
    {
        var stops = ProcessStops?.Invoke() ?? [];
        var bySerial = new Dictionary<(int Clip, long Serial), ProbedFrame>(frames.Count);
        foreach (var frame in frames)
        {
            bySerial[(frame.Clip, frame.Serial)] = frame;
        }

        foreach (var frame in frames)
        {
            if (frame.Truth == FrameCode.Unreadable)
            {
                Unreadable++;
                continue;
            }

            var clip = frame.Clip == 0 ? "screen" : "camera";
            switch (frame.Kind)
            {
                case StudioPreviewHandOverKind.Late:
                    Late++;
                    break;

                case StudioPreviewHandOverKind.Answer:
                    Answers++;
                    if (frame.Frame != frame.Truth)
                    {
                        AnswersWrong++;
                        if (WrongAnswers.Count < 6)
                        {
                            WrongAnswers.Add($"{clip} copy {frame.Serial}: handed over at position {frame.PositionTicks / 10000.0:0.0} ms, frame {frame.Frame}, and shows frame {frame.Truth}");
                        }
                    }

                    break;

                default:
                    Playback++;
                    PositionsWrong += frame.NameByPosition != frame.Truth ? 1 : 0;
                    LaterThanPosition += frame.Truth > frame.NameByPosition ? 1 : 0;
                    switch (frame.Knowledge)
                    {
                        case StudioPreviewFrameKnowledge.Known:
                            Numbered++;
                            if (frame.Frame != frame.Truth)
                            {
                                var text = $"{clip} copy {frame.Serial}: numbered {frame.Frame} ({frame.Rule}) and shows frame {frame.Truth}; its position named frame {frame.NameByPosition}, {F(frame.IntoMilliseconds)} ms into it; the copy began {F(Stopwatch.GetElapsedTime(frame.StartedAt, frame.CopyBeganAt).TotalMilliseconds)} ms after the hand-over set out and took {F(frame.CopyMilliseconds)} ms";
                                if (!JudgeLimits && frame.Truth > frame.Frame && StopThatBeganIn(frame) is { } stop)
                                {
                                    StoppedInTheMiddle++;
                                    LimitFrames.Add((frame.Clip, frame.Truth));
                                    if (StoppedExamples.Count < 6)
                                    {
                                        StoppedExamples.Add($"{text}; the process was stopped for {F(Stopwatch.GetElapsedTime(stop.From, stop.To).TotalMilliseconds)} ms from {F(Milliseconds(frame.StartedAt, stop.From))} ms after it set out");
                                    }
                                }
                                else if (!JudgeLimits && frame.Rule == "kept by the collector")
                                {
                                    InferredWrong++;
                                    LimitFrames.Add((frame.Clip, frame.Truth));
                                    if (InferredExamples.Count < 6)
                                    {
                                        InferredExamples.Add(text);
                                    }
                                }
                                else
                                {
                                    Note(text);
                                }
                            }

                            break;

                        case StudioPreviewFrameKnowledge.Unsure:
                            Unsure++;
                            UnsureWrong += frame.Frame != frame.Truth ? 1 : 0;
                            break;

                        default:
                            WithoutNumber++;
                            if (frame.Rule == "copy held up")
                            {
                                CopiesHeldUp++;
                                CopiesHeldUpLater += frame.Truth > frame.NameByPosition ? 1 : 0;
                                CopiesHeldUpTook.Add(frame.CopyMilliseconds);
                            }

                            break;
                    }

                    break;
            }

            // The frame before, numbered by this hand-over.
            if (frame.EarlierSerial != 0 && bySerial.TryGetValue((frame.Clip, frame.EarlierSerial), out var earlier) && earlier.Truth != FrameCode.Unreadable)
            {
                Numbered++;
                NumberedLate++;
                WithoutNumber--;
                if (frame.EarlierFrame != earlier.Truth)
                {
                    Note($"{clip} copy {earlier.Serial}: numbered {frame.EarlierFrame} by the frame after it and shows frame {earlier.Truth}; its position had named frame {earlier.NameByPosition}");
                }
            }
        }

        void Note(string text)
        {
            WrongNumbers++;
            if (_wrong.Count < 6)
            {
                _wrong.Add(text);
            }
        }

        // A stop of the whole process that began while the hand-over was under way: from the
        // moment it set out, give or take what it takes for a stop to take hold, to the end of
        // its copy.
        (long From, long To)? StopThatBeganIn(in ProbedFrame frame)
        {
            var slack = 2 * Stopwatch.Frequency / 1000;
            foreach (var stop in stops)
            {
                if (stop.From >= frame.StartedAt - slack && stop.From <= frame.CopiedAt)
                {
                    return stop;
                }
            }

            return null;
        }

        static double Milliseconds(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;

        static string F(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);
    }

    public string Describe() =>
        $"of {Playback} frames handed over while the clock ran, {Numbered} were given a number ({NumberedLate} of them by the frame after them) and {WrongNumbers} of those numbers were wrong; {WithoutNumber} stayed without one; {Unsure} were shown under the number of their position because none had had a number for too long, {UnsureWrong} of them wrongly. Believing every position would have misnumbered {PositionsWrong}{(LaterThanPosition > 0 ? $"; {LaterThanPosition} showed a later frame than their position named" : string.Empty)}{(CopiesHeldUp > 0 ? $"; {CopiesHeldUp} got no number because their copy took long while a frame came due ({CopiesHeldUpTook.Summary()}), and {CopiesHeldUpLater} of those held a later frame than the one announced" : string.Empty)}{(InferredWrong > 0 ? $"; apart from those, {InferredWrong} of the {Numbered} had a wrong number from rule 3, which takes a hand-over the garbage collector kept waiting for the frame next in line: the engine calls such a number inferred, draws by it and fetches the frame anew when the clock stops on it ({InferredAllowed} allowed for {Numbered} numbers; --judge-limits allows none): {string.Join(" | ", InferredExamples.Take(2))}" : string.Empty)}{(StoppedInTheMiddle > 0 ? $"; apart from those, {StoppedInTheMiddle} of the {Numbered} had the number of their position and showed a later frame, the process having been stopped from outside in the middle of their hand-over: a limit of the engine that is not judged here (--judge-limits judges it): {string.Join(" | ", StoppedExamples.Take(2))}" : string.Empty)}{(Unreadable + Lost > 0 ? $"; {Unreadable} could not be read and {Lost} were not kept" : string.Empty)}";
}

/// <summary>
/// Finds out which frame every copy of an engine's players really holds, without getting in the
/// way: the engine says after each copy what it took the frame for, and the strip with the frame
/// number is copied on, inside the graphics device, into a small texture of that copy's own.
/// The strips are read when asked for.
/// </summary>
internal sealed class TruthProbe : IDisposable
{
    private readonly StudioPreviewEngine _engine;
    private readonly StudioGraphicsDevice _graphics;
    private readonly ClipSpec[] _specs;
    private readonly ID3D11Texture2D[] _strips;
    private readonly (StudioPreviewHandOver HandOver, int Width, int Height)[] _kept;
    private readonly int _slotWidth;
    private readonly int _slotHeight;
    private readonly byte[] _pixels;
    private int _count;
    private int _lost;

    /// <param name="capacity">How many copies can be kept between two calls of <see cref="Read"/>.</param>
    public TruthProbe(StudioPreviewEngine engine, ClipSpec screen, ClipSpec? camera, int capacity)
    {
        _engine = engine;
        _graphics = engine.GraphicsDevice;
        _specs = camera is null ? [screen] : [screen, camera];
        foreach (var spec in _specs)
        {
            // Room for the strip of a copy at the clip's own size, which is the largest there is.
            var bounds = FrameCode.Bounds(spec, ClipMap.Scaled(spec, spec.Width, spec.Height), spec.Width, spec.Height);
            _slotWidth = Math.Max(_slotWidth, bounds.Width);
            _slotHeight = Math.Max(_slotHeight, bounds.Height);
        }

        _strips = new ID3D11Texture2D[capacity];
        _kept = new (StudioPreviewHandOver, int, int)[capacity];
        _pixels = new byte[_slotWidth * _slotHeight * 4];
        lock (_graphics.Gate)
        {
            for (var index = 0; index < capacity; index++)
            {
                _strips[index] = _graphics.Device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)_slotWidth,
                    Height = (uint)_slotHeight,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource,
                    CPUAccessFlags = CpuAccessFlags.None,
                    MiscFlags = ResourceOptionFlags.None,
                });
            }
        }

        engine.AfterCopy = OnCopy;
    }

    /// <summary>Copies there was no room to keep since the last <see cref="Read"/>.</summary>
    public int Lost => Volatile.Read(ref _lost);

    /// <summary>
    /// Every copy since the last call, with the frame its pixels show, in the order they were
    /// made. Waits for the graphics device, so not to be called while something is being timed.
    /// </summary>
    public List<ProbedFrame> Read(ProbeTotals? totals = null)
    {
        var frames = new List<ProbedFrame>();
        lock (_graphics.Gate)
        {
            var count = Math.Min(_count, _strips.Length);
            for (var index = 0; index < count; index++)
            {
                var (h, width, height) = _kept[index];
                var spec = _specs[h.Clip];
                var map = ClipMap.Scaled(spec, width, height);
                var bounds = FrameCode.Bounds(spec, map, width, height);
                _graphics.ReadTexture(_strips[index], 0, _pixels);
                var truth = FrameCode.Decode(_pixels, _slotWidth, _slotHeight, spec, map.Offset(-bounds.X, -bounds.Y));
                frames.Add(new ProbedFrame(
                    h.Clip, h.Serial, h.Kind, h.StartedAt, h.PositionTicks, h.IntoMilliseconds, h.CopyBeganAt, h.CopiedAt, h.PositionAfterTicks,
                    h.CollectorBefore, h.CollectorAfter, h.NameByPosition, h.Knowledge, h.Frame, h.Rule, h.EarlierSerial, h.EarlierFrame, truth, h.TimelineFrame, h.EarlierTimelineFrame));
                _kept[index] = default;
            }

            if (totals is not null)
            {
                totals.Lost += _lost;
            }

            _count = 0;
            _lost = 0;
        }

        totals?.Add(frames);
        return frames;
    }

    public void Dispose()
    {
        if (_engine.AfterCopy == OnCopy)
        {
            _engine.AfterCopy = null;
        }

        lock (_graphics.Gate)
        {
            foreach (var strip in _strips)
            {
                strip.Dispose();
            }
        }
    }

    // On the player's own thread, with the device lock held.
    private void OnCopy(StudioPreviewHandOver handOver)
    {
        var index = _count;
        if (index >= _strips.Length || handOver.Clip >= _specs.Length || !ReferenceEquals(_graphics, _engine.GraphicsDevice))
        {
            _lost++;
            return;
        }

        var spec = _specs[handOver.Clip];
        var bounds = FrameCode.Bounds(spec, ClipMap.Scaled(spec, handOver.Width, handOver.Height), handOver.Width, handOver.Height);
        if (bounds.Width > _slotWidth || bounds.Height > _slotHeight)
        {
            _lost++;
            return;
        }

        _graphics.Context.CopySubresourceRegion(_strips[index], 0, 0, 0, 0, handOver.Texture, 0, new Box(bounds.X, bounds.Y, 0, bounds.X + bounds.Width, bounds.Y + bounds.Height, 1));

        // The texture is the engine's, and is not kept.
        _kept[index] = (handOver with { Texture = null! }, handOver.Width, handOver.Height);
        _count = index + 1;
    }
}
