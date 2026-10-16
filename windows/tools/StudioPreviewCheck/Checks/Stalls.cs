using System.Diagnostics;
using System.Runtime;
using TinyClips.Core.Studio.Preview;
using TinyClips.Core.Studio.Rendering;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace TinyClips.Tools.StudioPreviewCheck.Checks;

/// <summary>
/// Holds this process up on demand, the way a PC that is busy does: a thread that makes the
/// garbage collector stop every managed thread, the preview's among them, for some tens of
/// milliseconds, again and again. A player's own threads are not managed and run on, but a frame
/// they hand over has to wait at the door until the collector is done.
/// </summary>
/// <remarks>
/// A full collection of a small heap is over in a millisecond or two, which holds nothing up. So
/// the heap is made large first: objects that point at each other and are never let go, as many
/// as it takes for one collection to last about the time asked for on this PC.
/// </remarks>
internal sealed class HeldUp : IDisposable
{
    private sealed class Link
    {
        public Link? Next;
        public long Weight;
    }

    private readonly List<Link[]> _ballast = [];
    private readonly List<(long From, long To)> _stalls = [];
    private readonly Thread _thread;
    private readonly Random _random;
    private readonly int _shortestGap;
    private readonly int _longestGap;
    private readonly TimeSpan _pausedBefore;
    private readonly int _collectionsBefore;
    private volatile bool _stop;
    private volatile bool _resting = true;

    /// <param name="stallMilliseconds">About how long each collection should hold the process.</param>
    /// <param name="shortestGap">The shortest time between two collections, in milliseconds.</param>
    /// <param name="longestGap">The longest.</param>
    public HeldUp(double stallMilliseconds, int shortestGap, int longestGap, int seed = 20261004)
    {
        _shortestGap = shortestGap;
        _longestGap = Math.Max(shortestGap, longestGap);
        _random = new Random(seed);
        StallMilliseconds = Grow(stallMilliseconds);
        _pausedBefore = GC.GetTotalPauseDuration();
        _collectionsBefore = GC.CollectionCount(2);
        _thread = new Thread(Run) { IsBackground = true, Name = "StudioPreviewCheck.HeldUp" };
        _thread.Start();
    }

    /// <summary>How long one collection held the process when the heap had been made large enough, in milliseconds.</summary>
    public double StallMilliseconds { get; }

    /// <summary>How many megabytes are kept alive to make a collection last.</summary>
    public double BallastMegabytes => _ballast.Sum(chunk => (long)chunk.Length) * 32 / 1048576.0;

    /// <summary>Full collections forced since this was made.</summary>
    public int Collections => GC.CollectionCount(2) - _collectionsBefore;

    /// <summary>How long the collector has held every thread since this was made.</summary>
    public TimeSpan Paused => GC.GetTotalPauseDuration() - _pausedBefore;

    /// <summary>The collections forced so far: when each began and ended, as Stopwatch timestamps.</summary>
    public (long From, long To)[] Stalls
    {
        get
        {
            lock (_stalls)
            {
                return [.. _stalls];
            }
        }
    }

    /// <summary>Starts holding the process up. Until then, and after <see cref="Rest"/>, nothing is collected.</summary>
    public void Begin() => _resting = false;

    public void Rest() => _resting = true;

    public string Describe()
    {
        // What a collection took while the preview played is not what it took beforehand, with
        // nothing else going on: so both are said.
        var lengths = Stalls.Select(stall => Stopwatch.GetElapsedTime(stall.From, stall.To).TotalMilliseconds).ToArray();
        var forced = lengths.Length == 0
            ? "none was forced"
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"the {lengths.Length} that were forced took {lengths.Average():0} ms on average, {lengths.Min():0} to {lengths.Max():0} ms");
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{Collections} full collections, each holding every managed thread for about {StallMilliseconds:0} ms when that was measured beforehand ({BallastMegabytes:0} MB kept alive to make them last); {forced}; the collector held the process for {Paused.TotalMilliseconds:0} ms in all");
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(TimeSpan.FromSeconds(5));
        _ballast.Clear();
        GC.Collect();
    }

    /// <summary>Makes the heap large enough for one full collection to take about the time asked for. Returns what it took at the end.</summary>
    private double Grow(double wanted)
    {
        const int ChunkLength = 250_000;
        const int MostChunks = 96;
        var took = Measure();
        while (took < wanted && _ballast.Count < MostChunks)
        {
            // Enough chunks at a time to get there in a few steps.
            var more = took <= 0.5 || _ballast.Count == 0 ? 2 : Math.Clamp((int)Math.Ceiling(_ballast.Count * ((wanted / took) - 1)), 1, 16);
            for (var index = 0; index < more && _ballast.Count < MostChunks; index++)
            {
                var chunk = new Link[ChunkLength];
                for (var item = 0; item < chunk.Length; item++)
                {
                    chunk[item] = new Link { Weight = item };
                }

                // Pointing at each other out of order, so that following them is slow.
                for (var item = 0; item < chunk.Length; item++)
                {
                    chunk[item].Next = chunk[_random.Next(chunk.Length)];
                }

                _ballast.Add(chunk);
            }

            took = Measure();
        }

        return took;
    }

    private static double Measure()
    {
        Collect();
        var before = GC.GetTotalPauseDuration();
        Collect();
        return (GC.GetTotalPauseDuration() - before).TotalMilliseconds;
    }

    private static void Collect() => GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);

    private void Run()
    {
        while (!_stop)
        {
            var gap = _random.Next(_shortestGap, _longestGap + 1);
            for (var waited = 0; waited < gap && !_stop; waited += 5)
            {
                Thread.Sleep(5);
            }

            if (_stop || _resting)
            {
                continue;
            }

            var from = Stopwatch.GetTimestamp();
            Collect();
            var to = Stopwatch.GetTimestamp();
            lock (_stalls)
            {
                _stalls.Add((from, to));
            }
        }
    }
}

/// <summary>
/// Makes the engine's render thread slow to draw, now and then: it is what the engine is given
/// as <c>StudioPreviewOptions.RenderDelay</c>. The render thread then keeps the graphics device
/// for that long, and a player that has a frame to hand over waits for it.
/// </summary>
internal sealed class SlowDraws(int shortest, int longest, int shortestGap, int longestGap, int seed = 20261004)
{
    private readonly List<(long From, long To)> _stalls = [];
    private readonly Random _random = new(seed);
    private long _nextAt;
    private volatile bool _going;

    /// <summary>The draws held up so far: when each began and ended, as Stopwatch timestamps.</summary>
    public (long From, long To)[] Stalls
    {
        get
        {
            lock (_stalls)
            {
                return [.. _stalls];
            }
        }
    }

    public void Begin()
    {
        Interlocked.Exchange(ref _nextAt, Stopwatch.GetTimestamp() + Ticks(_random.Next(shortestGap, longestGap + 1)));
        _going = true;
    }

    public void Rest() => _going = false;

    /// <summary>Called by the render thread before each scene it draws: how long it is to wait first.</summary>
    public TimeSpan Next()
    {
        var now = Stopwatch.GetTimestamp();
        if (!_going || now < Interlocked.Read(ref _nextAt))
        {
            return TimeSpan.Zero;
        }

        int hold;
        lock (_stalls)
        {
            hold = _random.Next(shortest, longest + 1);
            _stalls.Add((now, now + Ticks(hold)));
            Interlocked.Exchange(ref _nextAt, now + Ticks(hold + _random.Next(shortestGap, longestGap + 1)));
        }

        return TimeSpan.FromMilliseconds(hold);
    }

    private static long Ticks(int milliseconds) => milliseconds * Stopwatch.Frequency / 1000;
}

/// <summary>
/// Keeps the thread that stops the engine's clock from going on, every time it has stopped it:
/// it is what the engine is given as <c>StudioPreviewOptions.StopDelay</c>. That is what happens
/// to the thread on a PC whose processors are all busy, at a moment nothing else can be aimed
/// at: a player goes on by itself for an instant after the clock it shares has stopped, and
/// hands over the frame that comes due in that instant, with the position the clock stopped on.
/// </summary>
internal sealed class SlowStops(int shortest, int longest, int seed = 20261004)
{
    private readonly List<(long From, long To)> _stalls = [];
    private readonly Random _random = new(seed);
    private volatile bool _going;

    /// <summary>The stops held up so far: when each hold began and ended, as Stopwatch timestamps.</summary>
    public (long From, long To)[] Stalls
    {
        get
        {
            lock (_stalls)
            {
                return [.. _stalls];
            }
        }
    }

    public void Begin() => _going = true;

    public void Rest() => _going = false;

    /// <summary>Called by the thread that has just stopped the clock: how long it is to wait before it goes on.</summary>
    public TimeSpan Next()
    {
        if (!_going)
        {
            return TimeSpan.Zero;
        }

        var now = Stopwatch.GetTimestamp();
        int hold;
        lock (_stalls)
        {
            hold = _random.Next(shortest, longest + 1);
            _stalls.Add((now, now + (hold * Stopwatch.Frequency / 1000)));
        }

        return TimeSpan.FromMilliseconds(hold);
    }
}

/// <summary>
/// Keeps every processor of the PC busy with threads of this process that do nothing but run,
/// as another program does that takes all the time it can get. The preview's threads then wait
/// their turn like everybody else.
/// </summary>
internal sealed class BusyProcessors : IDisposable
{
    private readonly Thread[] _threads;
    private volatile bool _going;
    private volatile bool _stop;
    private long _busyTicks;
    private long _since;

    public BusyProcessors(int threads)
    {
        _threads = new Thread[Math.Max(1, threads)];
        for (var index = 0; index < _threads.Length; index++)
        {
            _threads[index] = new Thread(Run) { IsBackground = true, Name = "StudioPreviewCheck.Busy" };
            _threads[index].Start();
        }
    }

    public int Threads => _threads.Length;

    /// <summary>For how long the processors have been kept busy so far.</summary>
    public TimeSpan Busy => Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _busyTicks) + (_going ? Stopwatch.GetTimestamp() - Interlocked.Read(ref _since) : 0));

    public void Begin()
    {
        if (!_going)
        {
            Interlocked.Exchange(ref _since, Stopwatch.GetTimestamp());
            _going = true;
        }
    }

    public void Rest()
    {
        if (_going)
        {
            _going = false;
            Interlocked.Add(ref _busyTicks, Stopwatch.GetTimestamp() - Interlocked.Read(ref _since));
        }
    }

    public void Dispose()
    {
        Rest();
        _stop = true;
        foreach (var thread in _threads)
        {
            thread.Join(TimeSpan.FromSeconds(2));
        }
    }

    private void Run()
    {
        var value = 1.0;
        while (!_stop)
        {
            if (!_going)
            {
                Thread.Sleep(2);
                continue;
            }

            // Work that makes nothing for the garbage collector to do.
            for (var step = 0; step < 20000; step++)
            {
                value = Math.Sqrt((value * 1.000001) + step);
            }
        }

        GC.KeepAlive(value);
    }
}

/// <summary>
/// Keeps the graphics adapter busy now and then, from a device of its own: large textures copied
/// back and forth, as many as take about the time asked for, and a wait for the adapter to be
/// done with them. It is what another program does that draws a lot. Nothing of the preview is
/// touched or locked; what its players copy waits for the adapter like anything else.
/// </summary>
internal sealed class BusyGraphics : IDisposable
{
    private const int Side = 4096;
    private const int MostPairs = 400;

    private readonly List<(long From, long To)> _stalls = [];
    private readonly ManualResetEventSlim _ready = new();
    private readonly Thread _thread;
    private readonly Random _random;
    private readonly double _milliseconds;
    private readonly int _shortestGap;
    private readonly int _longestGap;
    private volatile bool _going;
    private volatile bool _stop;
    private volatile string? _problem;
    private volatile string _adapter = "no adapter";
    private int _pairs = 1;

    /// <param name="milliseconds">About how long the adapter is kept busy each time.</param>
    /// <param name="shortestGap">The shortest time between two such times, in milliseconds.</param>
    /// <param name="longestGap">The longest.</param>
    public BusyGraphics(double milliseconds, int shortestGap, int longestGap, int seed = 20261004)
    {
        _milliseconds = milliseconds;
        _shortestGap = shortestGap;
        _longestGap = Math.Max(shortestGap, longestGap);
        _random = new Random(seed);
        _thread = new Thread(Run) { IsBackground = true, Name = "StudioPreviewCheck.BusyGraphics" };
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(20));
    }

    /// <summary>The times the adapter was kept busy so far: when each began and ended, as Stopwatch timestamps.</summary>
    public (long From, long To)[] Stalls
    {
        get
        {
            lock (_stalls)
            {
                return [.. _stalls];
            }
        }
    }

    public void Begin() => _going = true;

    public void Rest() => _going = false;

    public string Describe()
    {
        if (_problem is { } problem)
        {
            return "the graphics adapter could not be kept busy: " + problem;
        }

        var lengths = Stalls.Select(stall => Stopwatch.GetElapsedTime(stall.From, stall.To).TotalMilliseconds).ToArray();
        return lengths.Length == 0
            ? $"the graphics adapter ({_adapter}) was never kept busy"
            : string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"the graphics adapter ({_adapter}) kept busy {lengths.Length} times from a device of the tool's own, {Side}x{Side} textures copied {_pairs * 2} times each time: {lengths.Average():0} ms on average, {lengths.Min():0} to {lengths.Max():0} ms, {lengths.Sum():0} ms in all");
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(TimeSpan.FromSeconds(10));
        _ready.Dispose();
    }

    private void Run()
    {
        StudioGraphicsDevice? graphics = null;
        ID3D11Texture2D? first = null;
        ID3D11Texture2D? second = null;
        ID3D11Texture2D? probe = null;
        try
        {
            graphics = StudioGraphicsDevice.CreateHardware();
            _adapter = graphics.IsSoftware ? graphics.AdapterName + ", software" : graphics.AdapterName;
            first = graphics.CreateRenderTexture(Side, Side);
            second = graphics.CreateRenderTexture(Side, Side);
            probe = graphics.CreateRenderTexture(4, 4);

            // As many copies as take about the time asked for, measured with nothing else going on.
            Work(graphics, first, second, probe, 2);
            var watch = Stopwatch.StartNew();
            Work(graphics, first, second, probe, 4);
            var each = Math.Max(0.05, watch.Elapsed.TotalMilliseconds / 4);
            _pairs = Math.Clamp((int)Math.Ceiling(_milliseconds / each), 1, MostPairs);
            _ready.Set();
            while (!_stop)
            {
                var gap = _random.Next(_shortestGap, _longestGap + 1);
                for (var waited = 0; waited < gap && !_stop; waited += 5)
                {
                    Thread.Sleep(5);
                }

                if (_stop || !_going)
                {
                    continue;
                }

                var from = Stopwatch.GetTimestamp();
                Work(graphics, first, second, probe, _pairs);
                var to = Stopwatch.GetTimestamp();
                lock (_stalls)
                {
                    _stalls.Add((from, to));
                }
            }
        }
        catch (Exception ex)
        {
            _problem = ex.Message;
        }
        finally
        {
            try
            {
                _ready.Set();
            }
            catch (ObjectDisposedException)
            {
                // Dispose gave up waiting for this thread.
            }

            probe?.Dispose();
            second?.Dispose();
            first?.Dispose();
            graphics?.Dispose();
        }
    }

    // Copies the two textures into each other, and returns when the adapter has done it all.
    private static void Work(StudioGraphicsDevice graphics, ID3D11Texture2D first, ID3D11Texture2D second, ID3D11Texture2D probe, int pairs)
    {
        for (var pair = 0; pair < pairs; pair++)
        {
            graphics.Context.CopyResource(second, first);
            graphics.Context.CopyResource(first, second);
        }

        // Reading a pixel back waits for everything before it.
        graphics.Context.CopySubresourceRegion(probe, 0, 0, 0, 0, first, 0, new Box(0, 0, 0, 4, 4, 1));
        graphics.ReadTexture(probe);
    }
}

/// <summary>
/// The ways the checks hold this process up while a preview plays, alone or together: the garbage
/// collector stopping every managed thread, the render thread slow to draw, the whole process
/// stopped from outside, every processor busy with something else, the graphics adapter busy
/// with something else, and the thread that stops the clock kept from going on once it has
/// ("afterstop"). "loaded" is busy processors with the collector at work as well, as on a PC
/// that is busy; "all" is the first three at once, which no PC does and which shows where the
/// rules for numbering the frames end.
/// </summary>
internal sealed class HoldUps : IDisposable
{
    public static readonly string[] Kinds = ["none", "collector", "draw", "stopped", "busy", "loaded", "gpu", "afterstop", "all"];

    private readonly HeldUp? _collector;
    private readonly SlowDraws? _draws;
    private readonly Freezer? _freezer;
    private readonly BusyProcessors? _busy;
    private readonly BusyGraphics? _graphics;
    private readonly SlowStops? _stops;

    /// <param name="kind">One of <see cref="Kinds"/>, or several of them joined with a plus sign, which hold the process up together.</param>
    /// <param name="milliseconds">About how long each hold-up lasts: the collector's every time, the others between a third of it and twice it.</param>
    /// <param name="collectorGap">The shortest time between two collections, the longest being three times that; 0 for the usual 150 to 450 ms.</param>
    public HoldUps(string kind, int milliseconds = 60, int collectorGap = 0)
    {
        Kind = kind;
        _parts = Parts(kind);
        if (Has("collector"))
        {
            _collector = collectorGap > 0 ? new HeldUp(milliseconds, collectorGap, collectorGap * 3) : new HeldUp(milliseconds, 150, 450);
        }

        if (Has("draw"))
        {
            _draws = new SlowDraws(Math.Max(10, milliseconds * 2 / 3), milliseconds * 5 / 2, 300, 900);
        }

        if (Has("stopped"))
        {
            _freezer = new Freezer(Math.Max(10, milliseconds / 3), milliseconds * 2, 200, 700);
        }

        if (Has("busy"))
        {
            // Half as many again as there are processors, so that none is ever free.
            _busy = new BusyProcessors(Environment.ProcessorCount * 3 / 2);
        }

        if (Has("gpu"))
        {
            // Busy about a third of the time.
            _graphics = new BusyGraphics(milliseconds, milliseconds, milliseconds * 3);
        }

        if (Has("afterstop"))
        {
            // Long enough, most times, for a player to look for a frame once or twice meanwhile.
            _stops = new SlowStops(Math.Max(4, milliseconds / 10), Math.Max(8, milliseconds / 2));
        }

        bool Has(string part) => _parts.Contains(part);
    }

    private readonly string[] _parts;

    public string Kind { get; }

    // What a kind is made of: "loaded" and "all" are names for several at once.
    private static string[] Parts(string kind) =>
    [
        .. kind.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(part => part switch
            {
                "loaded" => ["busy", "collector"],
                "all" => ["collector", "draw", "stopped"],
                "none" => [],
                _ => new[] { part },
            })
            .Distinct(),
    ];

    /// <summary>What an engine has to be opened with for its draws to be held up, or null.</summary>
    public Func<TimeSpan>? RenderDelay => _draws is null ? null : _draws.Next;

    /// <summary>What an engine has to be opened with for the thread that stops its clock to be held up, or null.</summary>
    public Func<TimeSpan>? StopDelay => _stops is null ? null : _stops.Next;

    /// <summary>The options an engine has to be opened with for this to hold it up.</summary>
    public StudioPreviewOptions With(StudioPreviewOptions options) => With(options, [this]);

    /// <summary>The options an engine has to be opened with for each of several to hold it up in its turn.</summary>
    public static StudioPreviewOptions With(StudioPreviewOptions options, IReadOnlyList<HoldUps> holdUps) => options with
    {
        RenderDelay = holdUps.Select(h => h.RenderDelay).FirstOrDefault(d => d is not null) ?? options.RenderDelay,
        StopDelay = holdUps.Select(h => h.StopDelay).FirstOrDefault(d => d is not null) ?? options.StopDelay,
    };

    /// <summary>The times the whole process was stopped from outside so far; none unless that is one of the hold-ups.</summary>
    public (long From, long To)[] ProcessStops => _freezer?.Stalls ?? [];

    /// <summary>Every hold-up so far, of every kind: when it began and ended, as Stopwatch timestamps.</summary>
    public (long From, long To)[] Stalls => [.. _collector?.Stalls ?? [], .. _draws?.Stalls ?? [], .. _freezer?.Stalls ?? [], .. _graphics?.Stalls ?? [], .. _stops?.Stalls ?? []];

    /// <summary>
    /// The kinds named in the value of <c>--stalls</c>, a list with commas; kinds joined with a
    /// plus sign hold the process up together. The names the investigations use for them are
    /// understood too.
    /// </summary>
    public static string[] Parse(string text)
    {
        var kinds = new List<string>();
        foreach (var name in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = name.ToLowerInvariant().Split('+', StringSplitOptions.TrimEntries).Select(part => part switch
            {
                "gc" => "collector",
                "gate" => "draw",
                "freeze" => "stopped",
                var other => other,
            }).ToArray();
            if (parts.Length == 0 || parts.Any(part => !Kinds.Contains(part)) || (parts.Length > 1 && parts.Contains("none")))
            {
                throw new ArgumentException($"--stalls takes {string.Join(", ", Kinds)}, or several of them joined with +, not '{name}'.");
            }

            kinds.Add(string.Join('+', parts));
        }

        return [.. kinds];
    }

    /// <summary>How many of some moments came while the process was held up, or within <paramref name="milliseconds"/> after a hold-up had ended.</summary>
    public int CountNear(IEnumerable<long> moments, double milliseconds)
    {
        var stalls = Stalls;
        var slack = (long)(milliseconds * Stopwatch.Frequency / 1000);
        return moments.Count(moment => stalls.Any(stall => moment >= stall.From && moment <= stall.To + slack));
    }

    public void Begin()
    {
        _collector?.Begin();
        _draws?.Begin();
        _freezer?.Begin();
        _busy?.Begin();
        _graphics?.Begin();
        _stops?.Begin();
    }

    public void Rest()
    {
        _collector?.Rest();
        _draws?.Rest();
        _freezer?.Rest();
        _busy?.Rest();
        _graphics?.Rest();
        _stops?.Rest();
    }

    /// <summary>In words, what holds the process up.</summary>
    public string Name => Kind.Contains('+') ? string.Join(", and ", Kind.Split('+').Select(NameOf)) : NameOf(Kind);

    private static string NameOf(string kind) => kind switch
    {
        "none" => "nothing holding the process up",
        "collector" => "the garbage collector stopping every thread of the tool's own code",
        "draw" => "the render thread slow to draw",
        "stopped" => "the whole process stopped from outside",
        "busy" => "every processor busy with something else",
        "loaded" => "every processor busy with something else and the garbage collector stopping every thread of the tool's own code",
        "gpu" => "the graphics adapter busy with something else",
        "afterstop" => "the thread that stops the clock kept from going on once it has",
        _ => "the garbage collector, slow draws and stops of the whole process, all at once",
    };

    public string Describe()
    {
        var parts = new List<string>();
        if (_collector is not null)
        {
            parts.Add(_collector.Describe());
        }

        if (_draws is not null)
        {
            var stalls = _draws.Stalls;
            parts.Add(stalls.Length == 0
                ? "no draw was held up"
                : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{stalls.Length} draws held up for {stalls.Min(Length):0} to {stalls.Max(Length):0} ms, {stalls.Sum(Length):0} ms in all"));
        }

        if (_freezer is not null)
        {
            parts.Add(_freezer.Describe());
        }

        if (_busy is not null)
        {
            parts.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{_busy.Threads} threads that only run kept the {Environment.ProcessorCount} processors busy for {_busy.Busy.TotalSeconds:0.0} s"));
        }

        if (_graphics is not null)
        {
            parts.Add(_graphics.Describe());
        }

        if (_stops is not null)
        {
            var stalls = _stops.Stalls;
            parts.Add(stalls.Length == 0
                ? "the clock was never stopped"
                : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"the clock was stopped {stalls.Length} times, and each time the thread that had stopped it was kept from going on for {stalls.Min(Length):0} to {stalls.Max(Length):0} ms, {stalls.Sum(Length):0} ms in all"));
        }

        return parts.Count == 0 ? "nothing held the process up" : string.Join("; ", parts);

        static double Length((long From, long To) stall) => Stopwatch.GetElapsedTime(stall.From, stall.To).TotalMilliseconds;
    }

    public void Dispose()
    {
        _collector?.Dispose();
        _freezer?.Dispose();
        _busy?.Dispose();
        _graphics?.Dispose();
    }
}

/// <summary>Several things that are let go of together.</summary>
internal sealed class Disposables(IReadOnlyList<IDisposable> items) : IDisposable
{
    public void Dispose()
    {
        foreach (var item in items)
        {
            item.Dispose();
        }
    }
}
