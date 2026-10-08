using System.Buffers.Binary;

namespace TinyClips.Core.Studio.Rendering;

/// <summary>
/// The project's volume on the export's sound: every sample of the 16-bit PCM on its way to the
/// encoder is multiplied by it. At volume 1 the samples are handed on as they are, the same
/// bytes, so an export that nobody turned down is what it was before there was a volume.
/// </summary>
/// <remarks>
/// A volume is never above 1, so a sample never grows and nothing has to be limited. A product
/// that falls between two steps of 16 bits goes to the nearer one, and one halfway goes away
/// from zero, so a sample and its negative stay each other's negative.
/// </remarks>
internal sealed class StudioPcmGain
{
    private readonly double _gain;
    private byte[] _scaled = [];

    /// <param name="volume">The stored volume; it is clamped here (<see cref="StudioSound.Volume(double)"/>).</param>
    public StudioPcmGain(double volume, int bitsPerSample = 16)
    {
        if (bitsPerSample != 16)
        {
            throw new ArgumentOutOfRangeException(nameof(bitsPerSample), bitsPerSample, "The volume is applied to 16-bit samples only.");
        }

        _gain = StudioSound.Volume(volume);
    }

    /// <summary>True at volume 1: <see cref="Apply"/> then returns what it was given.</summary>
    public bool LeavesSamplesAlone => _gain == 1;

    /// <summary>
    /// The samples at the volume. What is returned is valid until the next call, and is
    /// <paramref name="pcm"/> itself at volume 1.
    /// </summary>
    public ReadOnlySpan<byte> Apply(ReadOnlySpan<byte> pcm)
    {
        if (LeavesSamplesAlone || pcm.IsEmpty)
        {
            return pcm;
        }

        if (_scaled.Length < pcm.Length)
        {
            _scaled = new byte[pcm.Length];
        }

        var scaled = _scaled.AsSpan(0, pcm.Length);
        Scale(pcm, scaled, _gain);
        return scaled;
    }

    /// <summary>
    /// Writes each little-endian 16-bit sample of <paramref name="pcm"/>, times
    /// <paramref name="gain"/>, to <paramref name="scaled"/>. A product outside 16 bits stops
    /// at the largest or smallest sample; it never wraps around.
    /// </summary>
    public static void Scale(ReadOnlySpan<byte> pcm, Span<byte> scaled, double gain)
    {
        if (scaled.Length < pcm.Length)
        {
            throw new ArgumentException("There is no room for the samples.", nameof(scaled));
        }

        var whole = pcm.Length - (pcm.Length % 2);
        for (var offset = 0; offset < whole; offset += 2)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(pcm[offset..]);
            var product = Math.Round(sample * gain, MidpointRounding.AwayFromZero);
            var limited = double.IsNaN(product) ? 0 : Math.Min(short.MaxValue, Math.Max(short.MinValue, product));
            BinaryPrimitives.WriteInt16LittleEndian(scaled[offset..], (short)limited);
        }

        // Half a sample at the end is not sound; it is handed on as it came.
        pcm[whole..].CopyTo(scaled[whole..]);
    }
}
