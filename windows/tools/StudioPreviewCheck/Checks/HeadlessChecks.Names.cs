using System.Diagnostics;
using System.Globalization;
using System.Text;
using TinyClips.Core.Capture;
using TinyClips.Core.Studio.Rendering;
using TinyClips.Tools.StudioPreviewCheck.Media;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace TinyClips.Tools.StudioPreviewCheck.Checks;

// --investigate names: which frame a player's copy really holds, against the frame the player's
// position names at that moment. Two bare players on one clock, set up as the engine sets them
// up, and nothing of the engine. Every copy keeps the strip with its frame number, in a texture
// of its own that is read when the clock has stopped, so that finding out costs the callback
// next to nothing. What is measured goes into a file, one line for every copy.
internal sealed partial class HeadlessChecks
{
    private void InvestigateNames()
    {
        var passes = _options.Number("count", 4);
        var stalls = _options.Text("stalls", "none").ToLowerInvariant();
        if (stalls is not ("none" or "gc" or "gate" or "freeze" or "both" or "all"))
        {
            throw new ArgumentException("--stalls takes none, gc, gate, freeze, both (gc and gate) or all.");
        }

        var fps = _options.Number("fps", TestMedia.Fps);
        if (fps is not (30 or 60))
        {
            throw new ArgumentException("--fps takes 30 or 60.");
        }

        var screen = fps == 60 ? TestMedia.Screen60 : TestMedia.Screen;
        var stallMilliseconds = _options.Number("stall-ms", 60);
        var pauses = !_options.Flag("no-pauses");
        var snap = _options.Flag("snap");
        var discardLate = _options.Flag("discard-late");
        var withGc = stalls is "gc" or "both" or "all";
        var withGate = stalls is "gate" or "both" or "all";
        var withFreeze = stalls is "freeze" or "all";
        _report.Section($"Investigation: the frame a copy holds and the frame its position names; the screen at {fps} frames a second; {passes} passes of about ten seconds, held up by: {stalls}{(pauses ? ", paused about once a second" : string.Empty)}{(snap ? ", the clock put in the middle of the frame named last after each pause" : string.Empty)}{(discardLate ? ", frames announced after a pause not copied" : string.Empty)}");
        if (fps == 60)
        {
            TestMedia.EnsureClip(_media, screen, _report);
        }

        var path = Path.Combine(_output, $"names-{_report.Stamp}.csv");
        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(false));
        writer.WriteLine("kind,pass,clip,index,start_ms,position_ms,controller_ms,read_ms,gate_wait_ms,copy_ms,position_after_ms,return_ms,gc_pause_ms,gc_return_ms,truth,name,name_after,overlap,fps");

        using var folder = TestFolder.Create(_media, TestMedia.Camera, cameraOffset: 0, screen: screen);
        using var rig = new NameRig(folder, screen);
        using var heldUp = withGc ? new HeldUp(stallMilliseconds, 150, 450) : null;
        if (heldUp is not null)
        {
            _report.Line($"  the garbage collector holds every managed thread for about {F(heldUp.StallMilliseconds, "0")} ms a time ({F(heldUp.BallastMegabytes, "0")} MB kept alive to make it last)");
        }

        using var freezer = withFreeze ? new Freezer(Math.Max(10, stallMilliseconds / 3), stallMilliseconds * 2, 200, 700) : null;
        if (!rig.Open(out var problem))
        {
            _report.Line("  the players did not open: " + problem);
            return;
        }

        var random = new Random(20261004);
        var origin = Stopwatch.GetTimestamp();
        double Ms(long timestamp) => Stopwatch.GetElapsedTime(origin, timestamp).TotalMilliseconds;
        var totals = new NameTotals();
        for (var pass = 1; pass <= passes; pass++)
        {
            var events = new List<(string What, long From, long To, double Value)>();
            rig.Rewind(seconds: 0.5);
            using var gate = withGate ? new GateStalls(rig.Device, 40, 150, 300, 900, pass) : null;
            var stallsBefore = heldUp?.Stalls.Length ?? 0;
            var freezesBefore = freezer?.Stalls.Length ?? 0;
            heldUp?.Begin();
            freezer?.Begin();
            var from = Stopwatch.GetTimestamp();
            rig.Controller.Resume();
            events.Add(("resume", from, Stopwatch.GetTimestamp(), rig.Controller.Position.TotalMilliseconds));
            while (true)
            {
                Thread.Sleep(pauses ? random.Next(500, 1500) : 250);
                if (rig.Controller.Position.TotalSeconds > 10.6)
                {
                    break;
                }

                if (!pauses)
                {
                    continue;
                }

                from = Stopwatch.GetTimestamp();
                rig.SetDiscarding(discardLate);
                rig.Controller.Pause();
                var to = Stopwatch.GetTimestamp();
                events.Add(("pause", from, to, rig.Controller.Position.TotalMilliseconds));
                Thread.Sleep(350);
                if (snap)
                {
                    // What the engine does next: the clock into the middle of the frame the screen was named last.
                    var named = rig.Screen.LastName;
                    from = Stopwatch.GetTimestamp();
                    rig.SetDiscarding(false);
                    rig.Controller.Position = TimeSpan.FromSeconds((named + 0.5) / fps);
                    events.Add(("snap", from, Stopwatch.GetTimestamp(), named));
                    Thread.Sleep(350);
                }

                from = Stopwatch.GetTimestamp();
                rig.SetDiscarding(false);
                rig.Controller.Resume();
                events.Add(("resume", from, Stopwatch.GetTimestamp(), rig.Controller.Position.TotalMilliseconds));
            }

            from = Stopwatch.GetTimestamp();
            rig.Controller.Pause();
            events.Add(("pause", from, Stopwatch.GetTimestamp(), rig.Controller.Position.TotalMilliseconds));
            heldUp?.Rest();
            freezer?.Rest();
            gate?.Dispose();
            Thread.Sleep(400);
            foreach (var stall in (heldUp?.Stalls ?? []).Skip(stallsBefore))
            {
                events.Add(("stall-gc", stall.From, stall.To, Stopwatch.GetElapsedTime(stall.From, stall.To).TotalMilliseconds));
            }

            foreach (var stall in (freezer?.Stalls ?? []).Skip(freezesBefore))
            {
                events.Add(("stall-freeze", stall.From, stall.To, Stopwatch.GetElapsedTime(stall.From, stall.To).TotalMilliseconds));
            }

            foreach (var stall in gate?.Stalls ?? [])
            {
                events.Add(("stall-gate", stall.From, stall.To, Stopwatch.GetElapsedTime(stall.From, stall.To).TotalMilliseconds));
            }

            foreach (var probe in new[] { rig.Screen, rig.Camera })
            {
                var copies = probe.Read();
                foreach (var copy in copies)
                {
                    writer.WriteLine(string.Create(
                        CultureInfo.InvariantCulture,
                        $"copy,{pass},{probe.Label},{copy.Index},{Ms(copy.StartedAt):0.000},{copy.PositionTicks / 10000.0:0.0000},{copy.ControllerTicks / 10000.0:0.0000},{Stopwatch.GetElapsedTime(copy.StartedAt, copy.ReadAt).TotalMilliseconds:0.000},{Stopwatch.GetElapsedTime(copy.ReadAt, copy.GateAt).TotalMilliseconds:0.000},{Stopwatch.GetElapsedTime(copy.GateAt, copy.CopiedAt).TotalMilliseconds:0.000},{copy.PositionAfterTicks / 10000.0:0.0000},{Ms(copy.ReturnedAt):0.000},{copy.GcPauseTicks / 10000.0:0.000},{copy.GcPauseAtReturnTicks / 10000.0:0.000},{copy.Truth},{copy.Name},{copy.NameAfter},{copy.Overlap},{copy.Fps}"));
                }

                foreach (var skipped in probe.TakeSkipped())
                {
                    events.Add(("skipped-" + probe.Label, skipped.At, skipped.At, skipped.PositionTicks / 10000.0));
                }

                totals.Add(probe.Label, copies, events);
                if (probe.Lost > 0)
                {
                    _report.Line($"  pass {pass}, {probe.Label}: {probe.Lost} copies had no place left to be kept");
                }
            }

            foreach (var (what, at, to, value) in events.OrderBy(e => e.From))
            {
                writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{what},{pass},,,{Ms(at):0.000},{value:0.0000},,,,,,{Ms(to):0.000},,,,,,,"));
            }

            writer.Flush();
            _report.Line($"  pass {pass}: {totals.LastPass}");
        }

        _report.Line();
        foreach (var line in totals.Summary())
        {
            _report.Line("  " + line);
        }

        if (heldUp is not null)
        {
            _report.Line("  " + heldUp.Describe());
        }

        if (freezer is not null)
        {
            _report.Line("  " + freezer.Describe());
        }

        _report.Line($"  every copy is in {path}");
    }

    /// <summary>One copy of a frame: when, what the player said, and what the texture then held.</summary>
    private readonly record struct NamedCopy(
        int Index,
        int Fps,
        long StartedAt,
        long PositionTicks,
        long ControllerTicks,
        long ReadAt,
        long GateAt,
        long CopiedAt,
        long PositionAfterTicks,
        long ReturnedAt,
        long GcPauseTicks,
        long GcPauseAtReturnTicks,
        int Overlap,
        int Truth)
    {
        /// <summary>The frame that contains the position the player reported when the callback began: what the engine calls the frame.</summary>
        public int Name => (int)Math.Floor((PositionTicks * Fps / 10_000_000.0) + 1e-6);

        public int NameAfter => (int)Math.Floor((PositionAfterTicks * Fps / 10_000_000.0) + 1e-6);
    }

    /// <summary>What the passes of one run add up to.</summary>
    private sealed class NameTotals
    {
        private readonly SortedDictionary<string, SortedDictionary<int, int>> _running = [];
        private readonly SortedDictionary<string, SortedDictionary<int, int>> _stopped = [];
        private readonly SortedDictionary<int, int> _restOff = [];
        private readonly SortedDictionary<int, int> _afterStop = [];
        private int _pauses;

        public string LastPass { get; private set; } = string.Empty;

        public void Add(string label, NamedCopy[] copies, List<(string What, long From, long To, double Value)> events)
        {
            // Whether the clock ran when a copy began, from the director's own pauses and resumes.
            var changes = events.Where(e => e.What is "pause" or "resume").OrderBy(e => e.From).ToList();
            bool Running(long at)
            {
                var running = false;
                foreach (var change in changes)
                {
                    if (change.To > at)
                    {
                        break;
                    }

                    running = change.What == "resume";
                }

                return running;
            }

            var wrongRunning = 0;
            var counted = 0;
            foreach (var copy in copies)
            {
                if (copy.Truth == FrameCode.Unreadable)
                {
                    continue;
                }

                var running = Running(copy.StartedAt);
                var table = running ? _running : _stopped;
                if (!table.TryGetValue(label, out var byOffset))
                {
                    table[label] = byOffset = [];
                }

                var off = copy.Name - copy.Truth;
                byOffset[off] = byOffset.GetValueOrDefault(off) + 1;
                counted += running ? 1 : 0;
                wrongRunning += running && off != 0 ? 1 : 0;
            }

            // Each pause: the last copy that began before the clock was stopped, its name
            // against what it held; and how many copies began after it.
            var restWrong = 0;
            var pauses = 0;
            for (var index = 0; index < changes.Count; index++)
            {
                if (changes[index].What != "pause")
                {
                    continue;
                }

                var until = index + 1 < changes.Count ? changes[index + 1].From : long.MaxValue;
                var before = copies.LastOrDefault(copy => copy.StartedAt < changes[index].From && copy.Truth != FrameCode.Unreadable);
                var after = copies.Count(copy => copy.StartedAt >= changes[index].From && copy.StartedAt < until);
                if (before.StartedAt == 0)
                {
                    continue;
                }

                pauses++;
                var off = before.Name - before.Truth;
                _restOff[off] = _restOff.GetValueOrDefault(off) + 1;
                restWrong += off != 0 ? 1 : 0;
                _afterStop[after] = _afterStop.GetValueOrDefault(after) + 1;
            }

            _pauses += pauses;
            LastPass = (LastPass.Length > 0 && label != "screen" ? LastPass + "; " : string.Empty)
                + $"{label}: {counted} copies while the clock ran, {wrongRunning} named another frame than they held; {pauses} pauses, the last copy before {restWrong} of them named another frame than it held";
        }

        public IEnumerable<string> Summary()
        {
            static string Table(SortedDictionary<int, int> byOffset) =>
                string.Join(", ", byOffset.Select(pair => $"{(pair.Key == 0 ? "the same frame" : pair.Key > 0 ? $"{pair.Key} ahead" : $"{-pair.Key} behind")}: {pair.Value}"));

            foreach (var (label, byOffset) in _running)
            {
                yield return $"{label}, copies that began while the clock ran: the name against the frame held: {Table(byOffset)}";
            }

            foreach (var (label, byOffset) in _stopped)
            {
                yield return $"{label}, copies that began while the clock stood: {Table(byOffset)}";
            }

            yield return $"{_pauses} pauses over both clips: the last copy that began before the clock was stopped: {Table(_restOff)}";
            yield return $"copies that began after the clock was stopped, by pause: {string.Join(", ", _afterStop.Select(pair => $"{pair.Key} copies: {pair.Value}"))}";
        }
    }

    /// <summary>A device, a clock and a player for each clip, as the engine sets them up, with nothing of the engine.</summary>
    private sealed class NameRig : IDisposable
    {
        public NameRig(TestFolder folder, ClipSpec screen)
        {
            Device = StudioGraphicsDevice.CreateHardware();
            Controller = new MediaTimelineController { ClockRate = 1.0, Duration = TimeSpan.FromSeconds(screen.Seconds) };
            Screen = new NameProbe("screen", folder.Paths.ScreenPath, screen, 1280, 720, Device, Controller);
            Camera = new NameProbe("camera", folder.Paths.CameraPath!, TestMedia.Camera, 640, 360, Device, Controller);
        }

        public StudioGraphicsDevice Device { get; }

        public MediaTimelineController Controller { get; }

        public NameProbe Screen { get; }

        public NameProbe Camera { get; }

        public bool Open(out string? problem)
        {
            Screen.Start();
            Camera.Start();
            var watch = Stopwatch.StartNew();
            while ((Screen.Count == 0 || Camera.Count == 0) && Screen.Failure is null && Camera.Failure is null && watch.ElapsedMilliseconds < 10_000)
            {
                Thread.Sleep(5);
            }

            problem = Screen.Failure ?? Camera.Failure ?? (Screen.Count == 0 || Camera.Count == 0 ? "no first frame within ten seconds" : null);
            return problem is null;
        }

        /// <summary>Puts the stopped clock at a time in the recording, waits for the players, and starts counting copies anew.</summary>
        public void Rewind(double seconds)
        {
            // By way of another place, so that both players have something to answer. Both are
            // in the middle of a frame at 30 and at 60 frames a second.
            Controller.Position = TimeSpan.FromSeconds(seconds + 1 + (1.0 / 120));
            Thread.Sleep(400);
            Controller.Position = TimeSpan.FromSeconds(seconds + (1.0 / 120));
            Thread.Sleep(500);
            Screen.Reset();
            Camera.Reset();
        }

        /// <summary>From now on a frame the players announce is not copied, or is again.</summary>
        public void SetDiscarding(bool discarding)
        {
            Screen.Discard = discarding;
            Camera.Discard = discarding;
        }

        public void Dispose()
        {
            Screen.Dispose();
            Camera.Dispose();
            Thread.Sleep(50);
            Device.Dispose();
        }
    }

    /// <summary>
    /// Takes the device's lock for a while, again and again, as a render thread that is slow to
    /// draw does. A player that has a frame to hand over waits for it.
    /// </summary>
    private sealed class GateStalls : IDisposable
    {
        private readonly List<(long From, long To)> _stalls = [];
        private readonly Thread _thread;
        private volatile bool _stop;

        public GateStalls(StudioGraphicsDevice device, int shortest, int longest, int shortestGap, int longestGap, int seed)
        {
            var random = new Random(seed);
            _thread = new Thread(() =>
            {
                while (!_stop)
                {
                    var gap = random.Next(shortestGap, longestGap + 1);
                    for (var waited = 0; waited < gap && !_stop; waited += 5)
                    {
                        Thread.Sleep(5);
                    }

                    if (_stop)
                    {
                        return;
                    }

                    var hold = random.Next(shortest, longest + 1);
                    lock (device.Gate)
                    {
                        var from = Stopwatch.GetTimestamp();
                        Thread.Sleep(hold);
                        lock (_stalls)
                        {
                            _stalls.Add((from, Stopwatch.GetTimestamp()));
                        }
                    }
                }
            })
            {
                IsBackground = true,
                Name = "StudioPreviewCheck.GateStalls",
            };
            _thread.Start();
        }

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

        public void Dispose()
        {
            _stop = true;
            _thread.Join(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>
    /// One muted frame-server player whose every frame is copied the way the engine copies it:
    /// the position is read when the callback begins, the copy is made with the device's lock
    /// held. The strip with the frame number is then copied on, inside the graphics device, into
    /// a small texture of that copy's own.
    /// </summary>
    private sealed class NameProbe : IDisposable
    {
        private const int Capacity = 900;

        private readonly ClipSpec _spec;
        private readonly StudioGraphicsDevice _device;
        private readonly MediaTimelineController _controller;
        private readonly MediaPlaybackSession _session;
        private readonly ID3D11Texture2D _texture;
        private readonly IDirect3DSurface _surface;
        private readonly ID3D11Texture2D[] _strips = new ID3D11Texture2D[Capacity];
        private readonly NamedCopy[] _copies = new NamedCopy[Capacity];
        private readonly List<(long At, long PositionTicks)> _skipped = [];
        private readonly Box _stripBox;
        private readonly int _stripWidth;
        private readonly int _stripHeight;
        private readonly ClipMap _stripMap;
        private int _count;
        private int _lost;
        private int _inFlight;
        private int _lastName;
        private volatile bool _closed;
        private volatile string? _failure;

        public NameProbe(string label, string path, ClipSpec spec, int width, int height, StudioGraphicsDevice device, MediaTimelineController controller)
        {
            Label = label;
            _spec = spec;
            _device = device;
            _controller = controller;
            _texture = device.CreateRenderTexture(width, height);
            _surface = WgcInterop.CreateDirect3DSurface(_texture);
            var map = ClipMap.Scaled(spec, width, height);
            var bounds = FrameCode.Bounds(spec, map, width, height);
            _stripBox = new Box(bounds.X, bounds.Y, 0, bounds.X + bounds.Width, bounds.Y + bounds.Height, 1);
            _stripWidth = bounds.Width;
            _stripHeight = bounds.Height;
            _stripMap = map.Offset(-bounds.X, -bounds.Y);
            for (var index = 0; index < Capacity; index++)
            {
                _strips[index] = device.Device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)bounds.Width,
                    Height = (uint)bounds.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource,
                    CPUAccessFlags = CpuAccessFlags.None,
                    MiscFlags = ResourceOptionFlags.None,
                });
            }

            // As the engine makes its players, and never audible.
            Player = new MediaPlayer { AutoPlay = false, IsMuted = true, Volume = 0, IsVideoFrameServerEnabled = true };
            Player.CommandManager.IsEnabled = false;
            Player.TimelineController = controller;
            _session = Player.PlaybackSession;
            Source = MediaSource.CreateFromUri(new Uri(path));
            Player.VideoFrameAvailable += OnFrame;
            Player.MediaFailed += (_, e) => _failure ??= $"{e.Error} 0x{e.ExtendedErrorCode?.HResult ?? 0:X8}";
        }

        public string Label { get; }

        public MediaPlayer Player { get; }

        public MediaSource Source { get; }

        /// <summary>A frame the player announces is not copied while this is set.</summary>
        public volatile bool Discard;

        /// <summary>Copies made since the last <see cref="Reset"/>.</summary>
        public int Count => Volatile.Read(ref _count);

        /// <summary>Copies there was no place left to keep the strip of.</summary>
        public int Lost => Volatile.Read(ref _lost);

        /// <summary>The frame the position named at the latest copy.</summary>
        public int LastName => Volatile.Read(ref _lastName);

        public string? Failure => _failure;

        public void Start() => Player.Source = Source;

        /// <summary>Forgets the copies made so far. Only while the clock stands and no frame is on its way.</summary>
        public void Reset()
        {
            lock (_device.Gate)
            {
                Volatile.Write(ref _count, 0);
                Volatile.Write(ref _lost, 0);
                lock (_skipped)
                {
                    _skipped.Clear();
                }
            }
        }

        /// <summary>The frames that were announced and not copied since the last call: when, and the position then.</summary>
        public (long At, long PositionTicks)[] TakeSkipped()
        {
            lock (_skipped)
            {
                var taken = _skipped.ToArray();
                _skipped.Clear();
                return taken;
            }
        }

        /// <summary>The copies since the last <see cref="Reset"/>, each with the frame its strip shows. Waits for the graphics device.</summary>
        public NamedCopy[] Read()
        {
            var pixels = new byte[_stripWidth * _stripHeight * 4];
            lock (_device.Gate)
            {
                var count = Math.Min(Count, Capacity);
                var copies = new NamedCopy[count];
                for (var index = 0; index < count; index++)
                {
                    _device.ReadTexture(_strips[index], 0, pixels);
                    copies[index] = _copies[index] with { Truth = FrameCode.Decode(pixels, _stripWidth, _stripHeight, _spec, _stripMap) };
                }

                return copies;
            }
        }

        public void Dispose()
        {
            _closed = true;
            try
            {
                Player.TimelineController = null;
                Player.Source = null;
            }
            catch (Exception)
            {
                // A failed player may refuse.
            }

            Player.Dispose();
            Source.Dispose();
            lock (_device.Gate)
            {
                (_surface as IDisposable)?.Dispose();
                _texture.Dispose();
                foreach (var strip in _strips)
                {
                    strip.Dispose();
                }
            }
        }

        private void OnFrame(MediaPlayer sender, object args)
        {
            // In the engine's order: the time, the position, the lock, the copy.
            var startedAt = Stopwatch.GetTimestamp();
            var overlap = Interlocked.Increment(ref _inFlight);
            try
            {
                var position = _session.Position.Ticks;
                var gcPause = GC.GetTotalPauseDuration().Ticks;
                if (Discard)
                {
                    lock (_skipped)
                    {
                        _skipped.Add((startedAt, position));
                    }

                    return;
                }

                var controller = _controller.Position.Ticks;
                var readAt = Stopwatch.GetTimestamp();
                long gateAt;
                long copiedAt;
                int index;
                lock (_device.Gate)
                {
                    gateAt = Stopwatch.GetTimestamp();
                    if (_closed)
                    {
                        return;
                    }

                    sender.CopyFrameToVideoSurface(_surface);
                    copiedAt = Stopwatch.GetTimestamp();
                    index = _count;
                    if (index < Capacity)
                    {
                        _device.Context.CopySubresourceRegion(_strips[index], 0, 0, 0, 0, _texture, 0, _stripBox);
                    }
                }

                var after = _session.Position.Ticks;
                var gcAtReturn = GC.GetTotalPauseDuration().Ticks;
                var returnedAt = Stopwatch.GetTimestamp();
                if (index < Capacity)
                {
                    _copies[index] = new NamedCopy(index, _spec.Fps, startedAt, position, controller, readAt, gateAt, copiedAt, after, returnedAt, gcPause, gcAtReturn, overlap, FrameCode.Unreadable);
                    Volatile.Write(ref _lastName, _copies[index].Name);
                    Volatile.Write(ref _count, index + 1);
                }
                else
                {
                    Interlocked.Increment(ref _lost);
                }
            }
            catch (Exception ex)
            {
                _failure ??= $"copy failed 0x{ex.HResult:X8}";
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }
}
