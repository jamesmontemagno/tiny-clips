using System.Drawing;
using System.Runtime.InteropServices;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Preview;
using TinyClips.Core.Studio.Rendering;
using Vortice.Direct3D11;

namespace TinyClips.Tools.StudioRenderCheck;

internal static class TimelineChecks
{
    public static async Task Run(Harness harness)
    {
        if (!harness.Wants("timeline")) return;
        foreach (var join in Enum.GetValues<StudioClipTransition>())
        {
            await harness.Check("timeline", $"timeline: decoded pictures, sound, posters and preview through {join}", context => Joins(context, join));
        }
        await harness.Check("timeline", "timeline: dissolve with software rendering and encoding", context => Joins(context, StudioClipTransition.CrossDissolve, warp: true));
        await harness.Check("timeline", "timeline: native camera offset and rotated Mac test card", NativeAndRotated);
        await harness.Check("timeline", "timeline: cancellation preserves destination and releases media", Cancellation);
    }

    private static (StudioProject Project, StudioProjectPaths Paths, TestClip Screen, TestClip Other) Setup(CheckContext context, StudioClipTransition join)
    {
        var screen = context.Harness.Clips.SmallScreen;
        var other = context.Harness.Clips.Camera;
        var folder = context.Folder();
        File.Copy(screen.Path, Path.Combine(folder, "first.mp4"), true);
        // Keep the independently generated green camera test card, but give its screen stream sound.
        var secondPath = Path.Combine(folder, "second.mp4");
        Media.WriteClip(secondPath, other.Spec, 30, 1, 120, Tones.Synthesize(4, Media.AudioRate));
        var project = StudioTimelineEditing.ToTimelineProject(Projects.Create(screen, null) with
        {
            Sources = new StudioSources { Screen = new StudioScreenSource { File = "first.mp4", Width = 640, Height = 480, Duration = screen.DurationSeconds, FrameRate = 30 } },
            Canvas = new StudioCanvas { Padding = 0, Background = new StudioBackground { Style = StudioBackgroundStyle.None } },
            Screen = new StudioScreenStyle { Shadow = 0, CornerRadius = 0 },
            Edits = new StudioEdits { TrimEnd = 2 },
            Audio = new StudioAudio { Volume = 0.8 },
        });
        project = project with
        {
            MediaSources = [project.MediaSources![0], new StudioMediaSource
            {
                Id = "other", Kind = StudioMediaSourceKind.FlatVideo,
                Sources = new StudioSources { Screen = new StudioScreenSource { File = "second.mp4", Width = other.Spec.Width, Height = other.Spec.Height, Duration = 4, FrameRate = 30 } },
            }],
            Timeline = project.Timeline! with
            {
                Clips = [project.Timeline.Clips[0] with
                {
                    Overlays = new StudioOverlays { Annotations = [new StudioAnnotation
                    {
                        Id = "redaction", Kind = StudioAnnotationKinds.Redact, Attach = StudioAnnotationAttach.Frame,
                        Start = 0, End = 2, X = .5, Y = .72, Width = .2, Height = .12,
                        RedactStyle = StudioRedactStyle.Solid, Fill = "#000000",
                    }] },
                }, new StudioTimelineClip
                {
                    Id = "second", SourceId = "other", SourceStart = 1, SourceEnd = 3, TransitionIn = join,
                    Audio = new StudioAudio { Volume = 0.4 },
                    Overlays = new StudioOverlays(),
                }],
            },
        };
        return (project, Projects.Paths(folder, screen, null), screen, other);
    }

    private static async Task Joins(CheckContext context, StudioClipTransition join, bool warp = false)
    {
        var (project, paths, screen, other) = Setup(context, join);
        var map = StudioTimelineProjection.Map(project);
        var duration = join == StudioClipTransition.CrossDissolve ? 3.5 : 4;
        var incoming = join == StudioClipTransition.CrossDissolve ? 1.5 : 2;
        var output = Path.Combine(paths.ProjectDirectory, "joined.mp4");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var result = await new StudioExporter().ExportAsync(project, new StudioEvents(), paths, output,
            new StudioExportOptions(LongSideLimit: 640,
                DevicePreference: warp ? StudioRenderDevicePreference.WarpOnly : StudioRenderDevicePreference.HardwareWithWarpFallback),
            cancellationToken: timeout.Token);
        context.Expect(result.Width == 640 && result.Height == 480 && result.FrameCount == (int)(duration * 30), $"canvas/fps/duration: {result.Width}x{result.Height}, {result.FrameCount} frames");
        var decoded = new Dictionary<int, Picture>();
        Media.ReadFrames(output, true, frame => { decoded[frame.Index] = frame.Picture; return true; });
        foreach (var index in new[] { 15, 46, 52, 57, 59, 60, 63, 68, 84 })
        {
            var time = (index + 0.5) / 30;
            var picture = decoded[index];
            var expected = time < incoming ? screen.Spec.Background : other.Spec.Background;
            if (join == StudioClipTransition.CrossDissolve && time is >= 1.5 and < 2)
                expected = screen.Spec.Background.Mix(other.Spec.Background, (time - 1.5) / 0.5);
            if (join is StudioClipTransition.FadeThroughBlack or StudioClipTransition.FadeThroughWhite && time is >= 1.75 and < 2.25)
                expected = expected.Mix(join == StudioClipTransition.FadeThroughWhite ? new Rgb(255, 255, 255) : new Rgb(0, 0, 0), 1 - Math.Abs(time - 2) / 0.25);
            var got = picture.Average(480, 360, 3);
            context.Expect(got.Distance(expected) < 14, $"frame {index}: color {got}, expected {expected}");
            var redaction = time < incoming ? new Rgb(0, 0, 0) : other.Spec.Background;
            if (join == StudioClipTransition.CrossDissolve && time is >= 1.5 and < 2)
                redaction = new Rgb(0, 0, 0).Mix(other.Spec.Background, (time - 1.5) / .5);
            if (join is StudioClipTransition.FadeThroughBlack or StudioClipTransition.FadeThroughWhite && time is >= 1.75 and < 2.25)
                redaction = redaction.Mix(join == StudioClipTransition.FadeThroughWhite ? new Rgb(255, 255, 255) : new Rgb(0, 0, 0), 1 - Math.Abs(time - 2) / .25);
            context.Expect(picture.Average(320, 346, 2).Distance(redaction) < 15, $"frame {index}: outgoing redaction remains local before/during/after join");
        }
        // The coded frames distinguish a new source and its trim, not just its background.
        context.Expect(FrameCode.Decode(decoded[15], screen.Spec, ClipMap.Identity) == 15, "outgoing source frame is 15");
        var secondIndex = 84;
        var local = StudioTimelineProjection.ProjectForClip(project, "second");
        var resolved = StudioLayoutResolver.Resolve(local, new StudioEvents(), 1 + (secondIndex + .5) / 30 - incoming, 640, 480);
        var decodedNumber = FrameCode.Decode(decoded[secondIndex], other.Spec, Projects.ScreenMap(resolved.Screen!.Value, other.Spec));
        context.Expect(decodedNumber == (int)Math.Floor((1 + (secondIndex + .5) / 30 - incoming) * 30), $"incoming coded frame {decodedNumber} has its own trim/time");

        var audio = Media.ReadAudio(output)!;
        double Expected(long sample)
        {
            var t = sample / (double)audio.SampleRate;
            var firstGain = t < 2 ? 0.8 : 0;
            var secondGain = t >= incoming ? 0.4 : 0;
            if (join == StudioClipTransition.CrossDissolve && t is >= 1.5 and < 2)
            {
                firstGain *= (2 - t) / .5;
                secondGain *= (t - 1.5) / .5;
            }
            return Tones.Value(sample, audio.SampleRate) * firstGain
                + Tones.Value((long)Math.Round((t - incoming + 1) * audio.SampleRate), audio.SampleRate) * secondGain;
        }
        var alignment = Tones.Align(audio.Left, Expected, Math.Min(audio.Length, (long)(duration * audio.SampleRate)), 240);
        context.Expect(Math.Abs(alignment.Lag) <= 4 && alignment.Match > .985, $"decoded sound lag={alignment.Lag}, correlation={alignment.Match:0.0000}");

        var poster = Path.Combine(paths.ProjectDirectory, "poster.jpg");
        await new StudioExporter().WritePosterAsync(project, new StudioEvents(), paths, poster, 640, 52 / 30d, timeout.Token);
        using (var bitmap = new Bitmap(poster))
        {
            var pixel = bitmap.GetPixel(480, 360);
            context.Expect(new Rgb(pixel.R, pixel.G, pixel.B).Distance(decoded[52].Average(480, 360, 2)) < 15, "poster matches the composed join frame");
        }

        // Same public factory and same surface as the app; zero volume still exercises the audio clock.
        await using var preview = await new StudioPreviewFactory(new StudioPreviewOptions { ZeroVolume = true, SoftwareDevice = warp }).OpenAsync(project, new StudioEvents(), paths, timeout.Token);
        var engine = (IStudioSurfacePreview)preview;
        var surface = new Surface();
        engine.AttachSurface(surface);
        foreach (var index in new[] { 15, 52, 63, 84, 0, 59 })
        {
            var before = surface.Count;
            preview.Seek(index / 30d);
            context.Expect(Math.Abs(preview.Position - index / 30d) < .001, "Seek immediately reports requested project position");
            await Wait(() => surface.Count > before, timeout.Token);
            // A queued preceding frame may finish immediately before the seek is admitted.
            await Task.Delay(80, timeout.Token);
            var got = surface.Picture!.Average(480, 360, 3);
            context.Expect(got.Distance(decoded[index].Average(480, 360, 3)) < 15, $"preview frame {index} agrees with export");
        }
        preview.Seek(1.4);
        preview.Play();
        await Wait(() => preview.Position > 2.4, timeout.Token);
        preview.Pause();
        var paused = preview.Position;
        await Task.Delay(150, timeout.Token);
        context.Expect(!preview.IsPlaying && preview.Position == paused, "pause freezes project clock across cut");
        preview.UpdateProject(project with { Timeline = project.Timeline! with
        {
            Clips = [project.Timeline.Clips[0], project.Timeline.Clips[1] with { Audio = new StudioAudio { Muted = true } }],
        } });
        await Task.Delay(150, timeout.Token);
        context.Expect(preview.Position == paused, "updating clip state preserves project position");
        preview.Seek(duration - .2);
        preview.Play();
        await Wait(() => !preview.IsPlaying, timeout.Token);
        context.Expect(preview.Position >= duration - .07, "playback stops at project end");
        engine.DetachSurface(surface);
        context.Note($"duration {result.DurationSeconds:0.000}s; decoded audio correlation {alignment.Match:0.0000}; preview crossed join and stopped");
    }

    private static async Task NativeAndRotated(CheckContext context)
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "TimelineFixture");
        var project = StudioProjectJson.ReadProject(File.ReadAllText(Path.Combine(folder, "project.json")));
        var paths = new StudioProjectPaths("render-test", folder, Path.Combine(folder, "project.json"), "", null, "", "");
        var output = Path.Combine(context.Folder(), "mac-cards.mp4");
        try { await new StudioExporter().ExportAsync(project, new StudioEvents(), paths, output); }
        catch (StudioExportException ex) { context.Note(ex.ToString()); throw; }
        var frames = new Dictionary<int, Picture>();
        Media.ReadFrames(output, true, frame => { frames[frame.Index] = frame.Picture; return true; });
        context.Expect(frames.Count == 30 && frames[0].Width == 96 && frames[0].Height == 64, "root 96x64 30fps canvas and one second duration");
        context.Expect(frames[0].Average(48, 32, 2).Luma < 8, "camera is absent before its source offset");
        context.Expect(frames[6].Average(48, 32, 2).Luma > 15, "native camera decodes after its source offset");
        var nativeFrames = new Dictionary<int, Picture>();
        Media.ReadFrames(Path.Combine(folder, "native.mp4"), true, frame => { nativeFrames[frame.Index] = frame.Picture; return true; });
        context.Expect(frames[12].Average(48, 32, 2).Distance(nativeFrames[17].Average(32, 24, 2)) < 15,
            "camera time follows the native cut, 2x speed, and .125s source offset");
        context.Expect(frames[23].Average(6, 12, 2).Luma < 8, "rotated portrait card fits with sidebars");
        context.Expect(frames[23].Average(48, 12, 2).Luma > 10, "portrait picture remains visible");
        var sourceFrames = new Dictionary<int, Picture>();
        using (var graphics = StudioGraphicsDevice.Create())
        using (var source = StudioVideoSource.Open(Path.Combine(folder, "flat.mp4"), "rotation fixture", graphics, null))
            context.Note($"MF card {source.Width}x{source.Height}, rotation {source.RotationDegrees}; sidebar={frames[23].Average(6, 12, 2)} center={frames[23].Average(48, 12, 2)}");
        Media.ReadFrames(Path.Combine(folder, "flat.mp4"), true, frame => { sourceFrames[frame.Index] = frame.Picture; return true; });
        // 270-degree clockwise rotation: top-left output is top-right encoded picture.
        var raw = sourceFrames[46];
        context.Expect(frames[23].Average(37, 8, 1).Distance(raw.Average(55, 5, 1)) < 20, $"rotated top-left marker {frames[23].Average(37, 8, 1)} equals source top-right {raw.Average(55, 5, 1)}");
        var audio = Media.ReadAudio(output)!;
        var silent = audio.Left.Skip((int)(.3 * audio.SampleRate)).Take((int)(.15 * audio.SampleRate)).Select(value => Math.Abs((int)value)).Average();
        context.Expect(silent < 60, $"speed-changed native range is silent, mean {silent:0.0}");

        var native = project.Timeline!.Clips[0] with
        {
            Scenes = [new StudioScene { Layout = StudioLayout.Screen }],
            Overlays = new StudioOverlays { Clicks = new StudioClickOverlay { Enabled = true, Color = "#FFFFFF", Size = 16, StrokeWidth = 3 } },
        };
        var clicks = project with { Timeline = project.Timeline with { Clips = [native, project.Timeline.Clips[1]] } };
        using (var graphics = StudioGraphicsDevice.Create())
        using (var target = graphics.CreateRenderTexture(96, 64))
        {
            byte[] before;
            byte[] after;
            lock (graphics.Gate)
            {
                using (var renderer = new StudioTimelineRenderer(clicks, paths, graphics))
                {
                    renderer.Render(.15, target, 96, 64, StudioRenderQuality.Export, CancellationToken.None);
                    before = graphics.ReadTexture(target);
                }
                var quiet = clicks with { Timeline = clicks.Timeline! with
                {
                    Clips = [native with { Overlays = new StudioOverlays { Clicks = new StudioClickOverlay { Enabled = false } } }, clicks.Timeline.Clips[1]],
                } };
                using var quietRenderer = new StudioTimelineRenderer(quiet, paths, graphics);
                quietRenderer.Render(.15, target, 96, 64, StudioRenderQuality.Export, CancellationToken.None);
                after = graphics.ReadTexture(target);
            }
            context.Expect(Picture.MaxDifference(Picture.FromBgra(before, 96, 64), Picture.FromBgra(after, 96, 64)).Difference > 100,
                "native registry events, not legacy root events, draw the source-local click");
        }
    }

    private static async Task Cancellation(CheckContext context)
    {
        var (project, paths, _, _) = Setup(context, StudioClipTransition.CrossDissolve);
        var output = Path.Combine(paths.ProjectDirectory, "cancel.mp4");
        File.WriteAllText(output, "keep");
        using var cancel = new CancellationTokenSource();
        var progress = new CancellingProgress(cancel);
        try
        {
            await new StudioExporter().ExportAsync(project, new StudioEvents(), paths, output,
                new StudioExportOptions(LongSideLimit: 640), progress, cancel.Token);
            context.Fail("export was not cancelled");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        context.Expect(File.ReadAllText(output) == "keep", "cancel leaves existing destination unchanged");
        context.Expect(Directory.GetFiles(paths.ProjectDirectory, "*" + StudioRenderingMath.TemporaryExtension).Length == 0, "cancel removes partial export");
        foreach (var name in new[] { "first.mp4", "second.mp4" })
        {
            using var file = new FileStream(Path.Combine(paths.ProjectDirectory, name), FileMode.Open, FileAccess.Read, FileShare.None);
            context.Expect(file.Length > 0, $"cancel releases {name}");
        }
    }

    private static async Task Wait(Func<bool> condition, CancellationToken token)
    {
        var start = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            token.ThrowIfCancellationRequested();
            if (start.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Timeline preview did not advance.");
            await Task.Delay(10, token);
        }
    }

    private sealed class CancellingProgress(CancellationTokenSource source) : IProgress<double>
    {
        public void Report(double value) { if (value > .05) source.Cancel(); }
    }

    private sealed class Surface : IStudioPreviewSurface
    {
        private ID3D11Texture2D? _target;
        private ID3D11DeviceContext? _context;
        private ID3D11Texture2D? _staging;
        private byte[]? _pixels;
        private int _count;
        public Picture? Picture => _pixels is { } pixels ? StudioRenderCheck.Picture.FromBgra(pixels, 640, 480) : null;
        public int Count => Volatile.Read(ref _count);
        public bool NeedsConfigure => _target is null;
        public event EventHandler? Invalidated { add { } remove { } }
        public void Configure(ID3D11Device device)
        {
            _context = device.ImmediateContext;
            var description = new Texture2DDescription
            {
                Width = 640, Height = 480, MipLevels = 1, ArraySize = 1,
                Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
                SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
                Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            };
            _target = device.CreateTexture2D(description);
            description.Usage = ResourceUsage.Staging;
            description.BindFlags = BindFlags.None;
            description.CPUAccessFlags = CpuAccessFlags.Read;
            _staging = device.CreateTexture2D(description);
        }
        public ID3D11Texture2D? AcquireTarget(out int pixelWidth, out int pixelHeight)
        {
            pixelWidth = 640; pixelHeight = 480;
            return _target?.QueryInterface<ID3D11Texture2D>();
        }
        public void Present()
        {
            _context!.CopyResource(_staging!, _target!);
            var mapped = _context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            var pixels = new byte[640 * 480 * 4];
            try
            {
                for (var row = 0; row < 480; row++)
                    Marshal.Copy(mapped.DataPointer + checked((int)(row * mapped.RowPitch)), pixels, row * 640 * 4, 640 * 4);
            }
            finally { _context.Unmap(_staging!, 0); }
            Volatile.Write(ref _pixels, pixels);
            Interlocked.Increment(ref _count);
        }
        public void ReleaseDeviceResources()
        {
            _target?.Dispose(); _target = null;
            _staging?.Dispose(); _staging = null;
            _context?.Dispose(); _context = null;
        }
    }
}
