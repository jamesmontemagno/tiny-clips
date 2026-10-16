namespace TinyClips.Tools.StudioRenderCheck;

/// <summary>An average colour, 0 to 255 per channel.</summary>
internal readonly record struct Rgb(double R, double G, double B)
{
    public static Rgb FromHex(string hex)
    {
        var value = Convert.ToInt32(hex.TrimStart('#'), 16);
        return new Rgb((value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF);
    }

    public double Luma => (0.2126 * R) + (0.7152 * G) + (0.0722 * B);

    /// <summary>The largest difference in any one channel.</summary>
    public double Distance(Rgb other) => Math.Max(Math.Abs(R - other.R), Math.Max(Math.Abs(G - other.G), Math.Abs(B - other.B)));

    public Rgb Mix(Rgb other, double amount) => new(R + ((other.R - R) * amount), G + ((other.G - G) * amount), B + ((other.B - B) * amount));

    public override string ToString() => $"({R:0},{G:0},{B:0})";
}

/// <summary>Maps clip pixel coordinates to image pixel coordinates: image = origin + clip × scale.</summary>
/// <remarks>A negative <see cref="ScaleX"/> is a mirrored camera.</remarks>
internal readonly record struct ClipMap(double OriginX, double OriginY, double ScaleX, double ScaleY)
{
    public static readonly ClipMap Identity = new(0, 0, 1, 1);

    public (double X, double Y) Apply(double clipX, double clipY) => (OriginX + (clipX * ScaleX), OriginY + (clipY * ScaleY));
}

/// <summary>
/// The picture every frame of a test clip carries: a flat background with a darker left third and
/// a red block top-right (so a flipped or mirrored picture is obvious), four flat colour patches,
/// and the frame number as a strip of 12 black/white cells with their complements underneath.
/// </summary>
/// <remarks>
/// A strip read counts only when every column has one bright and one dark cell, so a covered,
/// blended, upside-down or stale strip never reads as a plausible wrong number. The camera's strip
/// and patches sit in the middle so they survive the square crop, the circle and the mirror.
/// All coordinates are even, so 4:2:0 chroma is exact inside the patches.
/// </remarks>
internal sealed class ClipSpec
{
    public const int CodeBits = 12;

    public static readonly Rgb[] PatchColors = [new(210, 40, 40), new(40, 200, 60), new(40, 60, 210), new(190, 190, 190)];
    public static readonly Rgb MarkerColor = new(220, 30, 40);

    private ClipSpec(string name, int width, int height, bool camera)
    {
        Name = name;
        Width = width;
        Height = height;
        IsCamera = camera;
        if (camera)
        {
            var scale = height / 720.0;
            CodeCell = Even(40 * scale);
            CodeX = Even((width - (CodeBits * CodeCell)) / 2.0);
            CodeY = Even((height / 2.0) - CodeCell);
            PatchSize = Even(48 * scale);
            PatchPitch = Even(64 * scale);
            PatchX = Even((width - ((3 * PatchPitch) + PatchSize)) / 2.0);
            PatchY = Even(430 * scale);
            Background = new Rgb(30, 90, 50);
            LeftBackground = new Rgb(16, 54, 28);
        }
        else
        {
            var scale = height / 1080.0;
            CodeCell = Even(48 * scale);
            CodeX = Even(160 * scale);
            CodeY = Even(120 * scale);
            PatchSize = Even(96 * scale);
            PatchPitch = Even(128 * scale);
            PatchX = Even(160 * scale);
            PatchY = Even(420 * scale);
            Background = new Rgb(30, 50, 100);
            LeftBackground = new Rgb(20, 34, 88);
        }

        MarkerWidth = Even(width * 0.06);
        MarkerHeight = Even(height * 0.16);
        MarkerX = Even(width - (width * 0.02) - MarkerWidth);
        MarkerY = Even(height * 0.04);
        LeftWidth = Even(width / 3.0);
    }

    public string Name { get; }

    public int Width { get; }

    public int Height { get; }

    public bool IsCamera { get; }

    public int CodeX { get; }

    public int CodeY { get; }

    public int CodeCell { get; }

    public int PatchX { get; }

    public int PatchY { get; }

    public int PatchSize { get; }

    public int PatchPitch { get; }

    public int MarkerX { get; }

    public int MarkerY { get; }

    public int MarkerWidth { get; }

    public int MarkerHeight { get; }

    public int LeftWidth { get; }

    public Rgb Background { get; }

    public Rgb LeftBackground { get; }

    public static ClipSpec Screen(int width, int height) => new("screen", width, height, camera: false);

    public static ClipSpec Camera(int width, int height) => new("camera", width, height, camera: true);

    /// <summary>
    /// A screen that also carries a field of fine stripes, one white column in three. Drawn at
    /// less than half its size, the field is flat grey when the picture was filtered before it
    /// was shrunk, and rippled when it was sampled linearly.
    /// </summary>
    public static ClipSpec StripedScreen(int width, int height) =>
        new("screen", width, height, camera: false) { Stripes = (Even(width * 0.5), Even(height * 0.3), 3 * (int)(width * 0.14 / 3), Even(height * 0.32)) };

    /// <summary>Where the stripe field is, when the picture has one.</summary>
    public (int X, int Y, int Width, int Height)? Stripes { get; private init; }

    /// <summary>The centre of colour patch <paramref name="index"/> in clip pixels.</summary>
    public (double X, double Y) PatchCenter(int index) => (PatchX + (index * PatchPitch) + (PatchSize / 2.0), PatchY + (PatchSize / 2.0));

    /// <summary>A point of plain background well away from everything drawn on it.</summary>
    public (double X, double Y) BackgroundPoint => (Width * 0.7, Height * 0.75);

    /// <summary>
    /// The colour the picture has at a point in clip pixels, or null when the point is within
    /// <paramref name="margin"/> pixels of a place where two colours meet, or off the picture.
    /// </summary>
    public Rgb? FlatColorAt(double x, double y, int frameNumber, double margin)
    {
        if (x < margin || y < margin || x > Width - margin || y > Height - margin)
        {
            return null;
        }

        bool Inside(double left, double top, double width, double height) =>
            x >= left + margin && x <= left + width - margin && y >= top + margin && y <= top + height - margin;
        bool Touches(double left, double top, double width, double height) =>
            x > left - margin && x < left + width + margin && y > top - margin && y < top + height + margin;

        if (Stripes is { } stripes && Touches(stripes.X, stripes.Y, stripes.Width, stripes.Height))
        {
            return null;
        }

        // In reverse painting order: what was drawn last is what shows.
        if (Touches(CodeX, CodeY, CodeBits * CodeCell, 2 * CodeCell))
        {
            for (var column = 0; column < CodeBits; column++)
            {
                var bit = ((frameNumber >> (CodeBits - 1 - column)) & 1) != 0;
                if (Inside(CodeX + (column * CodeCell), CodeY, CodeCell, CodeCell))
                {
                    return bit ? new Rgb(255, 255, 255) : new Rgb(0, 0, 0);
                }

                if (Inside(CodeX + (column * CodeCell), CodeY + CodeCell, CodeCell, CodeCell))
                {
                    return bit ? new Rgb(0, 0, 0) : new Rgb(255, 255, 255);
                }
            }

            return null;
        }

        for (var index = 0; index < PatchColors.Length; index++)
        {
            if (Inside(PatchX + (index * PatchPitch), PatchY, PatchSize, PatchSize))
            {
                return PatchColors[index];
            }

            if (Touches(PatchX + (index * PatchPitch), PatchY, PatchSize, PatchSize))
            {
                return null;
            }
        }

        if (Inside(MarkerX, MarkerY, MarkerWidth, MarkerHeight))
        {
            return MarkerColor;
        }

        if (Touches(MarkerX, MarkerY, MarkerWidth, MarkerHeight) || Math.Abs(x - LeftWidth) < margin)
        {
            return null;
        }

        return x < LeftWidth ? LeftBackground : Background;
    }

    /// <summary>The whole frame as tightly packed top-down BGRA.</summary>
    public byte[] DrawBgra(int frameNumber)
    {
        var pixels = new byte[Width * Height * 4];
        Fill(pixels, 0, 0, Width, Height, Background);
        Fill(pixels, 0, 0, LeftWidth, Height, LeftBackground);
        if (Stripes is { } stripes)
        {
            Fill(pixels, stripes.X, stripes.Y, stripes.Width, stripes.Height, new Rgb(0, 0, 0));
            for (var x = stripes.X; x < stripes.X + stripes.Width; x += 3)
            {
                Fill(pixels, x, stripes.Y, 1, stripes.Height, new Rgb(255, 255, 255));
            }
        }

        Fill(pixels, MarkerX, MarkerY, MarkerWidth, MarkerHeight, MarkerColor);
        for (var index = 0; index < PatchColors.Length; index++)
        {
            Fill(pixels, PatchX + (index * PatchPitch), PatchY, PatchSize, PatchSize, PatchColors[index]);
        }

        for (var column = 0; column < CodeBits; column++)
        {
            var bit = ((frameNumber >> (CodeBits - 1 - column)) & 1) != 0;
            var x = CodeX + (column * CodeCell);
            Fill(pixels, x, CodeY, CodeCell, CodeCell, bit ? new Rgb(255, 255, 255) : new Rgb(0, 0, 0));
            Fill(pixels, x, CodeY + CodeCell, CodeCell, CodeCell, bit ? new Rgb(0, 0, 0) : new Rgb(255, 255, 255));
        }

        return pixels;
    }

    /// <summary>
    /// The frame as limited-range NV12 (luma plane, then interleaved chroma at half size), ready
    /// for <see cref="StampNv12"/> to write each frame's strip into.
    /// </summary>
    /// <param name="bt601">Convert with the BT.601 matrix instead of BT.709.</param>
    public byte[] DrawNv12Background(bool bt601 = false)
    {
        var bgra = DrawBgra(0);
        var nv12 = new byte[Width * Height * 3 / 2];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var i = ((y * Width) + x) * 4;
                nv12[(y * Width) + x] = (byte)Math.Round(ColorMath.ToYuv(bgra[i + 2], bgra[i + 1], bgra[i], bt601).Y);
            }
        }

        var chroma = Width * Height;
        for (var y = 0; y < Height; y += 2)
        {
            for (var x = 0; x < Width; x += 2)
            {
                double r = 0;
                double g = 0;
                double b = 0;
                for (var dy = 0; dy < 2; dy++)
                {
                    for (var dx = 0; dx < 2; dx++)
                    {
                        var i = (((y + dy) * Width) + x + dx) * 4;
                        b += bgra[i];
                        g += bgra[i + 1];
                        r += bgra[i + 2];
                    }
                }

                var (_, cb, cr) = ColorMath.ToYuv(r / 4, g / 4, b / 4, bt601);
                var o = chroma + ((y / 2) * Width) + x;
                nv12[o] = (byte)Math.Round(cb);
                nv12[o + 1] = (byte)Math.Round(cr);
            }
        }

        // Black and white have no chroma, so the strip only ever changes luma.
        for (var y = CodeY / 2; y < (CodeY + (2 * CodeCell)) / 2; y++)
        {
            for (var x = CodeX; x < CodeX + (CodeBits * CodeCell); x++)
            {
                nv12[chroma + (y * Width) + x] = 128;
            }
        }

        return nv12;
    }

    /// <summary>Writes the strip for <paramref name="frameNumber"/> into the luma plane of an NV12 frame.</summary>
    public void StampNv12(Span<byte> nv12, int frameNumber)
    {
        for (var column = 0; column < CodeBits; column++)
        {
            var bit = ((frameNumber >> (CodeBits - 1 - column)) & 1) != 0;
            var x = CodeX + (column * CodeCell);
            for (var row = 0; row < CodeCell; row++)
            {
                nv12.Slice(((CodeY + row) * Width) + x, CodeCell).Fill(bit ? (byte)235 : (byte)16);
                nv12.Slice(((CodeY + CodeCell + row) * Width) + x, CodeCell).Fill(bit ? (byte)16 : (byte)235);
            }
        }
    }

    /// <summary>Writes the strip for <paramref name="frameNumber"/> into a tightly packed top-down BGRA frame, leaving alpha alone.</summary>
    public void StampBgra(Span<byte> bgra, int frameNumber)
    {
        for (var column = 0; column < CodeBits; column++)
        {
            var bit = ((frameNumber >> (CodeBits - 1 - column)) & 1) != 0;
            var x = CodeX + (column * CodeCell);
            for (var row = 0; row < 2 * CodeCell; row++)
            {
                var value = (row < CodeCell) == bit ? (byte)255 : (byte)0;
                var cells = bgra.Slice((((CodeY + row) * Width) + x) * 4, CodeCell * 4);
                for (var i = 0; i < cells.Length; i += 4)
                {
                    cells[i] = value;
                    cells[i + 1] = value;
                    cells[i + 2] = value;
                }
            }
        }
    }

    private void Fill(byte[] bgra, int x, int y, int width, int height, Rgb color)
    {
        for (var row = Math.Max(0, y); row < Math.Min(Height, y + height); row++)
        {
            for (var column = Math.Max(0, x); column < Math.Min(Width, x + width); column++)
            {
                var i = ((row * Width) + column) * 4;
                bgra[i] = (byte)color.B;
                bgra[i + 1] = (byte)color.G;
                bgra[i + 2] = (byte)color.R;
                bgra[i + 3] = 255;
            }
        }
    }

    private static int Even(double value) => Math.Max(2, 2 * (int)Math.Round(value / 2, MidpointRounding.AwayFromZero));
}

/// <summary>BT.709 limited-range conversion, the tool's own, so colour checks do not lean on Media Foundation's.</summary>
internal static class ColorMath
{
    /// <summary>Limited-range Y, Cb, Cr of an 8-bit RGB colour, with the BT.709 or the BT.601 matrix.</summary>
    public static (double Y, double Cb, double Cr) ToYuv(double r, double g, double b, bool bt601)
    {
        var (kr, kb) = bt601 ? (0.299, 0.114) : (0.2126, 0.0722);
        var y = (kr * r) + ((1 - kr - kb) * g) + (kb * b);
        return (16 + (219 * y / 255), 128 + (224 * ((b - y) / (2 * (1 - kb))) / 255), 128 + (224 * ((r - y) / (2 * (1 - kr))) / 255));
    }

    public static Rgb FromYuv709(double y, double cb, double cr)
    {
        var luma = (y - 16) * 255 / 219;
        var pb = (cb - 128) * 255 / 224;
        var pr = (cr - 128) * 255 / 224;
        var r = luma + (1.5748 * pr);
        var b = luma + (1.8556 * pb);
        var g = (luma - (0.2126 * r) - (0.0722 * b)) / 0.7152;
        return new Rgb(r, g, b);
    }

    /// <summary>The same YUV read as BT.601, to tell which matrix an encoder really used.</summary>
    public static Rgb FromYuv601(double y, double cb, double cr)
    {
        var luma = (y - 16) * 255 / 219;
        var pb = (cb - 128) * 255 / 224;
        var pr = (cr - 128) * 255 / 224;
        var r = luma + (1.402 * pr);
        var b = luma + (1.772 * pb);
        var g = (luma - (0.299 * r) - (0.114 * b)) / 0.587;
        return new Rgb(r, g, b);
    }
}

/// <summary>A picture to read from: BGRA pixels, or the planes of a decoded video frame.</summary>
internal sealed class Picture
{
    private readonly byte[] _data;
    private readonly byte[]? _chroma;
    private readonly bool _bgra;
    private readonly bool _bt601;

    private Picture(int width, int height, byte[] data, byte[]? chroma, bool bgra, bool bt601)
    {
        Width = width;
        Height = height;
        _data = data;
        _chroma = chroma;
        _bgra = bgra;
        _bt601 = bt601;
    }

    public int Width { get; }

    public int Height { get; }

    public bool HasColor => _bgra || _chroma is not null;

    public static Picture FromBgra(byte[] pixels, int width, int height) => new(width, height, pixels, null, bgra: true, bt601: false);

    /// <param name="luma">Width × height bytes.</param>
    /// <param name="chroma">Interleaved Cb, Cr at half size (width × height / 2 bytes), or null for luma only.</param>
    /// <param name="bt601">Which matrix turns the planes into colours: BT.601, or else BT.709.</param>
    public static Picture FromNv12(byte[] luma, byte[]? chroma, int width, int height, bool bt601) => new(width, height, luma, chroma, bgra: false, bt601);

    /// <summary>The same planes read with the other matrix, to show which one a file was really made with.</summary>
    public Picture WithOtherMatrix() => new(Width, Height, _data, _chroma, _bgra, !_bt601);

    /// <summary>The pixels of a BGRA picture.</summary>
    public byte[] Bgra => _bgra ? _data : throw new InvalidOperationException("Not a BGRA picture.");

    /// <summary>
    /// The largest difference in any colour channel between two BGRA pictures of one size, over
    /// the pixels from (left, top) up to but not including (right, bottom), and where it is.
    /// </summary>
    public static (int Difference, int X, int Y) MaxDifference(Picture a, Picture b, int left = 0, int top = 0, int right = int.MaxValue, int bottom = int.MaxValue)
    {
        if (a.Width != b.Width || a.Height != b.Height)
        {
            throw new ArgumentException("The pictures differ in size.");
        }

        var first = a.Bgra;
        var second = b.Bgra;
        var worst = (Difference: 0, X: -1, Y: -1);
        for (var y = Math.Max(0, top); y < Math.Min(a.Height, bottom); y++)
        {
            for (var x = Math.Max(0, left); x < Math.Min(a.Width, right); x++)
            {
                var i = ((y * a.Width) + x) * 4;
                var difference = Math.Max(Math.Abs(first[i] - second[i]), Math.Max(Math.Abs(first[i + 1] - second[i + 1]), Math.Abs(first[i + 2] - second[i + 2])));
                if (difference > worst.Difference)
                {
                    worst = (difference, x, y);
                }
            }
        }

        return worst;
    }

    private Rgb FromYuv(double y, double cb, double cr) => _bt601 ? ColorMath.FromYuv601(y, cb, cr) : ColorMath.FromYuv709(y, cb, cr);

    /// <summary>Luma, 0 to 255, of one pixel. A decoded frame's 16–235 is stretched to that.</summary>
    public double Luma(int x, int y)
    {
        if (_bgra)
        {
            var i = ((y * Width) + x) * 4;
            return (0.2126 * _data[i + 2]) + (0.7152 * _data[i + 1]) + (0.0722 * _data[i]);
        }

        return Math.Clamp((_data[(y * Width) + x] - 16) * 255.0 / 219, 0, 255);
    }

    public Rgb Pixel(int x, int y)
    {
        x = Math.Clamp(x, 0, Width - 1);
        y = Math.Clamp(y, 0, Height - 1);
        if (_bgra)
        {
            var i = ((y * Width) + x) * 4;
            return new Rgb(_data[i + 2], _data[i + 1], _data[i]);
        }

        if (_chroma is null)
        {
            var gray = Luma(x, y);
            return new Rgb(gray, gray, gray);
        }

        var c = ((y / 2) * Width) + (x & ~1);
        return FromYuv(_data[(y * Width) + x], _chroma[c], _chroma[c + 1]);
    }

    /// <summary>Writes the picture as a PNG, for looking at a frame a check did not like.</summary>
    public unsafe void SavePng(string path)
    {
        var bgra = new byte[Width * Height * 4];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var pixel = Pixel(x, y);
                var i = ((y * Width) + x) * 4;
                bgra[i] = (byte)Math.Clamp(Math.Round(pixel.B), 0, 255);
                bgra[i + 1] = (byte)Math.Clamp(Math.Round(pixel.G), 0, 255);
                bgra[i + 2] = (byte)Math.Clamp(Math.Round(pixel.R), 0, 255);
                bgra[i + 3] = 255;
            }
        }

        fixed (byte* pixels = bgra)
        {
            using var bitmap = new System.Drawing.Bitmap(Width, Height, Width * 4, System.Drawing.Imaging.PixelFormat.Format32bppRgb, (nint)pixels);
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
    }

    /// <summary>The average colour of the square of pixels within <paramref name="radius"/> of a point.</summary>
    public Rgb Average(double x, double y, int radius)
    {
        var cx = (int)Math.Floor(x);
        var cy = (int)Math.Floor(y);
        double r = 0;
        double g = 0;
        double b = 0;
        var count = 0;
        if (!_bgra && _chroma is not null)
        {
            // Average the planes, then convert once: that is exact for a flat patch.
            double luma = 0;
            double cb = 0;
            double cr = 0;
            for (var py = cy - radius; py <= cy + radius; py++)
            {
                for (var px = cx - radius; px <= cx + radius; px++)
                {
                    var qx = Math.Clamp(px, 0, Width - 1);
                    var qy = Math.Clamp(py, 0, Height - 1);
                    var c = ((qy / 2) * Width) + (qx & ~1);
                    luma += _data[(qy * Width) + qx];
                    cb += _chroma[c];
                    cr += _chroma[c + 1];
                    count++;
                }
            }

            return FromYuv(luma / count, cb / count, cr / count);
        }

        for (var py = cy - radius; py <= cy + radius; py++)
        {
            for (var px = cx - radius; px <= cx + radius; px++)
            {
                var p = Pixel(px, py);
                r += p.R;
                g += p.G;
                b += p.B;
                count++;
            }
        }

        return new Rgb(r / count, g / count, b / count);
    }
}

/// <summary>Reads the frame-number strip and the colour patches of a <see cref="ClipSpec"/> back out of a picture.</summary>
internal static class FrameCode
{
    /// <summary>Returned when the strip is missing, covered, upside down, or a blend of two frames.</summary>
    public const int Unreadable = -1;

    public static int Decode(Picture picture, ClipSpec clip, ClipMap map)
    {
        var cellPixels = Math.Min(Math.Abs(map.ScaleX), Math.Abs(map.ScaleY)) * clip.CodeCell;
        var radius = Math.Clamp((int)(cellPixels / 6), 0, 6);
        var value = 0;
        for (var column = 0; column < ClipSpec.CodeBits; column++)
        {
            var centerX = clip.CodeX + ((column + 0.5) * clip.CodeCell);
            var top = Sample(picture, map.Apply(centerX, clip.CodeY + (0.5 * clip.CodeCell)), radius);
            var bottom = Sample(picture, map.Apply(centerX, clip.CodeY + (1.5 * clip.CodeCell)), radius);
            // A sample that falls off the picture is NaN, and no comparison with NaN is true.
            if (!(Math.Abs(top - bottom) >= 96))
            {
                return Unreadable;
            }

            if (top > bottom)
            {
                value |= 1 << (ClipSpec.CodeBits - 1 - column);
            }
        }

        return value;
    }

    /// <summary>The luma read above and below in each column, for explaining a strip that would not read.</summary>
    public static string Describe(Picture picture, ClipSpec clip, ClipMap map)
    {
        var cellPixels = Math.Min(Math.Abs(map.ScaleX), Math.Abs(map.ScaleY)) * clip.CodeCell;
        var radius = Math.Clamp((int)(cellPixels / 6), 0, 6);
        var columns = new List<string>();
        for (var column = 0; column < ClipSpec.CodeBits; column++)
        {
            var centerX = clip.CodeX + ((column + 0.5) * clip.CodeCell);
            var top = Sample(picture, map.Apply(centerX, clip.CodeY + (0.5 * clip.CodeCell)), radius);
            var bottom = Sample(picture, map.Apply(centerX, clip.CodeY + (1.5 * clip.CodeCell)), radius);
            columns.Add($"{top:0}/{bottom:0}");
        }

        return $"cells of {cellPixels:0.0} px, top/bottom luma per column: {string.Join(" ", columns)}";
    }

    /// <summary>The average colour of each patch, sampled around its centre.</summary>
    public static Rgb[] ReadPatches(Picture picture, ClipSpec clip, ClipMap map)
    {
        var patchPixels = Math.Min(Math.Abs(map.ScaleX), Math.Abs(map.ScaleY)) * clip.PatchSize;
        var radius = Math.Clamp((int)(patchPixels / 5), 0, 8);
        var result = new Rgb[ClipSpec.PatchColors.Length];
        for (var index = 0; index < result.Length; index++)
        {
            var (x, y) = clip.PatchCenter(index);
            var (px, py) = map.Apply(x, y);
            result[index] = picture.Average(px, py, radius);
        }

        return result;
    }

    /// <summary>
    /// How far, in picture pixels, the grey patch's top, bottom and near-side edges are from
    /// where <paramref name="map"/> puts them. Each edge is found to a fraction of a pixel from
    /// the luma across it, so a picture that is shifted, stretched or letterboxed by a pixel or
    /// two shows up. Null when the patch is drawn too small to measure; NaN when no edge is found.
    /// </summary>
    public static double? PatchEdgeError(Picture picture, ClipSpec clip, ClipMap map)
    {
        var clipLeft = clip.PatchX + (3 * clip.PatchPitch);
        var (x0, y0) = map.Apply(clipLeft, clip.PatchY);
        var (x1, y1) = map.Apply(clipLeft + clip.PatchSize, clip.PatchY + clip.PatchSize);
        var gap = (clip.PatchPitch - clip.PatchSize) * Math.Abs(map.ScaleX);
        var half = (int)Math.Min(6, Math.Min(Math.Min(Math.Abs(x1 - x0), Math.Abs(y1 - y0)) / 4, gap / 2));
        if (half < 2)
        {
            return null;
        }

        var centerX = (x0 + x1) / 2;
        var centerY = (y0 + y1) / 2;
        var inside = Sample(picture, (centerX, centerY), 1);
        var top = Math.Min(y0, y1);
        var bottom = Math.Max(y0, y1);
        var worst = Math.Abs(EdgeAlong(picture, centerX, top, vertical: true, rising: true, half, inside) - top);
        worst = Math.Max(worst, Math.Abs(EdgeAlong(picture, centerX, bottom, vertical: true, rising: false, half, inside) - bottom));

        // The side that faces the next patch across a gap of plain background.
        var side = Math.Abs(EdgeAlong(picture, x0, centerY, vertical: false, rising: map.ScaleX > 0, half, inside) - x0);
        return double.IsNaN(worst) || double.IsNaN(side) ? double.NaN : Math.Max(worst, side);
    }

    /// <summary>
    /// The position of an edge between a patch and its surround, from 2 × half pixels across
    /// it: each pixel counts for as much of itself as lies outside the patch.
    /// </summary>
    private static double EdgeAlong(Picture picture, double x, double y, bool vertical, bool rising, int half, double inside)
    {
        double At(int index) => vertical ? Sample(picture, (x, index), 1, alongX: true) : Sample(picture, (index, y), 1, alongX: false);
        var start = (int)Math.Floor(vertical ? y : x) - half;
        var outside = At(rising ? start - 1 : start + (2 * half));
        if (!(Math.Abs(inside - outside) >= 40))
        {
            return double.NaN;
        }

        double sum = 0;
        for (var index = start; index < start + (2 * half); index++)
        {
            var amount = Math.Clamp((At(index) - outside) / (inside - outside), 0, 1);
            sum += rising ? 1 - amount : amount;
        }

        return start + sum;
    }

    /// <summary>The luma of three pixels in a line through a point: along x, or along y.</summary>
    private static double Sample(Picture picture, (double X, double Y) point, int radius, bool alongX)
    {
        var cx = (int)Math.Floor(point.X);
        var cy = (int)Math.Floor(point.Y);
        double sum = 0;
        for (var offset = -radius; offset <= radius; offset++)
        {
            var px = alongX ? cx + offset : cx;
            var py = alongX ? cy : cy + offset;
            if (px < 0 || py < 0 || px >= picture.Width || py >= picture.Height)
            {
                return double.NaN;
            }

            sum += picture.Luma(px, py);
        }

        return sum / ((2 * radius) + 1);
    }

    private static double Sample(Picture picture, (double X, double Y) point, int radius)
    {
        var cx = (int)Math.Floor(point.X);
        var cy = (int)Math.Floor(point.Y);
        if (cx - radius < 0 || cy - radius < 0 || cx + radius >= picture.Width || cy + radius >= picture.Height)
        {
            return double.NaN;
        }

        double sum = 0;
        var count = 0;
        for (var y = cy - radius; y <= cy + radius; y++)
        {
            for (var x = cx - radius; x <= cx + radius; x++)
            {
                sum += picture.Luma(x, y);
                count++;
            }
        }

        return sum / count;
    }
}
