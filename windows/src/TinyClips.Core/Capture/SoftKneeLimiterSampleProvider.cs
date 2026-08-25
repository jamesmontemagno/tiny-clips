using NAudio.Wave;

namespace TinyClips.Core.Capture;

/// <summary>
/// Per-sample soft-knee limiter for float PCM. Samples at or below <see cref="Knee"/> pass
/// through untouched; anything hotter is compressed along an <c>atan</c> curve that approaches
/// full scale asymptotically, so a hot microphone rounds off instead of hard-clipping. Mirrors
/// the macOS <c>VideoRecorder.softKneeLimitedSample</c> curve. Sample count and timing are
/// never altered, so A/V sync is unaffected.
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

    public int Read(float[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public int Read(Span<float> buffer)
    {
        var read = _source.Read(buffer);
        if (read <= 0)
        {
            return read;
        }

        try
        {
            LimitInPlace(buffer[..read]);
        }
        catch (Exception ex)
        {
            // Limit() is pure arithmetic and cannot fail mid-buffer, so any samples already in
            // the buffer are either untouched or fully limited; never drop audio either way.
            if (!_loggedFailure)
            {
                _loggedFailure = true;
                WebcamDiagnostics.Log($"Microphone limiter failed; passing audio through unlimited: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return read;
    }

    /// <summary>Applies <see cref="Limit(float)"/> to every sample in <paramref name="samples"/>.</summary>
    public static void LimitInPlace(Span<float> samples)
    {
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = Limit(samples[i]);
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
