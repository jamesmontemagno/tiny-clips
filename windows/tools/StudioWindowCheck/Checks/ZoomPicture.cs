using System.Globalization;
using TinyClips.Core.Studio;
using TinyClips.Tools.StudioPreviewCheck.Media;
using TinyClips.Tools.StudioWindowCheck.Capture;

namespace TinyClips.Tools.StudioWindowCheck.Checks;

// Reading from a picture which part of a recording it shows. The test clips have straight edges
// between flat colours whose places in the clip are known: the colour bars, the colour patches and
// the cells of the frame-number strip. Where each of them is in the picture says which part of the
// clip the picture shows, and how large. Colours alone would not: a picture of the wrong part of
// the screen is made of the same colours.
internal sealed partial class WindowChecks
{
    // How far an edge may be from where the wanted part of the recording puts it, in pixels of the
    // picture. The format lets a renderer move a layer's edges to the nearest whole pixel, and the
    // preview scales a frame twice on its way to the screen: into a texture of whole pixels that
    // is between one and two times the size needed, and from there onto the canvas.
    private const double EdgeTolerance = 0.75;

    // Where the recording is magnified so far that half a pixel of it is more than that, half a
    // pixel of the recording: nothing in a video is placed more closely than its own pixels, and
    // where an edge is read from its colours, the video holds those for every other pixel only.
    private const double EdgeToleranceInClipPixels = 0.5;

    // How far a flat colour of the test clips may be from its nominal value once it has been
    // encoded, decoded and scaled.
    private const double LandmarkColorTolerance = 48;

    private static readonly Rgb Red = new(255, 0, 0);
    private static readonly Rgb Green = new(0, 255, 0);
    private static readonly Rgb Yellow = new(255, 255, 0);
    private static readonly Rgb Blue = new(0, 0, 255);
    private static readonly Rgb Magenta = new(255, 0, 255);
    private static readonly Rgb Cyan = new(0, 255, 255);
    private static readonly Rgb Gray = new(128, 128, 128);

    /// <summary>A part of a recording's frame, as fractions of it.</summary>
    private readonly record struct ScreenPart(double X, double Y, double Width, double Height)
    {
        public static readonly ScreenPart Whole = new(0, 0, 1, 1);

        public StudioFrameRect Rect => new(X, Y, Width, Height);

        /// <summary>The part that is a share of the way from one part to another, each of x, y, width and height by itself.</summary>
        public static ScreenPart Between(ScreenPart from, ScreenPart to, double amount) => new(
            from.X + ((to.X - from.X) * amount),
            from.Y + ((to.Y - from.Y) * amount),
            from.Width + ((to.Width - from.Width) * amount),
            from.Height + ((to.Height - from.Height) * amount));

        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"({X:0.0000}, {Y:0.0000}, {Width:0.0000}, {Height:0.0000})");
    }

    /// <summary>
    /// A straight edge between two flat colours of a test clip, in clip pixels. An upright edge is
    /// at x = <paramref name="At"/> and is looked for along one of the rows in
    /// <paramref name="Across"/>; a level edge is at y = <paramref name="At"/> and is looked for
    /// down one of the columns.
    /// </summary>
    /// <param name="Before">The colour left of an upright edge or above a level one, or null when it is not a round number.</param>
    /// <param name="After">The colour on the other side.</param>
    /// <param name="Flat">How many clip pixels of flat colour there are on the side of the edge that has fewer.</param>
    private sealed record Landmark(string Name, bool IsUpright, double At, double[] Across, Rgb? Before, Rgb? After, double Flat);

    /// <summary>An edge found in a picture: where the wanted part puts it, and where it is, in pixels of the picture.</summary>
    private readonly record struct EdgeReading(string Name, bool IsUpright, double Expected, double Found)
    {
        public double Off => Math.Abs(Found - Expected);
    }

    /// <summary>What a picture shows inside a layer's rectangle, read against the part of the clip it should show.</summary>
    /// <param name="Strip">The frame number read where the strip should be, or <see cref="FrameCode.Unreadable"/>.</param>
    /// <param name="StripIsInside">Whether the whole strip is in the part that should show.</param>
    /// <param name="Edges">The edges that were found.</param>
    /// <param name="Missing">The edges that should be in the picture and were not found where they should be.</param>
    /// <param name="Scale">How many pixels of the picture one pixel of the clip is.</param>
    private sealed record PartReading(ScreenPart Wanted, StudioFrameRect Layer, int Strip, bool StripIsInside, IReadOnlyList<EdgeReading> Edges, IReadOnlyList<string> Missing, double Scale)
    {
        public double Worst => Edges.Count == 0 ? double.NaN : Edges.Max(edge => edge.Off);

        /// <summary>How far an edge of this picture may be from its place: see <see cref="EdgeTolerance"/>.</summary>
        public double Allowed => Math.Max(EdgeTolerance, EdgeToleranceInClipPixels * Scale);

        /// <summary>Every edge that is further than a distance from its place, with how far and which way.</summary>
        public string Beyond(double distance) => string.Join(", ", Edges.Where(edge => edge.Off > distance).Select(edge => string.Create(CultureInfo.InvariantCulture, $"{edge.Name} {edge.Found - edge.Expected:+0.00;-0.00} px at {edge.Expected:0}")));

        public override string ToString()
        {
            var edges = Edges.Count == 0
                ? "no edge found"
                : string.Create(CultureInfo.InvariantCulture, $"{Edges.Count} edges within {Worst:0.00} px of where {Wanted} puts them (the furthest: {Edges.MaxBy(edge => edge.Off).Name}{(Allowed > EdgeTolerance ? $"; at {Scale:0.00} pixels to one of the clip's, {Allowed:0.00} px is allowed" : string.Empty)})");
            var missing = Missing.Count == 0 ? string.Empty : $"; not found where they should be: {string.Join(", ", Missing)}";
            var strip = !StripIsInside ? "the frame strip is not in that part" : Strip == FrameCode.Unreadable ? "the frame strip does not read" : $"the frame strip reads {Strip}";
            return $"{edges}{missing}; {strip}";
        }
    }

    // ---------------------------------------------------------------------------------------
    // The edges of the two test clips
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The edges of the screen clip that do not move: its six colour bars, 320 pixels each, the
    /// colour patches at y 450 to 522, and the frame strip at y 48 to 144, whose cells depend on
    /// the frame. The rows and columns named for each edge stay clear of the clip's text and of
    /// the other landmarks; what the clip animates across them is noticed when an edge is read.
    /// </summary>
    private static IEnumerable<Landmark> ScreenLandmarks(int frame)
    {
        var clip = TestMedia.Screen;
        double[] barRows = [200, 300, 400, 560, 640, 720, 800, 900, 1000];
        yield return new Landmark("the red and green bars", true, 320, [380, 420, 600, 700, 800, 900, 1000], Red, Green, 120);
        yield return new Landmark("the green and yellow bars", true, 640, [340, 380, 420, 600, 700, 800, 900, 1000], Green, Yellow, 80);
        yield return new Landmark("the yellow and blue bars", true, 960, barRows, Yellow, Blue, 150);
        yield return new Landmark("the blue and magenta bars", true, 1280, barRows, Blue, Magenta, 150);
        yield return new Landmark("the magenta and cyan bars", true, 1600, barRows, Magenta, Cyan, 150);

        // The patches: red on the red bar, which does not show; lime, of which the 32 pixels on
        // the red bar show; blue and gray on the green bar.
        var top = clip.PatchY;
        var bottom = clip.PatchY + clip.PatchSize;
        double[] patchRows = [top + 20, top + 36, top + 52];
        var lime = clip.PatchX + clip.PatchPitch;
        var blue = clip.PatchX + (2 * clip.PatchPitch);
        var gray = clip.PatchX + (3 * clip.PatchPitch);
        yield return new Landmark("the lime patch's left edge", true, lime, patchRows, Red, Green, 24);
        yield return new Landmark("the blue patch's left edge", true, blue, patchRows, Green, Blue, 24);
        yield return new Landmark("the blue patch's right edge", true, blue + clip.PatchSize, patchRows, Blue, Green, 24);
        yield return new Landmark("the gray patch's left edge", true, gray, patchRows, Green, Gray, 24);
        yield return new Landmark("the gray patch's right edge", true, gray + clip.PatchSize, patchRows, Gray, Green, 72);
        yield return new Landmark("the lime patch's top", false, top, [lime + 8, lime + 16, lime + 24], Red, Green, 72);
        yield return new Landmark("the lime patch's bottom", false, bottom, [lime + 8, lime + 16, lime + 24], Green, Red, 72);
        yield return new Landmark("the blue patch's top", false, top, [blue + 18, blue + 36, blue + 54], Green, Blue, 72);
        yield return new Landmark("the blue patch's bottom", false, bottom, [blue + 18, blue + 36, blue + 54], Blue, Green, 72);
        yield return new Landmark("the gray patch's top", false, top, [gray + 18, gray + 36, gray + 54], Green, Gray, 72);
        yield return new Landmark("the gray patch's bottom", false, bottom, [gray + 18, gray + 36, gray + 54], Gray, Green, 72);

        foreach (var landmark in StripLandmarks(clip, frame, x => x < 320 ? Red : x < 640 ? Green : Yellow))
        {
            yield return landmark;
        }
    }

    /// <summary>
    /// The edges of the camera clip that do not move. Its bars are a turned hue of the screen's,
    /// so their colours are not round numbers and only their flatness is asked for; the patches
    /// and the strip are drawn over them in plain colours.
    /// </summary>
    private static IEnumerable<Landmark> CameraLandmarks(int frame)
    {
        var clip = TestMedia.Camera;

        // The one bar edge that is on a whole pixel: the middle of the picture.
        yield return new Landmark("the two bars in the middle", true, 640, [275, 292, 540, 580, 620, 660], null, null, 60);

        var top = clip.PatchY;
        var bottom = clip.PatchY + clip.PatchSize;
        double[] patchRows = [top + 16, top + 32, top + 48];
        Rgb[] colors = [Red, Green, Blue, Gray];
        string[] names = ["red", "lime", "blue", "gray"];
        for (var index = 0; index < colors.Length; index++)
        {
            var left = clip.PatchX + (index * clip.PatchPitch);
            double[] columns = [left + 16, left + 32, left + 48];
            yield return new Landmark($"the {names[index]} patch's left edge", true, left, patchRows, null, colors[index], 32);
            yield return new Landmark($"the {names[index]} patch's right edge", true, left + clip.PatchSize, patchRows, colors[index], null, 32);
            yield return new Landmark($"the {names[index]} patch's top", false, top, columns, null, colors[index], 32);
            yield return new Landmark($"the {names[index]} patch's bottom", false, bottom, columns, colors[index], null, 64);
        }

        foreach (var landmark in StripLandmarks(clip, frame, _ => null))
        {
            yield return landmark;
        }
    }

    /// <summary>
    /// The level edges of a clip's frame strip for one frame: between its two rows, which are
    /// each other's opposite in every column, and along its top, where the upper row is white or
    /// black against what is above it.
    /// </summary>
    private static IEnumerable<Landmark> StripLandmarks(ClipSpec clip, int frame, Func<double, Rgb?> above)
    {
        var white = new Rgb(255, 255, 255);
        var black = new Rgb(0, 0, 0);
        foreach (var column in new[] { 1, 4, 7, 10 })
        {
            var isSet = ((frame >> (TestMedia.CodeBits - 1 - column)) & 1) != 0;
            var x = clip.CodeX + ((column + 0.5) * clip.CodeCell);
            double[] columns = [x - 8, x, x + 8];
            yield return new Landmark($"the frame strip's two rows, column {column}", false, clip.CodeY + clip.CodeCell, columns, isSet ? white : black, isSet ? black : white, clip.CodeCell);

            // White on the yellow bar is too alike to find an edge between.
            var over = above(x);
            if (!(isSet && over is { } color && color == Yellow))
            {
                yield return new Landmark($"the frame strip's top, column {column}", false, clip.CodeY, columns, over, isSet ? white : black, 40);
            }
        }
    }

    // ---------------------------------------------------------------------------------------
    // Reading
    // ---------------------------------------------------------------------------------------

    /// <summary>A layer rectangle with its edges on whole pixels, as the format lets a renderer draw it, and as this one does.</summary>
    private static StudioFrameRect Aligned(StudioFrameRect rect)
    {
        var left = Math.Round(rect.X, MidpointRounding.AwayFromZero);
        var top = Math.Round(rect.Y, MidpointRounding.AwayFromZero);
        var right = Math.Round(rect.X + rect.Width, MidpointRounding.AwayFromZero);
        var bottom = Math.Round(rect.Y + rect.Height, MidpointRounding.AwayFromZero);
        return new StudioFrameRect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    /// <summary>
    /// Reads a picture against the part of a clip that should fill a layer's rectangle: the frame
    /// number where the strip should be, and every edge of the clip that should be in the
    /// picture, each looked for within a few pixels of its place.
    /// </summary>
    /// <param name="layer">The layer's rectangle in pixels of the picture.</param>
    /// <param name="inset">How far inside the rectangle an edge has to be to count: the layer's corner radius, or its border.</param>
    /// <param name="shown">
    /// For a layer that does not fill its rectangle, or that another layer lies over: whether a
    /// point of the picture shows this layer. Only what is read where it does counts.
    /// </param>
    /// <param name="trace">
    /// Null, or where to write down every line that was read: where it is in the picture, and
    /// what it found or why it found nothing. For a check that did not hold, which then says
    /// why an edge was not found and not only that it was not.
    /// </param>
    private static PartReading ReadPart(Shot shot, ClipSpec clip, IEnumerable<Landmark> landmarks, StudioFrameRect layer, ScreenPart want, bool mirror, double inset, Func<double, double, bool>? shown = null, List<string>? trace = null)
    {
        var map = ClipMap.ForLayer(clip, layer, want.Rect, mirror);
        var scale = Math.Min(Math.Abs(map.ScaleX), map.ScaleY);

        // How far from an edge its colours are mixed, in pixels of the picture: a video keeps a
        // colour for every other pixel only, so the colour of the two clip pixels next to an edge
        // is a mixture, and scaling the frame spreads that by a pixel more.
        var blur = 1 + (2 * scale);
        var (left, top, right, bottom) = (layer.X + inset, layer.Y + inset, layer.X + layer.Width - inset, layer.Y + layer.Height - inset);
        bool Inside(double x, double y, double reachX, double reachY) =>
            x - reachX >= left && x + reachX <= right && y - reachY >= top && y + reachY <= bottom
            && (shown is null || (shown(x - reachX, y - reachY) && shown(x + reachX, y - reachY) && shown(x - reachX, y + reachY) && shown(x + reachX, y + reachY)));

        var (stripLeft, stripTop) = map.Apply(clip.CodeX, clip.CodeY);
        var (stripRight, stripBottom) = map.Apply(clip.CodeX + clip.CodeWidth, clip.CodeY + clip.CodeHeight);
        var stripIsInside = Inside((stripLeft + stripRight) / 2, (stripTop + stripBottom) / 2, Math.Abs(stripRight - stripLeft) / 2, (stripBottom - stripTop) / 2);
        var strip = stripIsInside ? FrameCode.Decode(shot.Bgra, shot.Width, shot.Height, clip, map) : FrameCode.Unreadable;
        trace?.Add(string.Create(CultureInfo.InvariantCulture, $"{clip.Label} in {R(layer)}, showing {want}: {scale:0.0000} pixels of the picture to one of the clip, colours mixed up to {blur:0.00} px from an edge; the frame strip {(stripIsInside ? $"reads {strip}" : "is not in that part")}"));

        var edges = new List<EdgeReading>();
        var missing = new List<string>();
        foreach (var landmark in landmarks)
        {
            // As long a line across the edge as the flat colour on both sides of it allows.
            var half = Math.Min(6, (int)(landmark.Flat * scale) - 4);
            if (half < 2)
            {
                trace?.Add($"  {landmark.Name}: not read, there is too little flat colour beside it at this size");
                continue;
            }

            var reach = half + 3;
            var lines = 0;
            var expected = double.NaN;
            var found = new List<double>();
            var read = trace is null ? null : new List<string>();
            foreach (var across in landmark.Across)
            {
                var (x, y) = landmark.IsUpright ? map.Apply(landmark.At, across) : map.Apply(across, landmark.At);
                if (!Inside(x, y, landmark.IsUpright ? reach : 2, landmark.IsUpright ? 2 : reach))
                {
                    read?.Add(string.Create(CultureInfo.InvariantCulture, $"    {(landmark.IsUpright ? "row" : "column")} {across:0} of the clip, at ({x:0.00}, {y:0.00}): not in the part of the layer that shows"));
                    continue;
                }

                lines++;
                expected = landmark.IsUpright ? x : y;

                // A mirrored layer shows the colour after an upright edge on its left.
                var swap = landmark.IsUpright && mirror;
                var at = FindEdge(shot, x, y, landmark.IsUpright, half, blur, swap ? landmark.After : landmark.Before, swap ? landmark.Before : landmark.After, out var why);
                if (!double.IsNaN(at))
                {
                    found.Add(at);
                }

                read?.Add(string.Create(CultureInfo.InvariantCulture, $"    {(landmark.IsUpright ? "row" : "column")} {across:0} of the clip, at ({x:0.00}, {y:0.00}): {(double.IsNaN(at) ? "nothing, " + why : $"the edge {at - expected:+0.00;-0.00} px from its place")}"));
            }

            // The edge is read along every line that crosses it inside the picture, and is where
            // most of them say. A wrong part of the clip moves an edge the same way on all of
            // them. Lines that do not agree have something else on them: what the clip animates,
            // which is never level or upright. An edge that only one line crosses cannot be
            // checked like that, and is left out.
            if (lines < 2)
            {
                trace?.Add($"  {landmark.Name}: left out, {(lines == 0 ? "no line" : "only one line")} crosses it inside the picture");
                trace?.AddRange(read!);
                continue;
            }

            if (Agreed(found, lines) is { } where)
            {
                edges.Add(new EdgeReading(landmark.Name, landmark.IsUpright, expected, where));
                trace?.Add(string.Create(CultureInfo.InvariantCulture, $"  {landmark.Name}: {where - expected:+0.00;-0.00} px from its place, by {found.Count} of {lines} lines of {2 * half} pixels"));
            }
            else
            {
                missing.Add(landmark.Name);
                trace?.Add($"  {landmark.Name}: NOT FOUND, {found.Count} of {lines} lines of {2 * half} pixels read an edge{(found.Count >= 2 ? " and they do not agree" : string.Empty)}");
            }

            trace?.AddRange(read!);
        }

        return new PartReading(want, layer, strip, stripIsInside, edges, missing, scale);
    }

    /// <summary>
    /// The middle one of the readings of an edge, when at least two of them, and more than half
    /// of the lines that were read, are within three quarters of a pixel of it; otherwise null.
    /// </summary>
    private static double? Agreed(List<double> readings, int lines)
    {
        if (readings.Count < 2)
        {
            return null;
        }

        readings.Sort();
        var middle = readings.Count % 2 == 1 ? readings[readings.Count / 2] : (readings[(readings.Count / 2) - 1] + readings[readings.Count / 2]) / 2;
        var agreeing = readings.Count(reading => Math.Abs(reading - middle) <= 0.75);
        return agreeing >= 2 && agreeing * 2 > lines ? middle : null;
    }

    /// <summary>
    /// Where an edge between two flat colours is, along a line of 2 × half pixels across it
    /// through (x, y): each pixel counts for as much of itself as is still the first colour. The
    /// line runs along x, or along y. NaN when the line leaves the picture, when the pixels just
    /// before and after it are not the two flat colours, or when something else lies on it.
    /// </summary>
    /// <remarks>
    /// How much of a pixel is still the first colour is taken from its brightness where the two
    /// colours differ in brightness. A video keeps the brightness of every pixel, and the colour
    /// of every other one only, so next to an edge the colours are a mixture that is on neither
    /// side's way to the other, while the brightness changes exactly at the edge.
    /// </remarks>
    /// <param name="blur">How far from the edge the colours may be a mixture, in pixels.</param>
    /// <param name="miss">When no edge is found: why not.</param>
    private static double FindEdge(Shot shot, double x, double y, bool alongX, int half, double blur, Rgb? before, Rgb? after, out EdgeMiss miss)
    {
        const double Flatness = 14;
        miss = default;
        var start = (int)Math.Floor(alongX ? x : y) - half;
        var count = 2 * half;
        var cross = (int)Math.Floor(alongX ? y : x);

        // Three pixels side by side along the edge, to steady the reading.
        Rgb At(int index)
        {
            double r = 0, g = 0, b = 0;
            for (var offset = -1; offset <= 1; offset++)
            {
                var (px, py) = alongX ? (index, cross + offset) : (cross + offset, index);
                if (px < 0 || py < 0 || px >= shot.Width || py >= shot.Height)
                {
                    return new Rgb(-1, -1, -1);
                }

                var at = ((py * shot.Width) + px) * 4;
                b += shot.Bgra[at];
                g += shot.Bgra[at + 1];
                r += shot.Bgra[at + 2];
            }

            return new Rgb(r / 3, g / 3, b / 3);
        }

        // Two pixels on each side of the line, a pixel clear of it, have to be flat.
        var (a1, a2, b1, b2) = (At(start - 3), At(start - 2), At(start + count + 1), At(start + count + 2));
        if (a1.R < 0 || a2.R < 0 || b1.R < 0 || b2.R < 0)
        {
            miss = new EdgeMiss(EdgeMissKind.LeavesThePicture, default, default, default, 0);
            return double.NaN;
        }

        if (a1.Distance(a2) > Flatness)
        {
            miss = new EdgeMiss(EdgeMissKind.NotFlatBefore, default, a1, a2, 0);
            return double.NaN;
        }

        if (b1.Distance(b2) > Flatness)
        {
            miss = new EdgeMiss(EdgeMissKind.NotFlatAfter, default, b1, b2, 0);
            return double.NaN;
        }

        var first = new Rgb((a1.R + a2.R) / 2, (a1.G + a2.G) / 2, (a1.B + a2.B) / 2);
        var second = new Rgb((b1.R + b2.R) / 2, (b1.G + b2.G) / 2, (b1.B + b2.B) / 2);
        if (before is { } wantFirst && first.Distance(wantFirst) > LandmarkColorTolerance)
        {
            miss = new EdgeMiss(EdgeMissKind.WrongColourBefore, first, wantFirst, default, 0);
            return double.NaN;
        }

        if (after is { } wantSecond && second.Distance(wantSecond) > LandmarkColorTolerance)
        {
            miss = new EdgeMiss(EdgeMissKind.WrongColourAfter, second, wantSecond, default, 0);
            return double.NaN;
        }

        // The brightness of a high-definition video (BT.709), which is what a decoder turns into red, green and blue.
        static double Brightness(Rgb color) => (0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B);

        var (dr, dg, db) = (second.R - first.R, second.G - first.G, second.B - first.B);
        var apart = (dr * dr) + (dg * dg) + (db * db);
        var brighter = Brightness(second) - Brightness(first);
        var byBrightness = Math.Abs(brighter) >= 40;
        if (!byBrightness && apart < 40 * 40)
        {
            miss = new EdgeMiss(EdgeMissKind.TooAlike, default, first, second, 0);
            return double.NaN;
        }

        // A colour as a video holds it: its brightness, and how blue and how red it is beside that.
        static (double Y, double Cb, double Cr) Parts(Rgb color)
        {
            var y = Brightness(color);
            return (y, (color.B - y) / 1.8556, (color.R - y) / 1.5748);
        }

        // What a video mixes next to an edge is, in each of the three, between the two colours.
        // Anything else on the line is not the edge: something the clip moves across it.
        const double Slack = 24;
        var (from, to) = (Parts(first), Parts(second));
        bool Between(Rgb color)
        {
            var (y, cb, cr) = Parts(color);
            return y >= Math.Min(from.Y, to.Y) - Slack && y <= Math.Max(from.Y, to.Y) + Slack
                && cb >= Math.Min(from.Cb, to.Cb) - Slack && cb <= Math.Max(from.Cb, to.Cb) + Slack
                && cr >= Math.Min(from.Cr, to.Cr) - Slack && cr <= Math.Max(from.Cr, to.Cr) + Slack;
        }

        Span<Rgb> line = stackalloc Rgb[count];
        double sum = 0;
        for (var index = 0; index < count; index++)
        {
            // How far the pixel is along the way from the first colour to the second.
            var color = At(start + index);
            line[index] = color;
            var along = byBrightness
                ? (Brightness(color) - Brightness(first)) / brighter
                : (((color.R - first.R) * dr) + ((color.G - first.G) * dg) + ((color.B - first.B) * db)) / apart;
            if (along < -0.25 || along > 1.25 || !Between(color))
            {
                miss = new EdgeMiss(EdgeMissKind.NotBetween, color, first, second, index - half);
                return double.NaN;
            }

            sum += 1 - Math.Clamp(along, 0, 1);
        }

        // Further from the edge than its mixture reaches, the line has to be the two flat colours.
        // Brightness is not mixed by the video, only spread by scaling the frame, so it has to be
        // that of the two colours from closer to the edge still.
        var edge = start + sum;
        var spread = Math.Min(blur, 2.5);
        for (var index = 0; index < count; index++)
        {
            var middle = start + index + 0.5;
            if ((middle < edge - blur && line[index].Distance(first) > LandmarkColorTolerance)
                || (middle > edge + blur && line[index].Distance(second) > LandmarkColorTolerance))
            {
                miss = new EdgeMiss(EdgeMissKind.NeitherColour, line[index], first, second, index - half);
                return double.NaN;
            }

            if (byBrightness
                && ((middle < edge - spread && Math.Abs(Brightness(line[index]) - Brightness(first)) > 0.2 * Math.Abs(brighter))
                    || (middle > edge + spread && Math.Abs(Brightness(line[index]) - Brightness(second)) > 0.2 * Math.Abs(brighter))))
            {
                miss = new EdgeMiss(EdgeMissKind.NeitherBrightness, line[index], first, second, index - half);
                return double.NaN;
            }
        }

        return edge;
    }

    private enum EdgeMissKind
    {
        None,
        LeavesThePicture,
        NotFlatBefore,
        NotFlatAfter,
        WrongColourBefore,
        WrongColourAfter,
        TooAlike,
        NotBetween,
        NeitherColour,
        NeitherBrightness,
    }

    /// <summary>Why a line across an edge found no edge. Made without work for the garbage collector, and put into words only when a check asks.</summary>
    /// <param name="Seen">The colour that was not expected, where one was.</param>
    /// <param name="First">The colour before the line, or the first of two that are compared.</param>
    /// <param name="Second">The colour after the line, or the second of the two.</param>
    /// <param name="Pixel">Which pixel of the line, counted from its middle: -1 is the last before it, 0 the first after it.</param>
    private readonly record struct EdgeMiss(EdgeMissKind Kind, Rgb Seen, Rgb First, Rgb Second, int Pixel)
    {
        public override string ToString() => Kind switch
        {
            EdgeMissKind.LeavesThePicture => "the line leaves the picture",
            EdgeMissKind.NotFlatBefore => $"the two pixels before the line are not one flat colour: {First} and {Second}",
            EdgeMissKind.NotFlatAfter => $"the two pixels after the line are not one flat colour: {First} and {Second}",
            EdgeMissKind.WrongColourBefore => $"before the line there is {Seen}, and the clip has {First} there",
            EdgeMissKind.WrongColourAfter => $"after the line there is {Seen}, and the clip has {First} there",
            EdgeMissKind.TooAlike => $"the colours before and after the line are too alike: {First} and {Second}",
            EdgeMissKind.NotBetween => $"pixel {Pixel:+0;-0} of the line is {Seen}, which is not on the way from {First} to {Second}",
            EdgeMissKind.NeitherColour => $"pixel {Pixel:+0;-0} of the line, further from the edge than a mixture reaches, is {Seen}, which is neither {First} nor {Second}",
            EdgeMissKind.NeitherBrightness => $"pixel {Pixel:+0;-0} of the line, away from the edge, is {Seen}, which is as bright as neither {First} nor {Second}",
            _ => "an edge",
        };
    }

    /// <summary>
    /// Null when a reading shows the wanted part: the strip reads the frame, where the strip is
    /// in that part; enough edges were found to fix the part across and down; none of them is
    /// further than the tolerance from its place; and few that should be there are missing.
    /// Otherwise what is wrong.
    /// </summary>
    /// <param name="frame">The frame the picture should show, or null when that is not asked.</param>
    /// <param name="more">How much further than the tolerance an edge may be: for a picture that has been through an encoder once more.</param>
    private static string? Judge(PartReading reading, int? frame, double more = 0)
    {
        var tolerance = reading.Allowed + more;
        if (frame is { } wanted && reading.StripIsInside && reading.Strip != wanted)
        {
            return $"the frame strip reads {(reading.Strip == FrameCode.Unreadable ? "nothing" : reading.Strip.ToString(CultureInfo.InvariantCulture))} where {reading.Wanted} puts it, and should read {wanted}";
        }

        // Two upright edges a fifth of the layer apart fix where the part starts and how wide it
        // is; two level edges fix the same down the picture. The level ones are closer together,
        // because all there are of them are in the top left of the clips.
        var upright = reading.Edges.Where(edge => edge.IsUpright).Select(edge => edge.Expected).ToArray();
        var level = reading.Edges.Where(edge => !edge.IsUpright).Select(edge => edge.Expected).ToArray();
        if (upright.Length < 2 || upright.Max() - upright.Min() < reading.Layer.Width / 5)
        {
            return $"too few upright edges were found to tell which part of the clip the picture shows ({reading})";
        }

        if (level.Length < 2 || level.Max() - level.Min() < 24)
        {
            return $"too few level edges were found to tell which part of the clip the picture shows ({reading})";
        }

        if (reading.Missing.Count > (reading.Edges.Count + reading.Missing.Count) / 3)
        {
            return $"too many edges are not where they should be ({reading})";
        }

        return reading.Worst <= tolerance ? null : string.Create(CultureInfo.InvariantCulture, $"an edge is {reading.Worst:0.00} px from its place, and may be {tolerance:0.00} px: {reading.Beyond(tolerance)} ({reading})");
    }

    /// <summary>
    /// The part of the clip that puts the found edges exactly where they are, from the two
    /// upright and the two level edges that are furthest apart. For the detail of a check.
    /// </summary>
    private static ScreenPart? Fitted(PartReading reading, ClipSpec clip, bool mirror)
    {
        var map = ClipMap.ForLayer(clip, reading.Layer, reading.Wanted.Rect, mirror);
        var upright = reading.Edges.Where(edge => edge.IsUpright).OrderBy(edge => edge.Expected).ToArray();
        var level = reading.Edges.Where(edge => !edge.IsUpright).OrderBy(edge => edge.Expected).ToArray();
        if (upright.Length < 2 || level.Length < 2 || upright[^1].Expected - upright[0].Expected < 1 || level[^1].Expected - level[0].Expected < 1)
        {
            return null;
        }

        // Clip pixels per picture pixel, as wanted, corrected by how much further apart the edges are found than expected.
        var stretchX = (upright[^1].Found - upright[0].Found) / (upright[^1].Expected - upright[0].Expected);
        var stretchY = (level[^1].Found - level[0].Found) / (level[^1].Expected - level[0].Expected);
        var scaleX = map.ScaleX * stretchX;
        var scaleY = map.ScaleY * stretchY;

        // The clip coordinate of the first edge, which is found at a known picture coordinate.
        var clipX = (upright[0].Expected - map.OriginX) / map.ScaleX;
        var clipY = (level[0].Expected - map.OriginY) / map.ScaleY;
        var originX = upright[0].Found - (clipX * scaleX);
        var originY = level[0].Found - (clipY * scaleY);
        var leftClip = ((mirror ? reading.Layer.X + reading.Layer.Width : reading.Layer.X) - originX) / scaleX;
        var topClip = (reading.Layer.Y - originY) / scaleY;
        return new ScreenPart(leftClip / clip.Width, topClip / clip.Height, reading.Layer.Width / (Math.Abs(scaleX) * clip.Width), reading.Layer.Height / (scaleY * clip.Height));
    }
}
