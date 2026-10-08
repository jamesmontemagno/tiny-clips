using System.Globalization;

namespace TinyClips.Tools.StudioRenderCheck;

/// <summary>
/// The clips are checked before anything is exported from them, so a bad source cannot make a
/// good exporter look wrong, or hide a bad one.
/// </summary>
internal static class SourceChecks
{
    /// <summary>Colour in a source clip may be this far from what was drawn (it has been through H.264 once).</summary>
    private const double ColorTolerance = 4;

    public static async Task Run(Harness harness)
    {
        await harness.Check("sources", "source clip: screen, 1920×1080 at 30 fps with sound", context => Verify(context, harness.Clips.Screen));
        await harness.Check("sources", "source clip: camera, 1280×720 at 30 fps", context => Verify(context, harness.Clips.Camera));
        await harness.Check("sources", "source clip: camera that ends after 3 s", context => Verify(context, harness.Clips.ShortCamera));
        await harness.Check("sources", "source clip: screen, 1280×720 at 29.97 fps with sound", context => Verify(context, harness.Clips.Screen2997));
        await harness.Check("sources", "source clip: screen, 1280×720 at 60 fps with sound", context => Verify(context, harness.Clips.Screen60));
        await harness.Check("sources", "source clip: screen, 1280×720 at 25 fps with sound", context => Verify(context, harness.Clips.Screen25));
        await harness.Check("sources", "source clip: screen, 640×480 with sound", context => Verify(context, harness.Clips.SmallScreen));
        await harness.Check("sources", "source clip: camera, 640×480", context => Verify(context, harness.Clips.SmallCamera));
        await harness.Check("sources", "source clip: screen, 1920×1080 HEVC with sound", context => Verify(context, harness.Clips.HevcScreen));
        await harness.Check("sources", "source clip: camera, 1280×720 HEVC", context => Verify(context, harness.Clips.HevcCamera));
        await harness.Check("sources", "source clip: screen, 640×480 tagged BT.709", context => Verify(context, harness.Clips.SmallScreen709));
        await harness.Check("sources", "source clip: screen, 1280×720 tagged BT.601", context => Verify(context, harness.Clips.Screen601));
        await harness.Check("sources", "source clip: screen, 1280×720 with 44.1 kHz mono sound", context => Verify(context, harness.Clips.MonoScreen));
        await harness.Check("sources", "source clip: screen, 1280×720 with 32 kHz stereo sound", context => Verify(context, harness.Clips.Screen32k));
        await harness.Check("sources", "source clip: screen, 1280×720 with 32 kHz mono sound", context => Verify(context, harness.Clips.Screen32kMono));
        await harness.Check("sources", "source clip: screen, 1280×720 with 5.1 sound", context => Verify(context, harness.Clips.SurroundScreen));
        await harness.Check("sources", "source clip: screen, 1280×720 with dropped frames", context => Verify(context, harness.Clips.GappyScreen));
        await harness.Check("sources", "source clip: screen, 1280×720 with a field of fine stripes", context => Verify(context, harness.Clips.StripedScreen));
        await harness.Check("sources", "source clip: screen, 2560×1440, 20 s with sound", context => Verify(context, harness.Clips.BigScreen));
        await harness.Check("sources", "source clip: camera, 1280×720, 20 s", context => Verify(context, harness.Clips.LongCamera));
        await FrameSourceChecks.Run(harness).ConfigureAwait(false);
    }

    public static void Verify(CheckContext context, TestClip clip)
    {
        var c = CultureInfo.InvariantCulture;
        var spec = clip.Spec;
        var info = Media.Probe(clip.Path);
        var encoding = clip.Encoding ?? ClipEncoding.Tagged709;
        context.Expect(info.IsHevc == encoding.Hevc, $"the clip is {(info.IsHevc ? "HEVC" : "H.264")}");
        if (encoding.Tagged)
        {
            context.Expect(info.Matrix == (encoding.Bt601 ? 2u : 1u) && info.Primaries == 2 && info.Transfer == 5 && info.NominalRange == 2, $"the clip's colour tags are: {info.ColorTags}");
        }
        else if (!encoding.Hevc)
        {
            context.Expect(info.Matrix is null && info.Primaries is null, $"a clip written without colour tags has: {info.ColorTags}");
        }
        else if (info.Matrix is not null)
        {
            // Nothing asked for tags, and the HEVC encoder wrote them anyway.
            context.Expect(info.Matrix == (encoding.Bt601 ? 2u : 1u), $"the HEVC encoder tagged the clip with: {info.ColorTags}");
            context.Note($"written without colour tags, and the HEVC encoder tagged it itself: {info.ColorTags}");
        }


        context.Expect(info.Width == spec.Width && info.Height == spec.Height, $"size {info.Width}×{info.Height}, want {spec.Width}×{spec.Height}");
        if (clip.FrameTimes is null)
        {
            context.Expect(
                (ulong)info.FrameRateNumerator * clip.Denominator == (ulong)clip.Numerator * info.FrameRateDenominator,
                $"frame rate {info.FrameRateNumerator}/{info.FrameRateDenominator}, want {clip.Numerator}/{clip.Denominator}");
        }
        else
        {
            var probed = TinyClips.Core.Studio.MediaFileProbe.Probe(clip.Path);
            context.Note(string.Create(c, $"frames are not evenly spaced; the file says {info.FrameRateNumerator}/{info.FrameRateDenominator} fps, and the app's probe reads {probed.FrameRate:0.###} fps and {probed.Duration:0.000} s"));
        }
        context.Expect(info.HasAudio == clip.HasAudio, clip.HasAudio ? "no sound track" : "a sound track that should not be there");

        var wrongNumbers = new List<string>();
        var wrongTimes = new List<string>();
        var worstColor = 0.0;
        var worstColorWhere = string.Empty;
        var count = Media.ReadFrames(clip.Path, chroma: true, frame =>
        {
            var picture = frame.Picture;
            if (picture.Width != spec.Width || picture.Height != spec.Height)
            {
                wrongNumbers.Add($"frame {frame.Index} decodes to {picture.Width}×{picture.Height}");
                return false;
            }

            var number = FrameCode.Decode(picture, spec, ClipMap.Identity);
            if (number != frame.Index)
            {
                wrongNumbers.Add($"frame {frame.Index} reads {number}");
            }

            var time = frame.Index < clip.Frames ? clip.FrameTime(frame.Index) : -1;
            if (Math.Abs(frame.Time - time) > 5000)
            {
                wrongTimes.Add($"frame {frame.Index} is at {frame.Time / 10000.0:0.00} ms, want {time / 10000.0:0.00}");
            }

            // Colours on the first, a middle and the last frame.
            if (frame.Index == 0 || frame.Index == clip.Frames / 2 || frame.Index == clip.Frames - 1)
            {
                var patches = FrameCode.ReadPatches(picture, spec, ClipMap.Identity);
                for (var index = 0; index < patches.Length; index++)
                {
                    Worst(patches[index].Distance(ClipSpec.PatchColors[index]), $"patch {index} is {patches[index]}");
                }

                if (!(FrameCode.PatchEdgeError(picture, spec, ClipMap.Identity) <= 0.5))
                {
                    wrongNumbers.Add($"frame {frame.Index}: the picture is not where it was drawn (an edge is {FrameCode.PatchEdgeError(picture, spec, ClipMap.Identity):0.00} px off)");
                }

                var (x, y) = spec.BackgroundPoint;
                var background = picture.Average(x, y, 6);
                Worst(background.Distance(spec.Background), $"background is {background}");

                // The red block is top-right and the darker third is on the left: the picture is
                // the right way up and not mirrored.
                var marker = picture.Average(spec.MarkerX + (spec.MarkerWidth / 2.0), spec.MarkerY + (spec.MarkerHeight / 2.0), 4);
                Worst(marker.Distance(ClipSpec.MarkerColor), $"the top-right marker is {marker}");
                var left = picture.Average(spec.LeftWidth / 2.0, spec.Height * 0.75, 6);
                Worst(left.Distance(spec.LeftBackground), $"the left third is {left}");
            }

            return true;
        });

        void Worst(double distance, string where)
        {
            if (distance > worstColor)
            {
                worstColor = distance;
                worstColorWhere = where;
            }
        }

        context.Expect(count == clip.Frames, $"{count} frames, want {clip.Frames}");
        context.Expect(wrongNumbers.Count == 0, $"{wrongNumbers.Count} frames carry the wrong number ({wrongNumbers.FirstOrDefault()})");
        context.Expect(wrongTimes.Count == 0, $"{wrongTimes.Count} frames are off the frame grid ({wrongTimes.FirstOrDefault()})");
        context.Expect(worstColor <= ColorTolerance, string.Create(c, $"colour is {worstColor:0.0} of 255 from what was drawn ({worstColorWhere}); the tolerance is {ColorTolerance:0}"));
        var summary = string.Create(c, $"{encoding.Describe()}: {count} frames, every strip reads its own number; colours within {worstColor:0.0} of 255");

        if (!clip.HasAudio)
        {
            context.Note(summary);
            return;
        }

        var audio = Media.ReadAudio(clip.Path);
        if (audio is null)
        {
            context.Fail("the sound track cannot be decoded");
            return;
        }

        context.Expect(audio.SampleRate == clip.AudioRate && audio.Channels == clip.AudioChannels, $"sound is {audio.SampleRate} Hz with {audio.Channels} channels, want {clip.AudioRate} Hz with {clip.AudioChannels}");
        context.Expect(audio.FirstSample == 0, $"sound starts at sample {audio.FirstSample}");

        // An AAC track is padded to whole 1024-sample frames, so it may be up to 21.3 ms long.
        var extra = audio.DurationSeconds - clip.AudioSeconds;
        context.Expect(extra is > -0.0005 and < 0.0225, string.Create(c, $"sound lasts {audio.DurationSeconds:0.0000} s, want {clip.AudioSeconds:0.0000} s plus padding"));

        var rate = audio.SampleRate;
        var (lag, match) = Tones.Align(audio.Left, sample => Tones.Value(sample, rate), (long)Math.Round(clip.AudioSeconds * rate), maxLag: rate / 20);
        context.Expect(match > 0.5, string.Create(c, $"the sound does not match what was written (correlation {match:0.00})"));
        context.Expect(Math.Abs(lag) <= ExportVerifier.AudioLagToleranceSamples, $"sound is {lag} samples off");

        var detected = Tones.DetectBursts(audio.Left, rate);
        var seconds = (int)Math.Floor(clip.AudioSeconds - Tones.BurstSeconds) + 1;
        var wrongBursts = new List<string>();
        var maxOffset = 0.0;
        for (var second = 0; second < seconds; second++)
        {
            var nearest = detected.OrderBy(burst => Math.Abs(burst.Time - second)).FirstOrDefault();
            if (detected.Count == 0 || Math.Abs(nearest.Time - second) > Tones.OnsetTolerance(second, rate))
            {
                wrongBursts.Add($"no burst at {second} s");
            }
            else if (Math.Abs(nearest.Frequency - Tones.Frequency(second)) > 60)
            {
                wrongBursts.Add(string.Create(c, $"the burst at {second} s has pitch {nearest.Frequency:0} Hz"));
            }
            else if (second > 0)
            {
                maxOffset = Math.Max(maxOffset, Math.Abs(nearest.Time - second) * 1000);
            }
        }

        context.Expect(wrongBursts.Count == 0, string.Create(c, $"{wrongBursts.Count} of {seconds} tone bursts wrong ({wrongBursts.FirstOrDefault()}; found at {string.Join(", ", detected.Select(burst => burst.Time.ToString("0.0000", c)))} s)"));

        // A clip that does not end on a whole second ends with the start of one more burst.
        var stray = detected.Where(burst => Math.Abs(burst.Time - Math.Round(burst.Time)) > Tones.OnsetTolerance(Math.Round(burst.Time), rate) || Math.Round(burst.Time) >= clip.AudioSeconds).ToList();
        context.Expect(stray.Count == 0, string.Create(c, $"{stray.Count} bursts where there should be none (first at {stray.FirstOrDefault().Time:0.0000} s)"));
        if (clip.AudioChannels > 1)
        {
            var left = Tones.Rms(audio.Left);
            var ratio = left > 0 ? Tones.Rms(audio.Right) / left : 0;
            context.Expect(ratio is > 0.4 and < 0.6, string.Create(c, $"right channel is {ratio:0.00} of the left, want 0.50"));
        }
        var first = detected.Count > 0 ? detected[0].Time * 1000 : double.NaN;
        context.Note(string.Create(c, $"{summary}; sound {audio.DurationSeconds:0.0000} s, {seconds} bursts: the one at 0 s is heard from {first:0.0} ms (the track fades in), the others within {maxOffset:0.00} ms of their second; lag {lag} samples (correlation {match:0.00})"));
    }
}
