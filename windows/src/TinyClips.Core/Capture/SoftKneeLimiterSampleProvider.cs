using System.Buffers;
using NAudio.Wave;

namespace TinyClips.Core.Capture;

/// <summary>
/// Per-sample soft-knee limiter for float PCM. Samples at or below <see cref="Knee"/> pass
/// through untouched; anything hotter is compressed along an <c>atan</c> curve that approaches
/// full scale asymptotically, so a hot microphone rounds off instead of hard-clipping. Mirrors
/// the macOS <c>VideoRecorder.softKneeLimitedSample</c> curve. Sample count and timing are
/// never altered, so A/V sync is unaffected. If processing ever throws, the untouched source
/// samples are returned as-is rather than dropping audio.
/// </summary>
public sealed class SoftKneeLimiterSampleProvider : ISampleProvider
{
    public const float Knee = 0.98f;

    private const float Headroom = 1f - Knee;
    private const float TwoOverPi = 2f / MathF.PI;
    private const float HalfPi = MathF.PI / 2f;

    private readonly ISampleProvider _source;
    private bool _loggedFailure;

    public SoftKneeLimiterSampleProvider(ISampleProvider source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);
        if (read <= 0)
        {
            return read;
        }

        // Transform into a pooled scratch buffer and copy back only on success so a failure
        // forwards the untouched source samples rather than a partially-limited mix.
        var scratch = ArrayPool<float>.Shared.Rent(read);
        try
        {
            var source = buffer.AsSpan(offset, read);
            var destination = scratch.AsSpan(0, read);
            Limit(source, destination);
            destination.CopyTo(source);
        }
        catch (Exception ex)
        {
            // Never drop audio: the original samples are still in the buffer, so they flow
            // through unlimited.
            if (!_loggedFailure)
            {
                _loggedFailure = true;
                WebcamDiagnostics.Log($"Microphone limiter failed; passing audio through unlimited: {ex.GetType().Name}: {ex.Message}");
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(scratch);
        }

        return read;
    }

    /// <summary>Applies <see cref="Limit(float)"/> to every sample in <paramref name="samples"/>.</summary>
    public static void LimitInPlace(Span<float> samples) => Limit(samples, samples);

    /// <summary>
    /// Writes the limited form of each sample in <paramref name="source"/> to the matching index
    /// of <paramref name="destination"/>. The spans may alias.
    /// </summary>
    public static void Limit(ReadOnlySpan<float> source, Span<float> destination)
    {
        if (destination.Length < source.Length)
        {
            throw new ArgumentException("Destination is shorter than source.", nameof(destination));
        }

        for (var i = 0; i < source.Length; i++)
        {
            destination[i] = Limit(source[i]);
        }
    }

    /// <summary>
    /// Soft-knee transfer function. Below the knee the sample is unchanged; above it the overage
    /// is mapped through <c>atan</c> so the magnitude approaches (but never reaches) 1.0.
    /// Non-finite input is returned unchanged.
    /// </summary>
    public static float Limit(float sample)
    {
        if (!float.IsFinite(sample))
        {
            return sample;
        }

        var magnitude = MathF.Abs(sample);
        if (magnitude <= Knee)
        {
            return sample;
        }

        var normalizedOverage = (magnitude - Knee) / Headroom;
        var limited = Knee + Headroom * TwoOverPi * MathF.Atan(HalfPi * normalizedOverage);
        return sample < 0f ? -limited : limited;
    }
}
