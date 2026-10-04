using System.Diagnostics;
using System.Globalization;
using TinyClips.Core.Models;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Rendering;

namespace TinyClips.Tools.StudioRenderCheck;

/// <summary>Runs an export through the public API and verifies the file it wrote.</summary>
internal static class Exporting
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    public static async Task<(StudioExportResult Result, ExportReading? Reading, double Seconds)> ExportAndVerify(
        CheckContext context,
        string label,
        StudioProject project,
        TestClip screen,
        TestClip? camera,
        int width,
        int height,
        uint rateNumerator,
        uint rateDenominator,
        bool expectAudio,
        StudioExportOptions? options = null,
        bool verify = true,
        StudioExporter? exporter = null,
        IProgress<double>? progress = null)
    {
        var folder = context.Folder();
        var output = Path.Combine(folder, label + ".mp4");
        using var timeout = new CancellationTokenSource(Timeout);
        var watch = Stopwatch.StartNew();
        var result = await (exporter ?? new StudioExporter()).ExportAsync(project, Projects.Events(screen), Projects.Paths(folder, screen, camera), output, options, progress, timeout.Token).ConfigureAwait(false);
        var seconds = watch.Elapsed.TotalSeconds;

        context.Expect(Directory.GetFiles(folder, "*" + StudioRenderingMath.TemporaryExtension).Length == 0, $"{label}: a temporary file was left behind");
        context.Expect(string.Equals(result.OutputPath, output, StringComparison.OrdinalIgnoreCase), $"{label}: the result names {result.OutputPath}");
        ExportReading? reading = null;
        if (verify)
        {
            var hevc = (options?.Codec ?? VideoCodec.H264) == VideoCodec.Hevc;
            reading = ExportVerifier.Verify(context, output, new ExportExpectation(project, screen, camera, width, height, rateNumerator, rateDenominator, expectAudio, hevc), label);
            if (reading is not null)
            {
                context.Expect(result.FrameCount == reading.FrameCount, $"{label}: the result says {result.FrameCount} frames, the file has {reading.FrameCount}");
                context.Expect(result.Width == width && result.Height == height, $"{label}: the result says {result.Width}×{result.Height}");
                context.Expect(result.HasAudio == expectAudio, $"{label}: the result says HasAudio = {result.HasAudio}");
            }
        }

        context.Note(string.Create(CultureInfo.InvariantCulture, $"{label}: {result.EncoderDescription}{(result.SoftwareRendering ? ", drawn by WARP" : string.Empty)}; exported in {seconds:0.00} s ({result.FrameCount / seconds:0} fps)"));
        return (result, reading, seconds);
    }
}

/// <summary>The exports of the brief, (a) to (j), and three more the design calls for.</summary>
internal static class ExportChecks
{
    private const int Hidden = FrameCode.Unreadable;

    public static async Task Run(Harness harness)
    {
        var clips = harness.Clips;

        await harness.Check("exports", "export (a): untrimmed, camera in the bubble", async context =>
        {
            // Size, rate and duration as a real project carries them: read from the file, as the app does.
            var probe = MediaFileProbe.Probe(clips.Screen.Path);
            var project = Projects.Create(clips.Screen, clips.Camera);
            project = project with { Sources = project.Sources with { Screen = project.Sources.Screen with { Width = probe.Width, Height = probe.Height, Duration = probe.Duration, FrameRate = probe.FrameRate } } };
            context.Note(string.Create(CultureInfo.InvariantCulture, $"the probe reports {probe.Width}×{probe.Height}, {probe.FrameRate:0.###} fps, {probe.Duration:0.0000} s for a clip of {clips.Screen.DurationSeconds:0.0000} s of picture"));
            var (_, reading, _) = await Exporting.ExportAndVerify(context, "a", project, clips.Screen, clips.Camera, 1920, 1080, 30, 1, expectAudio: true);

            // Without the plan: output frame i is screen frame i, and the camera, which started
            // 0.2 s (6 frames) late, is hidden for 6 frames and then 6 behind.
            ExpectNumbers(context, reading, 180, index => index, index => index < 6 ? Hidden : index - 6);
        });

        await harness.Check("exports", "export (b): trimmed at both ends, off frame and keyframe boundaries", async context =>
        {
            // Keyframes are 2 s apart. 0.525 s is three quarters into frame 15; 5.14 s is inside frame 154.
            var project = Projects.Create(clips.Screen, clips.Camera) with { Edits = new StudioEdits { TrimStart = 0.525, TrimEnd = 5.14 } };
            var (_, reading, _) = await Exporting.ExportAndVerify(context, "b", project, clips.Screen, clips.Camera, 1920, 1080, 30, 1, expectAudio: true);

            // 4.615 s is 138.45 frames: 138. The middle of output frame i is 0.525 + (i + 0.5) / 30 s,
            // which is a quarter into screen frame 16 + i; the camera is 6 behind.
            ExpectNumbers(context, reading, 138, index => 16 + index, index => 10 + index);
            if (reading is not null)
            {
                // Seconds 1 to 5 start inside the kept range: five bursts, the first 0.475 s in.
                context.Expect(reading.Bursts == 5, $"{reading.Bursts} bursts expected by the time map, want 5");
            }
        });

        await harness.Check("exports", "export (c): screen only, muted", async context =>
        {
            var project = Projects.Create(clips.Screen, null) with { Audio = new StudioAudio { Muted = true } };
            var (_, reading, _) = await Exporting.ExportAndVerify(context, "c", project, clips.Screen, null, 1920, 1080, 30, 1, expectAudio: false);
            ExpectNumbers(context, reading, 180, index => index, null);
        });

        await harness.Check("exports", "export (d): side by side", async context =>
        {
            var project = Projects.Create(clips.Screen, clips.Camera) with { Scenes = [new StudioScene { Layout = StudioLayout.SideBySide }] };
            var (_, reading, _) = await Exporting.ExportAndVerify(context, "d", project, clips.Screen, clips.Camera, 1920, 1080, 30, 1, expectAudio: true);
            ExpectNumbers(context, reading, 180, index => index, index => index < 6 ? Hidden : index - 6);
        });

        await harness.Check("exports", "export (e): camera only", async context =>
        {
            var project = Projects.Create(clips.Screen, clips.Camera) with { Scenes = [new StudioScene { Layout = StudioLayout.Camera }] };
            var (_, reading, _) = await Exporting.ExportAndVerify(context, "e", project, clips.Screen, clips.Camera, 1920, 1080, 30, 1, expectAudio: true);

            // The screen is not drawn in this layout, so no screen strip is looked for; its sound is still there.
            ExpectNumbers(context, reading, 180, null, index => index < 6 ? Hidden : index - 6);
        });

        await harness.Check("exports", "export (f): camera shorter than the screen, starting 0.5 s late", async context =>
        {
            var project = Projects.Create(clips.Screen, clips.ShortCamera, cameraOffset: 0.5);
            var (_, reading, _) = await Exporting.ExportAndVerify(context, "f-late", project, clips.Screen, clips.ShortCamera, 1920, 1080, 30, 1, expectAudio: true);

            // Hidden for 15 frames, then camera frames 0 to 89 (its 3 s), then hidden again.
            ExpectNumbers(context, reading, 180, index => index, index => index is >= 15 and < 105 ? index - 15 : Hidden);
        });

        await harness.Check("exports", "export (f): camera shorter than the screen, starting 0.4 s early", async context =>
        {
            var project = Projects.Create(clips.Screen, clips.ShortCamera, cameraOffset: -0.4);
            var (_, reading, _) = await Exporting.ExportAndVerify(context, "f-early", project, clips.Screen, clips.ShortCamera, 1920, 1080, 30, 1, expectAudio: true);

            // The camera's first 12 frames are before the video starts; it runs out after frame 77.
            ExpectNumbers(context, reading, 180, index => index, index => index < 78 ? index + 12 : Hidden);
        });

        await harness.Check("exports", "export (g): 29.97 fps source", async context =>
        {
            var screen = clips.Screen2997;
            var project = Projects.Create(screen, clips.Camera);
            var (result, reading, _) = await Exporting.ExportAndVerify(context, "g", project, screen, clips.Camera, 1280, 720, 30000, 1001, expectAudio: true);
            context.Expect(result.FrameRate == new StudioFrameRate(30000, 1001), $"the result says {result.FrameRate.Numerator}/{result.FrameRate.Denominator}");

            // Output frames are 1.001 / 30 s apart and the camera's 1 / 30 s, so the camera falls
            // a frame further behind once in a thousand frames; over 120 frames it stays 6 behind.
            ExpectNumbers(context, reading, 120, index => index, index => index < 6 ? Hidden : index - 6);
        });

        await harness.Check("exports", "export (h): natural size above the 3840 limit", async context =>
        {
            // A 2560×1440 recording on a 9:16 canvas is 2560×4552 before the limit; 2 s of it.
            var project = Projects.Create(clips.BigScreen, clips.LongCamera) with
            {
                Canvas = new StudioCanvas { Aspect = StudioCanvasAspect.Portrait9X16, Background = new StudioBackground { Style = StudioBackgroundStyle.Gradient, Primary = "#203060", Secondary = "#80C0E0" } },
                Edits = new StudioEdits { TrimStart = 1, TrimEnd = 3 },
            };
            var natural = StudioCanvasMath.NaturalSize(project);
            context.Expect(natural.Width == 2560 && natural.Height == 4552, $"natural size {natural.Width}×{natural.Height}, want 2560×4552");
            var (_, reading, _) = await Exporting.ExportAndVerify(context, "h", project, clips.BigScreen, clips.LongCamera, 2160, 3840, 30, 1, expectAudio: true);
            ExpectNumbers(context, reading, 60, index => 30 + index, index => 24 + index);
        });

        await harness.Check("exports", "export (i): three frames", async context =>
        {
            var project = Projects.Create(clips.Screen, clips.Camera) with { Edits = new StudioEdits { TrimStart = 1, TrimEnd = 1.1 } };
            var (_, reading, _) = await Exporting.ExportAndVerify(context, "i", project, clips.Screen, clips.Camera, 1920, 1080, 30, 1, expectAudio: true);
            ExpectNumbers(context, reading, 3, index => 30 + index, index => 24 + index);
        });

        await harness.Check("exports", "export (j): HEVC", async context =>
        {
            if (!Media.HasEncoder(hevc: true, hardware: null))
            {
                throw new CheckSkippedException("this PC has no HEVC encoder");
            }

            var project = Projects.Create(clips.Screen, clips.Camera);
            var (_, reading, _) = await Exporting.ExportAndVerify(context, "j", project, clips.Screen, clips.Camera, 1920, 1080, 30, 1, expectAudio: true, new StudioExportOptions(Codec: VideoCodec.Hevc));
            ExpectNumbers(context, reading, 180, index => index, index => index < 6 ? Hidden : index - 6);
        });

        await harness.Check("exports", "export (k): a cut in the middle", async context =>
        {
            // Keep 0.525–2.31 s and 3.77–5.14 s. The cut is 1.46 s, long enough that the readers seek across it.
            var project = Projects.Create(clips.Screen, clips.Camera) with
            {
                Edits = new StudioEdits { TrimStart = 0.525, TrimEnd = 5.14, Cuts = [new StudioTimeRange { Start = 2.31, End = 3.77 }] },
            };
            var (_, reading, _) = await Exporting.ExportAndVerify(context, "k", project, clips.Screen, clips.Camera, 1920, 1080, 30, 1, expectAudio: true);

            // 1.785 s + 1.37 s = 3.155 s, 94 frames. The first range ends inside output frame 53
            // (1.785 × 30 = 53.55), whose middle, 1.7833 s, is still before the cut: screen 69.
            // Frame 54's middle is 0.0483 s into the second range: source 3.8183 s, screen 114.
            ExpectNumbers(context, reading, 94, index => index < 54 ? 16 + index : 114 + (index - 54), index => index < 54 ? 10 + index : 108 + (index - 54));
        });

        await harness.Check("exports", "export (m): HEVC sources, the screen at 1080 lines", async context =>
        {
            var screen = clips.HevcScreen;
            var camera = clips.HevcCamera;
            var (_, reading, _) = await Exporting.ExportAndVerify(context, "m", Projects.Create(screen, camera), screen, camera, 1920, 1080, 30, 1, expectAudio: true);
            ExpectNumbers(context, reading, 90, index => index, index => index < 6 ? Hidden : index - 6);
        });

        await harness.Check("exports", "export (n): edge to edge from a 1080-line H.264 source", context => EdgeToEdge(context, clips.Screen));
        await harness.Check("exports", "export (n): edge to edge from a 1080-line HEVC source", context => EdgeToEdge(context, clips.HevcScreen));

        await harness.Check("exports", "export (o): a screen recording with no sound track", async context =>
        {
            // A clip made as a camera clip, which has no sound, used as the screen. Not muted: there is just nothing to keep.
            var screen = clips.Camera;
            var project = Projects.Create(screen, null) with { Edits = new StudioEdits { TrimStart = 0.5, TrimEnd = 2.5 } };
            context.Expect(!project.Audio.Muted, "the project is muted");
            var (result, reading, _) = await Exporting.ExportAndVerify(context, "o", project, screen, null, 1280, 720, 30, 1, expectAudio: false);
            context.Expect(!result.HasAudio, "the result says the export has sound");
            ExpectNumbers(context, reading, 60, index => 15 + index, null);
        });

        await harness.Check("exports", "export (p): a source with 44.1 kHz mono sound", async context =>
        {
            var screen = clips.MonoScreen;
            var project = Projects.Create(screen, clips.Camera) with { Edits = new StudioEdits { TrimStart = 0.31, TrimEnd = 2.77 } };
            var (_, reading, _) = await Exporting.ExportAndVerify(context, "p", project, screen, clips.Camera, 1280, 720, 30, 1, expectAudio: true);

            // 2.46 s is 73.8 frames: 73. The middle of frame i is 0.31 + (i + 0.5) / 30 s = screen frame 9.8 + i,
            // so 9 + i until the fraction carries: 9.8 + i floors to 9 + i. The camera is 6 behind: 3.8 + i.
            ExpectNumbers(context, reading, 73, index => 9 + index, index => 3 + index);
            if (reading?.Audio is { } audio)
            {
                context.Expect(audio.SampleRate == 44100 && audio.Channels == 1, $"the export's sound is {audio.SampleRate} Hz with {audio.Channels} channels, want the source's 44100 Hz mono");
            }
        });

        foreach (var (letter, name, screen, rate, channels) in new[]
        {
            ("v", "32 kHz stereo sound", clips.Screen32k, 48000, 2),
            ("w", "32 kHz mono sound", clips.Screen32kMono, 48000, 1),
            ("x", "5.1 sound", clips.SurroundScreen, 48000, 2),
        })
        {
            await harness.Check("exports", $"export ({letter}): a source with {name}", async context =>
            {
                // Sound the AAC encoder is not given as it is: the reader converts it, and the
                // trim points and the bursts must land where they do for any other source.
                var project = Projects.Create(screen, clips.Camera) with { Edits = new StudioEdits { TrimStart = 0.31, TrimEnd = 2.77 } };
                var (_, reading, _) = await Exporting.ExportAndVerify(context, letter, project, screen, clips.Camera, 1280, 720, 30, 1, expectAudio: true);

                // As export (p): 73 frames, screen 9 + i, camera 3 + i.
                ExpectNumbers(context, reading, 73, index => 9 + index, index => 3 + index);
                if (reading?.Audio is { } audio)
                {
                    context.Expect(audio.SampleRate == rate && audio.Channels == channels, $"the export's sound is {audio.SampleRate} Hz with {audio.Channels} channels, want {rate} Hz with {channels}");
                    context.Expect(reading.Bursts == 2, $"{reading.Bursts} bursts expected by the time map, want 2 (seconds 1 and 2)");
                }
            });
        }

        await harness.Check("exports", "export (q): a recording that dropped frames", async context =>
        {
            // 67 frames over 5 s: slots 20–34 and 50–109 of the 30 fps grid are missing, only the
            // even slots from 115 to 129 are there, and from slot 132 on the frames are 7 ms late
            // or 5 ms early. The export is still 150 evenly spaced frames, each showing the last
            // frame the recording has for its moment.
            var screen = clips.GappyScreen;
            context.Expect(screen.Frames == 67, $"the clip has {screen.Frames} frames, want 67");
            var (_, reading, _) = await Exporting.ExportAndVerify(context, "q", Projects.Create(screen, clips.Camera), screen, clips.Camera, 1280, 720, 30, 1, expectAudio: true);

            // Output frame i is slot i of the grid, seen half a slot in: after a late frame has
            // arrived and before the next early one.
            ExpectNumbers(context, reading, 150, DroppedFramesSlot, index => index < 6 ? Hidden : index - 6);
        });

        await harness.Check("exports", "export (q): that recording, trimmed to start inside its two-second gap", async context =>
        {
            // 2.21 s is 0.3 of a frame into slot 66, a third of a second into the gap that runs
            // from 1.667 s to 3.667 s; the frame showing there (slot 49's) began 0.58 s earlier,
            // so the reader has to deliver frames from before the point it was sent to.
            var screen = clips.GappyScreen;
            var project = Projects.Create(screen, clips.Camera) with { Edits = new StudioEdits { TrimStart = 2.21, TrimEnd = 4.5 } };
            var (_, reading, _) = await Exporting.ExportAndVerify(context, "q-trim", project, screen, clips.Camera, 1280, 720, 30, 1, expectAudio: true);

            // 2.29 s is 68.7 frames: 68. The middle of frame i is 2.21 + (i + 0.5) / 30 s, 0.8 of
            // the way through slot 66 + i. The camera is 6 slots behind: 60.8 + i.
            ExpectNumbers(context, reading, 68, index => DroppedFramesSlot(66 + index), index => 60 + index);
            if (reading is not null)
            {
                context.Expect(reading.Screen[0] == 34 && reading.Screen[43] == 34 && reading.Screen[44] == 35, $"around the end of the gap the export shows screen {reading.Screen[0]}, {reading.Screen[43]}, {reading.Screen[44]}; want 34, 34, 35");
                context.Expect(reading.Bursts == 2, $"{reading.Bursts} bursts expected by the time map, want 2 (seconds 3 and 4)");
            }
        });

        await harness.Check("exports", "export (r): that recording at 13.4 fps, the average rate its file reports", async context =>
        {
            // A project made from a file that only says its average rate, 67 frames in 5 s (the
            // recorder writes the rate it recorded at; a video brought in from elsewhere has
            // only this). The export is then 13.4 evenly spaced frames a second.
            var screen = clips.GappyScreen;
            var probed = MediaFileProbe.Probe(screen.Path, 30).FrameRate;
            context.Expect(Math.Abs(probed - 13.4) < 0.001, string.Create(CultureInfo.InvariantCulture, $"the probe reads {probed:0.####} fps, and this check was worked out for 13.4"));
            var created = Projects.Create(screen, clips.Camera);
            var project = created with { Sources = created.Sources with { Screen = created.Sources.Screen with { FrameRate = probed } } };
            var (result, reading, _) = await Exporting.ExportAndVerify(context, "r", project, screen, clips.Camera, 1280, 720, 67, 5, expectAudio: true);
            context.Expect(result.FrameRate == new StudioFrameRate(67, 5), $"the result says {result.FrameRate.Numerator}/{result.FrameRate.Denominator} fps");

            // 5 s at 13.4 fps is 67 frames, the middle of frame i at (i + 0.5) × 5 / 67 s. The
            // camera shows from 0.2 s on; frame 33's middle is 2.5 s, exactly where camera frame
            // 69 starts, and a frame shows from the instant it starts.
            static double Middle(int index) => (index + 0.5) * 5 / 67;
            ExpectNumbers(
                context,
                reading,
                67,
                index => DroppedFramesAt(Middle(index)),
                index => Middle(index) < 0.2 ? Hidden : (int)Math.Floor(((Middle(index) - 0.2) * 30) + 1e-9));
            if (reading is not null)
            {
                // Worked out one by one: 37.3 ms is in slot 1; 111.9 ms in slot 3; 783.6 ms in
                // dropped slot 23; 1529.9 ms in slot 45; 3022.4 ms in dropped slot 90; 3768.7 ms in
                // slot 113; 3917.9 ms in dropped slot 117; 4514.9 ms in slot 135, which came 5 ms
                // early; 4962.7 ms is 1 ms after slot 149 arrived, early too.
                foreach (var (index, want) in new[] { (0, 1), (1, 3), (10, 19), (20, 30), (40, 34), (50, 38), (52, 40), (60, 52), (66, 66) })
                {
                    context.Expect(reading.Screen[index] == want, $"frame {index} shows screen {reading.Screen[index]}, worked out as {want}");
                }

                context.Expect(reading.Camera[33] == 69, $"frame 33 shows camera {reading.Camera[33]}, want 69");
            }
        });

        await harness.Check("exports", "export (s): a frame rate just under 30, as a recording that lost a few frames reports", async context =>
        {
            // 29.67 fps is 2967/100. The source's frames are still 1/30 s apart, so the export
            // has to skip one now and then: frame 44's middle is 44.99 source frames in, and
            // frame 45's is 46.01.
            var created = Projects.Create(clips.Screen, clips.Camera);
            var project = created with { Sources = created.Sources with { Screen = created.Sources.Screen with { FrameRate = 29.67 } } };
            var (result, reading, _) = await Exporting.ExportAndVerify(context, "s", project, clips.Screen, clips.Camera, 1920, 1080, 2967, 100, expectAudio: true);
            context.Expect(result.FrameRate == new StudioFrameRate(2967, 100), $"the result says {result.FrameRate.Numerator}/{result.FrameRate.Denominator} fps");

            // 6 s is 178.02 frames: 178. The camera is hidden until 0.2 s, which is 5.93 frames.
            ExpectNumbers(
                context,
                reading,
                178,
                index => (int)Math.Floor((index + 0.5) * 30 / 29.67),
                index => index < 6 ? Hidden : (int)Math.Floor(((index + 0.5) * 30 / 29.67) - 6));
            if (reading is not null)
            {
                context.Expect(reading.Screen[44] == 44 && reading.Screen[45] == 46 && reading.Screen[177] == 179, $"frames 44, 45 and 177 show screen {reading.Screen[44]}, {reading.Screen[45]}, {reading.Screen[177]}; want 44, 46, 179");
            }
        });

        await harness.Check("exports", "export (u): 25 fps source with the 30 fps camera", async context =>
        {
            var screen = clips.Screen25;
            var (result, reading, _) = await Exporting.ExportAndVerify(context, "u", Projects.Create(screen, clips.Camera), screen, clips.Camera, 1280, 720, 25, 1, expectAudio: true);
            context.Expect(result.FrameRate == new StudioFrameRate(25, 1), $"the result says {result.FrameRate.Numerator}/{result.FrameRate.Denominator} fps");

            // The middle of frame i is (i + 0.5) / 25 s, which for the camera, 0.2 s behind, is
            // frame 1.2 i - 5.4: hidden up to frame 4, and from frame 7 on every fifth frame's
            // middle is the very instant a camera frame starts (7 → 3.0, 12 → 9.0), and a frame
            // shows from the instant it starts.
            ExpectNumbers(context, reading, 50, index => index, index => index < 5 ? Hidden : (int)Math.Floor((1.2 * index) - 5.4 + 1e-9));
            if (reading is not null)
            {
                context.Expect(
                    reading.Camera[5] == 0 && reading.Camera[6] == 1 && reading.Camera[7] == 3 && reading.Camera[12] == 9 && reading.Camera[49] == 53,
                    $"frames 5, 6, 7, 12 and 49 show camera {reading.Camera[5]}, {reading.Camera[6]}, {reading.Camera[7]}, {reading.Camera[12]}, {reading.Camera[49]}; want 0, 1, 3, 9, 53");
            }
        });

        await harness.Check("exports", "export (t): a project that says the recording is half a second longer than the file", async context =>
        {
            // The app reads a recording's length from the file as a whole, and the sound track
            // is often a little longer than the picture; here the difference is made large.
            // The last picture stays up, the sound is silent, and the camera's 6 s are over.
            var created = Projects.Create(clips.Screen, clips.Camera);
            var project = created with { Sources = created.Sources with { Screen = created.Sources.Screen with { Duration = 6.5 } } };
            var (_, reading, _) = await Exporting.ExportAndVerify(context, "t", project, clips.Screen, clips.Camera, 1920, 1080, 30, 1, expectAudio: true);

            // 195 frames. The screen has frames 0 to 179; the camera, 0.2 s behind, shows its
            // 180 frames in output frames 6 to 185.
            ExpectNumbers(context, reading, 195, index => Math.Min(index, 179), index => index is >= 6 and < 186 ? index - 6 : Hidden);
            if (reading?.Audio is { } audio)
            {
                // The file's sound ends at 6 s; leave the encoder 50 ms to settle.
                var from = (int)(6.05 * audio.SampleRate);
                var loudest = 0;
                for (var sample = from; sample < audio.Length; sample++)
                {
                    loudest = Math.Max(loudest, Math.Abs((int)audio.Left[sample]));
                }

                context.Expect(audio.Length > from, "the sound stops with the file");
                context.Expect(loudest < 328, $"after the file's sound ends the export is not silent (peak {loudest} of 32768)");
                context.Note($"the last {audio.Length - from} samples of sound peak at {loudest} of 32768");
            }
        });

        await harness.Check("exports", "export (l): 60 fps source", async context =>
        {
            var screen = clips.Screen60;
            var project = Projects.Create(screen, clips.Camera) with { Edits = new StudioEdits { TrimStart = 0.21, TrimEnd = 2.27 } };
            var (_, reading, _) = await Exporting.ExportAndVerify(context, "l", project, screen, clips.Camera, 1280, 720, 60, 1, expectAudio: true);

            // 2.06 s is 123.6 frames: 123. Frame i's middle is 0.21 + (i + 0.5) / 60 s = screen frame
            // 12.6 + i + 0.5, so 13 + i. The camera at 30 fps: (0.01 + (i + 0.5) / 60) × 30 = 0.55 + i / 2.
            ExpectNumbers(context, reading, 123, index => 13 + index, index => (int)Math.Floor(0.55 + (index / 2.0)));
        });
    }

    /// <summary>
    /// The frame of the dropped-frames recording showing during a slot of its 30 fps grid, from
    /// how the clip is described: slots 20–34 and 50–109 are missing, and of 115–129 only the
    /// even ones are there.
    /// </summary>
    private static int DroppedFramesSlot(int slot) => slot switch
    {
        < 20 => slot,
        < 35 => 19,
        < 50 => slot - 15,
        < 110 => 34,
        < 115 => slot - 75,
        < 130 => 39 + ((slot - 114) / 2),
        _ => slot - 83,
    };

    /// <summary>The same at a time: from slot 132 on, even slots arrive 7 ms late and odd ones 5 ms early.</summary>
    private static int DroppedFramesAt(double seconds)
    {
        static double Arrives(int slot) => (slot / 30.0) + (slot < 132 ? 0 : slot % 2 == 0 ? 0.007 : -0.005);
        var slot = 149;
        while (slot > 0 && Arrives(slot) > seconds)
        {
            slot--;
        }

        return DroppedFramesSlot(slot);
    }

    /// <summary>
    /// The screen alone, edge to edge, exported at two thirds of its size: the scaler reaches past
    /// the picture's last row and column, so anything a decoder keeps there (a 1080-line video is
    /// decoded into 1088 rows) would show at the bottom and right of every frame.
    /// </summary>
    private static async Task EdgeToEdge(CheckContext context, TestClip screen)
    {
        var project = Projects.Create(screen, null) with
        {
            Canvas = new StudioCanvas { Padding = 0, Background = new StudioBackground { Style = StudioBackgroundStyle.Solid, Primary = "#FF00FF" } },
            Screen = new StudioScreenStyle { CornerRadius = 0, Shadow = 0 },
            Edits = new StudioEdits { TrimStart = 1, TrimEnd = 2 },
        };
        var (result, reading, _) = await Exporting.ExportAndVerify(context, "edges", project, screen, null, 1280, 720, 30, 1, expectAudio: true, new StudioExportOptions(LongSideLimit: 1280));
        ExpectNumbers(context, reading, 30, index => 30 + index, null);

        var spec = screen.Spec;
        var worst = 0.0;
        var where = string.Empty;
        var frames = Media.ReadFrames(result.OutputPath, chroma: true, frame =>
        {
            var picture = frame.Picture;
            foreach (var (x, y, want, name) in new[]
            {
                (200.0, 718.0, spec.LeftBackground, "the bottom rows, left"),
                (900.0, 718.0, spec.Background, "the bottom rows, right"),
                (1278.0, 500.0, spec.Background, "the right columns"),
                (1278.0, 718.0, spec.Background, "the bottom-right corner"),
                (1.0, 500.0, spec.LeftBackground, "the left columns"),
                (640.0, 1.0, spec.Background, "the top rows"),
            })
            {
                var got = picture.Average(x, y, 1);
                if (got.Distance(want) > worst)
                {
                    worst = got.Distance(want);
                    where = $"frame {frame.Index}, {name}: {got}, and the screen has {want} there";
                }
            }

            return true;
        });
        context.Expect(frames == 30, $"{frames} frames read back, want 30");
        context.Expect(worst <= ExportVerifier.ColorTolerance, string.Create(CultureInfo.InvariantCulture, $"the edge of the picture is {worst:0.0} of 255 off ({where})"));
        context.Note(string.Create(CultureInfo.InvariantCulture, $"the outermost rows and columns of all 30 frames are within {worst:0.0} of 255 of the screen's own colours"));
    }

    /// <summary>
    /// Compares what was read from the file with numbers worked out by hand in the check, so the
    /// frame plan itself is not what vouches for the result.
    /// </summary>
    public static void ExpectNumbers(CheckContext context, ExportReading? reading, int frames, Func<int, int>? screen, Func<int, int>? camera)
    {
        if (reading is null)
        {
            return;
        }

        context.Expect(reading.FrameCount == frames, $"{reading.FrameCount} frames, worked out by hand as {frames}");
        var count = Math.Min(frames, reading.Screen.Length);
        for (var index = 0; index < count; index++)
        {
            if (screen is not null && reading.Screen[index] != screen(index))
            {
                context.Fail($"by hand, frame {index} should show screen {screen(index)}; it shows {reading.Screen[index]}");
                break;
            }
        }

        for (var index = 0; index < count; index++)
        {
            if (camera is not null && reading.Camera[index] != camera(index))
            {
                context.Fail($"by hand, frame {index} should show camera {camera(index)}; it shows {reading.Camera[index]}");
                break;
            }
        }
    }
}
