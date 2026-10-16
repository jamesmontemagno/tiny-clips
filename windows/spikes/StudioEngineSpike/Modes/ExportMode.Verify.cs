using System.Globalization;
using StudioEngineSpike.Engine;

namespace StudioEngineSpike.Modes;

/// <summary>Checks an exported MP4 from the outside: ffprobe for structure, ffmpeg for pixels and PCM.</summary>
internal sealed partial class ExportSession
{
    private List<(double Time, double Frequency)>? _sourceBurstsFfmpeg;

    /// <returns>Mean offset in ms of the audio bursts from where the video says they belong (ffmpeg's decode).</returns>
    private double Verify(ExportResult result, int width, int height, (double Start, double End)[] segments, bool brief = false)
    {
        var path = result.Path!;
        var name = Path.GetFileName(path);
        var expectedDuration = segments.Sum(s => s.End - s.Start);
        _report.Line($"-- checking {name} (expected duration {expectedDuration.F("0.000")} s, {result.Expected.Count} frames)");

        // 1. Structure.
        var video = Probe(path, "v:0", "codec_name,profile,level,width,height,pix_fmt,r_frame_rate,avg_frame_rate,nb_frames,duration,start_time,bit_rate,has_b_frames,color_range,color_space,color_transfer,color_primaries");
        var audio = Probe(path, "a:0", "codec_name,profile,sample_rate,channels,nb_frames,duration,start_time,bit_rate");
        var container = ProbeFormat(path);
        _report.Line($"   video: {Describe(video)}");
        _report.Line($"   audio: {Describe(audio)}");
        _report.Line($"   container: {Describe(container)}");

        var packets = TestMedia.RunTool("ffprobe", new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time,flags", "-of", "csv=p=0", path }, null)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(','))
            .Where(parts => parts.Length >= 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            .Select(parts => (Pts: double.Parse(parts[0], CultureInfo.InvariantCulture), Key: parts[1].Contains('K')))
            .OrderBy(packet => packet.Pts)
            .ToList();
        if (packets.Count > 1)
        {
            var deltas = packets.Zip(packets.Skip(1), (a, b) => b.Pts - a.Pts).ToList();
            var keys = packets.Where(packet => packet.Key).Select(packet => packet.Pts).ToList();
            var keySpacing = keys.Zip(keys.Skip(1), (a, b) => b - a).ToList();
            _report.Line($"   video packets: {packets.Count}, first PTS {packets[0].Pts.F("0.0000")} s, last {packets[^1].Pts.F("0.0000")} s, spacing {(deltas.Min() * 1000).F("0.00")}–{(deltas.Max() * 1000).F("0.00")} ms; {keys.Count} keyframes, {(keySpacing.Count == 0 ? "-" : keySpacing.Min().F("0.00") + "–" + keySpacing.Max().F("0.00") + " s apart")}");
        }

        var videoDuration = Number(video, "duration");
        var audioDuration = Number(audio, "duration");
        var containerDuration = Number(container, "duration");
        _report.Line($"   duration: expected {expectedDuration.F("0.000")} s; container {containerDuration.F("0.000")} s ({((containerDuration - expectedDuration) * 1000).F("+0;-0;0")} ms), video {videoDuration.F("0.000")} s ({((videoDuration - expectedDuration) * 1000).F("+0;-0;0")} ms), audio {audioDuration.F("0.000")} s ({((audioDuration - expectedDuration) * 1000).F("+0;-0;0")} ms); stream start times video {Number(video, "start_time").F("0.0000")} s, audio {Number(audio, "start_time").F("0.0000")} s");

        // 2. Frame accuracy: decode every frame, crop each strip, read the numbers.
        var layout = SceneLayout.Resolve(_settings, width, height, TestMedia.Screen.Width, TestMedia.Screen.Height, TestMedia.Camera.Width, TestMedia.Camera.Height);
        var screenFrames = DecodeStrips(path, TestMedia.Screen, layout.ScreenMap(TestMedia.Screen), width, height);
        var cameraFrames = DecodeStrips(path, TestMedia.Camera, layout.CameraMap(TestMedia.Camera), width, height);
        var count = Math.Min(result.Expected.Count, Math.Min(screenFrames.Count, cameraFrames.Count));
        var screenWrong = new List<string>();
        var cameraWrong = new List<string>();
        for (var index = 0; index < count; index++)
        {
            if (screenFrames[index] != result.Expected[index].Screen)
            {
                screenWrong.Add($"#{index}: {screenFrames[index]} (want {result.Expected[index].Screen})");
            }

            if (cameraFrames[index] != result.Expected[index].Camera)
            {
                cameraWrong.Add($"#{index}: {cameraFrames[index]} (want {result.Expected[index].Camera})");
            }
        }

        var hidden = result.Expected.Count(e => e.Camera < 0);
        var countOk = screenFrames.Count == result.Expected.Count;
        _report.Line($"   frames decoded from the file: {screenFrames.Count} (written {result.Expected.Count}) {Verdict(countOk)}");
        _report.Line($"   screen frame number in every output frame: {count - screenWrong.Count} of {count} as expected {Verdict(screenWrong.Count == 0)}{(screenWrong.Count > 0 ? " — " + string.Join("; ", screenWrong.Take(8)) : string.Empty)}");
        _report.Line($"   camera frame number in every output frame: {count - cameraWrong.Count} of {count} as expected ({hidden} of them with the camera not yet visible) {Verdict(cameraWrong.Count == 0)}{(cameraWrong.Count > 0 ? " — " + string.Join("; ", cameraWrong.Take(8)) : string.Empty)}");
        if (count > 0)
        {
            _report.Line($"   first output frame shows screen {screenFrames[0]} / camera {cameraFrames[0]}, last shows screen {screenFrames[count - 1]} / camera {cameraFrames[count - 1]}");
        }

        _failures += (countOk ? 0 : 1) + (screenWrong.Count > 0 ? 1 : 0) + (cameraWrong.Count > 0 ? 1 : 0);

        // 3. Audio against video: where do the tone bursts land?
        var expectedBursts = new List<(int Second, double OutputTime)>();
        double outputOffset = 0;
        foreach (var (start, end) in segments)
        {
            for (var second = (int)Math.Ceiling(start); second < end && second < TestMedia.DurationSeconds; second++)
            {
                // A burst lasts 50 ms; skip one that the segment end would cut short.
                if (second + TestMedia.BurstSeconds <= end)
                {
                    expectedBursts.Add((second, outputOffset + (second - start)));
                }
            }

            outputOffset += end - start;
        }

        _sourceBurstsFfmpeg ??= DetectBursts(DecodeAudioWithFfmpeg(_screenPath), 48000);
        var sourceBias = MatchBursts(_sourceBurstsFfmpeg, Enumerable.Range(0, TestMedia.DurationSeconds).Select(second => (second, (double)second)).ToList(), out _, out _);
        var exported = DetectBursts(DecodeAudioWithFfmpeg(path), 48000);
        var offsets = MatchBursts(exported, expectedBursts, out var missing, out var misidentified);
        _report.Line($"   audio bursts (decoded by ffmpeg): {expectedBursts.Count - missing} of {expectedBursts.Count} found where the video says they belong, {misidentified} with the wrong pitch; onset − expected time: {offsets.Summary("ms")} min={offsets.Min.F()}");
        _report.Line($"     the same measurement on the source file: {sourceBias.Summary("ms")} min={sourceBias.Min.F()} → audio is {(offsets.Mean - sourceBias.Mean).F("+0.00;-0.00")} ms {(offsets.Mean - sourceBias.Mean >= 0 ? "later" : "earlier")} relative to video than in the source");
        _failures += missing > 0 || misidentified > 0 ? 1 : 0;

        if (brief)
        {
            return offsets.Mean;
        }

        // The same through Media Foundation's own MP4 source and AAC decoder (what MediaPlayer hears).
        var mfSource = MatchBursts(DetectBursts(DecodeAudioWithMediaFoundation(_screenPath), 48000), Enumerable.Range(0, TestMedia.DurationSeconds).Select(second => (second, (double)second)).ToList(), out _, out _);
        var mfExported = MatchBursts(DetectBursts(DecodeAudioWithMediaFoundation(path), 48000), expectedBursts, out var mfMissing, out _);
        _report.Line($"   audio bursts (decoded by Media Foundation): {expectedBursts.Count - mfMissing} of {expectedBursts.Count} found; onset − expected: {mfExported.Summary("ms")} min={mfExported.Min.F()}; source file: {mfSource.Summary("ms")}");

        // 4. Colour through the whole chain: decode → Direct2D → encoder's RGB→YUV → file.
        if (_screenReference is not null && _cameraReference is not null && result.Frames > 20)
        {
            var frame = result.Frames / 2;
            var yuv = TestMedia.RunToolBinary("ffmpeg", new[]
            {
                "-v", "error", "-i", path, "-vf", $"select='eq(n\\,{frame})'", "-fps_mode", "passthrough", "-frames:v", "1",
                "-f", "rawvideo", "-pix_fmt", "yuv420p", "-",
            });
            if (yuv.Length == width * height * 3 / 2)
            {
                var screen = ReferenceColors.FromYuv420(yuv, width, height, TestMedia.Screen, layout.ScreenMap(TestMedia.Screen));
                var camera = ReferenceColors.FromYuv420(yuv, width, height, TestMedia.Camera, layout.CameraMap(TestMedia.Camera));
                _report.Line($"   colour of the screen card in output frame {frame} (file's YUV → BT.709 limited): {ReferenceColors.Compare(_screenReference, screen)}");
                _report.Line($"   colour of the camera bubble: {ReferenceColors.Compare(_cameraReference, camera)}");
            }
            else
            {
                _report.Line($"   colour check skipped: ffmpeg returned {yuv.Length} bytes for one frame");
            }

            var png = Path.Combine(_output, Path.GetFileNameWithoutExtension(path) + $"-frame{frame}.png");
            TestMedia.RunTool("ffmpeg", new[] { "-v", "error", "-y", "-i", path, "-vf", $"select='eq(n\\,{frame})'", "-fps_mode", "passthrough", "-frames:v", "1", png }, null);
            _report.Line($"   wrote {Path.GetRelativePath(_options.Root, png)}");
        }

        return offsets.Mean;
    }

    /// <summary>Frame number shown by one clip in every frame of an exported file.</summary>
    private static List<int> DecodeStrips(string path, ClipSpec clip, ClipMap map, int width, int height)
    {
        var bounds = FrameCode.Bounds(clip, map, width, height);

        // Even origin and size keep ffmpeg's crop exact on 4:2:0 video.
        var x = bounds.X & ~1;
        var y = bounds.Y & ~1;
        var w = Math.Min(width - x, ((bounds.X + bounds.Width - x) + 1) & ~1);
        var h = Math.Min(height - y, ((bounds.Y + bounds.Height - y) + 1) & ~1);
        var raw = TestMedia.RunToolBinary("ffmpeg", new[]
        {
            "-v", "error", "-i", path, "-vf", $"crop={w}:{h}:{x}:{y}:exact=1,format=gray", "-fps_mode", "passthrough",
            "-f", "rawvideo", "-pix_fmt", "gray", "-",
        });
        var frameBytes = w * h;
        var local = map.Offset(-x, -y);
        var result = new List<int>(raw.Length / frameBytes);
        for (var offset = 0; offset + frameBytes <= raw.Length; offset += frameBytes)
        {
            result.Add(FrameCode.Decode(raw.AsSpan(offset, frameBytes), w, h, 1, clip, local));
        }

        return result;
    }

    private static short[] DecodeAudioWithFfmpeg(string path)
    {
        var raw = TestMedia.RunToolBinary("ffmpeg", new[] { "-v", "error", "-i", path, "-vn", "-ac", "1", "-ar", "48000", "-f", "s16le", "-" });
        var samples = new short[raw.Length / 2];
        Buffer.BlockCopy(raw, 0, samples, 0, samples.Length * 2);
        return samples;
    }

    /// <summary>Mono PCM laid out on the timeline by the timestamps the Media Foundation reader reports.</summary>
    private static short[] DecodeAudioWithMediaFoundation(string path)
    {
        var format = new AudioEncoderSettings();
        using var reader = AudioReader.Open(path, format);
        var blocks = new List<(long Start, short[] Mono)>();
        long end = 0;
        while (reader.Read() is { } block)
        {
            var frames = block.Pcm.Length / format.BlockAlign;
            var mono = new short[frames];
            for (var index = 0; index < frames; index++)
            {
                var left = BitConverter.ToInt16(block.Pcm, index * format.BlockAlign);
                var right = BitConverter.ToInt16(block.Pcm, (index * format.BlockAlign) + 2);
                mono[index] = (short)((left + right) / 2);
            }

            var start = (long)Math.Round(block.Time * (double)format.SampleRate / TimeSpan.TicksPerSecond);
            blocks.Add((start, mono));
            end = Math.Max(end, start + frames);
        }

        var result = new short[Math.Max(end, 1)];
        foreach (var (start, mono) in blocks)
        {
            if (start >= 0)
            {
                Array.Copy(mono, 0, result, start, Math.Min(mono.Length, result.Length - start));
            }
        }

        return result;
    }

    /// <summary>Finds tone bursts: their onset time and pitch (from zero crossings over 40 ms).</summary>
    private static List<(double Time, double Frequency)> DetectBursts(short[] mono, int rate)
    {
        var result = new List<(double, double)>();
        const int quietLevel = 600;
        const int loudLevel = 3000;
        var quietNeeded = rate / 50;
        var quiet = quietNeeded;
        for (var index = 0; index < mono.Length; index++)
        {
            var level = Math.Abs((int)mono[index]);
            if (level < quietLevel)
            {
                quiet++;
                continue;
            }

            if (level > loudLevel && quiet >= quietNeeded)
            {
                // Walk back to where the signal first left the quiet band (at most 2 ms).
                var onset = index;
                while (onset > 0 && index - onset < rate / 500 && Math.Abs((int)mono[onset - 1]) >= quietLevel)
                {
                    onset--;
                }

                var end = Math.Min(mono.Length - 1, onset + (rate * 40 / 1000));
                var crossings = 0;
                for (var cursor = onset + 1; cursor <= end; cursor++)
                {
                    if ((mono[cursor - 1] < 0) != (mono[cursor] < 0))
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

    /// <summary>Onset − expected time in ms for each expected burst found within 0.3 s.</summary>
    private static Samples MatchBursts(List<(double Time, double Frequency)> detected, List<(int Second, double OutputTime)> expected, out int missing, out int misidentified)
    {
        var offsets = new Samples();
        missing = 0;
        misidentified = 0;
        foreach (var (second, time) in expected)
        {
            var candidates = detected.Where(burst => Math.Abs(burst.Time - time) < 0.3).ToList();
            if (candidates.Count == 0)
            {
                missing++;
                continue;
            }

            var best = candidates.OrderBy(burst => Math.Abs(burst.Time - time)).First();
            offsets.Add((best.Time - time) * 1000);
            if (Math.Abs(best.Frequency - TestMedia.BurstFrequency(second)) > 60)
            {
                misidentified++;
            }
        }

        return offsets;
    }

    private static Dictionary<string, string> Probe(string path, string stream, string entries) =>
        Parse(TestMedia.RunTool("ffprobe", new[] { "-v", "error", "-select_streams", stream, "-show_entries", "stream=" + entries, "-of", "default=nw=1", path }, null));

    private static Dictionary<string, string> ProbeFormat(string path) =>
        Parse(TestMedia.RunTool("ffprobe", new[] { "-v", "error", "-show_entries", "format=duration,start_time,bit_rate,size", "-of", "default=nw=1", path }, null));

    private static Dictionary<string, string> Parse(string output)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf('=');
            if (separator > 0)
            {
                result[line[..separator]] = line[(separator + 1)..];
            }
        }

        return result;
    }

    private static string Describe(Dictionary<string, string> values) => values.Count == 0 ? "(none)" : string.Join(", ", values.Select(pair => $"{pair.Key}={pair.Value}"));

    private static double Number(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : double.NaN;
}
