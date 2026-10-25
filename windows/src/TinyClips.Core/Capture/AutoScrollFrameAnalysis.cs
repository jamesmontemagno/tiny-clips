using System.Runtime.InteropServices;

namespace TinyClips.Core.Capture;

/// <summary>Rows/columns (inclusive) of a frame that change when the pane scrolls.</summary>
public readonly record struct AutoScrollBounds(int Top, int Bottom, int Left, int Right)
{
    public int Height => Bottom - Top + 1;
}

/// <summary>
/// Pixel analysis for auto-scroll capture, where the app drives the scrolling in controlled
/// steps. Ported from ScrollShot (MIT, https://github.com/crutkas/ScrollShot).
/// </summary>
/// <remarks>
/// Unlike <see cref="PanoramaAccumulator"/>, which favors the smallest credible shift because a
/// user scrolls in small increments between frames, auto-scroll steps are large (~60 % of the
/// viewport). The shift is therefore chosen near a hint (the previous step or a prediction from
/// the pane's scroll position), which also disambiguates periodic content.
/// </remarks>
public static class AutoScrollFrameAnalysis
{
    internal static ReadOnlySpan<int> Pixels(CapturedFrame frame)
        => MemoryMarshal.Cast<byte, int>(frame.BgraPixels.AsSpan(0, frame.Width * frame.Height * 4));

    internal static ReadOnlySpan<int> Row(CapturedFrame frame, int y)
        => Pixels(frame).Slice(y * frame.Width, frame.Width);

    /// <summary>Rec.601 luma (0-255) of a BGRA pixel read as a little-endian int.</summary>
    internal static int Gray(int pixel)
        => ((((pixel >> 16) & 0xFF) * 77) + (((pixel >> 8) & 0xFF) * 150) + ((pixel & 0xFF) * 29)) >> 8;

    internal static bool Close(int a, int b) => a == b || Math.Abs(Gray(a) - Gray(b)) <= 24;

    /// <summary>
    /// True when two frames show the same content. Tolerates a few changed pixels (blinking caret,
    /// hover effects) and changes confined to a narrow vertical strip (overlay scrollbars fading).
    /// </summary>
    public static bool AreSame(CapturedFrame a, CapturedFrame b)
    {
        if (a.Width != b.Width || a.Height != b.Height)
        {
            return false;
        }

        var pa = Pixels(a);
        var pb = Pixels(b);
        var diff = 0;
        var minX = int.MaxValue;
        var maxX = -1;
        for (var i = 0; i < pa.Length; i++)
        {
            if (pa[i] == pb[i])
            {
                continue;
            }

            diff++;
            var x = i % a.Width;
            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
            if (diff > 150 && maxX - minX > 24)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Bounding box of every pixel that changed between two frames: after one scroll step this is
    /// the scrolling pane, excluding fixed headers, footers and toolbars.
    /// </summary>
    public static bool TryFindChangedBounds(CapturedFrame a, CapturedFrame b, out AutoScrollBounds bounds)
    {
        bounds = default;
        if (a.Width != b.Width || a.Height != b.Height)
        {
            return false;
        }

        int top = int.MaxValue, bottom = -1, left = int.MaxValue, right = -1;
        for (var y = 0; y < a.Height; y++)
        {
            var ra = Row(a, y);
            var rb = Row(b, y);
            if (ra.SequenceEqual(rb))
            {
                continue;
            }

            top = Math.Min(top, y);
            bottom = y;
            for (var x = 0; x < a.Width; x++)
            {
                if (ra[x] != rb[x])
                {
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                }
            }
        }

        if (bottom < 0)
        {
            return false;
        }

        bounds = new AutoScrollBounds(top, bottom, left, right);
        return true;
    }

    /// <summary>
    /// Finds the shift <c>dy</c> such that <paramref name="current"/> row <c>top + i</c> matches
    /// <paramref name="previous"/> row <c>top + i + dy</c> inside the scrolling bounds, using
    /// binned row signatures that tolerate sub-pixel anti-aliasing. Returns -1 when no credible
    /// match exists. Among near-equal candidates the one closest to <paramref name="hint"/> wins.
    /// </summary>
    public static int FindShift(
        CapturedFrame previous,
        CapturedFrame current,
        int top,
        int bottom,
        int left,
        int right,
        int? hint,
        out double score,
        int minShift = 1,
        int maxShift = int.MaxValue)
    {
        score = 0;
        var height = bottom - top + 1;
        if (height < 2 || right < left)
        {
            return -1;
        }

        var bins = Math.Clamp((right - left + 1) / 4, 8, 64);
        var a = new float[height][];
        var b = new float[height][];
        var informative = new bool[height];
        for (var i = 0; i < height; i++)
        {
            a[i] = RowSignature(Row(previous, top + i)[left..(right + 1)], bins, out _);
            b[i] = RowSignature(Row(current, top + i)[left..(right + 1)], bins, out informative[i]);
        }

        var errors = new double[height];
        Array.Fill(errors, double.MaxValue);
        var best = double.MaxValue;
        var valid = new List<double>();
        for (var dy = 1; dy < height; dy++)
        {
            if (dy < minShift)
            {
                continue;
            }

            // Tiny overlaps match by accident, so require real overlap.
            if (dy > maxShift || height - dy < height / 5)
            {
                break;
            }

            double sum = 0;
            var count = 0;
            for (var i = 0; i + dy < height; i++)
            {
                if (!informative[i])
                {
                    continue;
                }

                var x = b[i];
                var y = a[i + dy];
                double d = 0;
                for (var k = 0; k < bins; k++)
                {
                    d += Math.Abs(x[k] - y[k]);
                }

                sum += d;
                count++;
            }

            if (count < 8)
            {
                continue;
            }

            errors[dy] = sum / (count * bins);
            valid.Add(errors[dy]);
            best = Math.Min(best, errors[dy]);
        }

        if (valid.Count == 0)
        {
            return -1;
        }

        valid.Sort();
        var median = valid[valid.Count / 2];
        score = median > 0 ? 1 - (best / median) : (best == 0 ? 1 : 0);
        if (best > 6 || score < 0.6)
        {
            return -1;
        }

        var pick = -1;
        for (var dy = 1; dy < height; dy++)
        {
            if (errors[dy] > (best * 1.15) + 0.3)
            {
                continue;
            }

            if (pick < 0)
            {
                pick = dy;
                continue;
            }

            if (hint is { } h)
            {
                if (Math.Abs(dy - h) < Math.Abs(pick - h))
                {
                    pick = dy;
                }
            }
            else if (errors[dy] < errors[pick])
            {
                pick = dy;
            }
        }

        return pick;
    }

    /// <summary>
    /// Per-column vote: does the column move with the content (scrolling pane) or stay put
    /// (sidebar, sticky panel)? Accumulated across steps and resolved when composing.
    /// </summary>
    public static void VoteColumns(
        CapturedFrame previous,
        CapturedFrame current,
        int top,
        int bottom,
        int shift,
        int[] scrollVotes,
        int[] staticVotes)
    {
        var width = current.Width;
        var prev = Pixels(previous);
        var cur = Pixels(current);
        for (var y = top; y + shift <= bottom; y++)
        {
            var c = y * width;
            var s = (y + shift) * width;
            for (var x = 0; x < width; x++)
            {
                var p = cur[c + x];
                var shifted = Close(p, prev[s + x]);
                var stationary = Close(p, prev[c + x]);
                if (shifted && !stationary)
                {
                    scrollVotes[x]++;
                }
                else if (stationary && !shifted)
                {
                    staticVotes[x]++;
                }
            }
        }
    }

    /// <summary>Grayscale averaged into bins. Flat rows match anything, so they are not evidence.</summary>
    private static float[] RowSignature(ReadOnlySpan<int> row, int bins, out bool informative)
    {
        var signature = new float[bins];
        var n = row.Length;
        int lo = 255, hi = 0;
        for (var x = 0; x < n; x++)
        {
            var g = Gray(row[x]);
            signature[x * bins / n] += g;
            lo = Math.Min(lo, g);
            hi = Math.Max(hi, g);
        }

        for (var k = 0; k < bins; k++)
        {
            var w = ((k + 1) * n / bins) - (k * n / bins);
            if (w > 0)
            {
                signature[k] /= w;
            }
        }

        informative = hi - lo > 24;
        return signature;
    }
}
