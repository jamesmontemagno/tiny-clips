using TinyClips.Core.Capture;

namespace TinyClips.Core.Tests;

public sealed class PanoramaStitcherTests
{
    private static PanoramaCaptureLimits TestLimits() => new(
        MaxFrames: 10,
        MaxOutputHeight: 1_000,
        MaxMemoryBytes: 2_000_000);

    [Fact]
    public void StitchesKnownVerticalShift()
    {
        var first = PanoramaFrameAt(globalStartRow: 0);
        var second = PanoramaFrameAt(globalStartRow: 20);

        var result = new PanoramaStitcher(TestLimits()).Stitch(new[] { first, second });

        Assert.Equal(2, result.FrameCount);
        Assert.Equal(120, result.OutputHeight);
        Assert.Equal(40, result.Image.Width);
        Assert.Equal(120, result.Image.Height);
        Assert.False(result.ReachedLimit);
    }

    [Fact]
    public void StitchedRowsMatchSourceContent()
    {
        var frames = new[]
        {
            PanoramaFrameAt(globalStartRow: 0),
            PanoramaFrameAt(globalStartRow: 20),
            PanoramaFrameAt(globalStartRow: 40),
        };

        var result = new PanoramaStitcher(TestLimits()).Stitch(frames);

        Assert.Equal(140, result.OutputHeight);
        for (var y = 0; y < result.OutputHeight; y += 7)
        {
            Assert.Equal(ExpectedValue(y, x: 3), RedValue(result.Image, x: 3, y: y));
        }
    }

    [Fact]
    public void RejectsFramesWithoutCredibleAlignment()
    {
        var accumulator = new PanoramaAccumulator(TestLimits());
        accumulator.Append(PanoramaFrameAt(globalStartRow: 0));
        var unrelatedPixels = new byte[40 * 100 * 4];
        Array.Fill(unrelatedPixels, (byte)255);

        var outcome = accumulator.Append(new PanoramaFrame(unrelatedPixels, 40, 100));

        Assert.Equal(PanoramaAppendStatus.Skipped, outcome.Status);
        Assert.Equal(1, accumulator.AcceptedFrameCount);
        Assert.Null(PanoramaAccumulator.EstimateVerticalShift(accumulator.PreviousFrame!, new PanoramaFrame(unrelatedPixels, 40, 100)));
    }

    [Fact]
    public void SuppressesStationaryFooterCopies()
    {
        var first = PanoramaFrameAt(globalStartRow: 0, fixedFooterHeight: 5);
        var second = PanoramaFrameAt(globalStartRow: 20, fixedFooterHeight: 5);

        var result = new PanoramaStitcher(TestLimits()).Stitch(new[] { first, second });

        Assert.Equal(120, result.OutputHeight);
        Assert.Equal((byte)((95 * 7) % 251), RedValue(result.Image, x: 0, y: 95));
        Assert.Equal((byte)32, RedValue(result.Image, x: 0, y: 119));
    }

    [Fact]
    public void SuppressesTallFooterOnSlowScroll()
    {
        // A 5 px sticky footer with a 4 px scroll step: the footer is taller than the step and
        // must still be held back rather than partially appended on every frame.
        var frames = new[]
        {
            PanoramaFrameAt(globalStartRow: 0, fixedFooterHeight: 5),
            PanoramaFrameAt(globalStartRow: 4, fixedFooterHeight: 5),
            PanoramaFrameAt(globalStartRow: 8, fixedFooterHeight: 5),
        };

        var result = new PanoramaStitcher(TestLimits()).Stitch(frames);

        // 95 content rows per frame + 2 x 4 scrolled rows + one footer copy.
        Assert.Equal(108, result.OutputHeight);
        for (var y = 0; y < 103; y += 3)
        {
            Assert.Equal(ExpectedValue(y, x: 0), RedValue(result.Image, x: 0, y: y));
        }

        for (var y = 103; y < 108; y++)
        {
            Assert.Equal((byte)32, RedValue(result.Image, x: 0, y: y));
        }
    }

    [Fact]
    public void EnforcesPeakMemoryBudget()
    {
        // 40x100 frames are 16 000 bytes. Frame 1 needs 16 000 (buffer) + 16 000 (copy) + 32 000
        // (two frames) = 64 000; stitching frame 2 needs 70 400, so a 70 000 budget keeps frame 1 only.
        var first = PanoramaFrameAt(globalStartRow: 0);
        var second = PanoramaFrameAt(globalStartRow: 20);
        var limits = TestLimits() with { MaxMemoryBytes = 70_000 };

        var result = new PanoramaStitcher(limits).Stitch(new[] { first, second });

        Assert.True(result.ReachedLimit);
        Assert.Equal(1, result.FrameCount);
        Assert.Equal(100, result.OutputHeight);
    }

    [Fact]
    public void MemoryBudgetAccountsForBufferOverAllocation()
    {
        // Stitching frame 3 grows the buffer from 19 200 to 33 600 bytes (1.5x the 22 400 logical
        // size). The naive "2x logical + 2 frames" estimate is 76 800 and would pass an 80 000
        // budget, but the real peak (33 600 + 22 400 + 32 000 = 88 000) must be what is enforced.
        var frames = new[]
        {
            PanoramaFrameAt(globalStartRow: 0),
            PanoramaFrameAt(globalStartRow: 20),
            PanoramaFrameAt(globalStartRow: 40),
        };
        var limits = TestLimits() with { MaxMemoryBytes = 80_000 };

        var result = new PanoramaStitcher(limits).Stitch(frames);

        Assert.True(result.ReachedLimit);
        Assert.Equal(2, result.FrameCount);
    }

    [Fact]
    public void KeepsPartialResultWhenMemoryLimitIsReached()
    {
        var frames = new[]
        {
            PanoramaFrameAt(globalStartRow: 0),
            PanoramaFrameAt(globalStartRow: 20),
            PanoramaFrameAt(globalStartRow: 40),
        };
        // Frame 2 peaks at 70 400 bytes, frame 3 at 88 000 (see MemoryBudgetAccountsForBufferOverAllocation).
        var limits = TestLimits() with { MaxMemoryBytes = 82_000 };

        var result = new PanoramaStitcher(limits).Stitch(frames);

        Assert.True(result.ReachedLimit);
        Assert.Equal(2, result.FrameCount);
        Assert.Equal(120, result.OutputHeight);
    }

    [Fact]
    public void KeepsPartialResultWhenOutputHeightIsReached()
    {
        var frames = new[]
        {
            PanoramaFrameAt(globalStartRow: 0),
            PanoramaFrameAt(globalStartRow: 20),
            PanoramaFrameAt(globalStartRow: 40),
        };
        var limits = TestLimits() with { MaxOutputHeight = 130 };

        var result = new PanoramaStitcher(limits).Stitch(frames);

        Assert.True(result.ReachedLimit);
        Assert.Equal(120, result.OutputHeight);
    }

    [Fact]
    public void StopsAtFrameLimit()
    {
        var frames = Enumerable.Range(0, 5).Select(i => PanoramaFrameAt(globalStartRow: i * 10)).ToArray();
        var limits = TestLimits() with { MaxFrames = 3 };

        var result = new PanoramaStitcher(limits).Stitch(frames);

        Assert.True(result.ReachedLimit);
        Assert.Equal(3, result.FrameCount);
        Assert.Equal(120, result.OutputHeight);
    }

    [Fact]
    public void DefaultMemoryUseDoesNotGrowWithFrameCount()
    {
        // Peak memory tracks the stitched output plus the retained and incoming frames, so a
        // long capture of a modest region must stay well inside the default budget.
        const int width = 2_400;
        const int height = 1_800;
        const long frameBytes = (long)width * height * 4;
        const int shiftPerFrame = 200;
        const int outputHeight = height + (shiftPerFrame * 150);
        const long outputBytes = (long)width * outputHeight * 4;

        // Buffer capacity can reach 1.5x the logical size; plus the final copy and two frames.
        Assert.True((outputBytes * 2.5) + (frameBytes * 2) <= PanoramaCaptureLimits.Default.MaxMemoryBytes);
        Assert.True(outputHeight <= PanoramaCaptureLimits.Default.MaxOutputHeight);
    }

    [Fact]
    public void EditorLimitsCapOutputHeightToTextureMaximum()
    {
        Assert.Equal(PanoramaCaptureLimits.EditorMaxOutputHeight, PanoramaCaptureLimits.ForEditor.MaxOutputHeight);
        Assert.Equal(PanoramaCaptureLimits.Default.MaxMemoryBytes, PanoramaCaptureLimits.ForEditor.MaxMemoryBytes);
        Assert.True(PanoramaCaptureLimits.ForEditor.MaxOutputHeight < PanoramaCaptureLimits.Default.MaxOutputHeight);
    }

    [Fact]
    public void PrefersSmallestShiftOnRepeatingContent()
    {
        // A page of repeating rows aliases at shift + N * period; picking a later alias
        // duplicates rows that are already committed.
        var first = RepeatingPanoramaFrame(globalStartRow: 0, period: 30);
        var second = RepeatingPanoramaFrame(globalStartRow: 20, period: 30);

        var result = new PanoramaStitcher(TestLimits()).Stitch(new[] { first, second });

        Assert.Equal(2, result.FrameCount);
        Assert.Equal(120, result.OutputHeight);
    }

    [Fact]
    public void AlignsSmallScrollSteps()
    {
        // Slow scrolling advances only a few pixels per frame, which must not be rounded up.
        var first = PanoramaFrameAt(globalStartRow: 0);
        var second = PanoramaFrameAt(globalStartRow: 4);

        var result = new PanoramaStitcher(TestLimits()).Stitch(new[] { first, second });

        Assert.Equal(104, result.OutputHeight);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(37)]
    [InlineData(120)]
    [InlineData(480)]
    public void AlignsPeriodicContentAcrossShiftSizes(int trueShift)
    {
        const int width = 320;
        const int height = 900;
        const int period = 100;

        static PanoramaFrame MakeFrame(int globalStartRow)
        {
            var pixels = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            {
                var row = globalStartRow + y;
                var band = (row % period) < period / 2 ? 40 : 210;
                for (var x = 0; x < width; x++)
                {
                    var index = ((y * width) + x) * 4;
                    var value = (byte)Math.Clamp(band + ((row % 7) * 3) + ((x % 11) * 2), 0, 255);
                    pixels[index] = value;
                    pixels[index + 1] = value;
                    pixels[index + 2] = value;
                    pixels[index + 3] = 255;
                }
            }

            return new PanoramaFrame(pixels, width, height);
        }

        var alignment = PanoramaAccumulator.EstimateVerticalShift(MakeFrame(0), MakeFrame(trueShift));

        Assert.NotNull(alignment);
        Assert.Equal(trueShift, alignment!.Value.Shift);
    }

    [Fact]
    public void AreMeaningfullyDifferent_DetectsIdenticalAndScrolledFrames()
    {
        var first = PanoramaFrameAt(globalStartRow: 0);
        var same = PanoramaFrameAt(globalStartRow: 0);
        var scrolled = PanoramaFrameAt(globalStartRow: 20);

        Assert.False(PanoramaAccumulator.AreMeaningfullyDifferent(first, same));
        Assert.True(PanoramaAccumulator.AreMeaningfullyDifferent(first, scrolled));
    }

    [Fact]
    public void RowLuma_UsesBgraChannelOrder()
    {
        // Row 0 is pure blue, row 1 pure red: red carries far more luma than blue.
        var pixels = new byte[4 * 2 * 4];
        for (var x = 0; x < 4; x++)
        {
            pixels[(x * 4) + 0] = 255;          // B on row 0
            pixels[(x * 4) + 3] = 255;
            pixels[((4 + x) * 4) + 2] = 255;    // R on row 1
            pixels[((4 + x) * 4) + 3] = 255;
        }

        var frame = new PanoramaFrame(pixels, 4, 2);

        Assert.InRange(frame.RowLuma[0], 28f, 30f);
        Assert.InRange(frame.RowLuma[1], 76f, 78f);
    }

    [Fact]
    public void Finish_WithSingleFrame_ReturnsThatFrame()
    {
        // Stopping must always produce an image from whatever was captured, even before any scroll.
        var accumulator = new PanoramaAccumulator(TestLimits());
        var only = PanoramaFrameAt(globalStartRow: 0);
        accumulator.Append(only);

        var result = accumulator.Finish();

        Assert.Equal(1, result.FrameCount);
        Assert.Equal(100, result.OutputHeight);
        Assert.Same(only.BgraPixels, result.Image.BgraPixels);
        Assert.False(result.ReachedLimit);
    }

    [Fact]
    public void Finish_WithOnlyUnalignableFrames_ReturnsFirstFrame()
    {
        var accumulator = new PanoramaAccumulator(TestLimits());
        accumulator.Append(PanoramaFrameAt(globalStartRow: 0));
        var unrelatedPixels = new byte[40 * 100 * 4];
        Array.Fill(unrelatedPixels, (byte)255);
        accumulator.Append(new PanoramaFrame(unrelatedPixels, 40, 100));

        var result = accumulator.Finish();

        Assert.Equal(1, result.FrameCount);
        Assert.Equal(ExpectedValue(50, x: 0), RedValue(result.Image, x: 0, y: 50));
    }

    [Fact]
    public void Finish_WithoutFrames_ReportsNoFrames()
    {
        var accumulator = new PanoramaAccumulator(TestLimits());

        var ex = Assert.Throws<PanoramaCaptureException>(() => accumulator.Finish());
        Assert.Equal(PanoramaCaptureError.NoFrames, ex.Error);
    }

    [Fact]
    public void Append_SkipsFramesWithDifferentDimensions()
    {
        var accumulator = new PanoramaAccumulator(TestLimits());
        accumulator.Append(PanoramaFrameAt(globalStartRow: 0));

        var outcome = accumulator.Append(new PanoramaFrame(new byte[20 * 50 * 4], 20, 50));

        Assert.Equal(PanoramaAppendStatus.Skipped, outcome.Status);
        Assert.Equal(1, accumulator.AcceptedFrameCount);
    }

    [Fact]
    public void PendingOutputHeight_TracksCommittedRows()
    {
        var accumulator = new PanoramaAccumulator(TestLimits());
        Assert.Equal(0, accumulator.PendingOutputHeight);

        accumulator.Append(PanoramaFrameAt(globalStartRow: 0));
        Assert.Equal(100, accumulator.PendingOutputHeight);

        accumulator.Append(PanoramaFrameAt(globalStartRow: 20));
        Assert.Equal(120, accumulator.PendingOutputHeight);
    }

    private static byte ExpectedValue(int globalRow, int x) => (byte)(((globalRow * 7) + (x * 13)) % 251);

    private static PanoramaFrame PanoramaFrameAt(int globalStartRow, int fixedFooterHeight = 0)
    {
        const int width = 40;
        const int height = 100;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = ((y * width) + x) * 4;
                var isFooter = fixedFooterHeight > 0 && y >= height - fixedFooterHeight;
                var value = isFooter ? (byte)32 : ExpectedValue(globalStartRow + y, x);
                pixels[index] = value;
                pixels[index + 1] = value;
                pixels[index + 2] = value;
                pixels[index + 3] = 255;
            }
        }

        return new PanoramaFrame(pixels, width, height);
    }

    private static PanoramaFrame RepeatingPanoramaFrame(int globalStartRow, int period)
    {
        const int width = 40;
        const int height = 100;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var row = (globalStartRow + y) % period;
            for (var x = 0; x < width; x++)
            {
                var index = ((y * width) + x) * 4;
                var value = (byte)(((row * 8) + (x * 3)) % 251);
                pixels[index] = value;
                pixels[index + 1] = value;
                pixels[index + 2] = value;
                pixels[index + 3] = 255;
            }
        }

        return new PanoramaFrame(pixels, width, height);
    }

    private static byte RedValue(CapturedFrame image, int x, int y)
        => image.BgraPixels[(((y * image.Width) + x) * 4) + 2];
}
