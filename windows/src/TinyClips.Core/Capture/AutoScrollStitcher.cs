using System.Runtime.InteropServices;

namespace TinyClips.Core.Capture;

/// <summary>
/// Collects the rows revealed by each auto-scroll step and composes the final image: fixed
/// header rows are kept once from the first frame, fixed footer rows once from the last frame,
/// and columns that never scroll (sidebars) are filled with the pane background instead of
/// repeating slices of static content. Ported from ScrollShot (MIT,
/// https://github.com/crutkas/ScrollShot), with TinyClips' <see cref="PanoramaCaptureLimits"/>.
/// </summary>
public sealed class AutoScrollStitcher
{
    private readonly List<byte[]> _strips = new();
    private readonly int[] _scrollVotes;
    private readonly int[] _staticVotes;
    private long _stripBytes;
    private int _stripRows;

    public AutoScrollStitcher(CapturedFrame first, PanoramaCaptureLimits limits)
    {
        First = first;
        Last = first;
        Limits = limits;
        FrameCount = 1;
        _scrollVotes = new int[first.Width];
        _staticVotes = new int[first.Width];
    }

    public CapturedFrame First { get; }

    public CapturedFrame Last { get; private set; }

    public PanoramaCaptureLimits Limits { get; }

    /// <summary>The scrolling pane inside the frame; unknown until the first step moved something.</summary>
    public AutoScrollBounds? Bounds { get; private set; }

    public int FrameCount { get; private set; }

    public int OutputHeight => First.Height + _stripRows;

    public PanoramaCaptureLimitReason? LimitReason { get; private set; }

    public bool ReachedLimit => LimitReason is not null;

    public void SetBounds(AutoScrollBounds bounds) => Bounds = bounds;

    /// <summary>
    /// Appends the <paramref name="shift"/> rows that scrolled into view at the bottom of the pane.
    /// With <paramref name="vote"/>, the step also votes on which columns scroll.
    /// </summary>
    public PanoramaAppendOutcome Append(CapturedFrame previous, CapturedFrame current, int shift, bool vote)
    {
        if (LimitReason is not null)
        {
            return PanoramaAppendOutcome.Skipped;
        }

        if (Bounds is not { } bounds
            || shift <= 0
            || current.Width != First.Width
            || current.Height != First.Height
            || previous.Width != First.Width
            || previous.Height != First.Height)
        {
            return PanoramaAppendOutcome.Skipped;
        }

        shift = Math.Min(shift, bounds.Height);
        var newHeight = OutputHeight + shift;
        if (newHeight > Limits.MaxOutputHeight)
        {
            LimitReason = PanoramaCaptureLimitReason.OutputHeight;
            return PanoramaAppendOutcome.LimitReached(PanoramaCaptureLimitReason.OutputHeight);
        }

        var rowBytes = (long)First.Width * 4;
        var frameBytes = rowBytes * First.Height;
        var stripBytes = shift * rowBytes;
        // Peak memory: every strip, the composed image, and the first, previous and current frames.
        if (_stripBytes + stripBytes + (newHeight * rowBytes) + (frameBytes * 3) > Limits.MaxMemoryBytes)
        {
            LimitReason = PanoramaCaptureLimitReason.Memory;
            return PanoramaAppendOutcome.LimitReached(PanoramaCaptureLimitReason.Memory);
        }

        if (vote)
        {
            AutoScrollFrameAnalysis.VoteColumns(previous, current, bounds.Top, bounds.Bottom, shift, _scrollVotes, _staticVotes);
        }

        var strip = new byte[stripBytes];
        Buffer.BlockCopy(current.BgraPixels, (int)((bounds.Bottom - shift + 1) * rowBytes), strip, 0, strip.Length);
        _strips.Add(strip);
        _stripBytes += stripBytes;
        _stripRows += shift;
        Last = current;
        FrameCount++;

        if (FrameCount >= Limits.MaxFrames)
        {
            LimitReason = PanoramaCaptureLimitReason.FrameCount;
            return PanoramaAppendOutcome.LimitReached(PanoramaCaptureLimitReason.FrameCount);
        }

        return PanoramaAppendOutcome.Accepted;
    }

    /// <summary>Which columns move with the content; ties (e.g. flat background) count as scrolling.</summary>
    public bool[] ScrollingColumns()
    {
        var result = new bool[_scrollVotes.Length];
        for (var x = 0; x < result.Length; x++)
        {
            result[x] = _scrollVotes[x] >= _staticVotes[x];
        }

        return result;
    }

    public PanoramaResult Finish()
    {
        if (Bounds is not { } bounds || _stripRows == 0)
        {
            return new PanoramaResult(First, FrameCount, First.Height, ReachedLimit);
        }

        var width = First.Width;
        var height = First.Height;
        var rowBytes = width * 4;
        var outputHeight = height + _stripRows;
        var pixels = new byte[(long)outputHeight * rowBytes];

        // Fixed header plus the first viewport of the pane.
        var offset = (bounds.Bottom + 1) * rowBytes;
        Buffer.BlockCopy(First.BgraPixels, 0, pixels, 0, offset);

        var scrolling = ScrollingColumns();
        var hasStaticColumns = Array.IndexOf(scrolling, false) >= 0;
        var background = hasStaticColumns ? BackgroundColor(First, bounds) : 0;
        foreach (var strip in _strips)
        {
            if (!hasStaticColumns)
            {
                Buffer.BlockCopy(strip, 0, pixels, offset, strip.Length);
                offset += strip.Length;
                continue;
            }

            var source = MemoryMarshal.Cast<byte, int>(strip.AsSpan());
            var target = MemoryMarshal.Cast<byte, int>(pixels.AsSpan(offset, strip.Length));
            for (var i = 0; i < source.Length; i++)
            {
                target[i] = scrolling[i % width] ? source[i] : background;
            }

            offset += strip.Length;
        }

        // Fixed footer from the last frame.
        var footerRows = height - 1 - bounds.Bottom;
        if (footerRows > 0)
        {
            Buffer.BlockCopy(Last.BgraPixels, (bounds.Bottom + 1) * rowBytes, pixels, offset, footerRows * rowBytes);
        }

        _strips.Clear();
        _stripBytes = 0;
        return new PanoramaResult(new CapturedFrame(pixels, width, outputHeight), FrameCount, outputHeight, ReachedLimit);
    }

    /// <summary>Most common pixel inside the pane of the first frame.</summary>
    private static int BackgroundColor(CapturedFrame frame, AutoScrollBounds bounds)
    {
        var counts = new Dictionary<int, int>();
        var rowStep = Math.Max(1, bounds.Height / 256);
        for (var y = bounds.Top; y <= bounds.Bottom; y += rowStep)
        {
            foreach (var pixel in AutoScrollFrameAnalysis.Row(frame, y))
            {
                counts[pixel] = counts.GetValueOrDefault(pixel) + 1;
            }
        }

        return counts.Count > 0 ? counts.MaxBy(kv => kv.Value).Key : unchecked((int)0xFFFFFFFF);
    }
}

/// <summary>
/// Adaptive step size for auto-scroll, in wheel notches. Each step aims to move ~60 % of the
/// viewport so consecutive frames always overlap; a step that overshoots (apps accelerate
/// bursts of wheel messages) is partly undone before stitching. Once one step has been matched
/// in pixels, the pane's scroll percentage predicts every later shift. Ported from ScrollShot
/// (MIT, https://github.com/crutkas/ScrollShot).
/// </summary>
public sealed class AutoScrollStepController
{
    public const double InitialNotches = 3;
    public const double MinNotches = 1;
    public const double MaxNotches = 40;

    /// <summary>Fraction of the viewport each step aims to scroll.</summary>
    public const double TargetFraction = 0.6;

    /// <summary>A step that scrolled more than this fraction of the viewport is backed up.</summary>
    public const double OvershootFraction = 0.85;

    public double Notches { get; private set; } = InitialNotches;

    /// <summary>Content pixels per scroll percent; 0 until learned from a confident pixel match.</summary>
    public double PixelsPerPercent { get; private set; }

    public static double Clamp(double notches) => Math.Clamp(notches, MinNotches, MaxNotches);

    /// <summary>Predicted shift in pixels from the change in scroll percentage, or -1 when unknown.</summary>
    public double PredictShift(double percentBefore, double percentAfter)
        => PixelsPerPercent > 0 && percentBefore >= 0 && percentAfter >= 0
            ? (percentAfter - percentBefore) * PixelsPerPercent
            : -1;

    public static bool Overshot(double predictedShift, int viewportHeight)
        => predictedShift > OvershootFraction * viewportHeight;

    /// <summary>Notches to scroll back so the step lands near <see cref="TargetFraction"/> of the viewport.</summary>
    public double BackUpNotches(double predictedShift, int viewportHeight)
        => predictedShift <= 0
            ? MinNotches
            : Math.Max(MinNotches, Notches * (predictedShift - (TargetFraction * viewportHeight)) / predictedShift);

    /// <summary>Learns the pixels-per-percent scale from a confident pixel match.</summary>
    public void LearnScale(int shift, double score, double percentBefore, double percentAfter)
    {
        var moved = percentAfter - percentBefore;
        if (shift > 0 && score >= 0.9 && percentBefore >= 0 && moved > 0.01)
        {
            PixelsPerPercent = shift / moved;
        }
    }

    /// <summary>A step grows at most this much at a time, so one bad measurement can't make the next step overshoot.</summary>
    public const double MaxGrowth = 2;

    public void OnMatched(int shift, int viewportHeight)
    {
        if (shift > 0)
        {
            Notches = Clamp(Math.Min(Notches * MaxGrowth, Notches * (TargetFraction * viewportHeight) / shift));
        }
    }

    /// <summary>No overlap was found, so the step was too large: halve it.</summary>
    public void OnNoOverlap() => Notches = Clamp(Notches / 2);
}
