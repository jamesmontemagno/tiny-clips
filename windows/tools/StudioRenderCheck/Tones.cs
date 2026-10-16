namespace TinyClips.Tools.StudioRenderCheck;

/// <summary>
/// The sound of the test clips and the means to find it again: a 50 ms tone burst at the start of
/// every second, whose pitch says which second it is. The left channel carries it at full level
/// and the right at half, so swapped or merged channels show.
/// </summary>
internal static class Tones
{
    public const double BurstSeconds = 0.05;
    private const double Amplitude = 0.5 * short.MaxValue;

    public static double Frequency(int second) => 400 + (100 * second);

    /// <summary>
    /// How far a burst's detected start may be from where it belongs, in seconds: 5 ms, except in
    /// the first AAC frame of a track. Media Foundation's AAC encoder has nothing to overlap its
    /// first frame with, so the first 512 samples of every track it writes fade in, and a burst
    /// that starts there is heard to start up to one frame (21.3 ms) late. Correlation, which
    /// looks at the whole burst, still places it to the sample.
    /// </summary>
    public static double OnsetTolerance(double expectedSeconds, int rate) => expectedSeconds < 1024.0 / rate ? 0.022 : 0.005;

    /// <summary>The left channel at source sample <paramref name="sample"/>.</summary>
    public static double Value(long sample, int rate)
    {
        if (sample < 0)
        {
            return 0;
        }

        var second = sample / rate;
        var into = sample - (second * rate);
        return into >= BurstSeconds * rate ? 0 : Amplitude * Math.Sin(2 * Math.PI * Frequency((int)second) * into / rate);
    }

    /// <summary>Interleaved 16-bit stereo, or mono: the left channel alone.</summary>
    public static short[] Synthesize(double seconds, int rate, int channels = 2)
    {
        var frames = (int)Math.Round(seconds * rate);
        var pcm = new short[frames * channels];
        for (var index = 0; index < frames; index++)
        {
            var value = Value(index, rate);
            pcm[index * channels] = (short)Math.Round(value);
            if (channels > 1)
            {
                pcm[(index * channels) + 1] = (short)Math.Round(value / 2);
            }
        }

        return pcm;
    }

    /// <summary>Finds bursts in one channel: where each starts, in seconds, and its pitch from zero crossings over 40 ms.</summary>
    public static List<(double Time, double Frequency)> DetectBursts(short[] channel, int rate)
    {
        var result = new List<(double, double)>();
        const int quietLevel = 600;
        const int loudLevel = 3000;
        var quietNeeded = rate / 50;
        var quiet = quietNeeded;
        for (var index = 0; index < channel.Length; index++)
        {
            var level = Math.Abs((int)channel[index]);
            if (level < quietLevel)
            {
                quiet++;
                continue;
            }

            if (level > loudLevel && quiet >= quietNeeded)
            {
                // Walk back to where the signal first left the quiet band (at most 2 ms).
                var onset = index;
                while (onset > 0 && index - onset < rate / 500 && Math.Abs((int)channel[onset - 1]) >= quietLevel)
                {
                    onset--;
                }

                var end = Math.Min(channel.Length - 1, onset + (rate * 40 / 1000));
                var crossings = 0;
                for (var cursor = onset + 1; cursor <= end; cursor++)
                {
                    if ((channel[cursor - 1] < 0) != (channel[cursor] < 0))
                    {
                        crossings++;
                    }
                }

                result.Add((onset / (double)rate, crossings / 2.0 / ((end - onset) / (double)rate)));
                index = end;
                quiet = 0;
                continue;
            }

            if (level >= loudLevel)
            {
                quiet = 0;
            }
        }

        return result;
    }

    /// <summary>
    /// How many samples <paramref name="decoded"/> is late (positive) or early against the sound
    /// that should be there: the lag at which the two correlate best.
    /// </summary>
    /// <remarks>
    /// The comparison covers each burst and 5 ms of the silence on either side of it, and is
    /// normalized by the energy the decoded sound has there. A tone repeats every cycle, so the
    /// burst alone would match equally well a cycle early; the silence that has to be silent is
    /// what makes one lag the best. The first AAC frame of the track is left out because it fades in.
    /// </remarks>
    /// <param name="expected">The value the track should have at each output sample.</param>
    /// <returns>The lag, and the normalized correlation there (1 is a perfect match).</returns>
    public static (int Lag, double Match) Align(short[] decoded, Func<long, double> expected, long length, int maxLag)
    {
        const int margin = 240;
        const int fadeIn = 1024;
        var values = new double[Math.Max(0, length)];
        var near = new bool[values.Length];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = expected(index);
            if (values[index] != 0)
            {
                // Every burst starts and ends within a few samples of a zero crossing, so marking
                // around each non-zero sample marks the burst and its margins.
                var from = Math.Max(0, index - margin);
                var to = Math.Min(values.Length - 1, index + margin);
                if (!near[from] || !near[to])
                {
                    Array.Fill(near, true, from, to - from + 1);
                }
            }
        }

        var points = new List<(int At, double Value)>();
        double energy = 0;
        for (var index = fadeIn; index < values.Length; index++)
        {
            if (near[index])
            {
                points.Add((index, values[index]));
                energy += values[index] * values[index];
            }
        }

        if (points.Count == 0 || energy == 0)
        {
            return (0, 0);
        }

        var bestLag = 0;
        var best = double.MinValue;
        for (var lag = -maxLag; lag <= maxLag; lag++)
        {
            double product = 0;
            double decodedEnergy = 0;
            foreach (var (at, value) in points)
            {
                var position = at + lag;
                if (position >= 0 && position < decoded.Length)
                {
                    double sample = decoded[position];
                    product += value * sample;
                    decodedEnergy += sample * sample;
                }
            }

            var score = decodedEnergy > 0 ? product / Math.Sqrt(energy * decodedEnergy) : 0;
            if (score > best)
            {
                best = score;
                bestLag = lag;
            }
        }

        return (bestLag, best);
    }
    public static double Rms(short[] channel)
    {
        if (channel.Length == 0)
        {
            return 0;
        }

        double sum = 0;
        foreach (var value in channel)
        {
            sum += (double)value * value;
        }

        return Math.Sqrt(sum / channel.Length);
    }
}
