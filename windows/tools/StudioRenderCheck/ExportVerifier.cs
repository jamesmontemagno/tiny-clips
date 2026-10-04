using System.Globalization;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Rendering;

namespace TinyClips.Tools.StudioRenderCheck;

/// <summary>What an exported file has to be.</summary>
/// <param name="Width">The frame size expected, written out by the check rather than computed by the code under test.</param>
internal sealed record ExportExpectation(
    StudioProject Project,
    TestClip Screen,
    TestClip? Camera,
    int Width,
    int Height,
    uint RateNumerator,
    uint RateDenominator,
    bool ExpectAudio,
    bool Hevc = false);

/// <summary>What was read back out of an exported file.</summary>
internal sealed class ExportReading
{
    public required MediaInfo Info { get; init; }

    /// <summary>The screen frame number read from each output frame; -1 where no strip could be read.</summary>
    public required int[] Screen { get; init; }

    /// <summary>The camera frame number read from each output frame; -1 where no strip could be read.</summary>
    public required int[] Camera { get; init; }

    public required int FrameCount { get; init; }

    public DecodedAudio? Audio { get; set; }

    /// <summary>Samples the sound is late (positive) against the picture, by correlation.</summary>
    public int AudioLagSamples { get; set; }

    public double AudioMatch { get; set; }

    public double MaxBurstOffsetMs { get; set; }

    public int Bursts { get; set; }

    /// <summary>Sound end minus picture end, in milliseconds.</summary>
    public double AudioMinusVideoMs { get; set; }

    /// <summary>The largest difference, per channel of 255, between a colour patch in the file and the colour drawn into the source.</summary>
    public double WorstColor { get; set; }

    public int ColorSamples { get; set; }

    /// <summary>The largest distance, in pixels, between an edge in the picture and where the layout puts it.</summary>
    public double WorstEdge { get; set; }
}

/// <summary>
/// Checks an exported MP4 from the outside. Every frame is decoded again and the frame-number
/// strips of the screen and the camera are read where the layout puts them; the sound is decoded
/// and lined up against the bursts the kept ranges should contain.
/// </summary>
internal static class ExportVerifier
{
    /// <summary>The sound may sit this far from where the picture puts it: one millisecond.</summary>
    public const int AudioLagToleranceSamples = 48;

    /// <summary>
    /// A flat colour in an export may be this far, per channel of 255, from the colour drawn into
    /// the source. It has been through two encoders by then. The wrong YUV matrix moves the
    /// patches by 22 to 27, so this tells right from wrong with room to spare.
    /// </summary>
    public const double ColorTolerance = 8;

    /// <summary>
    /// An edge inside the screen or camera picture may be this far, in pixels, from where the
    /// layout puts it. Padding rows drawn as picture, or a picture letterboxed by its decoder,
    /// move edges by two pixels and more.
    /// </summary>
    public const double EdgeTolerance = 1;

    private const long FrameTimeToleranceTicks = 5000;

    public static ExportReading? Verify(CheckContext context, string path, ExportExpectation expectation, string label = "")
    {
        var prefix = label.Length == 0 ? string.Empty : label + ": ";
        if (!context.Expect(File.Exists(path), $"{prefix}no file was written"))
        {
            return null;
        }

        var c = CultureInfo.InvariantCulture;
        var info = Media.Probe(path);
        context.Expect(info.Width == expectation.Width && info.Height == expectation.Height, $"{prefix}frame size {info.Width}×{info.Height}, want {expectation.Width}×{expectation.Height}");

        // An MP4 file states a whole or an NTSC frame rate as it is. Any other it does not
        // store: Media Foundation reports the frames divided by the duration, to its own
        // precision (2967/100 reads back as 125000/4213). Every frame's time is checked below.
        var exactRate = (ulong)info.FrameRateNumerator * expectation.RateDenominator == (ulong)expectation.RateNumerator * info.FrameRateDenominator;
        var wantRate = expectation.RateNumerator / (double)expectation.RateDenominator;
        var gotRate = info.FrameRateDenominator == 0 ? 0 : info.FrameRateNumerator / (double)info.FrameRateDenominator;
        var commonRate = expectation.RateDenominator is 1 or 1001;
        context.Expect(
            commonRate ? exactRate : Math.Abs(gotRate - wantRate) <= wantRate * 1e-5,
            $"{prefix}frame rate {info.FrameRateNumerator}/{info.FrameRateDenominator}, want {expectation.RateNumerator}/{expectation.RateDenominator}");
        context.Expect(info.IsHevc == expectation.Hevc, $"{prefix}the video is {(info.IsHevc ? "HEVC" : "not HEVC")}");
        context.Expect(info.HasAudio == expectation.ExpectAudio, expectation.ExpectAudio ? $"{prefix}there is no sound track" : $"{prefix}there is a sound track and should be none");

        // BT.709 primaries (2) and transfer (5), limited range (2), and a matrix: 1 is BT.709, 2 is BT.601.
        context.Expect(info.Primaries == 2 && info.Transfer == 5 && info.NominalRange == 2 && info.Matrix is 1 or 2, $"{prefix}the stream's colour tags are incomplete: {info.ColorTags}");

        var fps = expectation.RateNumerator / (double)expectation.RateDenominator;
        var plan = StudioRenderingMath.BuildFramePlan(expectation.Project, fps);
        var layout = StudioLayoutPlan.Create(expectation.Project);
        var screenRead = new int[plan.Count];
        var cameraRead = new int[plan.Count];
        Array.Fill(screenRead, FrameCode.Unreadable);
        Array.Fill(cameraRead, FrameCode.Unreadable);
        var screenWrong = new List<string>();
        var cameraWrong = new List<string>();
        var timeWrong = new List<string>();
        var sizeWrong = false;
        var hiddenCamera = 0;
        var saved = 0;
        var worstColor = 0.0;
        var worstColorWhere = string.Empty;
        var colorSamples = 0;
        var worstEdge = 0.0;
        var worstEdgeWhere = string.Empty;
        var edgesMeasured = 0;

        void Edge(Picture picture, ClipSpec spec, ClipMap map, string what)
        {
            if (FrameCode.PatchEdgeError(picture, spec, map) is { } error)
            {
                // An edge that cannot be found at all is NaN, and stays the worst.
                edgesMeasured++;
                if (!double.IsNaN(worstEdge) && !(error <= worstEdge))
                {
                    worstEdge = error;
                    worstEdgeWhere = what;
                }
            }
        }

        // The first frames a check does not like are kept as pictures next to the export.
        void Keep(Picture picture, int index)
        {
            if (saved++ < 3)
            {
                picture.SavePng(Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}-frame{index}.png"));
            }
        }

        var decoded = Media.ReadFrames(path, chroma: true, frame =>
        {
            if (frame.Index >= plan.Count)
            {
                return true;
            }

            var picture = frame.Picture;
            if (picture.Width != expectation.Width || picture.Height != expectation.Height)
            {
                sizeWrong = true;
                return false;
            }

            var index = frame.Index;
            var sourceTime = plan[index].SourceTimeSeconds;
            var resolved = layout.Resolve(sourceTime, expectation.Width, expectation.Height);
            if (resolved.Screen is { } screen)
            {
                var want = expectation.Screen.FrameAt(sourceTime);
                var got = FrameCode.Decode(picture, expectation.Screen.Spec, Projects.ScreenMap(screen, expectation.Screen.Spec));
                screenRead[index] = got;
                if (got != want)
                {
                    screenWrong.Add($"frame {index} shows screen {Show(got)}, want {want}");
                    Keep(picture, index);
                }

                // Colour on the first, a middle and the last frame, where the whole screen is shown
                // large enough for its patches to be sampled.
                var screenMap = Projects.ScreenMap(screen, expectation.Screen.Spec);
                var whole = screen.Source is { X: 0, Y: 0, Width: 1, Height: 1 };
                if (whole && Math.Abs(screenMap.ScaleX) * expectation.Screen.Spec.PatchSize >= 12 && (index == 0 || index == plan.Count / 2 || index == plan.Count - 1))
                {
                    Edge(picture, expectation.Screen.Spec, screenMap, $"frame {index}, the screen");
                    var patches = FrameCode.ReadPatches(picture, expectation.Screen.Spec, screenMap);
                    for (var patch = 0; patch < patches.Length; patch++)
                    {
                        colorSamples++;
                        var distance = patches[patch].Distance(ClipSpec.PatchColors[patch]);
                        if (distance > worstColor)
                        {
                            worstColor = distance;
                            worstColorWhere = $"frame {index}, screen patch {patch} is {patches[patch]}, drawn {ClipSpec.PatchColors[patch]}";
                        }
                    }
                }
            }

            if (resolved.Camera is { } camera && expectation.Camera is { } cameraClip)
            {
                var map = Projects.CameraMap(camera, cameraClip.Spec);
                var got = FrameCode.Decode(picture, cameraClip.Spec, map);
                cameraRead[index] = got;
                if (camera.Visible)
                {
                    var want = cameraClip.FrameAt(camera.SourceTime);
                    if (got != want)
                    {
                        cameraWrong.Add($"frame {index} shows camera {Show(got)}, want {want}{(got == FrameCode.Unreadable ? "; " + FrameCode.Describe(picture, cameraClip.Spec, map) : string.Empty)}");
                        Keep(picture, index);
                    }

                    if (index % 30 == 15 || index == plan.Count - 1)
                    {
                        Edge(picture, cameraClip.Spec, map, $"frame {index}, the camera");
                    }
                }
                else
                {
                    hiddenCamera++;
                    var (x, y) = map.Apply(cameraClip.Spec.BackgroundPoint.X, cameraClip.Spec.BackgroundPoint.Y);
                    var there = picture.Average(x, y, 2);
                    if (got != FrameCode.Unreadable || there.Distance(cameraClip.Spec.Background) < 40)
                    {
                        cameraWrong.Add($"frame {index} shows the camera ({Show(got)}, {there}) while it should be hidden");
                    }
                }
            }

            var wantTime = Media.FrameTime(index, expectation.RateNumerator, expectation.RateDenominator);
            if (Math.Abs(frame.Time - wantTime) > FrameTimeToleranceTicks)
            {
                timeWrong.Add($"frame {index} is at {frame.Time / 10000.0:0.00} ms, want {wantTime / 10000.0:0.00}");
            }

            return true;
        });

        context.Expect(!sizeWrong, $"{prefix}decoded frames are not {expectation.Width}×{expectation.Height}");
        context.Expect(decoded == plan.Count, $"{prefix}{decoded} frames in the file, the plan has {plan.Count}");
        context.Expect(screenWrong.Count == 0, $"{prefix}screen frame number wrong in {screenWrong.Count} of {plan.Count} frames ({screenWrong.FirstOrDefault()})");
        context.Expect(cameraWrong.Count == 0, $"{prefix}camera wrong in {cameraWrong.Count} of {plan.Count} frames ({cameraWrong.FirstOrDefault()})");
        context.Expect(timeWrong.Count == 0, $"{prefix}{timeWrong.Count} frames are not on the frame grid ({timeWrong.FirstOrDefault()})");

        context.Expect(worstColor <= ColorTolerance, string.Create(c, $"{prefix}colour is {worstColor:0.0} of 255 off ({worstColorWhere}); the tolerance is {ColorTolerance:0}"));

        context.Expect(worstEdge <= EdgeTolerance, string.Create(c, $"{prefix}an edge in the picture is {worstEdge:0.00} px from where the layout puts it ({worstEdgeWhere}); the tolerance is {EdgeTolerance:0}"));

        var reading = new ExportReading { Info = info, Screen = screenRead, Camera = cameraRead, FrameCount = decoded, WorstColor = worstColor, ColorSamples = colorSamples, WorstEdge = worstEdge };
        var videoSeconds = plan.Count / fps;
        var summary = string.Create(c, $"{prefix}{info.Width}×{info.Height}, {info.FrameRateNumerator}/{info.FrameRateDenominator} fps, {decoded} frames; screen strip right in {plan.Count - screenWrong.Count} of {plan.Count}");
        if (expectation.Camera is not null)
        {
            summary += string.Create(c, $", camera right in {plan.Count - cameraWrong.Count} of {plan.Count} ({hiddenCamera} with it hidden)");
        }

        if (colorSamples > 0)
        {
            summary += string.Create(c, $"; colours within {worstColor:0.0} of 255 (matrix tag {info.Matrix})");
        }

        if (edgesMeasured > 0)
        {
            summary += string.Create(c, $"; picture edges within {worstEdge:0.00} px");
        }

        if (!expectation.ExpectAudio)
        {
            context.Expect(Media.ReadAudio(path) is null, $"{prefix}sound can be decoded from a file that should have none");
            context.Note(summary + "; no sound");
            return reading;
        }

        var audio = Media.ReadAudio(path);
        if (audio is null)
        {
            context.Note(summary);
            return reading;
        }

        reading.Audio = audio;
        var rate = audio.SampleRate;

        // Sound keeps its shape when every Windows AAC encoder takes it: 44.1 or 48 kHz, mono or
        // stereo. Any other rate comes out at 48 kHz, and more than two channels as stereo.
        var wantSoundRate = expectation.Screen.AudioRate is 44100 or 48000 ? expectation.Screen.AudioRate : 48000;
        var wantChannels = expectation.Screen.AudioChannels is 1 or 2 ? expectation.Screen.AudioChannels : 2;
        context.Expect(rate == wantSoundRate && audio.Channels == wantChannels, $"{prefix}sound is {rate} Hz with {audio.Channels} channels; the source has {expectation.Screen.AudioRate} Hz with {expectation.Screen.AudioChannels}, so want {wantSoundRate} Hz with {wantChannels}");
        context.Expect(audio.FirstSample == 0, $"{prefix}sound starts {audio.FirstSample} samples after the picture");
        reading.AudioMinusVideoMs = (audio.DurationSeconds - videoSeconds) * 1000;
        context.Expect(
            Math.Abs(audio.DurationSeconds - videoSeconds) <= (1 / fps) + 1e-9,
            string.Create(c, $"{prefix}sound lasts {audio.DurationSeconds:0.0000} s and picture {videoSeconds:0.0000} s: more than one frame apart"));

        // The kept ranges in samples, worked out here from the time map.
        var map = StudioTimeMap.FromProject(expectation.Project);
        var ranges = new List<(long SourceStart, long SourceEnd, long OutputStart)>();
        long outputCursor = 0;
        foreach (var segment in map.Segments)
        {
            var start = (long)Math.Round(segment.Start * rate, MidpointRounding.AwayFromZero);
            var end = (long)Math.Round(segment.End * rate, MidpointRounding.AwayFromZero);
            ranges.Add((start, end, outputCursor));
            outputCursor += end - start;
        }

        var sourceSamples = (long)Math.Round(expectation.Screen.AudioSeconds * rate);
        double Expected(long output)
        {
            foreach (var (sourceStart, sourceEnd, outputStart) in ranges)
            {
                if (output >= outputStart && output < outputStart + (sourceEnd - sourceStart))
                {
                    var source = sourceStart + (output - outputStart);
                    return source < sourceSamples ? Tones.Value(source, rate) : 0;
                }
            }

            return 0;
        }

        var (lag, match) = Tones.Align(audio.Left, Expected, Math.Min(outputCursor, audio.Length), maxLag: rate / 20);
        reading.AudioLagSamples = lag;
        reading.AudioMatch = match;
        context.Expect(match > 0.5, string.Create(c, $"{prefix}the sound does not match what the kept ranges contain (correlation {match:0.00})"));
        context.Expect(Math.Abs(lag) <= AudioLagToleranceSamples, $"{prefix}sound is {lag} samples from where the picture puts it");

        // Each burst that lies whole inside a kept range and inside the video.
        var detected = Tones.DetectBursts(audio.Left, rate);
        var missing = new List<string>();
        var maxOffset = 0.0;
        var bursts = 0;
        foreach (var segment in map.Segments)
        {
            for (var second = (int)Math.Ceiling(segment.Start - 1e-9); second + Tones.BurstSeconds <= segment.End && second < expectation.Screen.AudioSeconds; second++)
            {
                var output = map.SourceToOutput(second);
                if (output + Tones.BurstSeconds > videoSeconds)
                {
                    continue;
                }

                bursts++;
                var nearest = detected.OrderBy(burst => Math.Abs(burst.Time - output)).FirstOrDefault();
                if (detected.Count == 0 || Math.Abs(nearest.Time - output) > Tones.OnsetTolerance(output, rate))
                {
                    missing.Add(string.Create(c, $"the burst of second {second} is not at {output:0.000} s"));
                    continue;
                }

                if (output >= 1024.0 / rate)
                {
                    maxOffset = Math.Max(maxOffset, Math.Abs(nearest.Time - output) * 1000);
                }

                if (Math.Abs(nearest.Frequency - Tones.Frequency(second)) > 60)
                {
                    missing.Add(string.Create(c, $"the burst at {output:0.000} s has pitch {nearest.Frequency:0} Hz, want {Tones.Frequency(second):0}"));
                }
            }
        }

        reading.Bursts = bursts;
        reading.MaxBurstOffsetMs = maxOffset;
        context.Expect(missing.Count == 0, $"{prefix}{missing.Count} of {bursts} tone bursts wrong ({missing.FirstOrDefault()})");

        if (audio.Channels > 1)
        {
            var left = Tones.Rms(audio.Left);
            var ratio = left > 0 ? Tones.Rms(audio.Right) / left : 0;
            context.Expect(ratio is > 0.35 and < 0.65, string.Create(c, $"{prefix}right channel is {ratio:0.00} of the left, want 0.50"));
        }

        context.Note(string.Create(c, $"{summary}; sound {rate} Hz × {audio.Channels}, {audio.DurationSeconds:0.000} s against picture {videoSeconds:0.000} s ({reading.AudioMinusVideoMs:+0.0;-0.0} ms), {bursts} bursts within {maxOffset:0.00} ms, lag {lag} samples (correlation {match:0.00})"));
        return reading;
    }

    private static string Show(int number) => number == FrameCode.Unreadable ? "nothing readable" : number.ToString(CultureInfo.InvariantCulture);
}
