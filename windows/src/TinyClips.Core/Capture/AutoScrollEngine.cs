using System.Diagnostics;

namespace TinyClips.Core.Capture;

/// <summary>
/// What auto-scroll drives: a scrollable pane that can be captured and scrolled in the
/// background, without moving the mouse or taking focus.
/// </summary>
public interface IAutoScrollSurface
{
    /// <summary>Captures the current content (tightly packed BGRA8).</summary>
    CapturedFrame Capture();

    /// <summary>Vertical scroll position of the pane (0-100), or a negative value when unknown.</summary>
    double ScrollPercent { get; }

    /// <summary>Frame column of the point the pane was picked at; matching starts around it.</summary>
    int PaneColumn { get; }

    /// <summary>Scrolls by whole wheel notches; positive scrolls down.</summary>
    void Wheel(int notches);

    void Wait(int milliseconds);
}

/// <param name="Panorama">The stitched image.</param>
/// <param name="Scrolled">False when the pane never moved (for example, the app ignores background scrolling).</param>
public sealed record AutoScrollResult(PanoramaResult Panorama, bool Scrolled);

/// <summary>
/// Hands-free scrolling capture: scrolls the pane in controlled steps, waits for each frame to
/// settle, matches the overlap (predicted from the scroll position when known, refined in
/// pixels), backs up steps that overshoot, and composes the result with
/// <see cref="AutoScrollStitcher"/>. Restores the original scroll position when done.
/// Ported from ScrollShot (MIT, https://github.com/crutkas/ScrollShot).
/// </summary>
public static class AutoScrollEngine
{
    /// <summary>Half-width of the band around <see cref="IAutoScrollSurface.PaneColumn"/> matched first.</summary>
    private const int PaneBandHalfWidth = 300;

    /// <summary>A predicted shift is refined in pixels within this many rows.</summary>
    private const int PredictionTolerance = 12;

    /// <summary>Polls (40 ms apart) for content to start moving after a scroll.</summary>
    private const int ScrollStartPolls = 25;

    private const int ScrollStartTimeoutMs = 1200;

    /// <summary>Content must stay unchanged this long to count as settled.</summary>
    private const int StableQuietMs = 350;

    /// <summary>...or match on this many consecutive polls.</summary>
    private const int StableMatches = 6;

    private const int StableTimeoutMs = 3000;

    /// <summary>
    /// Runs a capture. Cancelling <paramref name="stop"/> ends it early and returns what was
    /// captured so far. Blocks the calling thread; run it off the UI thread.
    /// </summary>
    public static AutoScrollResult Run(
        IAutoScrollSurface surface,
        PanoramaCaptureLimits limits,
        bool fromTop,
        Action<int>? progress,
        CancellationToken stop)
    {
        var scroller = new WheelScroller(surface);
        try
        {
            var previous = WaitStable(surface, stop);
            if (fromTop)
            {
                for (var i = 0; i < 200 && !stop.IsCancellationRequested; i++)
                {
                    var more = scroller.ToTop(stop);
                    // A seek via the scroll position has already settled; blind wheel bursts have not.
                    var frame = more ? WaitScrolled(surface, previous, stop) : WaitStable(surface, stop);
                    var same = AutoScrollFrameAnalysis.AreSame(previous, frame);
                    previous = frame;
                    if (same || !more)
                    {
                        break;
                    }
                }
            }

            var stitcher = new AutoScrollStitcher(previous, limits);
            progress?.Invoke(stitcher.FrameCount);
            var steps = new AutoScrollStepController();
            var regionKnown = false;
            var bounds = default(AutoScrollBounds);
            int? lastShift = null;
            var lastStepNotches = 0;

            while (!stop.IsCancellationRequested && !stitcher.ReachedLimit)
            {
                var percentBefore = surface.ScrollPercent;
                var stepNotches = WheelScroller.Notches(steps.Notches);
                if (!scroller.Down(steps.Notches))
                {
                    break; // already at the end
                }

                var current = WaitScrolled(surface, previous, stop);
                if (AutoScrollFrameAnalysis.AreSame(previous, current) && !stop.IsCancellationRequested)
                {
                    // Covered windows sometimes repaint late: give the app one more chance before
                    // deciding nothing scrolled or the end was reached. No new wheel is posted, so a
                    // late repaint can't turn into a double step.
                    current = WaitScrolled(surface, previous, stop);
                }

                if (current.Width != previous.Width || current.Height != previous.Height)
                {
                    break; // the window was resized
                }

                if (!regionKnown)
                {
                    if (!AutoScrollFrameAnalysis.TryFindChangedBounds(previous, current, out bounds))
                    {
                        break; // nothing scrolled
                    }

                    stitcher.SetBounds(bounds);
                    regionKnown = true;
                }
                else if (AutoScrollFrameAnalysis.AreSame(previous, current))
                {
                    break; // reached the end
                }

                var viewport = bounds.Height;
                var predicted = steps.PredictShift(percentBefore, surface.ScrollPercent);
                // Apps accelerate bursts of wheel messages; if a step overshot, back up so the frames still overlap.
                for (var k = 0; k < 3 && AutoScrollStepController.Overshot(predicted, viewport) && !stop.IsCancellationRequested; k++)
                {
                    surface.Wheel(-Math.Max(1, (int)Math.Round(steps.BackUpNotches(predicted, viewport))));
                    current = WaitScrolled(surface, current, stop);
                    predicted = steps.PredictShift(percentBefore, surface.ScrollPercent);
                }

                if (current.Width != previous.Width || current.Height != previous.Height)
                {
                    break;
                }

                // Match on a band around the pane point first: that's the pane being scrolled.
                var center = Math.Clamp(surface.PaneColumn, bounds.Left, bounds.Right);
                var bandLeft = Math.Max(bounds.Left, center - PaneBandHalfWidth);
                var bandRight = Math.Min(bounds.Right, center + PaneBandHalfWidth);
                int shift;
                double score;
                if (predicted >= 1)
                {
                    // The scroll position tells us the shift; the pixels just refine it.
                    var expected = (int)Math.Round(predicted);
                    shift = AutoScrollFrameAnalysis.FindShift(
                        previous, current, bounds.Top, bounds.Bottom, bandLeft, bandRight, expected, out score,
                        expected - PredictionTolerance, expected + PredictionTolerance);
                    if (shift <= 0)
                    {
                        shift = Math.Min(expected, viewport);
                    }
                }
                else
                {
                    // Without a scroll position, the last step's pixels-per-notch predicts this one.
                    // Repeating content (lists, text lines) matches at several shifts, and the one
                    // nearest the prediction is the real one.
                    int? hint = lastShift is { } last && lastStepNotches > 0
                        ? (int)Math.Round((double)last * stepNotches / lastStepNotches)
                        : null;
                    shift = AutoScrollFrameAnalysis.FindShift(previous, current, bounds.Top, bounds.Bottom, bandLeft, bandRight, hint, out score);
                    if (shift <= 0)
                    {
                        shift = AutoScrollFrameAnalysis.FindShift(previous, current, bounds.Top, bounds.Bottom, bounds.Left, bounds.Right, hint, out score);
                    }

                    steps.LearnScale(shift, score, percentBefore, surface.ScrollPercent);
                }

                var matched = shift > 0;
                if (matched)
                {
                    lastShift = shift;
                    lastStepNotches = stepNotches;
                    steps.OnMatched(shift, viewport);
                }
                else
                {
                    // No overlap: keep the whole viewport and take smaller steps from now on.
                    shift = viewport;
                    steps.OnNoOverlap();
                }

                var outcome = stitcher.Append(previous, current, shift, vote: matched);
                if (outcome.Status == PanoramaAppendStatus.Skipped)
                {
                    break;
                }

                progress?.Invoke(stitcher.FrameCount);
                previous = current;
            }

            return new AutoScrollResult(stitcher.Finish(), regionKnown);
        }
        finally
        {
            scroller.Restore();
        }
    }

    /// <summary>
    /// After a scroll, waits for the content to start moving (apps can take a few hundred
    /// milliseconds, especially while covered by other windows), then for it to settle. Gives up
    /// waiting for movement after ~1 s, which is how the end of the document is detected.
    /// </summary>
    internal static CapturedFrame WaitScrolled(IAutoScrollSurface surface, CapturedFrame before, CancellationToken stop)
    {
        // Bounded by polls (instant test surfaces) and by time (captures of real windows can be slow).
        var timer = Stopwatch.StartNew();
        for (var i = 0; i < ScrollStartPolls && timer.ElapsedMilliseconds < ScrollStartTimeoutMs && !stop.IsCancellationRequested; i++)
        {
            surface.Wait(40);
            if (!AutoScrollFrameAnalysis.AreSame(before, surface.Capture()))
            {
                break;
            }
        }

        return WaitStable(surface, stop);
    }

    /// <summary>
    /// Waits until the content has stopped changing (smooth scrolling and repaints have settled).
    /// One matching pair isn't enough: covered windows repaint slowly, so two captures can match in
    /// the middle of a scroll animation, where text sits at sub-pixel offsets and repeating lines
    /// become ambiguous. Settled means unchanged for a quiet period (or for several polls, which
    /// keeps instant test surfaces fast).
    /// </summary>
    internal static CapturedFrame WaitStable(IAutoScrollSurface surface, CancellationToken stop)
    {
        surface.Wait(80);
        var a = surface.Capture();
        var timer = Stopwatch.StartNew();
        var quiet = Stopwatch.StartNew();
        var matches = 0;
        while (timer.ElapsedMilliseconds < StableTimeoutMs && !stop.IsCancellationRequested)
        {
            surface.Wait(60);
            var b = surface.Capture();
            if (AutoScrollFrameAnalysis.AreSame(a, b))
            {
                matches++;
                if (quiet.ElapsedMilliseconds >= StableQuietMs || matches >= StableMatches)
                {
                    return b;
                }
            }
            else
            {
                matches = 0;
                quiet.Restart();
            }

            a = b;
        }

        return a;
    }

    /// <summary>
    /// Wheel-based scrolling that uses the pane's scroll percentage (when known) to seek and to
    /// restore the user's original position.
    /// </summary>
    private sealed class WheelScroller
    {
        private readonly IAutoScrollSurface _surface;
        private readonly double _startPercent;
        private double _percentPerNotch;
        private double _lastPercent = -1;
        private int _lastNotches;

        public WheelScroller(IAutoScrollSurface surface)
        {
            _surface = surface;
            _startPercent = surface.ScrollPercent;
        }

        private double Percent => _surface.ScrollPercent;

        /// <summary>Returns false when nothing more needs to be done.</summary>
        public bool ToTop(CancellationToken stop)
        {
            if (Percent < 0)
            {
                // No position info: scroll up and let the caller watch the pixels.
                _surface.Wheel(-15);
                return true;
            }

            SeekTo(0, 40, stop);
            return false;
        }

        /// <summary>Whole notches actually posted for a step size.</summary>
        public static int Notches(double notches) => Math.Max(1, (int)Math.Round(notches));

        /// <summary>Scrolls down; false when the pane is already at the end.</summary>
        public bool Down(double notches)
        {
            var now = Percent;
            // Calibrate once, from a full step: the last step is cut short at the end of the
            // document and would under-estimate the rate used to restore the original position.
            if (_percentPerNotch <= 0 && _lastPercent >= 0 && now > _lastPercent && _lastNotches > 0)
            {
                _percentPerNotch = (now - _lastPercent) / _lastNotches;
            }

            if (now >= 99.999)
            {
                return false;
            }

            _lastPercent = now;
            _lastNotches = Notches(notches);
            _surface.Wheel(_lastNotches);
            return true;
        }

        public void Restore()
        {
            if (_startPercent >= 0)
            {
                SeekTo(_startPercent, 40, CancellationToken.None);
            }
        }

        /// <summary>Posts wheel notches in bursts, watching the scroll position, until it reaches the target.</summary>
        private void SeekTo(double targetPercent, int maxNotches, CancellationToken stop)
        {
            var timer = Stopwatch.StartNew();
            var last = Percent;
            long lastMove = 0;
            var flips = 0;
            var lastSign = 0;
            while (timer.ElapsedMilliseconds < 15000
                && timer.ElapsedMilliseconds - lastMove < 1000
                && !stop.IsCancellationRequested)
            {
                var diff = targetPercent - last;
                // Wheel steps are coarse; within half a step is as close as we can get.
                var tolerance = Math.Max(0.2, _percentPerNotch * 0.6);
                if (Math.Abs(diff) < tolerance
                    || (targetPercent <= 0 && last <= 0.001)
                    || (targetPercent >= 100 && last >= 99.999))
                {
                    break;
                }

                var sign = Math.Sign(diff);
                if (lastSign != 0 && sign != lastSign && ++flips >= 3)
                {
                    break; // oscillating around the target
                }

                lastSign = sign;
                var notches = _percentPerNotch > 0 ? (int)Math.Round(diff / _percentPerNotch) : sign * maxNotches;
                if (notches == 0)
                {
                    notches = sign;
                }

                _surface.Wheel(Math.Clamp(notches, -maxNotches, maxNotches));
                var settled = Settle(last);
                if (Math.Abs(settled - last) > 0.0001)
                {
                    last = settled;
                    lastMove = timer.ElapsedMilliseconds;
                }
            }
        }

        /// <summary>Smooth scrolling animates: wait for the position to start moving, then to stop.</summary>
        private double Settle(double from)
        {
            var current = Percent;
            for (var i = 0; i < 12 && Math.Abs(current - from) < 0.0001; i++)
            {
                _surface.Wait(25);
                current = Percent;
            }

            for (var i = 0; i < 40; i++)
            {
                _surface.Wait(40);
                var now = Percent;
                if (Math.Abs(now - current) < 0.0001)
                {
                    break;
                }

                current = now;
            }

            return current;
        }
    }
}
