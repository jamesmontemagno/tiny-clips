using System.Buffers;
using System.Runtime.InteropServices;
using TinyClips.Core.Studio.Rendering;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace TinyClips.Core.Studio.Preview;

/// <summary>
/// Looks at what a player's copy left in a texture. The engine knows which frame a texture holds
/// only from the position the player reports with it, and a player that has just been moved to
/// another graphics adapter reports positions for pictures it has not got: see
/// <see cref="StudioPreviewProof"/>. Every member waits for the GPU and needs the device's lock.
/// </summary>
internal static class StudioPreviewPixels
{
    /// <summary>What <see cref="Fingerprint"/> returns for a texture with nothing in it.</summary>
    public const ulong Nothing = 0;

    // Where a texture is sampled, as fractions of its width and height: the middle and a point
    // inside each corner.
    private static readonly (double X, double Y)[] SamplePoints = [(0.5, 0.5), (0.1, 0.1), (0.9, 0.1), (0.1, 0.9), (0.9, 0.9)];

    /// <summary>
    /// Whether a copy left nothing in the texture: every sampled pixel is transparent. A frame of
    /// video is opaque everywhere, however dark it is.
    /// </summary>
    public static unsafe bool IsEmpty(StudioGraphicsDevice graphics, ID3D11Texture2D texture)
    {
        ArgumentNullException.ThrowIfNull(graphics);
        ArgumentNullException.ThrowIfNull(texture);
        var description = texture.Description;
        if (description.Format is not (Format.B8G8R8A8_UNorm or Format.B8G8R8A8_UNorm_SRgb or Format.R8G8B8A8_UNorm))
        {
            // No alpha to go by.
            return false;
        }

        using var staging = graphics.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)SamplePoints.Length,
            Height = 1,
            MipLevels = 1,
            ArraySize = 1,
            Format = description.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        });
        for (var index = 0; index < SamplePoints.Length; index++)
        {
            var x = (int)Math.Clamp(SamplePoints[index].X * description.Width, 0, description.Width - 1);
            var y = (int)Math.Clamp(SamplePoints[index].Y * description.Height, 0, description.Height - 1);
            graphics.Context.CopySubresourceRegion(staging, 0, (uint)index, 0, 0, texture, 0, new Box(x, y, 0, x + 1, y + 1, 1));
        }

        var mapped = graphics.Context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            return IsEmpty(new ReadOnlySpan<byte>((void*)mapped.DataPointer, SamplePoints.Length * 4));
        }
        finally
        {
            graphics.Context.Unmap(staging, 0);
        }
    }

    /// <summary>Whether every pixel of a run of four-byte pixels with alpha last is transparent.</summary>
    public static bool IsEmpty(ReadOnlySpan<byte> pixels)
    {
        for (var index = 3; index < pixels.Length; index += 4)
        {
            if (pixels[index] != 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A number that is the same for two textures exactly when they hold the same picture, give or
    /// take one chance in 2^64. <see cref="Nothing"/> for a texture with nothing in it.
    /// </summary>
    public static ulong Fingerprint(StudioGraphicsDevice graphics, ID3D11Texture2D texture)
    {
        ArgumentNullException.ThrowIfNull(graphics);
        ArgumentNullException.ThrowIfNull(texture);
        var description = texture.Description;
        var length = checked((int)description.Width * (int)description.Height * 4);
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            var pixels = buffer.AsSpan(0, length);
            graphics.ReadTexture(texture, 0, pixels);
            return Fingerprint(pixels);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>The fingerprint of four-byte pixels with alpha last: <see cref="Nothing"/> when all are transparent.</summary>
    public static ulong Fingerprint(ReadOnlySpan<byte> pixels)
    {
        if (IsEmpty(pixels))
        {
            return Nothing;
        }

        // FNV-1a over whole pixels, then over the bytes left.
        var hash = 14695981039346656037UL;
        var words = MemoryMarshal.Cast<byte, uint>(pixels);
        foreach (var word in words)
        {
            hash = (hash ^ word) * 1099511628211UL;
        }

        foreach (var value in pixels[(words.Length * 4)..])
        {
            hash = (hash ^ value) * 1099511628211UL;
        }

        return hash == Nothing ? 1 : hash;
    }
}

/// <summary>
/// Decides when the pictures the players hand over can be believed.
/// <para>
/// A player decodes on a Direct3D device of its own. When the texture it is told to copy into
/// belongs to a device on another graphics adapter, it moves its work over to that device, and
/// until it has settled there it hands over frames with the right position on them and the wrong
/// picture in them. Measured, with the players on the graphics hardware and the engine on the
/// software adapter: the first copy leaves nothing in the texture; the answer to the next position
/// change is black; after that an answer was seen empty, and one frame ahead of its position.
/// </para>
/// <para>
/// Nothing a player says tells these apart from good frames, so the pictures are compared: the
/// players are sent to one frame, away, and back, round after round, until two rounds in a row
/// leave the same picture in every texture and none of them empty. A player that is still
/// settling does not give the same wrong answer twice.
/// </para>
/// Not thread-safe.
/// </summary>
internal sealed class StudioPreviewProof
{
    private ulong[]? _previous;

    /// <summary>Rounds offered so far.</summary>
    public int Rounds { get; private set; }

    /// <summary>
    /// Takes the fingerprint of every texture after a round. True when they are the ones the round
    /// before left, and none is <see cref="StudioPreviewPixels.Nothing"/>.
    /// </summary>
    public bool Offer(ReadOnlySpan<ulong> pictures)
    {
        Rounds++;
        var agrees = pictures.Length > 0 && _previous is not null && _previous.Length == pictures.Length;
        for (var index = 0; agrees && index < pictures.Length; index++)
        {
            agrees = pictures[index] != StudioPreviewPixels.Nothing && _previous![index] == pictures[index];
        }

        _previous = pictures.ToArray();
        return agrees;
    }
}
