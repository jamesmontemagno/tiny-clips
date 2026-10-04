using System.Diagnostics;
using System.Globalization;
using TinyClips.Core.Models;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Rendering;

namespace TinyClips.Tools.StudioRenderCheck;

/// <summary>Encoders, colour, cancellation, failures, the poster, the service adapter and WARP.</summary>
internal static class OtherChecks
{

    /// <summary>The same for the poster, which is scaled down and JPEG-compressed.</summary>
    private const double PosterColorTolerance = 10;

    private const int Hidden = FrameCode.Unreadable;

    public static async Task Run(Harness harness)
    {
        await Encoders(harness).ConfigureAwait(false);
        await Color(harness).ConfigureAwait(false);
        await Robustness(harness).ConfigureAwait(false);
        await Poster(harness).ConfigureAwait(false);
        await Service(harness).ConfigureAwait(false);
        await Warp(harness).ConfigureAwait(false);
    }

    /// <summary>Two seconds of the standard project: frames 30 to 89 of the screen, the camera six behind.</summary>
    private static StudioProject TwoSeconds(Harness harness) =>
        Projects.Create(harness.Clips.Screen, harness.Clips.Camera) with { Edits = new StudioEdits { TrimStart = 1, TrimEnd = 3 } };

    private static async Task Encoders(Harness harness)
    {
        var clips = harness.Clips;

        await harness.Check("encoders", "encoder: hardware H.264", async context =>
        {
            RequireHardwareEncoder();
            var (result, reading, _) = await Exporting.ExportAndVerify(context, "hardware", TwoSeconds(harness), clips.Screen, clips.Camera, 1920, 1080, 30, 1, expectAudio: true, new StudioExportOptions(EncoderPreference: StudioEncoderPreference.HardwareOnly));
            context.Expect(result.HardwareEncoder, $"the result says the encoder was not hardware: {result.EncoderDescription}");
            ExportChecks.ExpectNumbers(context, reading, 60, index => 30 + index, index => 24 + index);
        });

        await harness.Check("encoders", "encoder: software H.264 forced", async context =>
        {
            var (result, reading, _) = await Exporting.ExportAndVerify(context, "software", TwoSeconds(harness), clips.Screen, clips.Camera, 1920, 1080, 30, 1, expectAudio: true, new StudioExportOptions(EncoderPreference: StudioEncoderPreference.SoftwareOnly));
            context.Expect(!result.HardwareEncoder, $"the result says the encoder was hardware: {result.EncoderDescription}");
            ExportChecks.ExpectNumbers(context, reading, 60, index => 30 + index, index => 24 + index);
        });

        await harness.Check("encoders", "encoder: a canvas the hardware encoder refuses is encoded in software", async context =>
        {
            RequireHardwareEncoder();

            // A crop to the frame-number strip alone makes a 576×96 canvas, which the hardware
            // encoders this was tried on do not take. Media Foundation itself then builds the
            // writer around the software encoder; the export does not have to start over.
            var screen = clips.Screen;
            var project = TinyCanvas(screen);
            var size = StudioCanvasMath.NaturalSize(project);
            var width = (int)size.Width;
            var height = (int)size.Height;
            context.Expect(width == 576 && height == 96, $"the canvas is {width}×{height}, want 576×96");

            var folder = context.Folder("hardware-only");
            var refused = await Catch(() => new StudioExporter().ExportAsync(project, Projects.Events(screen), Projects.Paths(folder, screen, null), Path.Combine(folder, "tiny.mp4"), new StudioExportOptions(EncoderPreference: StudioEncoderPreference.HardwareOnly))).ConfigureAwait(false);
            if (refused is null)
            {
                throw new CheckSkippedException($"the hardware encoder on this PC takes a {width}×{height} canvas");
            }

            context.Expect(refused is StudioExportException, $"with the hardware encoder only, the export threw {refused.GetType().Name}");
            context.Expect(Directory.GetFiles(folder).Length == 0, "the refused export left a file");
            context.Note($"hardware only: {refused.Message}");

            var (result, reading, _) = await Exporting.ExportAndVerify(context, "tiny", project, screen, null, width, height, 30, 1, expectAudio: true);
            context.Expect(!result.HardwareEncoder, $"the export used a hardware encoder: {result.EncoderDescription}");
            ExportChecks.ExpectNumbers(context, reading, 60, index => 30 + index, null);
        });

        await harness.Check("encoders", "encoder: the export starts over in software when the hardware encoder fails on a frame", async context =>
        {
            RequireHardwareEncoder();

            // Injected: the attempt that may use hardware fails two thirds of the way through
            // (frame 40 of 60), which is still inside the two seconds in which an encoder is replaced.
            var attempts = new List<string>();
            var exporter = new StudioExporter
            {
                Fault = (allowHardware, textureFrames, frame) =>
                {
                    if (frame == 0)
                    {
                        attempts.Add($"{(allowHardware ? "hardware allowed" : "software")}, {(textureFrames ? "texture frames" : "memory frames")}");
                    }

                    // Slow enough that progress has been reported before the failure.
                    Thread.Sleep(allowHardware ? 5 : 0);
                    return allowHardware && frame == 40;
                },
            };
            var progress = new ThresholdProgress(2);
            var (result, reading, _) = await Exporting.ExportAndVerify(context, "restarted", TwoSeconds(harness), clips.Screen, clips.Camera, 1920, 1080, 30, 1, expectAudio: true, exporter: exporter, progress: progress);
            context.Expect(!result.HardwareEncoder, $"the export used a hardware encoder: {result.EncoderDescription}");
            context.Expect(attempts.Count == 2, $"{attempts.Count} attempts, want 2");
            context.Expect(progress.Count >= 3 && progress.Ordered && progress.Last == 1, $"progress: {progress.Count} reports, {(progress.Ordered ? "in order" : "going backwards")}, the last {progress.Last}");
            context.Note("attempts: " + string.Join("; then ", attempts) + $"; progress reported {progress.Count} times and never backwards");
            ExportChecks.ExpectNumbers(context, reading, 60, index => 30 + index, index => 24 + index);
        });

        await harness.Check("encoders", "encoder: frames go through system memory when no encoder takes textures", async context =>
        {
            // Injected: every attempt that hands the encoder textures fails on its fourth frame,
            // which leaves the last resort: frames read back and written from system memory.
            var exporter = new StudioExporter { Fault = (_, textureFrames, frame) => textureFrames && frame == 3 };
            var (result, reading, _) = await Exporting.ExportAndVerify(context, "memory", TwoSeconds(harness), clips.Screen, clips.Camera, 1920, 1080, 30, 1, expectAudio: true, exporter: exporter);
            context.Expect(result.EncoderDescription.Contains("system memory", StringComparison.Ordinal), $"the result does not say the frames came from system memory: {result.EncoderDescription}");
            ExportChecks.ExpectNumbers(context, reading, 60, index => 30 + index, index => 24 + index);
        });

        await harness.Check("encoders", "encoder: a failure later than two seconds in fails the export", async context =>
        {
            // Two seconds is 60 frames; an encoder that dies on frame 100 is not swapped for another.
            var folder = context.Folder();
            var output = Path.Combine(folder, "late.mp4");
            var exporter = new StudioExporter { Fault = (_, _, frame) => frame == 100 };
            var readable = true;
            var thrown = await Catch(() => exporter.ExportAsync(Projects.Create(clips.Screen, clips.Camera), Projects.Events(clips.Screen), Projects.Paths(folder, clips.Screen, clips.Camera), output)).ConfigureAwait(false);
            context.Expect(thrown is StudioExportException, $"threw {thrown?.GetType().Name ?? "nothing"}, want StudioExportException");
            context.Expect(Directory.GetFiles(folder).Length == 0, "a file was left behind");

            // A sentence for a person: no HRESULT text, no type names.
            readable = thrown is not null && !thrown.Message.Contains("HRESULT", StringComparison.OrdinalIgnoreCase) && !thrown.Message.Contains("Exception", StringComparison.Ordinal) && thrown.Message.EndsWith('.');
            context.Expect(readable, $"the message is not one to show a user: {thrown?.Message}");
            context.Note($"message: {thrown?.Message}");
        });

        static void RequireHardwareEncoder()
        {
            if (!Media.HasEncoder(hevc: false, hardware: true))
            {
                throw new CheckSkippedException("this PC has no hardware H.264 encoder");
            }
        }
    }

    /// <summary>The standard screen clip cropped to its frame-number strip, with no padding: a 576×96 canvas.</summary>
    private static StudioProject TinyCanvas(TestClip screen)
    {
        var spec = screen.Spec;
        return Projects.Create(screen, null) with
        {
            Canvas = new StudioCanvas { Padding = 0, Background = new StudioBackground { Style = StudioBackgroundStyle.None } },
            Screen = new StudioScreenStyle
            {
                CornerRadius = 0,
                Shadow = 0,
                Crop = new StudioRect(spec.CodeX / (double)spec.Width, spec.CodeY / (double)spec.Height, ClipSpec.CodeBits * spec.CodeCell / (double)spec.Width, 2.0 * spec.CodeCell / spec.Height),
            },
            Edits = new StudioEdits { TrimStart = 1, TrimEnd = 3 },
        };
    }

    private static async Task Color(Harness harness)
    {
        var clips = harness.Clips;
        var cases = new (string Name, Func<TestClip> Screen, Func<TestClip> Camera, double Limit, int Width, int Height, StudioExportOptions Options)[]
        {
            ("colour: export at 1280×720 against the source", () => clips.Screen, () => clips.Camera, 1280, 1280, 720, new StudioExportOptions()),
            ("colour: export at 640×360 against the source", () => clips.Screen, () => clips.Camera, 640, 640, 360, new StudioExportOptions()),
            ("colour: export at 640×360 with the software encoder", () => clips.Screen, () => clips.Camera, 640, 640, 360, new StudioExportOptions(EncoderPreference: StudioEncoderPreference.SoftwareOnly)),
            ("colour: export at 640×360 drawn by WARP", () => clips.Screen, () => clips.Camera, 640, 640, 360, new StudioExportOptions(DevicePreference: StudioRenderDevicePreference.WarpOnly)),
            ("colour: a 640×480 recording with a 640×480 camera, both untagged BT.601", () => clips.SmallScreen, () => clips.SmallCamera, 3840, 640, 480, new StudioExportOptions()),
            ("colour: a 640×480 source tagged BT.709, against its size", () => clips.SmallScreen709, () => clips.SmallCamera, 3840, 640, 480, new StudioExportOptions()),
            ("colour: a 1280×720 source tagged BT.601, against its size", () => clips.Screen601, () => clips.Camera, 3840, 1280, 720, new StudioExportOptions()),
            ("colour: HEVC sources exported as HEVC", () => clips.HevcScreen, () => clips.HevcCamera, 3840, 1920, 1080, new StudioExportOptions(Codec: VideoCodec.Hevc)),
        };

        foreach (var (name, screenClip, cameraClip, limit, width, height, options) in cases)
        {
            await harness.Check("color", name, async context =>
            {
                var screen = screenClip();
                var camera = cameraClip();
                var background = "#C86432";

                // No shadows, so the margin around the card is the background colour and nothing else.
                var project = Projects.Create(screen, camera) with
                {
                    Canvas = new StudioCanvas { Background = new StudioBackground { Style = StudioBackgroundStyle.Solid, Primary = background } },
                    Screen = new StudioScreenStyle { Shadow = 0 },
                    Camera = new StudioCameraStyle { Shape = StudioCameraShape.Circle, Mirror = true, Shadow = 0, BorderWidth = 0 },
                    Scenes = [new StudioScene { Layout = StudioLayout.Bubble, Bubble = new StudioBubble { Size = 0.5 } }],
                    Edits = new StudioEdits { TrimStart = 1, TrimEnd = 2 },
                };
                var folder = context.Folder();
                var output = Path.Combine(folder, "colour.mp4");
                var result = await new StudioExporter().ExportAsync(project, Projects.Events(screen), Projects.Paths(folder, screen, camera), output, options with { LongSideLimit = limit }).ConfigureAwait(false);
                var info = Media.Probe(output);
                context.Expect(info.Width == width && info.Height == height, $"size {info.Width}×{info.Height}, want {width}×{height}");
                context.Expect(info.Primaries == 2 && info.Transfer == 5 && info.NominalRange == 2 && info.Matrix is 1 or 2, $"the stream's colour tags are incomplete: {info.ColorTags}");

                var worst = 0.0;
                var worstWhere = string.Empty;
                var worstOther = 0.0;
                var frames = 0;
                var layout = StudioLayoutPlan.Create(project);
                Media.ReadFrames(output, chroma: true, frame =>
                {
                    if (frame.Index % 10 != 5)
                    {
                        return true;
                    }

                    frames++;
                    var picture = frame.Picture;
                    var other = picture.WithOtherMatrix();
                    var resolved = layout.Resolve(1 + ((frame.Index + 0.5) / 30), width, height);
                    var screenMap = Projects.ScreenMap(resolved.Screen!.Value, screen.Spec);
                    var patches = FrameCode.ReadPatches(picture, screen.Spec, screenMap);
                    var otherPatches = FrameCode.ReadPatches(other, screen.Spec, screenMap);
                    for (var index = 0; index < patches.Length; index++)
                    {
                        Worst(patches[index].Distance(ClipSpec.PatchColors[index]), $"screen patch {index} is {patches[index]}, drawn {ClipSpec.PatchColors[index]}");
                        worstOther = Math.Max(worstOther, otherPatches[index].Distance(ClipSpec.PatchColors[index]));
                    }

                    // The darker left third of the screen clip, clear of the camera bubble.
                    var (sx, sy) = screenMap.Apply(screen.Spec.LeftWidth / 2.0, screen.Spec.Height * 0.75);
                    var card = picture.Average(sx, sy, 3);
                    Worst(card.Distance(screen.Spec.LeftBackground), $"the screen's own background is {card}, drawn {screen.Spec.LeftBackground}");

                    // The canvas background, in the margin left of the card, above it, and in the corner.
                    var margin = resolved.Screen.Value.Rect.X / 2;
                    foreach (var (x, y) in new[] { (margin, height / 2.0), (width / 2.0, margin), (margin, margin) })
                    {
                        var there = picture.Average(x, y, 2);
                        Worst(there.Distance(Rgb.FromHex(background)), $"the canvas background at ({x:0},{y:0}) is {there}, want {Rgb.FromHex(background)}");
                    }

                    // The camera's patches, where the bubble is large enough for them to be sampled.
                    var cameraMap = Projects.CameraMap(resolved.Camera!.Value, camera.Spec);
                    if (Math.Abs(cameraMap.ScaleX) * camera.Spec.PatchSize >= 12)
                    {
                        var cameraPatches = FrameCode.ReadPatches(picture, camera.Spec, cameraMap);
                        for (var index = 0; index < cameraPatches.Length; index++)
                        {
                            Worst(cameraPatches[index].Distance(ClipSpec.PatchColors[index]), $"camera patch {index} is {cameraPatches[index]}, drawn {ClipSpec.PatchColors[index]}");
                        }
                    }

                    return true;
                });

                void Worst(double distance, string where)
                {
                    if (distance > worst)
                    {
                        worst = distance;
                        worstWhere = where;
                    }
                }

                var c = CultureInfo.InvariantCulture;
                var tolerance = ExportVerifier.ColorTolerance;
                context.Expect(frames == 3, $"{frames} frames sampled, want 3");
                var encoding = screen.Encoding ?? ClipEncoding.Tagged709;
                if (encoding is { Tagged: true, Bt601: true } && screen.Spec.Height > 576 && worst > tolerance && worstOther <= tolerance)
                {
                    // Not the exporter's doing: the picture the source reader hands over is already wrong.
                    context.Known(string.Create(c, $"Media Foundation decodes a video taller than 576 lines with the BT.709 matrix even when it is tagged BT.601, so its colours come out {worst:0.0} of 255 off. Tiny Clips' own recordings are never tagged that way."));
                    return;
                }

                context.Expect(worst <= tolerance, string.Create(c, $"colour is {worst:0.0} of 255 off ({worstWhere}); the tolerance is {tolerance:0}"));
                context.Measure(string.Create(c, $"largest difference from the colours drawn into the source {worst:0.0} of 255 (tolerance {tolerance:0}); matrix tag {(info.Matrix == 2 ? "BT.601" : "BT.709")}, and read with the other matrix the patches would be {worstOther:0.0} off; {result.EncoderDescription}{(result.SoftwareRendering ? ", drawn by WARP" : string.Empty)}"));
            });
        }
    }
    private static async Task Robustness(Harness harness)
    {
        var clips = harness.Clips;

        await harness.Check("robustness", "cancellation in the middle of an export", async context =>
        {
            var screen = clips.BigScreen;
            var camera = clips.LongCamera;
            var folder = context.Folder();
            var output = Path.Combine(folder, "cancelled.mp4");
            using var cancel = new CancellationTokenSource();
            var progress = new ThresholdProgress(0.3);
            var task = new StudioExporter().ExportAsync(Projects.Create(screen, camera), Projects.Events(screen), Projects.Paths(folder, screen, camera), output, null, progress, cancel.Token);
            var reached = await Task.WhenAny(progress.Reached, task).ConfigureAwait(false);
            context.Expect(reached == progress.Reached, "the export finished before it could be cancelled");

            // While it runs, the only file is the temporary one, under a name the Clips Library does not scan.
            var during = Directory.GetFiles(folder).Select(Path.GetFileName).ToArray();
            context.Expect(during.Length == 1 && during[0]!.EndsWith(StudioRenderingMath.TemporaryExtension, StringComparison.Ordinal) && during[0]!.StartsWith(".cancelled.", StringComparison.Ordinal), $"while exporting the folder holds: {string.Join(", ", during)}");

            var watch = Stopwatch.StartNew();
            cancel.Cancel();
            var thrown = await Catch(() => task).ConfigureAwait(false);
            var milliseconds = watch.Elapsed.TotalMilliseconds;
            context.Expect(thrown is OperationCanceledException, $"the export threw {thrown?.GetType().Name ?? "nothing"}, want OperationCanceledException");
            context.Expect(task.IsCanceled, "the task did not end as cancelled");
            context.Expect(milliseconds < 1000, string.Create(CultureInfo.InvariantCulture, $"the export took {milliseconds:0} ms to stop"));
            context.Expect(Directory.GetFiles(folder).Length == 0, $"after the cancel the folder holds: {string.Join(", ", Directory.GetFiles(folder).Select(Path.GetFileName))}");
            context.Measure(string.Create(CultureInfo.InvariantCulture, $"stopped {milliseconds:0} ms after the cancel, at {progress.Last:P0}; neither the temporary file nor the output is left"));
        });

        await harness.Check("robustness", "stale temporary files: what a killed export left goes, and a running export keeps its own", async context =>
        {
            var screen = clips.BigScreen;
            var camera = clips.LongCamera;
            var project = Projects.Create(screen, camera);
            var folder = context.Folder();
            var output = Path.Combine(folder, "running.mp4");

            // What an export that was killed three days ago left here, and a video of the user's.
            var stale = StudioRenderingMath.TemporaryOutputPath(Path.Combine(folder, "killed.mp4"));
            var video = Path.Combine(folder, "keep.mp4");
            foreach (var path in new[] { stale, video })
            {
                File.WriteAllText(path, "not a video");
                File.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-3));
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-3));
            }

            var progress = new ThresholdProgress(0.3);
            var task = new StudioExporter().ExportAsync(project, Projects.Events(screen), Projects.Paths(folder, screen, camera), output, new StudioExportOptions(LongSideLimit: 1280), progress, CancellationToken.None);
            var reached = await Task.WhenAny(progress.Reached, task).ConfigureAwait(false);
            context.Expect(reached == progress.Reached, "the export finished before the clean-up could be tried on it");
            var own = Directory.GetFiles(folder, "*" + StudioRenderingMath.TemporaryExtension).Where(path => !string.Equals(path, stale, StringComparison.OrdinalIgnoreCase)).ToArray();
            context.Expect(own.Length == 1, $"while exporting there are {own.Length} temporary files of the export's own");

            // As the app will call it: anything a day old. Then with no age at all, which only
            // the running export's hold on its file can stop.
            var first = StudioRenderingMath.DeleteStaleTemporaryFiles(folder, TimeSpan.FromDays(1));
            context.Expect(first == 1 && !File.Exists(stale), $"asked for files a day old, the clean-up deleted {first} and the stale file is {(File.Exists(stale) ? "still there" : "gone")}");
            var second = StudioRenderingMath.DeleteStaleTemporaryFiles(folder, TimeSpan.Zero);
            context.Expect(second == 0 && own.All(File.Exists), $"asked for files of any age, the clean-up deleted {second} while the export was writing its file");
            context.Expect(File.Exists(video), "the clean-up deleted a file that is not a temporary one");

            var result = await task.ConfigureAwait(false);
            var reading = ExportVerifier.Verify(context, output, new ExportExpectation(project, screen, camera, 1280, 720, 30, 1, ExpectAudio: true), "running");
            ExportChecks.ExpectNumbers(context, reading, 600, index => index, index => index < 6 ? FrameCode.Unreadable : index - 6);
            context.Expect(result.FrameCount == 600, $"the export wrote {result.FrameCount} frames");
            var left = Directory.GetFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
            context.Expect(left.SequenceEqual(["keep.mp4", "running.mp4"]), $"afterwards the folder holds: {string.Join(", ", left)}");
        });

        await harness.Check("robustness", "cancellation leaves an earlier file at the output path alone", async context =>
        {
            var screen = clips.BigScreen;
            var camera = clips.LongCamera;
            var project = Projects.Create(screen, camera);
            var folder = context.Folder();
            var output = Path.Combine(folder, "cancelled.mp4");

            // A file that is already there has to survive a cancelled export untouched.
            var earlier = new byte[] { 1, 2, 3, 4, 5 };
            await File.WriteAllBytesAsync(output, earlier).ConfigureAwait(false);

            using var cancel = new CancellationTokenSource();
            var progress = new ThresholdProgress(0.2);
            var task = new StudioExporter().ExportAsync(project, Projects.Events(screen), Projects.Paths(folder, screen, camera), output, null, progress, cancel.Token);
            var reached = await Task.WhenAny(progress.Reached, task).ConfigureAwait(false);
            context.Expect(reached == progress.Reached, "the export finished before it could be cancelled");

            var watch = Stopwatch.StartNew();
            cancel.Cancel();
            Exception? thrown = null;
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                thrown = ex;
            }

            var milliseconds = watch.Elapsed.TotalMilliseconds;
            context.Expect(thrown is OperationCanceledException, $"the export threw {thrown?.GetType().Name ?? "nothing"}, want OperationCanceledException");
            context.Expect(milliseconds < 1000, string.Create(CultureInfo.InvariantCulture, $"the export took {milliseconds:0} ms to stop"));
            context.Expect(Directory.GetFiles(folder, "*" + StudioRenderingMath.TemporaryExtension).Length == 0, "the temporary file was left behind");
            context.Expect(File.Exists(output) && (await File.ReadAllBytesAsync(output).ConfigureAwait(false)).AsSpan().SequenceEqual(earlier), "the file that was at the output path before is gone or changed");
            context.Expect(Directory.GetFiles(folder).Length == 1, $"{Directory.GetFiles(folder).Length} files in the folder, want only the earlier one");
            context.Measure(string.Create(CultureInfo.InvariantCulture, $"stopped {milliseconds:0} ms after the cancel, at {progress.Last:P0}"));
        });

        await harness.Check("robustness", "cancellation before the export starts", async context =>
        {
            var folder = context.Folder();
            var output = Path.Combine(folder, "never.mp4");
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            var thrown = await Catch(() => new StudioExporter().ExportAsync(Projects.Create(clips.Screen, null), Projects.Events(clips.Screen), Projects.Paths(folder, clips.Screen, null), output, null, null, cancel.Token)).ConfigureAwait(false);
            context.Expect(thrown is OperationCanceledException, $"threw {thrown?.GetType().Name ?? "nothing"}, want OperationCanceledException");
            context.Expect(Directory.GetFiles(folder).Length == 0, "a file was written");
        });

        await harness.Check("robustness", "failure: the camera file is missing", async context =>
        {
            var folder = context.Folder();
            var output = Path.Combine(folder, "out.mp4");
            var missing = clips.Camera with { Path = Path.Combine(folder, "camera.mp4") };
            var thrown = await Catch(() => new StudioExporter().ExportAsync(Projects.Create(clips.Screen, missing), Projects.Events(clips.Screen), Projects.Paths(folder, clips.Screen, missing), output)).ConfigureAwait(false);
            context.Expect(thrown is FileNotFoundException, $"threw {thrown?.GetType().Name ?? "nothing"}, want FileNotFoundException");
            context.Expect(thrown?.Message.Contains("camera", StringComparison.OrdinalIgnoreCase) == true, $"the message does not say which file: {thrown?.Message}");
            context.Expect(Directory.GetFiles(folder).Length == 0, "a file was written");
            context.Note($"message: {thrown?.Message}");
        });

        await harness.Check("robustness", "failure: the output folder does not exist", async context =>
        {
            var folder = context.Folder();
            var output = Path.Combine(folder, "no-such-folder", "out.mp4");
            var thrown = await Catch(() => new StudioExporter().ExportAsync(Projects.Create(clips.Screen, null), Projects.Events(clips.Screen), Projects.Paths(folder, clips.Screen, null), output)).ConfigureAwait(false);
            context.Expect(thrown is DirectoryNotFoundException, $"threw {thrown?.GetType().Name ?? "nothing"}, want DirectoryNotFoundException");
            context.Expect(!Directory.Exists(Path.Combine(folder, "no-such-folder")) && Directory.GetFiles(folder).Length == 0, "something was written");
            context.Note($"message: {thrown?.Message}");
        });

        await harness.Check("robustness", "failure: the file at the output path cannot be replaced", async context =>
        {
            var folder = context.Folder();
            var output = Path.Combine(folder, "locked.mp4");
            await File.WriteAllTextAsync(output, "in use").ConfigureAwait(false);
            Exception? thrown;
            await using (new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var project = Projects.Create(clips.Screen, null) with { Edits = new StudioEdits { TrimStart = 1, TrimEnd = 2 } };
                thrown = await Catch(() => new StudioExporter().ExportAsync(project, Projects.Events(clips.Screen), Projects.Paths(folder, clips.Screen, null), output)).ConfigureAwait(false);
            }

            context.Expect(thrown is StudioExportException, $"threw {thrown?.GetType().Name ?? "nothing"}, want StudioExportException");
            context.Expect(Directory.GetFiles(folder).Length == 1 && await File.ReadAllTextAsync(output).ConfigureAwait(false) == "in use", "the folder holds something besides the untouched earlier file");
            context.Note($"message: {thrown?.Message}");
        });

        await harness.Check("robustness", "failure: the screen file is not a video", async context =>
        {
            var folder = context.Folder();
            var output = Path.Combine(folder, "out.mp4");
            var broken = clips.Screen with { Path = Path.Combine(folder, "screen.mp4") };
            await File.WriteAllTextAsync(broken.Path, "this is not a video").ConfigureAwait(false);
            var thrown = await Catch(() => new StudioExporter().ExportAsync(Projects.Create(broken, null), Projects.Events(broken), Projects.Paths(folder, broken, null), output)).ConfigureAwait(false);
            context.Expect(thrown is StudioExportException, $"threw {thrown?.GetType().Name ?? "nothing"}, want StudioExportException");
            context.Expect(Directory.GetFiles(folder).Length == 1, "something besides the broken source is in the folder");
            context.Note($"message: {thrown?.Message}");
        });

        await harness.Check("robustness", "failure: the trim leaves less than one frame", async context =>
        {
            var folder = context.Folder();
            var output = Path.Combine(folder, "out.mp4");
            var project = Projects.Create(clips.Screen, null) with { Edits = new StudioEdits { TrimStart = 1, TrimEnd = 1.01 } };
            var thrown = await Catch(() => new StudioExporter().ExportAsync(project, Projects.Events(clips.Screen), Projects.Paths(folder, clips.Screen, null), output)).ConfigureAwait(false);
            context.Expect(thrown is InvalidOperationException, $"threw {thrown?.GetType().Name ?? "nothing"}, want InvalidOperationException");
            context.Expect(Directory.GetFiles(folder).Length == 0, "a file was written");
            context.Note($"message: {thrown?.Message}");
        });

        await harness.Check("robustness", "the calls return at once and do nothing on the caller's thread", async context =>
        {
            var folder = context.Folder();
            var project = Projects.Create(clips.Screen, clips.Camera);
            var paths = Projects.Paths(folder, clips.Screen, clips.Camera);
            var c = CultureInfo.InvariantCulture;

            var export = await CallerThread.Watch(() => new StudioExporter().ExportAsync(project, Projects.Events(clips.Screen), paths, Path.Combine(folder, "out.mp4"))).ConfigureAwait(false);
            context.Expect(!export.CompletedOnReturn, "ExportAsync had already finished when it returned");
            context.Expect(export.ReturnedAfterMilliseconds < 50, string.Create(c, $"ExportAsync took {export.ReturnedAfterMilliseconds:0.0} ms to return"));
            context.Expect(export.Posts == 0, $"ExportAsync posted {export.Posts} continuations to the caller's thread");
            context.Expect(export.Failure is null, $"the export failed: {export.Failure?.Message}");

            var poster = await CallerThread.Watch(() => new StudioExporter().WritePosterAsync(project, Projects.Events(clips.Screen), paths, Path.Combine(folder, "poster.jpg"), 640, 0)).ConfigureAwait(false);
            context.Expect(!poster.CompletedOnReturn, "WritePosterAsync had already finished when it returned");
            context.Expect(poster.ReturnedAfterMilliseconds < 50, string.Create(c, $"WritePosterAsync took {poster.ReturnedAfterMilliseconds:0.0} ms to return"));
            context.Expect(poster.Posts == 0, $"WritePosterAsync posted {poster.Posts} continuations to the caller's thread");
            context.Expect(poster.Failure is null, $"the poster failed: {poster.Failure?.Message}");
            context.Measure(string.Create(c, $"ExportAsync returned after {export.ReturnedAfterMilliseconds:0.0} ms and ran {export.TotalMilliseconds:0} ms; WritePosterAsync returned after {poster.ReturnedAfterMilliseconds:0.0} ms and ran {poster.TotalMilliseconds:0} ms"));
        });
    }

    private static async Task Poster(Harness harness)
    {
        await harness.Check("poster", "poster: a JPEG of the frame at 1 s", async context =>
        {
            var clips = harness.Clips;
            var folder = context.Folder();
            var posterPath = Path.Combine(folder, "poster.jpg");
            var project = Projects.Create(clips.Screen, clips.Camera) with
            {
                Canvas = new StudioCanvas { Background = new StudioBackground { Style = StudioBackgroundStyle.Solid, Primary = "#C86432" } },
                Screen = new StudioScreenStyle { Shadow = 0 },
            };

            // Something is already there: the poster replaces it.
            await File.WriteAllTextAsync(posterPath, "old").ConfigureAwait(false);
            await new StudioExporter().WritePosterAsync(project, Projects.Events(clips.Screen), Projects.Paths(folder, clips.Screen, clips.Camera), posterPath, 640, 1.0).ConfigureAwait(false);
            context.Expect(Directory.GetFiles(folder).Length == 1, $"{Directory.GetFiles(folder).Length} files in the folder, want the poster alone");

            var header = new byte[3];
            await using (var stream = File.OpenRead(posterPath))
            {
                await stream.ReadExactlyAsync(header).ConfigureAwait(false);
            }

            context.Expect(header is [0xFF, 0xD8, 0xFF], "the file does not start like a JPEG");
            var picture = Images.Load(posterPath);
            context.Expect(picture.Width == 640 && picture.Height == 360, $"the poster is {picture.Width}×{picture.Height}, want 640×360");

            // Output time 1 s is frame 30 of the export, which shows screen frame 30 and camera frame 24.
            var resolved = StudioLayoutResolver.Resolve(project, 1 + (0.5 / 30), picture.Width, picture.Height);
            var screenMap = Projects.ScreenMap(resolved.Screen!.Value, clips.Screen.Spec);
            var cameraMap = Projects.CameraMap(resolved.Camera!.Value, clips.Camera.Spec);
            var screenNumber = FrameCode.Decode(picture, clips.Screen.Spec, screenMap);
            var cameraNumber = FrameCode.Decode(picture, clips.Camera.Spec, cameraMap);
            context.Expect(screenNumber == 30, $"the poster shows screen frame {screenNumber}, want 30");
            context.Expect(cameraNumber == 24, $"the poster shows camera frame {cameraNumber}, want 24");

            var worst = 0.0;
            var worstWhere = string.Empty;
            var patches = FrameCode.ReadPatches(picture, clips.Screen.Spec, screenMap);
            for (var index = 0; index < patches.Length; index++)
            {
                Worst(patches[index].Distance(ClipSpec.PatchColors[index]), $"screen patch {index} is {patches[index]}");
            }

            var margin = resolved.Screen.Value.Rect.X / 2;
            var corner = picture.Average(margin, margin, 2);
            Worst(corner.Distance(Rgb.FromHex("#C86432")), $"the background is {corner}");
            var (cx, cy) = cameraMap.Apply(clips.Camera.Spec.BackgroundPoint.X, clips.Camera.Spec.BackgroundPoint.Y);
            var cameraBackground = picture.Average(cx, cy, 2);
            Worst(cameraBackground.Distance(clips.Camera.Spec.Background), $"the camera's background is {cameraBackground}");

            void Worst(double distance, string where)
            {
                if (distance > worst)
                {
                    worst = distance;
                    worstWhere = where;
                }
            }

            var c = CultureInfo.InvariantCulture;
            context.Expect(worst <= PosterColorTolerance, string.Create(c, $"colour is {worst:0.0} of 255 off ({worstWhere}); the tolerance is {PosterColorTolerance:0}"));
            context.Measure(string.Create(c, $"640×360 JPEG, {new FileInfo(posterPath).Length / 1024.0:0} KB, screen frame 30 and camera frame 24, colours within {worst:0.0} of 255 (tolerance {PosterColorTolerance:0})"));
        });
    }

    private static async Task Service(Harness harness)
    {
        await harness.Check("service", "service adapter: export and poster through IStudioExportService", async context =>
        {
            var clips = harness.Clips;
            var folder = context.Folder();
            var output = Path.Combine(folder, "service.mp4");
            var project = TwoSeconds(harness);
            var paths = Projects.Paths(folder, clips.Screen, clips.Camera);
            IStudioExportService service = new StudioExportService();

            var progress = new ThresholdProgress(2);
            await service.ExportAsync(project, Projects.Events(clips.Screen), paths, output, VideoCodec.H264, progress, CancellationToken.None).ConfigureAwait(false);
            var reading = ExportVerifier.Verify(context, output, new ExportExpectation(project, clips.Screen, clips.Camera, 1920, 1080, 30, 1, ExpectAudio: true));
            ExportChecks.ExpectNumbers(context, reading, 60, index => 30 + index, index => 24 + index);
            context.Expect(progress.Count > 0 && progress.Last == 1, $"progress ended at {progress.Last} after {progress.Count} reports");
            context.Expect(progress.Ordered, "progress went backwards");

            await service.WritePosterAsync(project, Projects.Events(clips.Screen), paths, CancellationToken.None).ConfigureAwait(false);
            context.Expect(File.Exists(paths.PosterPath), "no poster at the project's poster path");
            if (File.Exists(paths.PosterPath))
            {
                var picture = Images.Load(paths.PosterPath);
                context.Expect(picture.Width == 640 && picture.Height == 360, $"the poster is {picture.Width}×{picture.Height}, want 640×360");

                // The poster is the first frame of the export: screen frame 30, camera frame 24.
                var resolved = StudioLayoutResolver.Resolve(project, 1 + (0.5 / 30), picture.Width, picture.Height);
                var number = FrameCode.Decode(picture, clips.Screen.Spec, Projects.ScreenMap(resolved.Screen!.Value, clips.Screen.Spec));
                context.Expect(number == 30, $"the poster shows screen frame {number}, want 30");
            }
        });
    }

    private static async Task Warp(Harness harness)
    {
        await harness.Check("warp", "WARP: export with no graphics hardware", async context =>
        {
            var clips = harness.Clips;
            var (result, reading, seconds) = await Exporting.ExportAndVerify(context, "warp", TwoSeconds(harness), clips.Screen, clips.Camera, 1920, 1080, 30, 1, expectAudio: true, new StudioExportOptions(DevicePreference: StudioRenderDevicePreference.WarpOnly));
            context.Expect(result.SoftwareRendering, "the result says the frames were not drawn by WARP");
            context.Expect(!result.HardwareEncoder, $"the result says a hardware encoder was used: {result.EncoderDescription}");
            ExportChecks.ExpectNumbers(context, reading, 60, index => 30 + index, index => 24 + index);
            context.Expect(!result.EncoderDescription.Contains("system memory", StringComparison.Ordinal), $"WARP did not hand the encoder textures: {result.EncoderDescription}");
            context.Note("on WARP the encoder takes its frames as textures from its own allocator, as on graphics hardware");
        });

        await harness.Check("warp", "WARP: frames through system memory, the last resort", async context =>
        {
            // Injected: the attempt that hands the encoder textures fails on its fourth frame.
            var clips = harness.Clips;
            var exporter = new StudioExporter { Fault = (_, textureFrames, frame) => textureFrames && frame == 3 };
            var (result, reading, _) = await Exporting.ExportAndVerify(context, "warp-memory", TwoSeconds(harness), clips.Screen, clips.Camera, 1920, 1080, 30, 1, expectAudio: true, new StudioExportOptions(DevicePreference: StudioRenderDevicePreference.WarpOnly), exporter: exporter);
            context.Expect(result.SoftwareRendering && result.EncoderDescription.Contains("system memory", StringComparison.Ordinal), $"not the system-memory path on WARP: {result.EncoderDescription}");
            ExportChecks.ExpectNumbers(context, reading, 60, index => 30 + index, index => 24 + index);
        });

        await harness.Check("warp", "WARP: HEVC sources and a hardware-only request", async context =>
        {
            var clips = harness.Clips;
            var project = Projects.Create(clips.HevcScreen, clips.HevcCamera) with { Edits = new StudioEdits { TrimStart = 1, TrimEnd = 2 } };
            var (result, reading, _) = await Exporting.ExportAndVerify(context, "warp-hevc", project, clips.HevcScreen, clips.HevcCamera, 1920, 1080, 30, 1, expectAudio: true, new StudioExportOptions(DevicePreference: StudioRenderDevicePreference.WarpOnly));
            context.Expect(result.SoftwareRendering, "the result says the frames were not drawn by WARP");
            ExportChecks.ExpectNumbers(context, reading, 30, index => 30 + index, index => 24 + index);

            // With no graphics hardware there is no hardware encoder to insist on.
            var folder = context.Folder("hardware-only");
            var thrown = await Catch(() => new StudioExporter().ExportAsync(project, Projects.Events(clips.HevcScreen), Projects.Paths(folder, clips.HevcScreen, clips.HevcCamera), Path.Combine(folder, "out.mp4"), new StudioExportOptions(DevicePreference: StudioRenderDevicePreference.WarpOnly, EncoderPreference: StudioEncoderPreference.HardwareOnly))).ConfigureAwait(false);
            context.Expect(thrown is StudioExportException && Directory.GetFiles(folder).Length == 0, $"hardware only on WARP: {thrown?.GetType().Name ?? "nothing thrown"}, {Directory.GetFiles(folder).Length} files left");
            context.Note($"hardware only on WARP: {thrown?.Message}");
        });
    }

    private static async Task<Exception?> Catch(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>Progress that says when a value has been reached. Reports arrive on the export's thread.</summary>
    private sealed class ThresholdProgress(double threshold) : IProgress<double>
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Reached => _reached.Task;

        public double Last { get; private set; } = double.NaN;

        public int Count { get; private set; }

        public bool Ordered { get; private set; } = true;

        public void Report(double value)
        {
            if (Count > 0 && value < Last)
            {
                Ordered = false;
            }

            Last = value;
            Count++;
            if (value >= threshold)
            {
                _reached.TrySetResult();
            }
        }
    }
}

/// <summary>Reads an image file into a picture.</summary>
internal static class Images
{
    public static unsafe Picture Load(string path)
    {
        using var bitmap = new System.Drawing.Bitmap(path);
        var width = bitmap.Width;
        var height = bitmap.Height;
        var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, width, height), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new byte[width * height * 4];
            fixed (byte* target = pixels)
            {
                for (var row = 0; row < height; row++)
                {
                    Buffer.MemoryCopy((byte*)data.Scan0 + ((long)row * data.Stride), target + ((long)row * width * 4), width * 4, width * 4);
                }
            }

            return Picture.FromBgra(pixels, width, height);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}

/// <summary>
/// Calls an async method from a thread that has a synchronization context, as a UI thread does,
/// and reports what the method did to that thread: how long it kept it, and whether it sent
/// anything back to it.
/// </summary>
internal static class CallerThread
{
    public sealed record Observation(double ReturnedAfterMilliseconds, bool CompletedOnReturn, int Posts, double TotalMilliseconds, Exception? Failure);

    public static async Task<Observation> Watch(Func<Task> start)
    {
        var context = new CountingContext();
        var started = new TaskCompletionSource<(Task Task, double Milliseconds, bool Completed)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watch = Stopwatch.StartNew();
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                var call = Stopwatch.StartNew();
                var task = start();
                started.SetResult((task, call.Elapsed.TotalMilliseconds, task.IsCompleted));
            }
            catch (Exception ex)
            {
                started.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Pretend UI thread",
        };
        thread.Start();

        var (running, milliseconds, completed) = await started.Task.ConfigureAwait(false);
        Exception? failure = null;
        try
        {
            await running.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        return new Observation(milliseconds, completed, context.Posts, watch.Elapsed.TotalMilliseconds, failure);
    }

    private sealed class CountingContext : SynchronizationContext
    {
        private int _posts;

        public int Posts => Volatile.Read(ref _posts);

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _posts);
            base.Post(d, state);
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _posts);
            base.Send(d, state);
        }
    }
}
