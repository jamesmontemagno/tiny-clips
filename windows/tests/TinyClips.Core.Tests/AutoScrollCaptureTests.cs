using TinyClips.Core.Capture;

namespace TinyClips.Core.Tests;

public sealed class AutoScrollCaptureTests
{
    private const int Width = 80;

    private static PanoramaCaptureLimits TestLimits() => new(
        MaxFrames: 200,
        MaxOutputHeight: 10_000,
        MaxMemoryBytes: 200_000_000);

    // -- Frame analysis ---------------------------------------------------------------------

    [Fact]
    public void AreSameToleratesCaretBlinkButNotScrolledContent()
    {
        var frame = DocumentFrame(offset: 0, rows: 100);
        var caret = Copy(frame);
        for (var y = 10; y < 70; y++)
        {
            for (var x = 30; x < 33; x++)
            {
                SetPixel(caret, x, y, unchecked((int)0xFF000000));
            }
        }

        Assert.True(AutoScrollFrameAnalysis.AreSame(frame, caret));
        Assert.False(AutoScrollFrameAnalysis.AreSame(frame, DocumentFrame(offset: 5, rows: 100)));
    }

    [Fact]
    public void ChangedBoundsExcludeFixedHeaderFooterAndSidebar()
    {
        var document = new FakeDocument(contentHeight: 600, paneHeight: 100) { HeaderRows = 10, FooterRows = 8, SidebarColumns = 12 };
        var before = document.Capture();
        document.Wheel(2);
        var after = document.Capture();

        Assert.True(AutoScrollFrameAnalysis.TryFindChangedBounds(before, after, out var bounds));
        Assert.Equal(new AutoScrollBounds(Top: 10, Bottom: 109, Left: 12, Right: Width - 1), bounds);
        Assert.False(AutoScrollFrameAnalysis.TryFindChangedBounds(before, before, out _));
    }

    [Theory]
    [InlineData(7)]
    [InlineData(45)]
    [InlineData(79)]
    public void FindShiftRecoversKnownShift(int shift)
    {
        var previous = DocumentFrame(offset: 0, rows: 100);
        var current = DocumentFrame(offset: shift, rows: 100);

        var found = AutoScrollFrameAnalysis.FindShift(previous, current, 0, 99, 0, Width - 1, hint: null, out var score);

        Assert.Equal(shift, found);
        Assert.True(score >= 0.9);
    }

    [Fact]
    public void FindShiftUsesHintToResolvePeriodicContent()
    {
        // Rows repeat every 30 px, so shifts 10, 40 and 70 all match perfectly; only the hint
        // (previous step or scroll-position prediction) tells them apart.
        var previous = PeriodicFrame(offset: 0, period: 30);
        var current = PeriodicFrame(offset: 70, period: 30);

        Assert.Equal(70, AutoScrollFrameAnalysis.FindShift(previous, current, 0, 99, 0, Width - 1, hint: 68, out _));
        Assert.Equal(40, AutoScrollFrameAnalysis.FindShift(previous, current, 0, 99, 0, Width - 1, hint: 41, out _));
        Assert.Equal(70, AutoScrollFrameAnalysis.FindShift(previous, current, 0, 99, 0, Width - 1, hint: 70, out _, minShift: 58, maxShift: 82));
    }

    [Fact]
    public void FindShiftRejectsUnrelatedFrames()
    {
        var previous = DocumentFrame(offset: 0, rows: 100);
        var unrelated = DocumentFrame(offset: 5_000, rows: 100);

        Assert.Equal(-1, AutoScrollFrameAnalysis.FindShift(previous, unrelated, 0, 99, 0, Width - 1, hint: null, out _));
    }

    // -- Step control -----------------------------------------------------------------------

    [Fact]
    public void StepControllerAimsForSixtyPercentOfViewport()
    {
        var steps = new AutoScrollStepController();
        Assert.Equal(AutoScrollStepController.InitialNotches, steps.Notches);

        steps.OnMatched(shift: 30, viewportHeight: 100);
        Assert.Equal(6, steps.Notches, precision: 6);

        steps.OnMatched(shift: 120, viewportHeight: 100);
        Assert.Equal(3, steps.Notches, precision: 6);

        steps.OnNoOverlap();
        Assert.Equal(1.5, steps.Notches, precision: 6);
    }

    [Fact]
    public void StepControllerClampsNotches()
    {
        var steps = new AutoScrollStepController();
        for (var i = 0; i < 10; i++)
        {
            steps.OnMatched(shift: 1, viewportHeight: 1_000);
        }

        Assert.Equal(AutoScrollStepController.MaxNotches, steps.Notches);

        for (var i = 0; i < 10; i++)
        {
            steps.OnNoOverlap();
        }

        Assert.Equal(AutoScrollStepController.MinNotches, steps.Notches);
    }

    [Fact]
    public void StepControllerLimitsGrowthPerStep()
    {
        // A mis-measured tiny shift must not make the next step jump far past the viewport.
        var steps = new AutoScrollStepController();
        steps.OnMatched(shift: 5, viewportHeight: 1_000);

        Assert.Equal(AutoScrollStepController.InitialNotches * AutoScrollStepController.MaxGrowth, steps.Notches, precision: 6);
    }

    [Fact]
    public void StepControllerPredictsShiftOnlyAfterLearningScale()
    {
        var steps = new AutoScrollStepController();
        Assert.Equal(-1, steps.PredictShift(10, 20));

        // A weak match is not trusted.
        steps.LearnScale(shift: 60, score: 0.5, percentBefore: 0, percentAfter: 12);
        Assert.Equal(-1, steps.PredictShift(10, 20));

        steps.LearnScale(shift: 60, score: 0.95, percentBefore: 0, percentAfter: 12);
        Assert.Equal(5, steps.PixelsPerPercent, precision: 6);
        Assert.Equal(50, steps.PredictShift(10, 20), precision: 6);
        Assert.Equal(-1, steps.PredictShift(-1, 20));
    }

    [Fact]
    public void StepControllerBacksUpOvershootingSteps()
    {
        var steps = new AutoScrollStepController();
        steps.OnMatched(shift: 50, viewportHeight: 100); // 3 * 60 / 50 = 3.6 notches

        Assert.False(AutoScrollStepController.Overshot(predictedShift: 85, viewportHeight: 100));
        Assert.True(AutoScrollStepController.Overshot(predictedShift: 86, viewportHeight: 100));

        // Back up the share of the step beyond 60 % of the viewport.
        Assert.Equal(3.6 * (180 - 60) / 180, steps.BackUpNotches(predictedShift: 180, viewportHeight: 100), precision: 6);
        Assert.Equal(AutoScrollStepController.MinNotches, steps.BackUpNotches(predictedShift: 62, viewportHeight: 100));
    }

    // -- Stitcher ---------------------------------------------------------------------------

    [Fact]
    public void StitcherReturnsFirstFrameWhenNothingScrolled()
    {
        var first = DocumentFrame(offset: 0, rows: 100);
        var stitcher = new AutoScrollStitcher(first, TestLimits());

        var result = stitcher.Finish();

        Assert.Same(first, result.Image);
        Assert.Equal(1, result.FrameCount);
    }

    [Fact]
    public void StitcherEnforcesMemoryBudget()
    {
        var first = DocumentFrame(offset: 0, rows: 100);
        var second = DocumentFrame(offset: 60, rows: 100);
        // Three frames plus the composed image do not fit.
        var stitcher = new AutoScrollStitcher(first, new PanoramaCaptureLimits(MaxFrames: 10, MaxOutputHeight: 10_000, MaxMemoryBytes: Width * 4 * 400));
        stitcher.SetBounds(new AutoScrollBounds(0, 99, 0, Width - 1));

        var outcome = stitcher.Append(first, second, 60, vote: true);

        Assert.Equal(PanoramaAppendStatus.LimitReached, outcome.Status);
        Assert.Equal(PanoramaCaptureLimitReason.Memory, outcome.LimitReason);
        Assert.Equal(100, stitcher.Finish().OutputHeight);
    }

    [Fact]
    public void StitcherStopsAtFrameLimitAndKeepsAcceptedFrames()
    {
        var first = DocumentFrame(offset: 0, rows: 100);
        var stitcher = new AutoScrollStitcher(first, TestLimits() with { MaxFrames = 2 });
        stitcher.SetBounds(new AutoScrollBounds(0, 99, 0, Width - 1));

        var outcome = stitcher.Append(first, DocumentFrame(offset: 60, rows: 100), 60, vote: true);

        Assert.Equal(PanoramaCaptureLimitReason.FrameCount, outcome.LimitReason);
        Assert.Equal(160, stitcher.Finish().OutputHeight);
    }

    // -- Engine (fake document, no real window) ---------------------------------------------

    [Fact]
    public void CapturesWholeDocumentFromTopAndRestoresScrollPosition()
    {
        var document = new FakeDocument(contentHeight: 600, paneHeight: 100) { Offset = 250 };

        var result = AutoScrollEngine.Run(document, TestLimits(), fromTop: true, progress: null, CancellationToken.None);

        Assert.True(result.Scrolled);
        AssertMatchesDocument(result.Panorama.Image, contentRows: 600);
        Assert.InRange(document.Offset, 230, 270);
    }

    [Fact]
    public void BacksUpWhenAcceleratedStepOvershoots()
    {
        // Bursts of more than 3 notches accelerate 5x, so the second step jumps past the viewport.
        var document = new FakeDocument(contentHeight: 600, paneHeight: 100)
        {
            PixelsPerNotch = 10,
            AccelerateAfterNotches = 3,
            AccelerationFactor = 5,
        };

        var result = AutoScrollEngine.Run(document, TestLimits(), fromTop: false, progress: null, CancellationToken.None);

        AssertMatchesDocument(result.Panorama.Image, contentRows: 600);
        var firstBackUp = document.WheelCalls.FindIndex(n => n < 0);
        Assert.True(firstBackUp > 0, "expected a back-up step");
        Assert.Contains(document.WheelCalls.Skip(firstBackUp), n => n > 0);
    }

    [Fact]
    public void CapturesByPixelsAloneWhenScrollPositionIsUnknown()
    {
        var document = new FakeDocument(contentHeight: 600, paneHeight: 100) { Offset = 200, ReportsPosition = false };

        var result = AutoScrollEngine.Run(document, TestLimits(), fromTop: true, progress: null, CancellationToken.None);

        Assert.True(result.Scrolled);
        AssertMatchesDocument(result.Panorama.Image, contentRows: 600);
    }

    [Fact]
    public void WaitsForAppsThatStartScrollingLate()
    {
        // Covered apps can take a few hundred milliseconds to react to wheel messages; two
        // identical captures right after a step must not be mistaken for "nothing scrolled".
        var document = new FakeDocument(contentHeight: 600, paneHeight: 100) { Offset = 200, LatencyCaptures = 4, ReportsPosition = false };

        var result = AutoScrollEngine.Run(document, TestLimits(), fromTop: true, progress: null, CancellationToken.None);

        Assert.True(result.Scrolled);
        AssertMatchesDocument(result.Panorama.Image, contentRows: 600);
    }

    [Fact]
    public void WaitStableIgnoresBriefPauseMidAnimation()
    {
        // The pane pauses for a few captures mid-animation, then moves again: only the final,
        // lasting position counts as settled.
        var moving = DocumentFrame(offset: 0, rows: 100);
        var paused = DocumentFrame(offset: 30, rows: 100);
        var settled = DocumentFrame(offset: 60, rows: 100);
        var surface = new SequenceSurface(moving, paused, paused, paused, settled);

        var frame = AutoScrollEngine.WaitStable(surface, CancellationToken.None);

        Assert.Same(settled, frame);
    }

    [Fact]
    public void KeepsFixedHeaderAndFooterOnceAndFillsStaticSidebar()
    {
        var document = new FakeDocument(contentHeight: 600, paneHeight: 100) { HeaderRows = 10, FooterRows = 8, SidebarColumns = 12 };

        var image = AutoScrollEngine.Run(document, TestLimits(), fromTop: false, progress: null, CancellationToken.None).Panorama.Image;

        Assert.Equal(10 + 600 + 8, image.Height);
        for (var y = 0; y < 10; y++)
        {
            Assert.Equal(FakeDocument.HeaderPixel(y, 40), GetPixel(image, 40, y));
        }

        for (var y = 0; y < 600; y += 3)
        {
            Assert.Equal(ContentPixel(y, 50), GetPixel(image, 50, 10 + y));
        }

        for (var y = 0; y < 8; y++)
        {
            Assert.Equal(FakeDocument.FooterPixel(y, 40), GetPixel(image, 40, 610 + y));
        }

        // Below the first viewport the sidebar doesn't repeat: it is one background color.
        var fill = GetPixel(image, 0, 110);
        for (var y = 110; y < 610; y++)
        {
            for (var x = 0; x < 12; x++)
            {
                Assert.Equal(fill, GetPixel(image, x, y));
            }
        }
    }

    [Fact]
    public void StopsAtOutputHeightLimitAndKeepsWhatWasCaptured()
    {
        var document = new FakeDocument(contentHeight: 600, paneHeight: 100);

        var panorama = AutoScrollEngine.Run(document, TestLimits() with { MaxOutputHeight = 250 }, fromTop: false, progress: null, CancellationToken.None).Panorama;

        Assert.True(panorama.ReachedLimit);
        Assert.InRange(panorama.OutputHeight, 160, 250);
        AssertMatchesDocument(panorama.Image, contentRows: panorama.OutputHeight);
    }

    [Fact]
    public void StopRequestReturnsCapturedFrame()
    {
        var document = new FakeDocument(contentHeight: 600, paneHeight: 100);
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        var result = AutoScrollEngine.Run(document, TestLimits(), fromTop: false, progress: null, stop.Token);

        Assert.False(result.Scrolled);
        Assert.Equal(100, result.Panorama.OutputHeight);
    }

    [Fact]
    public void ReportsNotScrolledWhenAppIgnoresWheel()
    {
        var document = new FakeDocument(contentHeight: 600, paneHeight: 100) { IgnoresWheel = true, ReportsPosition = false };

        var result = AutoScrollEngine.Run(document, TestLimits(), fromTop: false, progress: null, CancellationToken.None);

        Assert.False(result.Scrolled);
        Assert.Equal(100, result.Panorama.OutputHeight);
    }

    [Fact]
    public void ReportsProgressForEveryFrame()
    {
        var document = new FakeDocument(contentHeight: 600, paneHeight: 100);
        var counts = new List<int>();

        var result = AutoScrollEngine.Run(document, TestLimits(), fromTop: false, counts.Add, CancellationToken.None);

        Assert.Equal(1, counts[0]);
        Assert.Equal(result.Panorama.FrameCount, counts[^1]);
        Assert.Equal(counts.OrderBy(c => c), counts);
    }

    // -- Helpers ----------------------------------------------------------------------------

    private static void AssertMatchesDocument(CapturedFrame image, int contentRows)
    {
        Assert.Equal(Width, image.Width);
        Assert.Equal(contentRows, image.Height);
        for (var y = 0; y < contentRows; y++)
        {
            Assert.Equal(ContentPixel(y, 3), GetPixel(image, 3, y));
            Assert.Equal(ContentPixel(y, 61), GetPixel(image, 61, y));
        }
    }

    /// <summary>Non-repeating content: every row has its own hashed colors and a horizontal ramp.</summary>
    internal static int ContentPixel(int row, int x)
    {
        var s = Hash(row);
        var b = (s + (x * 5)) & 0xFF;
        var g = ((s >> 8) + (x * 3)) & 0xFF;
        var r = ((s >> 16) + (x * 7)) & 0xFF;
        return unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
    }

    private static int Hash(int value)
    {
        unchecked
        {
            var h = (uint)value * 2654435761u;
            h ^= h >> 13;
            h *= 0x5bd1e995;
            h ^= h >> 15;
            return (int)(h & 0x7FFFFFFF);
        }
    }

    private static int Gray(int value)
    {
        var v = Math.Clamp(value, 0, 255);
        return unchecked((int)0xFF000000) | (v << 16) | (v << 8) | v;
    }

    private static CapturedFrame DocumentFrame(int offset, int rows)
    {
        var frame = new CapturedFrame(new byte[Width * rows * 4], Width, rows);
        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                SetPixel(frame, x, y, ContentPixel(offset + y, x));
            }
        }

        return frame;
    }

    private static CapturedFrame PeriodicFrame(int offset, int period)
    {
        var frame = new CapturedFrame(new byte[Width * 100 * 4], Width, 100);
        for (var y = 0; y < 100; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                SetPixel(frame, x, y, ContentPixel((offset + y) % period, x));
            }
        }

        return frame;
    }

    private static CapturedFrame Copy(CapturedFrame frame)
        => new((byte[])frame.BgraPixels.Clone(), frame.Width, frame.Height);

    private static int GetPixel(CapturedFrame frame, int x, int y)
        => BitConverter.ToInt32(frame.BgraPixels, ((y * frame.Width) + x) * 4);

    private static void SetPixel(CapturedFrame frame, int x, int y, int value)
        => BitConverter.TryWriteBytes(frame.BgraPixels.AsSpan(((y * frame.Width) + x) * 4, 4), value);

    /// <summary>Returns the given frames in order, then repeats the last one.</summary>
    private sealed class SequenceSurface(params CapturedFrame[] frames) : IAutoScrollSurface
    {
        private int _next;

        public double ScrollPercent => -1;

        public int PaneColumn => Width / 2;

        public CapturedFrame Capture() => frames[Math.Min(_next++, frames.Length - 1)];

        public void Wheel(int notches)
        {
        }

        public void Wait(int milliseconds)
        {
        }
    }

    /// <summary>
    /// A scrollable pane with optional fixed header, footer and sidebar. Wheel notches move a
    /// fixed number of pixels, optionally accelerating for longer bursts like real apps do.
    /// </summary>
    private sealed class FakeDocument : IAutoScrollSurface
    {
        private readonly int _contentHeight;
        private readonly int _paneHeight;

        public FakeDocument(int contentHeight, int paneHeight)
        {
            _contentHeight = contentHeight;
            _paneHeight = paneHeight;
        }

        public int Offset { get; set; }

        public int PixelsPerNotch { get; init; } = 20;

        public int AccelerateAfterNotches { get; init; } = int.MaxValue;

        public int AccelerationFactor { get; init; } = 1;

        public bool ReportsPosition { get; init; } = true;

        public bool IgnoresWheel { get; init; }

        public int HeaderRows { get; init; }

        public int FooterRows { get; init; }

        public int SidebarColumns { get; init; }

        public List<int> WheelCalls { get; } = new();

        /// <summary>Wheel moves become visible only on this many captures later (apps start scrolling late).</summary>
        public int LatencyCaptures { get; init; }

        private int? _pendingOffset;
        private int _capturesUntilApplied;

        private int MaxOffset => _contentHeight - _paneHeight;

        public double ScrollPercent => ReportsPosition ? Offset * 100.0 / MaxOffset : -1;

        public int PaneColumn => Width / 2;

        public static int HeaderPixel(int y, int x) => Gray(40 + ((y * 17) + (x * 3)) % 180);

        public static int FooterPixel(int y, int x) => Gray(20 + ((y * 29) + (x * 5)) % 200);

        private static int SidebarPixel(int viewRow) => Gray(30 + ((viewRow * 53) % 7 * 30));

        public void Wheel(int notches)
        {
            WheelCalls.Add(notches);
            if (IgnoresWheel)
            {
                return;
            }

            var pixels = 0;
            for (var i = 0; i < Math.Abs(notches); i++)
            {
                pixels += i < AccelerateAfterNotches ? PixelsPerNotch : PixelsPerNotch * AccelerationFactor;
            }

            var target = Math.Clamp((_pendingOffset ?? Offset) + (Math.Sign(notches) * pixels), 0, MaxOffset);
            if (LatencyCaptures > 0)
            {
                _pendingOffset = target;
                _capturesUntilApplied = LatencyCaptures;
            }
            else
            {
                Offset = target;
            }
        }

        public void Wait(int milliseconds)
        {
        }

        public CapturedFrame Capture()
        {
            if (_pendingOffset is { } pending && --_capturesUntilApplied <= 0)
            {
                Offset = pending;
                _pendingOffset = null;
            }

            var height = HeaderRows + _paneHeight + FooterRows;
            var frame = new CapturedFrame(new byte[Width * height * 4], Width, height);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < Width; x++)
                {
                    int pixel;
                    if (y < HeaderRows)
                    {
                        pixel = HeaderPixel(y, x);
                    }
                    else if (y >= HeaderRows + _paneHeight)
                    {
                        pixel = FooterPixel(y - HeaderRows - _paneHeight, x);
                    }
                    else if (x < SidebarColumns)
                    {
                        pixel = SidebarPixel(y - HeaderRows);
                    }
                    else
                    {
                        pixel = ContentPixel(Offset + y - HeaderRows, x);
                    }

                    SetPixel(frame, x, y, pixel);
                }
            }

            return frame;
        }
    }
}
