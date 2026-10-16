using TinyClips.Core.Studio.Rendering;
using TinyClips.Tools.StudioPreviewCheck.Media;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace TinyClips.Tools.StudioPreviewCheck.Windowed;

/// <summary>One marker block found in a screenshot.</summary>
/// <param name="X">Left edge of the block (its key-colour bar) in screenshot pixels.</param>
/// <param name="KeyWidth">Width of the key-colour bar as found (8 when shown 1:1).</param>
/// <param name="VerticalContrast">Mean step between neighbouring pixels across the 1 px vertical stripes (255 when shown 1:1).</param>
internal readonly record struct MarkerHit(int X, int Y, int KeyWidth, int KeyHeight, double VerticalContrast, double HorizontalContrast)
{
    /// <summary>The block is on screen pixel for pixel: exact size and full-contrast 1 px stripes.</summary>
    public bool Crisp =>
        KeyWidth == PanelMarkers.KeyWidth && KeyHeight == PanelMarkers.BlockHeight && VerticalContrast >= 250 && HorizontalContrast >= 250;
}

/// <summary>The pixels of one key colour found in a part of a screenshot: their bounding box and how many there are.</summary>
internal readonly record struct KeyBox(int X, int Y, int Width, int Height, int Count)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;
}

/// <summary>What a screenshot says about the panel.</summary>
/// <param name="Width">Width of the swap chain on screen, from the distance between the corner markers; 0 unless all four were found.</param>
/// <param name="TopLeftSquare">The XAML square that layout puts in the panel's top-left corner, as found near the swap chain's.</param>
/// <param name="HandleCoverage">How much of the square where layout puts the XAML element in the panel's centre has that element's colour (1 when it is drawn whole).</param>
/// <param name="HandleSpill">How much of a thin frame just outside that square has it (0 when the element is in place and no larger).</param>
/// <param name="Serial">The number the tool wrote into the scene the screenshot shows, or -1 when it cannot be read.</param>
internal sealed record PanelReading(
    MarkerHit[] Markers,
    int OriginX,
    int OriginY,
    int Width,
    int Height,
    KeyBox? TopLeftSquare,
    KeyBox? BottomRightSquare,
    double HandleCoverage,
    double HandleSpill,
    Shown Frames,
    int Serial = -1)
{
    public bool AllFound => Markers.Length == 4;

    /// <summary>All four corner markers are on screen pixel for pixel.</summary>
    public bool AllCrisp => AllFound && Markers.All(m => m.Crisp);

    /// <summary>The XAML element in the middle of the panel is drawn over the picture, whole and in place.</summary>
    public bool HandleVisible => AllFound && HandleCoverage >= 0.98 && HandleSpill <= 0.5;

    /// <summary>
    /// The swap chain's corners sit exactly on the corners XAML layout gives the panel: the picture
    /// fills the panel's rectangle, no more and no less.
    /// </summary>
    public bool FillsRectangle(int tolerance = 1) =>
        AllFound && TopLeftSquare is { } topLeft && BottomRightSquare is { } bottomRight &&
        Math.Abs(topLeft.X - OriginX) <= tolerance && Math.Abs(topLeft.Y - OriginY) <= tolerance &&
        Math.Abs(bottomRight.Right - (OriginX + Width)) <= tolerance && Math.Abs(bottomRight.Bottom - (OriginY + Height)) <= tolerance;

    public string Describe()
    {
        if (Markers.Length == 0)
        {
            return "no markers found";
        }

        var worst = Markers.OrderBy(m => Math.Min(m.VerticalContrast, m.HorizontalContrast)).First();
        var text = $"markers {Markers.Length}/4, pixel-exact {Markers.Count(m => m.Crisp)}/4 (worst: bar {worst.KeyWidth}x{worst.KeyHeight}, stripe contrast {worst.VerticalContrast:0}/{worst.HorizontalContrast:0})";
        if (AllFound)
        {
            text += $", swap chain on screen {Width}x{Height} px at {OriginX},{OriginY}";
            text += TopLeftSquare is { } topLeft ? $", XAML top-left square at {topLeft.X - OriginX:+0;-0;0},{topLeft.Y - OriginY:+0;-0;0} ({topLeft.Width}x{topLeft.Height})" : ", XAML top-left square not found";
            text += BottomRightSquare is { } bottomRight ? $", bottom-right square ends at {bottomRight.Right - (OriginX + Width):+0;-0;0},{bottomRight.Bottom - (OriginY + Height):+0;-0;0} ({bottomRight.Width}x{bottomRight.Height})" : ", XAML bottom-right square not found";
            text += $", centre element {HandleCoverage:0%} drawn";
        }

        return text + $", shows {Frames}{(Serial >= 0 ? $" (scene {Serial})" : string.Empty)}";
    }
}

/// <summary>
/// A small test pattern written into each corner of what the engine draws, and the code that finds
/// it again in a screenshot. Each block is a bar of a key colour, then 1 px vertical stripes, then
/// 1 px horizontal stripes. If the swap chain reaches the screen pixel for pixel the bar is exactly
/// 8x24 and neighbouring stripe pixels differ by 255; any scaling by XAML or the compositor blurs
/// the stripes and shows up as a lower contrast.
/// </summary>
internal static class PanelMarkers
{
    public const int Inset = 24;
    public const int KeyWidth = 8;
    public const int StripeWidth = 16;
    public const int BlockWidth = KeyWidth + StripeWidth + StripeWidth;
    public const int BlockHeight = 24;

    // The number of the scene, next to the bottom-left block: 16 bits, most significant first, and
    // their complement underneath, as in the clips' own frame numbers.
    public const int SerialBits = 16;
    public const int SerialCellWidth = 4;
    public const int SerialCellHeight = 10;
    public const int SerialGap = 4;
    public const int SerialWidth = SerialBits * SerialCellWidth;
    public const int SerialHeight = 2 * SerialCellHeight;
    public const int SerialMask = (1 << SerialBits) - 1;

    // Colours the test clips do not contain (their bars are the saturated primaries and secondaries).
    public static readonly (byte B, byte G, byte R) MarkerKey = (255, 128, 0);
    public static readonly (byte B, byte G, byte R) CornerKey = (0, 128, 255);
    public static readonly (byte B, byte G, byte R) HandleKey = (128, 0, 255);

    public static byte[] BuildBlock()
    {
        var block = new byte[BlockWidth * BlockHeight * 4];
        for (var y = 0; y < BlockHeight; y++)
        {
            for (var x = 0; x < BlockWidth; x++)
            {
                var i = ((y * BlockWidth) + x) * 4;
                byte b;
                byte g;
                byte r;
                if (x < KeyWidth)
                {
                    (b, g, r) = MarkerKey;
                }
                else
                {
                    var white = x < KeyWidth + StripeWidth ? (x & 1) == 0 : (y & 1) == 0;
                    b = g = r = white ? (byte)255 : (byte)0;
                }

                block[i] = b;
                block[i + 1] = g;
                block[i + 2] = r;
                block[i + 3] = 255;
            }
        }

        return block;
    }

    /// <summary>Writes the block into all four corners of <paramref name="target"/>. The caller holds the device lock.</summary>
    public static void Draw(StudioGraphicsDevice graphics, ID3D11Texture2D target, int width, int height, byte[] block)
    {
        if (width < (2 * Inset) + BlockWidth || height < (2 * Inset) + BlockHeight)
        {
            return;
        }

        Span<(int X, int Y)> corners =
        [
            (Inset, Inset),
            (width - Inset - BlockWidth, Inset),
            (Inset, height - Inset - BlockHeight),
            (width - Inset - BlockWidth, height - Inset - BlockHeight),
        ];
        foreach (var (x, y) in corners)
        {
            graphics.Context.UpdateSubresource(block, target, 0, BlockWidth * 4, 0, new Box(x, y, 0, x + BlockWidth, y + BlockHeight, 1));
        }
    }

    /// <summary>
    /// Writes a scene's number next to the bottom-left block, so that a screenshot says which of
    /// the scenes the engine drew it shows. The caller holds the device lock.
    /// </summary>
    /// <param name="strip">Scratch space of <see cref="SerialWidth"/> x <see cref="SerialHeight"/> BGRA pixels.</param>
    public static void DrawSerial(StudioGraphicsDevice graphics, ID3D11Texture2D target, int width, int height, int serial, byte[] strip)
    {
        if (width < (2 * Inset) + (2 * BlockWidth) + (2 * SerialGap) + SerialWidth || height < (2 * Inset) + BlockHeight)
        {
            return;
        }

        for (var y = 0; y < SerialHeight; y++)
        {
            for (var x = 0; x < SerialWidth; x++)
            {
                var bit = ((serial >> (SerialBits - 1 - (x / SerialCellWidth))) & 1) != 0;
                var white = y < SerialCellHeight ? bit : !bit;
                var i = ((y * SerialWidth) + x) * 4;
                strip[i] = strip[i + 1] = strip[i + 2] = white ? (byte)255 : (byte)0;
                strip[i + 3] = 255;
            }
        }

        var left = Inset + BlockWidth + SerialGap;
        var top = height - Inset - BlockHeight;
        graphics.Context.UpdateSubresource(strip, target, 0, SerialWidth * 4, 0, new Box(left, top, 0, left + SerialWidth, top + SerialHeight, 1));
    }

    /// <summary>Reads the scene number next to a bottom-left block that is on screen pixel for pixel, or -1.</summary>
    private static int ReadSerial(ReadOnlySpan<byte> bgra, int width, int height, MarkerHit bottomLeft)
    {
        if (!bottomLeft.Crisp)
        {
            return -1;
        }

        var left = bottomLeft.X + BlockWidth + SerialGap;
        var top = bottomLeft.Y;
        if (left < 0 || top < 0 || left + SerialWidth > width || top + SerialHeight > height)
        {
            return -1;
        }

        var serial = 0;
        for (var column = 0; column < SerialBits; column++)
        {
            var x = left + (column * SerialCellWidth) + (SerialCellWidth / 2);
            var upper = Brightness(bgra, ((top + (SerialCellHeight / 2)) * width) + x);
            var lower = Brightness(bgra, ((top + SerialCellHeight + (SerialCellHeight / 2)) * width) + x);
            if ((upper > 128) == (lower > 128))
            {
                return -1;
            }

            serial = (serial << 1) | (upper > 128 ? 1 : 0);
        }

        return serial;
    }

    private static int Brightness(ReadOnlySpan<byte> bgra, int pixel) =>
        (bgra[pixel * 4] + bgra[(pixel * 4) + 1] + bgra[(pixel * 4) + 2]) / 3;

    /// <summary>The XAML squares in two of the panel's corners are this many effective pixels wide, and the one in its centre this many.</summary>
    public const double CornerSquare = 6;
    public const double HandleSquare = 20;

    /// <summary>Reads the panel out of a BGRA screenshot of the window.</summary>
    /// <param name="scale">Physical pixels per effective pixel inside the panel: its composition scale.</param>
    public static PanelReading Read(CapturedImage image, Func<int, int, SceneView> viewFor, ClipSpec screen, ClipSpec? camera, double scale)
    {
        ReadOnlySpan<byte> bgra = image.Pixels;
        var width = image.Width;
        var height = image.Height;
        var none = new Shown(FrameCode.Unreadable, FrameCode.Unreadable);
        var hits = FindBlocks(bgra, width, height);
        if (hits.Count == 0)
        {
            return new PanelReading([], 0, 0, 0, 0, null, null, 0, 0, none);
        }

        var topLeft = hits.MinBy(h => h.X + h.Y);
        var bottomRight = hits.MaxBy(h => h.X + h.Y);
        var originX = topLeft.X - Inset;
        var originY = topLeft.Y - Inset;
        if (hits.Count != 4)
        {
            return new PanelReading([.. hits], originX, originY, 0, 0, null, null, 0, 0, none);
        }

        var panelWidth = bottomRight.X - topLeft.X + BlockWidth + (2 * Inset);
        var panelHeight = bottomRight.Y - topLeft.Y + BlockHeight + (2 * Inset);

        // The XAML corner squares: looked for close around the swap chain's corners, where there is
        // nothing but the canvas background inside the panel and the window's own background outside.
        var reach = (int)Math.Ceiling(CornerSquare * scale) + 6;
        var topLeftSquare = FindBox(bgra, width, height, originX - reach, originY - reach, 2 * reach, 2 * reach, CornerKey);
        var bottomRightSquare = FindBox(bgra, width, height, originX + panelWidth - reach, originY + panelHeight - reach, 2 * reach, 2 * reach, CornerKey);

        // The XAML element in the centre: every pixel where layout puts it, and next to none just outside.
        var half = HandleSquare * scale / 2;
        var centreX = originX + (panelWidth / 2.0);
        var centreY = originY + (panelHeight / 2.0);
        var inner = Coverage(bgra, width, height, centreX - half + 1, centreY - half + 1, centreX + half - 1, centreY + half - 1, HandleKey, 0, 0, 0, 0);
        var spill = Coverage(bgra, width, height, centreX - half - 4, centreY - half - 4, centreX + half + 4, centreY + half + 4, HandleKey, centreX - half - 2, centreY - half - 2, centreX + half + 2, centreY + half + 2);

        var view = viewFor(panelWidth, panelHeight).Offset(originX, originY);
        var frames = Shown.Read(bgra, width, height, view, screen, camera);
        var serial = ReadSerial(bgra, width, height, hits.MaxBy(h => h.Y - h.X));
        return new PanelReading([.. hits], originX, originY, panelWidth, panelHeight, topLeftSquare, bottomRightSquare, inner, spill, frames, serial);
    }

    /// <summary>
    /// The share of pixels of a key colour in a rectangle, leaving out the pixels inside a second
    /// rectangle (an empty one leaves nothing out).
    /// </summary>
    private static double Coverage(ReadOnlySpan<byte> bgra, int width, int height, double x0, double y0, double x1, double y1, (byte B, byte G, byte R) key, double holeX0, double holeY0, double holeX1, double holeY1)
    {
        var left = Math.Max(0, (int)Math.Ceiling(x0));
        var top = Math.Max(0, (int)Math.Ceiling(y0));
        var right = Math.Min(width, (int)Math.Floor(x1));
        var bottom = Math.Min(height, (int)Math.Floor(y1));
        var total = 0;
        var matching = 0;
        for (var row = top; row < bottom; row++)
        {
            for (var column = left; column < right; column++)
            {
                if (column >= holeX0 && column < holeX1 && row >= holeY0 && row < holeY1)
                {
                    continue;
                }

                total++;
                matching += Matches(bgra, ((row * width) + column) * 4, key) ? 1 : 0;
            }
        }

        return total == 0 ? 0 : (double)matching / total;
    }

    private static List<MarkerHit> FindBlocks(ReadOnlySpan<byte> bgra, int width, int height)
    {
        var hits = new List<MarkerHit>();
        for (var y = 0; y < height; y++)
        {
            var row = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                if (!Matches(bgra, row + (x * 4), MarkerKey) ||
                    (x > 0 && Matches(bgra, row + ((x - 1) * 4), MarkerKey)) ||
                    (y > 0 && Matches(bgra, row - (width * 4) + (x * 4), MarkerKey)))
                {
                    continue;
                }

                var keyWidth = 1;
                while (x + keyWidth < width && Matches(bgra, row + ((x + keyWidth) * 4), MarkerKey))
                {
                    keyWidth++;
                }

                var keyHeight = 1;
                while (y + keyHeight < height && Matches(bgra, ((y + keyHeight) * width * 4) + (x * 4), MarkerKey))
                {
                    keyHeight++;
                }

                // A scaled block is still roughly 1:3; anything else is scene content.
                if (keyWidth < 4 || keyWidth > 40 || keyHeight < 12 || keyHeight > 120 || Math.Abs(((double)keyHeight / keyWidth) - 3.0) > 0.8)
                {
                    continue;
                }

                if (hits.Any(h => Math.Abs(h.X - x) < 24 && Math.Abs(h.Y - y) < 24))
                {
                    continue;
                }

                var scale = keyWidth / (double)KeyWidth;
                var stripeLength = (int)Math.Round(StripeWidth * scale);
                var vertical = Contrast(bgra, width, height, x + keyWidth, y + (keyHeight / 2), 1, 0, stripeLength);
                var horizontal = Contrast(bgra, width, height, x + keyWidth + stripeLength + (stripeLength / 2), y, 0, 1, keyHeight);
                if (vertical < 40 || horizontal < 40)
                {
                    // A bar of the key colour without both stripe fields next to it is not a marker.
                    continue;
                }

                hits.Add(new MarkerHit(x, y, keyWidth, keyHeight, vertical, horizontal));
            }
        }

        return hits;
    }

    /// <summary>The bounding box of the pixels of a key colour inside a rectangle of the screenshot, or null when there are none.</summary>
    private static KeyBox? FindBox(ReadOnlySpan<byte> bgra, int width, int height, int x, int y, int boxWidth, int boxHeight, (byte B, byte G, byte R) key)
    {
        var left = Math.Max(0, x);
        var top = Math.Max(0, y);
        var right = Math.Min(width, x + boxWidth);
        var bottom = Math.Min(height, y + boxHeight);
        var minX = int.MaxValue;
        var minY = int.MaxValue;
        var maxX = -1;
        var maxY = -1;
        var count = 0;
        for (var row = top; row < bottom; row++)
        {
            for (var column = left; column < right; column++)
            {
                if (!Matches(bgra, ((row * width) + column) * 4, key))
                {
                    continue;
                }

                count++;
                minX = Math.Min(minX, column);
                minY = Math.Min(minY, row);
                maxX = Math.Max(maxX, column);
                maxY = Math.Max(maxY, row);
            }
        }

        return count == 0 ? null : new KeyBox(minX, minY, maxX - minX + 1, maxY - minY + 1, count);
    }

    private static bool Matches(ReadOnlySpan<byte> bgra, int index, (byte B, byte G, byte R) key) =>
        Math.Abs(bgra[index] - key.B) <= 24 && Math.Abs(bgra[index + 1] - key.G) <= 24 && Math.Abs(bgra[index + 2] - key.R) <= 24;

    /// <summary>Mean absolute step in brightness between neighbouring pixels along a line.</summary>
    private static double Contrast(ReadOnlySpan<byte> bgra, int width, int height, int x, int y, int stepX, int stepY, int count)
    {
        double sum = 0;
        var pairs = 0;
        var previous = -1.0;
        for (var i = 0; i < count; i++)
        {
            var px = x + (i * stepX);
            var py = y + (i * stepY);
            if (px < 0 || py < 0 || px >= width || py >= height)
            {
                break;
            }

            var index = ((py * width) + px) * 4;
            var value = (bgra[index] + bgra[index + 1] + bgra[index + 2]) / 3.0;
            if (previous >= 0)
            {
                sum += Math.Abs(value - previous);
                pairs++;
            }

            previous = value;
        }

        return pairs == 0 ? 0 : sum / pairs;
    }
}
