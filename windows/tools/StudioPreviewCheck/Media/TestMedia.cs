using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace TinyClips.Tools.StudioPreviewCheck.Media;

/// <summary>Geometry of one generated clip, its frame-number strip and its colour patches, in source pixels.</summary>
internal sealed record ClipSpec(
    string FileName,
    string Label,
    int Width,
    int Height,
    int Seconds,
    bool HasAudio,
    int CodeX,
    int CodeY,
    int CodeCell,
    int PatchX,
    int PatchY,
    int PatchSize,
    int PatchPitch)
{
    public int FrameCount => Seconds * TestMedia.Fps;

    public int CodeWidth => TestMedia.CodeBits * CodeCell;

    public int CodeHeight => CodeCell * 2;
}

/// <summary>
/// The generated source clips. Every frame carries its frame number twice: as large text, for a
/// person looking at a screenshot, and as a strip of black and white cells, for the checks. The
/// strip has two rows: the 12 bits of the number, most significant first, and their complement
/// underneath. A read counts only when every column has one bright and one dark cell, so a strip
/// that is hidden, half covered, or a blend of two frames never reads as a wrong number.
/// </summary>
internal static class TestMedia
{
    public const int Fps = 30;
    public const int CodeBits = 12;

    // Bump when the clips change, so that clips from an older build are made again.
    private const string Stamp = "studio-preview-check clips v1";

    /// <summary>1920×1080, 12 s, with the tone-burst audio track a real screen recording would have.</summary>
    public static readonly ClipSpec Screen = new("screen.mp4", "SCREEN", 1920, 1080, 12, HasAudio: true, CodeX: 192, CodeY: 48, CodeCell: 48, PatchX: 192, PatchY: 450, PatchSize: 72, PatchPitch: 96);

    /// <summary>1280×720, 12 s, no audio. The strip sits in the centre so it survives the square crop, the circle mask and mirroring.</summary>
    public static readonly ClipSpec Camera = new("camera.mp4", "CAM", 1280, 720, 12, HasAudio: false, CodeX: 352, CodeY: 312, CodeCell: 48, PatchX: 448, PatchY: 440, PatchSize: 64, PatchPitch: 96);

    /// <summary>The same camera, half as long, for a camera that ends before the screen does.</summary>
    public static readonly ClipSpec ShortCamera = Camera with { FileName = "camera-short.mp4", Seconds = 6 };

    public static readonly ClipSpec[] All = [Screen, Camera, ShortCamera];

    /// <summary>Flat patches drawn into every frame: ffmpeg's name and the nominal colour.</summary>
    public static readonly (string Name, byte R, byte G, byte B)[] PatchColors =
    [
        ("red", 255, 0, 0),
        ("lime", 0, 255, 0),
        ("blue", 0, 0, 255),
        ("gray", 128, 128, 128),
    ];

    public static string PathOf(string mediaDirectory, ClipSpec clip) => Path.Combine(mediaDirectory, clip.FileName);

    /// <summary>Generates the clips that are missing with ffmpeg, and checks each one with ffprobe.</summary>
    public static void Ensure(string mediaDirectory, Report report)
    {
        Directory.CreateDirectory(mediaDirectory);
        var stampPath = Path.Combine(mediaDirectory, "stamp.txt");
        var current = File.Exists(stampPath) && File.ReadAllText(stampPath).Trim() == Stamp;
        foreach (var clip in All)
        {
            var path = PathOf(mediaDirectory, clip);
            if (current && File.Exists(path))
            {
                report.Line($"{clip.FileName}: present ({new FileInfo(path).Length / 1024.0 / 1024.0:0.0} MB)");
            }
            else
            {
                var watch = Stopwatch.StartNew();
                Run("ffmpeg", Arguments(clip, path));
                report.Line($"{clip.FileName}: generated in {watch.Elapsed.TotalSeconds:0.0} s ({new FileInfo(path).Length / 1024.0 / 1024.0:0.0} MB)");
            }

            var probe = Run("ffprobe",
            [
                "-v", "error", "-select_streams", "v:0", "-count_packets",
                "-show_entries", "stream=width,height,r_frame_rate,nb_read_packets",
                "-of", "csv=p=0", path,
            ]).Trim();
            var expected = string.Create(CultureInfo.InvariantCulture, $"{clip.Width},{clip.Height},{Fps}/1,{clip.FrameCount}");
            if (probe != expected)
            {
                throw new InvalidOperationException($"{clip.FileName} is not what was asked for: ffprobe says '{probe}', expected '{expected}'.");
            }

            var audio = Run("ffprobe", ["-v", "error", "-select_streams", "a", "-show_entries", "stream=codec_name", "-of", "csv=p=0", path]).Trim();
            if ((audio.Length > 0) != clip.HasAudio)
            {
                throw new InvalidOperationException($"{clip.FileName}: audio track present = {audio.Length > 0}, expected {clip.HasAudio}.");
            }
        }

        File.WriteAllText(stampPath, Stamp);
    }

    private static List<string> Arguments(ClipSpec clip, string path)
    {
        var c = CultureInfo.InvariantCulture;
        var filter = new StringBuilder("[0:v]");
        if (!clip.HasAudio)
        {
            // Tint the camera so the two sources are easy to tell apart in a screenshot.
            filter.Append("hue=h=120,");
        }

        filter.Append(c, $"drawbox=x={clip.CodeX}:y={clip.CodeY}:w={clip.CodeWidth}:h={clip.CodeHeight}:color=black:t=fill,");
        for (var column = 0; column < CodeBits; column++)
        {
            var weight = 1 << (CodeBits - 1 - column);
            var x = clip.CodeX + (column * clip.CodeCell);
            filter.Append(c, $"drawbox=x={x}:y={clip.CodeY}:w={clip.CodeCell}:h={clip.CodeCell}:color=white:t=fill:enable='eq(mod(floor(n/{weight}),2),1)',");
            filter.Append(c, $"drawbox=x={x}:y={clip.CodeY + clip.CodeCell}:w={clip.CodeCell}:h={clip.CodeCell}:color=white:t=fill:enable='eq(mod(floor(n/{weight}),2),0)',");
        }

        for (var index = 0; index < PatchColors.Length; index++)
        {
            filter.Append(c, $"drawbox=x={clip.PatchX + (index * clip.PatchPitch)}:y={clip.PatchY}:w={clip.PatchSize}:h={clip.PatchSize}:color={PatchColors[index].Name}:t=fill,");
        }

        // The number as text is only for people. It needs a font file; without one it is left out.
        if (File.Exists(@"C:\Windows\Fonts\consolab.ttf"))
        {
            var textX = clip.HasAudio ? clip.CodeX.ToString(c) : "(w-text_w)/2";
            var textY = clip.HasAudio ? (clip.CodeY + clip.CodeHeight + 44).ToString(c) : (clip.CodeY - 150).ToString(c);
            filter.Append(c, $"drawtext=fontfile='C\\:/Windows/Fonts/consolab.ttf':text='{clip.Label} %{{frame_num}}':fontsize=84:fontcolor=white:box=1:boxcolor=black@0.8:boxborderw=14:x={textX}:y={textY},");
        }

        filter.Append("scale=out_color_matrix=bt709:out_range=tv,format=yuv420p[v]");

        // The shape of a Tiny Clips recording: H.264 High, a two second GOP, no B-frames.
        var bitrate = Math.Clamp((long)clip.Width * clip.Height * Fps / 10, 2_000_000, 24_000_000);
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", string.Create(c, $"testsrc2=size={clip.Width}x{clip.Height}:rate={Fps}:duration={clip.Seconds}"),
        };
        if (clip.HasAudio)
        {
            // A 50 ms tone burst at the start of every second, its pitch naming the second.
            var burst = "0.5*sin(2*PI*(400+100*floor(t))*t)*lt(mod(t,1),0.05)";
            args.AddRange(["-f", "lavfi", "-i", string.Create(c, $"aevalsrc=exprs='{burst}|{burst}':channel_layout=stereo:sample_rate=48000:duration={clip.Seconds}")]);
        }

        args.AddRange(["-filter_complex", filter.ToString(), "-map", "[v]"]);
        if (clip.HasAudio)
        {
            args.AddRange(["-map", "1:a", "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2"]);
        }
        else
        {
            args.Add("-an");
        }

        args.AddRange(
        [
            "-c:v", "libx264", "-profile:v", "high", "-preset", "veryfast", "-pix_fmt", "yuv420p",
            "-b:v", bitrate.ToString(c), "-maxrate", bitrate.ToString(c), "-bufsize", (bitrate * 2).ToString(c),
            "-g", (Fps * 2).ToString(c), "-keyint_min", (Fps * 2).ToString(c), "-sc_threshold", "0", "-bf", "0",
            "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "tv",
            "-r", Fps.ToString(c), "-movflags", "+faststart",
            path,
        ]);
        return args;
    }

    /// <summary>Runs ffmpeg or ffprobe from PATH and returns what it printed. Throws with its error text when it fails.</summary>
    private static string Run(string tool, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(tool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {tool}.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException($"{tool} is needed to generate the test clips and was not found on PATH.", ex);
        }

        using (process)
        {
            var errors = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"{tool} exited with {process.ExitCode}: {errors.GetAwaiter().GetResult()}");
            }

            return output;
        }
    }
}
