using System.Diagnostics;
using TinyClips.Core.Studio.Rendering;

namespace TinyClips.Tools.StudioPreviewCheck.Checks;

/// <summary>
/// Stand-ins for what finds the people in a camera picture, and what they were asked. Every
/// renderer of an engine gets one of its own from <see cref="Make"/>, and all of them count
/// here. A stand-in calls the whole frame a person, so that the camera is drawn whole and its
/// frame number can be read as ever; and it can take its time, as a model does: some
/// milliseconds on the processor for every picture, and far longer once, when it is loaded.
/// </summary>
internal sealed class StandInFinders
{
    private int _made;
    private int _disposed;
    private long _calls;

    /// <summary>Milliseconds each look takes, with the processor kept busy for as long.</summary>
    public double EachMilliseconds { get; init; }

    /// <summary>Milliseconds it takes to make a finder: the model being loaded, which is done inside the draw that first needs it.</summary>
    public double FirstMilliseconds { get; init; }

    /// <summary>Finders made: one for every renderer that had a camera picture to look at.</summary>
    public int Made => Volatile.Read(ref _made);

    public int Disposed => Volatile.Read(ref _disposed);

    /// <summary>Camera pictures looked at, by all of them together.</summary>
    public long Calls => Interlocked.Read(ref _calls);

    /// <summary>What <c>StudioPreviewOptions.PersonFinderFactory</c> is given.</summary>
    public IStudioPersonFinder? Make()
    {
        Interlocked.Increment(ref _made);
        if (FirstMilliseconds > 0)
        {
            Thread.Sleep(TimeSpan.FromMilliseconds(FirstMilliseconds));
        }

        return new Finder(this);
    }

    /// <summary>
    /// What <c>--people</c> names: the milliseconds a look takes and, after a comma, the
    /// milliseconds it takes to make a finder. Null when the option is not given.
    /// </summary>
    public static StandInFinders? FromOptions(CheckOptions options)
    {
        var text = options.Text("people", string.Empty);
        if (text.Length == 0)
        {
            return null;
        }

        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        var numbers = new double[2];
        if (parts.Length > 2)
        {
            throw new ArgumentException($"--people takes the milliseconds a look takes and, after a comma, the milliseconds the first one takes more, not '{text}'.");
        }

        for (var index = 0; index < parts.Length; index++)
        {
            if (!double.TryParse(parts[index], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out numbers[index]) || numbers[index] < 0 || numbers[index] > 5000)
            {
                throw new ArgumentException($"--people takes the milliseconds a look takes and, after a comma, the milliseconds the first one takes more, each from 0 to 5000, not '{text}'.");
            }
        }

        return new StandInFinders { EachMilliseconds = numbers[0], FirstMilliseconds = numbers[1] };
    }

    public string Describe() =>
        $"{Made} finder{(Made == 1 ? string.Empty : "s")} made, {Disposed} disposed, {Calls} camera pictures looked at";

    private sealed class Finder(StandInFinders owner) : IStudioPersonFinder
    {
        private int _isDisposed;

        public int Width => 256;

        public int Height => 144;

        public bool TryFind(ReadOnlySpan<byte> bgra, Span<byte> mask)
        {
            Interlocked.Increment(ref owner._calls);
            if (owner.EachMilliseconds > 0)
            {
                // Busy, not asleep: a model works for its time, and a sleep of a few
                // milliseconds lasts as long as the system's timer lets it.
                var until = Stopwatch.GetTimestamp() + (long)(owner.EachMilliseconds * Stopwatch.Frequency / 1000);
                while (Stopwatch.GetTimestamp() < until)
                {
                    Thread.SpinWait(40);
                }
            }

            mask.Fill(255);
            return true;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) == 0)
            {
                Interlocked.Increment(ref owner._disposed);
            }
        }
    }
}
