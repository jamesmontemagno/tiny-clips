using StudioEngineSpike.Engine;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace StudioEngineSpike.Present;

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

/// <summary>What a screenshot says about one panel.</summary>
/// <param name="Width">Swap chain width implied by the distance between the corner markers (0 when not all four were found).</param>
/// <param name="XamlDx">Offset from the bottom-right marker to the XAML corner square; with <see cref="XamlFound"/> it tells whether the swap chain ends where XAML layout says the panel ends.</param>
internal sealed record PanelReading(
    string Name,
    MarkerHit[] Markers,
    int OriginX,
    int OriginY,
    int Width,
    int Height,
    bool XamlFound,
    int XamlDx,
    int XamlDy,
    int XamlSize,
    int ScreenFrame,
    int CameraFrame)
{
    public bool AllFound => Markers.Length == 4;

    public bool AllCrisp => AllFound && Markers.All(m => m.Crisp);

    /// <summary>True when the swap chain's bottom-right corner sits exactly on the panel's bottom-right corner.</summary>
    public bool InSync(int tolerance = 1) =>
        AllCrisp && XamlFound &&
        Math.Abs(XamlDx - (PanelMarkers.Inset + PanelMarkers.BlockWidth - XamlSize)) <= tolerance &&
        Math.Abs(XamlDy - (PanelMarkers.Inset + PanelMarkers.BlockHeight - XamlSize)) <= tolerance;

    public string Describe()
    {
        if (Markers.Length == 0)
        {
            return $"{Name}: no markers found";
        }

        var crisp = Markers.Count(m => m.Crisp);
        var worst = Markers.OrderBy(m => Math.Min(m.VerticalContrast, m.HorizontalContrast)).First();
        var text = $"{Name}: markers {Markers.Length}/4, pixel-exact {crisp}/4 (worst: bar {worst.KeyWidth}x{worst.KeyHeight}, stripe contrast {worst.VerticalContrast.F("0")}/{worst.HorizontalContrast.F("0")})";
        if (AllFound)
        {
            text += $", swap chain on screen {Width}x{Height} px";
        }

        text += XamlFound
            ? $", XAML corner square {XamlSize} px at +{XamlDx},+{XamlDy} from the bottom-right marker (in step: +{PanelMarkers.Inset + PanelMarkers.BlockWidth - XamlSize},+{PanelMarkers.Inset + PanelMarkers.BlockHeight - XamlSize})"
            : ", XAML corner square not found";
        text += $", shows screen frame {FrameText(ScreenFrame)} / camera frame {FrameText(CameraFrame)}";
        return text;
    }

    private static string FrameText(int frame) => frame == FrameCode.Unreadable ? "unreadable" : frame.ToString();
}

/// <summary>
/// A small test pattern written into each corner of what a presenter draws, and the code that
/// finds it again in a screenshot. Each block is a bar of a key colour (rose for the Win2D
/// path, azure for the DXGI path), then 1 px vertical stripes, then 1 px horizontal stripes.
/// If the swap chain reaches the screen pixel for pixel the bar is exactly 8x24 and neighbouring
/// stripe pixels differ by 255; any scaling by XAML or DWM blurs the stripes and shows up as a
/// lower contrast.
/// </summary>
internal static class PanelMarkers
{
    public const int Inset = 16;
    public const int KeyWidth = 8;
    public const int StripeWidth = 16;
    public const int BlockWidth = KeyWidth + StripeWidth + StripeWidth;
    public const int BlockHeight = 24;

    // Colours the test clips do not contain (their bars are the saturated primaries and secondaries).
    public static readonly (byte B, byte G, byte R) Win2DKey = (128, 0, 255);
    public static readonly (byte B, byte G, byte R) NativeKey = (255, 128, 0);
    public static readonly (byte B, byte G, byte R) XamlKey = (0, 128, 255);

    public static byte[] BuildBlock((byte B, byte G, byte R) key)
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
                    (b, g, r) = key;
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

    /// <summary>Writes the block into all four corners of <paramref name="target"/>. The caller holds the device gate.</summary>
    public static void Draw(GraphicsDevice graphics, ID3D11Texture2D target, int width, int height, byte[] block)
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

    /// <summary>Reads one panel out of a BGRA screenshot.</summary>
    public static PanelReading Read(string name, ReadOnlySpan<byte> bgra, int width, int height, (byte B, byte G, byte R) key, SceneSettings settings, bool cameraVisible)
    {
        var hits = FindBlocks(bgra, width, height, key);
        if (hits.Count == 0)
        {
            return new PanelReading(name, [], 0, 0, 0, 0, false, 0, 0, 0, FrameCode.Unreadable, FrameCode.Unreadable);
        }

        var topLeft = hits.MinBy(h => h.X + h.Y);
        var bottomRight = hits.MaxBy(h => h.X + h.Y);
        var originX = topLeft.X - Inset;
        var originY = topLeft.Y - Inset;
        var panelWidth = 0;
        var panelHeight = 0;
        if (hits.Count == 4)
        {
            panelWidth = bottomRight.X - topLeft.X + BlockWidth + (2 * Inset);
            panelHeight = bottomRight.Y - topLeft.Y + BlockHeight + (2 * Inset);
        }

        // The XAML square nearest to the bottom-right marker.
        var squares = FindSquares(bgra, width, height, XamlKey);
        var xamlFound = false;
        var dx = 0;
        var dy = 0;
        var size = 0;
        var best = double.MaxValue;
        foreach (var square in squares)
        {
            var distance = Math.Abs(square.X - (bottomRight.X + BlockWidth)) + Math.Abs(square.Y - (bottomRight.Y + BlockHeight));
            if (distance < best && distance < 400)
            {
                best = distance;
                xamlFound = true;
                dx = square.X - bottomRight.X;
                dy = square.Y - bottomRight.Y;
                size = square.Size;
            }
        }

        var screenFrame = FrameCode.Unreadable;
        var cameraFrame = FrameCode.Unreadable;
        if (panelWidth > 0 && panelHeight > 0)
        {
            var layout = SceneLayout.Resolve(settings, panelWidth, panelHeight, TestMedia.Screen.Width, TestMedia.Screen.Height, TestMedia.Camera.Width, TestMedia.Camera.Height);
            screenFrame = FrameCode.Decode(bgra, width, height, 4, TestMedia.Screen, layout.ScreenMap(TestMedia.Screen).Offset(originX, originY));
            if (cameraVisible)
            {
                cameraFrame = FrameCode.Decode(bgra, width, height, 4, TestMedia.Camera, layout.CameraMap(TestMedia.Camera).Offset(originX, originY));
            }
        }

        return new PanelReading(name, hits.ToArray(), originX, originY, panelWidth, panelHeight, xamlFound, dx, dy, size, screenFrame, cameraFrame);
    }

    private static List<MarkerHit> FindBlocks(ReadOnlySpan<byte> bgra, int width, int height, (byte B, byte G, byte R) key)
    {
        var hits = new List<MarkerHit>();
        for (var y = 0; y < height; y++)
        {
            var row = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                if (!Matches(bgra, row + (x * 4), key) ||
                    (x > 0 && Matches(bgra, row + ((x - 1) * 4), key)) ||
                    (y > 0 && Matches(bgra, row - (width * 4) + (x * 4), key)))
                {
                    continue;
                }

                var keyWidth = 1;
                while (x + keyWidth < width && Matches(bgra, row + ((x + keyWidth) * 4), key))
                {
                    keyWidth++;
                }

                var keyHeight = 1;
                while (y + keyHeight < height && Matches(bgra, ((y + keyHeight) * width * 4) + (x * 4), key))
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

    private static List<(int X, int Y, int Size)> FindSquares(ReadOnlySpan<byte> bgra, int width, int height, (byte B, byte G, byte R) key)
    {
        var squares = new List<(int X, int Y, int Size)>();
        for (var y = 0; y < height; y++)
        {
            var row = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                if (!Matches(bgra, row + (x * 4), key) ||
                    (x > 0 && Matches(bgra, row + ((x - 1) * 4), key)) ||
                    (y > 0 && Matches(bgra, row - (width * 4) + (x * 4), key)))
                {
                    continue;
                }

                var w = 1;
                while (x + w < width && Matches(bgra, row + ((x + w) * 4), key))
                {
                    w++;
                }

                var h = 1;
                while (y + h < height && Matches(bgra, ((y + h) * width * 4) + (x * 4), key))
                {
                    h++;
                }

                if (w >= 4 && w <= 40 && Math.Abs(w - h) <= 2)
                {
                    squares.Add((x, y, w));
                }
            }
        }

        return squares;
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
