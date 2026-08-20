using NAudio.Wave;
using TinyClips.Core.Capture;

namespace TinyClips.Core.Tests;

public sealed class SoftKneeLimiterSampleProviderTests
{
    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(-0.5f)]
    [InlineData(0.98f)]
    [InlineData(-0.98f)]
    public void Limit_AtOrBelowKnee_PassesThroughUnchanged(float sample)
    {
        Assert.Equal(sample, SoftKneeLimiterSampleProvider.Limit(sample));
    }

    [Theory]
    [InlineData(0.99f)]
    [InlineData(1.0f)]
    [InlineData(1.5f)]
    [InlineData(4.0f)]
    [InlineData(100f)]
    public void Limit_AboveKnee_StaysBetweenKneeAndFullScale(float sample)
    {
        var limited = SoftKneeLimiterSampleProvider.Limit(sample);

        Assert.True(limited > SoftKneeLimiterSampleProvider.Knee, $"{sample} -> {limited}");
        Assert.True(limited < 1f, $"{sample} -> {limited}");
        Assert.True(limited < sample, $"{sample} -> {limited}");
    }

    [Fact]
    public void Limit_PreservesSign()
    {
        var positive = SoftKneeLimiterSampleProvider.Limit(1.3f);
        var negative = SoftKneeLimiterSampleProvider.Limit(-1.3f);

        Assert.Equal(positive, -negative);
        Assert.True(negative < 0f);
    }

    [Fact]
    public void Limit_IsMonotonicAndApproachesFullScale()
    {
        var previous = SoftKneeLimiterSampleProvider.Limit(0.98f);
        for (var input = 0.981f; input < 50f; input *= 1.1f)
        {
            var current = SoftKneeLimiterSampleProvider.Limit(input);
            Assert.True(current >= previous, $"{input}: {current} < {previous}");
            previous = current;
        }

        Assert.True(previous > 0.999f);
        Assert.True(previous < 1f);
    }

    [Fact]
    public void Limit_MatchesReferenceCurve()
    {
        // Reference: knee + (1 - knee) * (2/π) * atan(π/2 * overage), overage = (|x| - knee)/(1 - knee).
        const float knee = 0.98f;
        const float input = 1.0f;
        var expected = knee + (1f - knee) * (2f / MathF.PI) * MathF.Atan(MathF.PI / 2f * ((input - knee) / (1f - knee)));

        Assert.Equal(expected, SoftKneeLimiterSampleProvider.Limit(input), 6);
    }

    [Fact]
    public void Limit_NonFiniteInput_PassesThrough()
    {
        Assert.True(float.IsNaN(SoftKneeLimiterSampleProvider.Limit(float.NaN)));
        Assert.Equal(float.PositiveInfinity, SoftKneeLimiterSampleProvider.Limit(float.PositiveInfinity));
        Assert.Equal(float.NegativeInfinity, SoftKneeLimiterSampleProvider.Limit(float.NegativeInfinity));
    }

    [Fact]
    public void Read_ReturnsSameSampleCountAndLimitsOnlyHotSamples()
    {
        var source = new ArraySampleProvider(new[] { 0.25f, -0.5f, 1.5f, -2.0f, 0.98f, 0.0f });
        var limiter = new SoftKneeLimiterSampleProvider(source);
        var buffer = new float[8];

        var read = limiter.Read(buffer, 1, 6);

        Assert.Equal(6, read);
        Assert.Equal(0f, buffer[0]);
        Assert.Equal(0.25f, buffer[1]);
        Assert.Equal(-0.5f, buffer[2]);
        Assert.InRange(buffer[3], 0.98f, 1f);
        Assert.InRange(buffer[4], -1f, -0.98f);
        Assert.Equal(0.98f, buffer[5]);
        Assert.Equal(0f, buffer[6]);
        Assert.Equal(0f, buffer[7]);
        Assert.Same(source.WaveFormat, limiter.WaveFormat);
    }

    [Fact]
    public void Read_EmptySource_ReturnsZero()
    {
        var limiter = new SoftKneeLimiterSampleProvider(new ArraySampleProvider(Array.Empty<float>()));

        Assert.Equal(0, limiter.Read(new float[4], 0, 4));
    }

    private sealed class ArraySampleProvider : ISampleProvider
    {
        private readonly float[] _samples;
        private int _position;

        public ArraySampleProvider(float[] samples)
        {
            _samples = samples;
        }

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            var available = Math.Min(count, _samples.Length - _position);
            Array.Copy(_samples, _position, buffer, offset, available);
            _position += available;
            return available;
        }
    }
}
