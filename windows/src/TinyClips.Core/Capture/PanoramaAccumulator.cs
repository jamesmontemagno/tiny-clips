namespace TinyClips.Core.Capture;

public enum PanoramaAppendStatus
{
    Accepted,
    Skipped,
    LimitReached,
}

public readonly record struct PanoramaAppendOutcome(PanoramaAppendStatus Status, PanoramaCaptureLimitReason? LimitReason)
{
    public static PanoramaAppendOutcome Accepted { get; } = new(PanoramaAppendStatus.Accepted, null);

    public static PanoramaAppendOutcome Skipped { get; } = new(PanoramaAppendStatus.Skipped, null);

    public static PanoramaAppendOutcome LimitReached(PanoramaCaptureLimitReason reason)
        => new(PanoramaAppendStatus.LimitReached, reason);
}

public sealed record PanoramaResult(CapturedFrame Image, int FrameCount, int OutputHeight, bool ReachedLimit);

/// <summary>
/// Stitches scrolling-capture frames into the output buffer as they arrive so that peak memory
/// tracks the size of the panorama instead of the number of captured frames. Direct port of the
/// macOS <c>PanoramaAccumulator</c>: vertical shift is found by comparing per-row luma
/// signatures, verified against real pixels, and a stationary bottom band (sticky footer) is
/// held back and written once at the end.
/// </summary>
public sealed class PanoramaAccumulator
{
    public readonly record struct Alignment(int Shift, double Score, int FixedBottomHeight);

    private byte[] _output = Array.Empty<byte>();
    private long _outputLength;
    private int _committedRows;
    private int _heldBottomBand;

    public PanoramaAccumulator(PanoramaCaptureLimits limits)
    {
        Limits = limits;
    }

    public PanoramaCaptureLimits Limits { get; }

    public PanoramaFrame? PreviousFrame { get; private set; }

    public int AcceptedFrameCount { get; private set; }

    public PanoramaCaptureLimitReason? LimitReason { get; private set; }

    public bool ReachedLimit => LimitReason is not null;

    /// <summary>Height the panorama would have if the capture stopped right now.</summary>
    public int PendingOutputHeight
    {
        get
        {
            if (PreviousFrame is not { } previous)
            {
                return 0;
            }

            return _committedRows == 0 ? previous.Height : _committedRows + _heldBottomBand;
        }
    }

    public PanoramaAppendOutcome Append(PanoramaFrame frame)
    {
        if (LimitReason is not null)
        {
            return PanoramaAppendOutcome.Skipped;
        }

        if (PreviousFrame is not { } previous)
        {
            if (!Fits(frame.Height, frame.Width, frame))
            {
                LimitReason = PanoramaCaptureLimitReason.Memory;
                return PanoramaAppendOutcome.LimitReached(PanoramaCaptureLimitReason.Memory);
            }

            PreviousFrame = frame;
            AcceptedFrameCount = 1;
            return PanoramaAppendOutcome.Accepted;
        }

        if (frame.Width != previous.Width || frame.Height != previous.Height)
        {
            return PanoramaAppendOutcome.Skipped;
        }

        if (EstimateVerticalShift(previous, frame) is not { } alignment)
        {
            return PanoramaAppendOutcome.Skipped;
        }

        var height = frame.Height;
        var isFirstCommit = _committedRows == 0;
        var previousBand = isFirstCommit ? alignment.FixedBottomHeight : _heldBottomBand;
        // The stationary band is detected independently of the scroll step, so a footer taller
        // than the step is still fully suppressed. It can only grow by the rows that scrolled in
        // since the last frame, which keeps every committed row accounted for exactly once.
        var fixedBottomHeight = isFirstCommit
            ? alignment.FixedBottomHeight
            : Math.Min(alignment.FixedBottomHeight, alignment.Shift + previousBand);
        var baseRows = isFirstCommit ? height - fixedBottomHeight : _committedRows;
        var appendCount = alignment.Shift + previousBand - fixedBottomHeight;
        var sourceStartRow = height - previousBand - alignment.Shift;
        if (appendCount < 0 || sourceStartRow < 0 || baseRows <= 0)
        {
            return PanoramaAppendOutcome.Skipped;
        }

        var prospectiveHeight = baseRows + appendCount + fixedBottomHeight;
        if (prospectiveHeight > Limits.MaxOutputHeight)
        {
            LimitReason = PanoramaCaptureLimitReason.OutputHeight;
            return PanoramaAppendOutcome.LimitReached(PanoramaCaptureLimitReason.OutputHeight);
        }

        if (!Fits(prospectiveHeight, frame.Width, frame))
        {
            LimitReason = PanoramaCaptureLimitReason.Memory;
            return PanoramaAppendOutcome.LimitReached(PanoramaCaptureLimitReason.Memory);
        }

        if (isFirstCommit)
        {
            // Reserve the whole first stitch at once so the actual capacity matches what Fits predicted.
            EnsureCapacity((long)(baseRows + appendCount) * frame.Width * 4);
            AppendRows(previous, 0, baseRows);
            _committedRows = baseRows;
        }

        AppendRows(frame, sourceStartRow, appendCount);
        _committedRows += appendCount;
        _heldBottomBand = fixedBottomHeight;
        PreviousFrame = frame;
        AcceptedFrameCount++;

        if (AcceptedFrameCount >= Limits.MaxFrames)
        {
            LimitReason = PanoramaCaptureLimitReason.FrameCount;
            return PanoramaAppendOutcome.LimitReached(PanoramaCaptureLimitReason.FrameCount);
        }

        return PanoramaAppendOutcome.Accepted;
    }

    /// <summary>
    /// Flushes the held footer band and materializes the panorama image. Stopping always yields
    /// an image when at least one frame was accepted: a single frame (nothing scrolled yet) is
    /// returned as-is.
    /// </summary>
    public PanoramaResult Finish()
    {
        if (PreviousFrame is not { } last)
        {
            throw new PanoramaCaptureException(PanoramaCaptureError.NoFrames);
        }

        if (_committedRows == 0)
        {
            // Nothing was stitched yet (no scroll, or a limit hit on the very first commit): the
            // single retained frame is still a usable screenshot.
            return new PanoramaResult(last.ToCapturedFrame(), AcceptedFrameCount, last.Height, ReachedLimit);
        }

        var bytesPerRow = last.Width * 4;
        var outputHeight = _committedRows + _heldBottomBand;
        var pixels = new byte[(long)outputHeight * bytesPerRow];
        Buffer.BlockCopy(_output, 0, pixels, 0, (int)_outputLength);
        if (_heldBottomBand > 0)
        {
            var start = (last.Height - _heldBottomBand) * bytesPerRow;
            Buffer.BlockCopy(last.BgraPixels, start, pixels, (int)_outputLength, _heldBottomBand * bytesPerRow);
        }

        _output = Array.Empty<byte>();
        _outputLength = 0;

        return new PanoramaResult(
            new CapturedFrame(pixels, last.Width, outputHeight),
            AcceptedFrameCount,
            outputHeight,
            LimitReason is not null);
    }

    private void AppendRows(PanoramaFrame frame, int startRow, int rowCount)
    {
        var bytesPerRow = frame.Width * 4;
        var byteCount = (long)rowCount * bytesPerRow;
        EnsureCapacity(_outputLength + byteCount);
        Buffer.BlockCopy(frame.BgraPixels, startRow * bytesPerRow, _output, (int)_outputLength, (int)byteCount);
        _outputLength += byteCount;
    }

    private void EnsureCapacity(long required)
    {
        if (required <= _output.LongLength)
        {
            return;
        }

        var grown = new byte[GrownCapacity(_output.LongLength, required)];
        Buffer.BlockCopy(_output, 0, grown, 0, (int)_outputLength);
        _output = grown;
    }

    /// <summary>Amortized growth: double, but never beyond 1.5x the required size, so over-allocation stays bounded and predictable for <see cref="Fits"/>.</summary>
    private static long GrownCapacity(long current, long required)
        => Math.Max(required, Math.Min(current * 2, Math.Min(required + (required / 2), Array.MaxLength)));

    /// <summary>Capacity the output buffer will actually hold after growing to fit <paramref name="requiredBytes"/>.</summary>
    private long PredictedCapacity(long requiredBytes)
        => requiredBytes <= _output.LongLength ? _output.LongLength : GrownCapacity(_output.LongLength, requiredBytes);

    /// <summary>
    /// Peak memory is the (possibly over-allocated) output buffer plus the exact-size copy made for
    /// the final image, plus the retained and incoming frames.
    /// </summary>
    private bool Fits(int outputHeight, int width, PanoramaFrame frame)
    {
        var outputBytes = (long)width * outputHeight * 4;
        var bufferBytes = PredictedCapacity(outputBytes);
        return bufferBytes + outputBytes + (frame.ByteCount * 2) <= Limits.MaxMemoryBytes;
    }

    /// <summary>
    /// Cheap duplicate-frame test: samples an 80x80 grid and reports whether the mean absolute
    /// luma difference is large enough to be worth aligning.
    /// </summary>
    public static bool AreMeaningfullyDifferent(PanoramaFrame first, PanoramaFrame second)
    {
        if (first.Width != second.Width || first.Height != second.Height)
        {
            return true;
        }

        var rowStep = Math.Max(1, first.Height / 80);
        var columnStep = Math.Max(1, first.Width / 80);
        var difference = 0.0;
        var samples = 0;
        for (var y = 0; y < first.Height; y += rowStep)
        {
            for (var x = 0; x < first.Width; x += columnStep)
            {
                var index = ((y * first.Width) + x) * 4;
                difference += Math.Abs(PanoramaFrame.Luma(first.BgraPixels, index) - PanoramaFrame.Luma(second.BgraPixels, index));
                samples++;
            }
        }

        return samples == 0 || difference / samples > 2.5;
    }

    public static Alignment? EstimateVerticalShift(PanoramaFrame previous, PanoramaFrame current)
    {
        var height = previous.Height;
        if (height <= 8 || previous.RowLuma.Length != height || current.RowLuma.Length != height)
        {
            return null;
        }

        // Scrolling content is usually periodic (repeating rows, cards, table stripes), so the
        // globally lowest score is often a later alias of the real shift. Overshooting duplicates
        // rows that are already committed, so the smallest credible shift is always the right choice.
        var ignoredTopBand = Math.Max(1, height / 20);
        var ignoredBottomBand = Math.Max(1, height / 20);
        const int minimumShift = 2;
        var maximumShift = Math.Max(minimumShift, height - (height / 10));
        var minimumSamples = Math.Max(8, height / 8);

        var scores = new float[maximumShift + 1];
        Array.Fill(scores, float.MaxValue);
        var bestScore = float.MaxValue;
        var previousRows = previous.RowLuma;
        var currentRows = current.RowLuma;
        for (var shift = minimumShift; shift <= maximumShift; shift++)
        {
            var comparisonEnd = height - ignoredBottomBand - shift;
            var samples = comparisonEnd - ignoredTopBand;
            if (samples < minimumSamples)
            {
                continue;
            }

            var total = 0f;
            for (var y = ignoredTopBand; y < comparisonEnd; y++)
            {
                total += Math.Abs(currentRows[y] - previousRows[y + shift]);
            }

            var score = total / samples;
            scores[shift] = score;
            if (score < bestScore)
            {
                bestScore = score;
            }
        }

        if (bestScore == float.MaxValue)
        {
            return null;
        }

        // Anything within this band of the best score is statistically the same match, so prefer
        // the earliest one.
        var acceptanceScore = Math.Max(bestScore * 1.6f, bestScore + 0.5f);
        int? candidate = null;
        var verificationAttempts = 0;
        for (var shift = minimumShift; shift <= maximumShift; shift++)
        {
            if (scores[shift] > acceptanceScore)
            {
                continue;
            }

            var isLocalMinimum = (shift == minimumShift || scores[shift] <= scores[shift - 1])
                && (shift == maximumShift || scores[shift] <= scores[shift + 1]);
            if (!isLocalMinimum)
            {
                continue;
            }

            if (PixelAlignmentScore(previous, current, shift) <= 12)
            {
                candidate = shift;
                break;
            }

            verificationAttempts++;
            // Verification is the expensive part, so give up rather than scan a frame that is
            // not a scroll of the previous one.
            if (verificationAttempts >= 6)
            {
                break;
            }
        }

        if (candidate is not { } bestShift)
        {
            return null;
        }

        // Sticky footers are detected independently of the scroll step (bounded to a quarter of
        // the frame) so slow scrolls under a tall footer still suppress it completely.
        var fixedBottomHeight = StationaryBottomBand(previous, current, previous.Height / 4);
        if (bestShift + fixedBottomHeight > height)
        {
            return null;
        }

        return new Alignment(bestShift, scores[bestShift], fixedBottomHeight);
    }

    /// <summary>
    /// Confirms a row-signature candidate against real pixels, which rejects shifts where
    /// unrelated rows happen to share the same average brightness.
    /// </summary>
    private static double PixelAlignmentScore(PanoramaFrame previous, PanoramaFrame current, int shift)
    {
        var height = previous.Height;
        var width = previous.Width;
        var ignoredTopBand = Math.Max(1, height / 20);
        var comparisonEnd = height - Math.Max(1, height / 20) - shift;
        if (comparisonEnd <= ignoredTopBand || width <= 0)
        {
            return double.MaxValue;
        }

        var columnStep = Math.Max(1, width / PanoramaFrame.SignatureColumns);
        var rowStep = Math.Max(1, (comparisonEnd - ignoredTopBand) / 80);
        var score = 0.0;
        var samples = 0;
        for (var y = ignoredTopBand; y < comparisonEnd; y += rowStep)
        {
            for (var x = 0; x < width; x += columnStep)
            {
                var previousIndex = (((y + shift) * width) + x) * 4;
                var currentIndex = ((y * width) + x) * 4;
                score += Math.Abs(PanoramaFrame.Luma(previous.BgraPixels, previousIndex) - PanoramaFrame.Luma(current.BgraPixels, currentIndex));
                samples++;
            }
        }

        return samples > 0 ? score / samples : double.MaxValue;
    }

    private static int StationaryBottomBand(PanoramaFrame previous, PanoramaFrame current, int maximumHeight)
    {
        var maximum = Math.Min(maximumHeight, previous.Height / 4);
        if (maximum <= 0)
        {
            return 0;
        }

        var columnStep = Math.Max(1, previous.Width / PanoramaFrame.SignatureColumns);
        var stationaryRows = 0;
        for (var offset = 0; offset < maximum; offset++)
        {
            var y = previous.Height - 1 - offset;
            var difference = 0.0;
            var samples = 0;
            for (var x = 0; x < previous.Width; x += columnStep)
            {
                var index = ((y * previous.Width) + x) * 4;
                difference += Math.Abs(PanoramaFrame.Luma(previous.BgraPixels, index) - PanoramaFrame.Luma(current.BgraPixels, index));
                samples++;
            }

            if (samples == 0 || difference / samples > 2)
            {
                break;
            }

            stationaryRows++;
        }

        return stationaryRows;
    }
}

/// <summary>Convenience wrapper that stitches an already-captured sequence of frames.</summary>
public sealed class PanoramaStitcher
{
    public PanoramaStitcher(PanoramaCaptureLimits limits)
    {
        Limits = limits;
    }

    public PanoramaCaptureLimits Limits { get; }

    public PanoramaResult Stitch(IEnumerable<PanoramaFrame> frames)
    {
        var accumulator = new PanoramaAccumulator(Limits);
        foreach (var frame in frames)
        {
            if (accumulator.Append(frame).Status == PanoramaAppendStatus.LimitReached)
            {
                break;
            }
        }

        return accumulator.Finish();
    }
}
