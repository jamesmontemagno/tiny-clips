using System.Diagnostics;
using System.Globalization;
using TinyClips.Core.Studio;

namespace TinyClips.Tools.StudioRenderCheck;

/// <summary>Thrown by a check to fail at once; most expectations are collected with <see cref="CheckContext.Expect"/> instead.</summary>
internal sealed class CheckFailedException(string message) : Exception(message);

/// <summary>What one running check reports.</summary>
internal sealed class CheckContext(string name, Harness harness)
{
    private readonly List<string> _problems = [];
    private readonly List<string> _notes = [];

    public string Name { get; } = name;

    public Harness Harness { get; } = harness;

    public IReadOnlyList<string> Problems => _problems;

    public IReadOnlyList<string> Notes => _notes;

    /// <summary>Set when the check measured a limitation that is known and reported, not a regression.</summary>
    public string? KnownLimitation { get; private set; }

    /// <summary>
    /// Records that what the check measured is a known limitation of the platform. The check is
    /// then reported as KNOWN, not as OK: it does not pass, and it does not fail the run.
    /// </summary>
    public void Known(string limitation) => KnownLimitation = limitation;

    /// <summary>Records a failed expectation and carries on, so one run shows everything that is wrong.</summary>
    public bool Expect(bool condition, string problem)
    {
        if (!condition)
        {
            _problems.Add(problem);
        }

        return condition;
    }

    public void Fail(string problem) => _problems.Add(problem);

    /// <summary>A line printed under the check.</summary>
    public void Note(string line) => _notes.Add(line);

    /// <summary>A measured number: printed under the check and again in the summary at the end.</summary>
    public void Measure(string line)
    {
        _notes.Add(line);
        Harness.Measurements.Add($"{Name}: {line}");
    }

    /// <summary>A folder of this check's own inside the run's folder.</summary>
    public string Folder(string? suffix = null)
    {
        var safe = string.Concat((Name + (suffix is null ? string.Empty : "-" + suffix)).Select(c => char.IsLetterOrDigit(c) ? c : '-'));
        var path = Path.Combine(Harness.Root, safe);
        Directory.CreateDirectory(path);
        return path;
    }
}

/// <summary>Runs checks, prints one line for each, and remembers which failed.</summary>
internal sealed class Harness
{
    private readonly HashSet<string>? _only;
    private readonly string? _match;

    public Harness(string root, IEnumerable<string>? only, string? match = null)
    {
        Root = root;
        _match = match;
        _only = only is null ? null : new HashSet<string>(only, StringComparer.OrdinalIgnoreCase);
        Clips = new TestClips(Path.Combine(root, "clips"));
    }

    public string Root { get; }

    /// <summary>Whether the run's files stay when every check passes, so a check may as well save what it looked at.</summary>
    public bool KeepsFiles { get; init; }

    public TestClips Clips { get; }

    public List<string> Failures { get; } = [];

    public List<string> Skipped { get; } = [];

    public List<string> Known { get; } = [];

    public List<string> Measurements { get; } = [];

    public int Passed { get; private set; }

    public bool Wants(string group) => _only is null || _only.Contains(group);

    public async Task Check(string group, string name, Func<CheckContext, Task> body)
    {
        if (!Wants(group) || (_match is not null && !name.Contains(_match, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var context = new CheckContext(name, this);
        var watch = Stopwatch.StartNew();
        try
        {
            await body(context).ConfigureAwait(false);
        }
        catch (CheckSkippedException ex)
        {
            Skipped.Add($"{name}: {ex.Message}");
            Console.WriteLine($"SKIP {name}: {ex.Message}");
            return;
        }
        catch (CheckFailedException ex)
        {
            context.Fail(ex.Message);
        }
        catch (Exception ex)
        {
            context.Fail($"{ex.GetType().Name}: {FirstLine(ex.Message)}");
        }

        var seconds = watch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);
        if (context.Problems.Count == 0 && context.KnownLimitation is { } limitation)
        {
            Known.Add($"{name}: {limitation}");
            Console.WriteLine($"KNOWN {name}: {limitation}");
        }
        else if (context.Problems.Count == 0)
        {
            Passed++;
            Console.WriteLine($"OK   {name} ({seconds} s)");
        }
        else
        {
            var shown = string.Join("; ", context.Problems.Take(4));
            var more = context.Problems.Count > 4 ? $"; and {context.Problems.Count - 4} more" : string.Empty;
            var line = $"FAIL {name}: {shown}{more}";
            Failures.Add(line);
            Console.WriteLine(line);
        }

        foreach (var note in context.Notes)
        {
            Console.WriteLine("       " + note);
        }
    }

    public Task Check(string group, string name, Action<CheckContext> body) =>
        Check(group, name, context =>
        {
            body(context);
            return Task.CompletedTask;
        });

    private static string FirstLine(string message)
    {
        var line = message.ReplaceLineEndings(" ").Trim();
        return line.Length > 300 ? line[..300] + "…" : line;
    }
}

/// <summary>A check that cannot run on this PC, with the reason.</summary>
internal sealed class CheckSkippedException(string message) : Exception(message);

/// <summary>A generated source clip and what the tool knows about it because it made it.</summary>
/// <param name="FrameTimes">For a clip with unevenly spaced frames: when each starts, in 100 ns units, and last of all when the clip ends.</param>
internal sealed record TestClip(string Path, ClipSpec Spec, uint Numerator, uint Denominator, int Frames, double AudioSeconds, ClipEncoding? Encoding = null, int AudioRate = Media.AudioRate, int AudioChannels = Media.AudioChannels, long[]? FrameTimes = null)
{
    public double FramesPerSecond => Numerator / (double)Denominator;

    public double DurationSeconds => FrameTimes is null ? Frames * (double)Denominator / Numerator : FrameTimes[^1] / (double)Media.TicksPerSecond;

    public bool HasAudio => AudioSeconds > 0;

    /// <summary>When frame <paramref name="index"/> starts, in 100 ns units.</summary>
    public long FrameTime(int index) => FrameTimes?[index] ?? Media.FrameTime(index, Numerator, Denominator);

    /// <summary>The number of the frame showing at <paramref name="seconds"/> of the clip's own time: the last one that has started.</summary>
    public int FrameAt(double seconds)
    {
        if (FrameTimes is null)
        {
            return (int)Math.Clamp(Math.Floor((seconds * Numerator / Denominator) + 1e-6), 0, Frames - 1);
        }

        var ticks = (long)Math.Round(seconds * Media.TicksPerSecond);
        var index = 0;
        while (index + 1 < Frames && FrameTimes[index + 1] <= ticks)
        {
            index++;
        }

        return index;
    }
}

/// <summary>The clips the checks export from. Each is written once, on first use.</summary>
internal sealed class TestClips(string folder)
{
    private readonly Dictionary<string, TestClip> _clips = [];

    public string Folder { get; } = folder;

    /// <summary>1920×1080, 30 fps, 6 s, with sound; tagged BT.709.</summary>
    public TestClip Screen => Get("screen-1080p30", () => Write("screen-1080p30", ClipSpec.Screen(1920, 1080), 30, 1, 180, audio: true));

    /// <summary>1280×720, 30 fps, 6 s, silent, without colour tags, as the recorder writes a camera.</summary>
    public TestClip Camera => Get("camera-720p30", () => Write("camera-720p30", ClipSpec.Camera(1280, 720), 30, 1, 180, audio: false, ClipEncoding.Recorder(720)));

    /// <summary>A camera recording that ends after 3 s.</summary>
    public TestClip ShortCamera => Get("camera-short", () => Write("camera-short", ClipSpec.Camera(1280, 720), 30, 1, 90, audio: false, ClipEncoding.Recorder(720)));

    /// <summary>1280×720 at 30000/1001 fps, 120 frames, with sound, without colour tags.</summary>
    public TestClip Screen2997 => Get("screen-720p2997", () => Write("screen-720p2997", ClipSpec.Screen(1280, 720), 30000, 1001, 120, audio: true, ClipEncoding.Recorder(720)));

    /// <summary>1280×720 at 25 fps, 2 s, with sound, without colour tags.</summary>
    public TestClip Screen25 => Get("screen-720p25", () => Write("screen-720p25", ClipSpec.Screen(1280, 720), 25, 1, 50, audio: true, ClipEncoding.Recorder(720)));

    /// <summary>1280×720 at 60 fps, 2.5 s, with sound, without colour tags.</summary>
    public TestClip Screen60 => Get("screen-720p60", () => Write("screen-720p60", ClipSpec.Screen(1280, 720), 60, 1, 150, audio: true, ClipEncoding.Recorder(720)));

    /// <summary>2560×1440, 30 fps, 20 s, with sound: the size the engine spike measured.</summary>
    public TestClip BigScreen => Get("screen-1440p30", () => Write("screen-1440p30", ClipSpec.Screen(2560, 1440), 30, 1, 600, audio: true));

    /// <summary>1280×720, 30 fps, 20 s, silent.</summary>
    public TestClip LongCamera => Get("camera-720p30-20s", () => Write("camera-720p30-20s", ClipSpec.Camera(1280, 720), 30, 1, 600, audio: false, ClipEncoding.Recorder(720)));

    /// <summary>
    /// 640×480, 30 fps, 3 s, with sound, in BT.601 without colour tags: what the recorder makes
    /// of a small window.
    /// </summary>
    public TestClip SmallScreen => Get("screen-480p30-bt601", () => Write("screen-480p30-bt601", ClipSpec.Screen(640, 480), 30, 1, 90, audio: true, ClipEncoding.Recorder(480)));

    /// <summary>A 640×480 camera, 3 s, in BT.601 without colour tags.</summary>
    public TestClip SmallCamera => Get("camera-480p30-bt601", () => Write("camera-480p30-bt601", ClipSpec.Camera(640, 480), 30, 1, 90, audio: false, ClipEncoding.Recorder(480)));

    /// <summary>1920×1080 HEVC, 3 s, with sound, as the recorder writes it when set to HEVC.</summary>
    public TestClip HevcScreen => Get("screen-1080p30-hevc", () => Write("screen-1080p30-hevc", ClipSpec.Screen(1920, 1080), 30, 1, 90, audio: true, ClipEncoding.Recorder(1080, hevc: true)));

    /// <summary>1280×720 HEVC camera, 3 s.</summary>
    public TestClip HevcCamera => Get("camera-720p30-hevc", () => Write("camera-720p30-hevc", ClipSpec.Camera(1280, 720), 30, 1, 90, audio: false, ClipEncoding.Recorder(720, hevc: true)));

    /// <summary>640×480 in BT.709 and tagged so: a small video whose tag disagrees with what its size suggests.</summary>
    public TestClip SmallScreen709 => Get("screen-480p30-bt709-tagged", () => Write("screen-480p30-bt709-tagged", ClipSpec.Screen(640, 480), 30, 1, 60, audio: true, new ClipEncoding(Bt601: false, Tagged: true)));

    /// <summary>1280×720 in BT.601 and tagged so: the same disagreement the other way round.</summary>
    public TestClip Screen601 => Get("screen-720p30-bt601-tagged", () => Write("screen-720p30-bt601-tagged", ClipSpec.Screen(1280, 720), 30, 1, 60, audio: true, new ClipEncoding(Bt601: true, Tagged: true)));
    /// <summary>1280×720, 3 s, with 44.1 kHz mono sound: a video from somewhere else.</summary>
    public TestClip MonoScreen => Get("screen-720p30-mono44", () => Write("screen-720p30-mono44", ClipSpec.Screen(1280, 720), 30, 1, 90, audio: true, ClipEncoding.Recorder(720), audioRate: 44100, audioChannels: 1));

    /// <summary>1280×720, 30 fps, 1 s, silent, with a field of fine stripes that shows how a picture was scaled down.</summary>
    public TestClip StripedScreen => Get("screen-720p30-stripes", () => Write("screen-720p30-stripes", ClipSpec.StripedScreen(1280, 720), 30, 1, 30, audio: false, ClipEncoding.Recorder(720)));

    /// <summary>1280×720, 3 s, with 32 kHz stereo sound: a rate the exporter's AAC encoder is not given.</summary>
    public TestClip Screen32k => Get("screen-720p30-32k", () => Write("screen-720p30-32k", ClipSpec.Screen(1280, 720), 30, 1, 90, audio: true, ClipEncoding.Recorder(720), audioRate: 32000, audioChannels: 2));

    /// <summary>1280×720, 3 s, with 32 kHz mono sound.</summary>
    public TestClip Screen32kMono => Get("screen-720p30-32k-mono", () => Write("screen-720p30-32k-mono", ClipSpec.Screen(1280, 720), 30, 1, 90, audio: true, ClipEncoding.Recorder(720), audioRate: 32000, audioChannels: 1));

    /// <summary>1280×720, 3 s, with 48 kHz 5.1 sound: the tone in the front left and, at half level, the front right.</summary>
    public TestClip SurroundScreen => Get("screen-720p30-surround", () => Write("screen-720p30-surround", ClipSpec.Screen(1280, 720), 30, 1, 90, audio: true, ClipEncoding.Recorder(720), audioRate: 48000, audioChannels: 6));

    /// <summary>
    /// 1280×720 with sound, 5 s, as a recording that dropped frames: a 30 fps grid with half a
    /// second missing, then two seconds missing, then every other frame, then frames a few
    /// milliseconds off the grid.
    /// </summary>
    public TestClip GappyScreen => Get("screen-720p-gaps", () =>
    {
        var times = new List<long>();
        for (var slot = 0; slot < 150; slot++)
        {
            var dropped = slot is >= 20 and < 35 || slot is >= 50 and < 110 || (slot is >= 115 and < 130 && slot % 2 == 1);
            if (!dropped)
            {
                var jitter = slot >= 132 ? (slot % 2 == 0 ? 70_000 : -50_000) : 0;
                times.Add(Media.FrameTime(slot, 30, 1) + jitter);
            }
        }

        times.Add(Media.FrameTime(150, 30, 1));
        return Write("screen-720p-gaps", ClipSpec.Screen(1280, 720), 30, 1, times.Count - 1, audio: true, ClipEncoding.Recorder(720), frameTimes: [.. times]);
    });

    public IEnumerable<TestClip> Written => _clips.Values;

    private TestClip Get(string name, Func<TestClip> create)
    {
        lock (_clips)
        {
            if (!_clips.TryGetValue(name, out var clip))
            {
                clip = create();
                _clips[name] = clip;
            }

            return clip;
        }
    }

    private TestClip Write(string name, ClipSpec spec, uint numerator, uint denominator, int frames, bool audio, ClipEncoding? encoding = null, int audioRate = Media.AudioRate, int audioChannels = Media.AudioChannels, long[]? frameTimes = null)
    {
        encoding ??= ClipEncoding.Tagged709;
        if (encoding.Hevc && !Media.HasEncoder(hevc: true, hardware: null))
        {
            throw new CheckSkippedException("this PC has no HEVC encoder to make the source clip with");
        }

        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, name + ".mp4");

        // Sound runs exactly as long as the picture.
        var seconds = frameTimes is null ? frames * (double)denominator / numerator : frameTimes[^1] / (double)Media.TicksPerSecond;
        Media.WriteClip(path, spec, numerator, denominator, frames, audio ? Tones.Synthesize(seconds, audioRate, audioChannels) : null, encoding, audioRate, audioChannels, frameTimes);
        return new TestClip(path, spec, numerator, denominator, frames, audio ? seconds : 0, encoding, audioRate, audioChannels, frameTimes);
    }
}

/// <summary>Builds the projects the checks render.</summary>
internal static class Projects
{
    public const double DefaultCameraOffset = 0.2;

    /// <summary>
    /// A project over generated clips, with a look chosen so the frame-number strips are easy to
    /// read back: gradient background, default card, a large circular camera bubble.
    /// </summary>
    public static StudioProject Create(TestClip screen, TestClip? camera, double cameraOffset = DefaultCameraOffset, double? cameraDuration = null) =>
        new()
        {
            Id = "00000000-0000-4000-8000-000000000001",
            Name = "Render check",
            Sources = new StudioSources
            {
                Screen = new StudioScreenSource
                {
                    File = screen.Path,
                    Width = screen.Spec.Width,
                    Height = screen.Spec.Height,
                    Duration = screen.DurationSeconds,
                    FrameRate = screen.FramesPerSecond,
                    External = true,
                },
                Camera = camera is null
                    ? null
                    : new StudioCameraSource
                    {
                        File = Path.GetFileName(camera.Path),
                        Width = camera.Spec.Width,
                        Height = camera.Spec.Height,
                        Duration = cameraDuration ?? camera.DurationSeconds,
                        StartOffset = cameraOffset,
                    },
            },
            Canvas = new StudioCanvas { Background = new StudioBackground { Style = StudioBackgroundStyle.Gradient, Primary = "#203060", Secondary = "#80C0E0" } },
            Camera = new StudioCameraStyle { Shape = StudioCameraShape.Circle, Mirror = true, BorderWidth = 0.004, BorderColor = "#FFFFFF" },
            Scenes = [new StudioScene { Layout = camera is null ? StudioLayout.Screen : StudioLayout.Bubble, Bubble = new StudioBubble { Size = 0.36 } }],
            Overlays = new StudioOverlays { Clicks = new StudioClickOverlay { Enabled = true }, Branding = false },
        };

    /// <summary>Paths as the store would build them, with the project folder at <paramref name="folder"/>.</summary>
    public static StudioProjectPaths Paths(string folder, TestClip screen, TestClip? camera) =>
        new(
            "00000000-0000-4000-8000-000000000001",
            folder,
            Path.Combine(folder, "project.json"),
            screen.Path,
            camera?.Path,
            Path.Combine(folder, "events.json"),
            Path.Combine(folder, "poster.jpg"));

    public static StudioEvents Events(TestClip screen) =>
        new() { Capture = new StudioCaptureInfo { Width = screen.Spec.Width, Height = screen.Spec.Height, Scale = 1 } };

    /// <summary>A layer rectangle with its edges on whole pixels, as the format lets a renderer draw it.</summary>
    public static StudioFrameRect Aligned(StudioFrameRect rect)
    {
        var left = Math.Round(rect.X, MidpointRounding.AwayFromZero);
        var top = Math.Round(rect.Y, MidpointRounding.AwayFromZero);
        var right = Math.Round(rect.X + rect.Width, MidpointRounding.AwayFromZero);
        var bottom = Math.Round(rect.Y + rect.Height, MidpointRounding.AwayFromZero);
        return new StudioFrameRect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    /// <summary>Where the screen clip's pixels land in a rendered frame.</summary>
    public static ClipMap ScreenMap(StudioResolvedScreen screen, ClipSpec clip)
    {
        var rect = Aligned(screen.Rect);
        var scaleX = rect.Width / (screen.Source.Width * clip.Width);
        var scaleY = rect.Height / (screen.Source.Height * clip.Height);
        return new ClipMap(rect.X - (screen.Source.X * clip.Width * scaleX), rect.Y - (screen.Source.Y * clip.Height * scaleY), scaleX, scaleY);
    }

    /// <summary>Where the camera clip's pixels land, mirrored or not.</summary>
    public static ClipMap CameraMap(StudioResolvedCamera camera, ClipSpec clip)
    {
        var rect = Aligned(camera.Rect);
        var cropX = camera.Source.X * clip.Width;
        var cropY = camera.Source.Y * clip.Height;
        var scaleX = rect.Width / (camera.Source.Width * clip.Width);
        var scaleY = rect.Height / (camera.Source.Height * clip.Height);
        return camera.Mirror
            ? new ClipMap(rect.X + rect.Width + (cropX * scaleX), rect.Y - (cropY * scaleY), -scaleX, scaleY)
            : new ClipMap(rect.X - (cropX * scaleX), rect.Y - (cropY * scaleY), scaleX, scaleY);
    }
}
