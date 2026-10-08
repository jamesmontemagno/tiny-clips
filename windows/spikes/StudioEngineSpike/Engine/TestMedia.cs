using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace StudioEngineSpike.Engine;

/// <summary>
/// The synthetic source clips and the machine-readable frame code painted into them.
///
/// Every frame carries its frame number twice: as large text (for people looking at a PNG) and as
/// a strip of black/white cells (for the spike, which reads it back from pixels). The strip has
/// two rows: the 12 bits of the frame number, most significant first, and their complement
/// underneath. A read is valid only when every column has exactly one bright and one dark cell,
/// so a strip that is hidden, half-covered or blended never decodes to a wrong number.
/// </summary>
internal static class TestMedia
{
    public const int Fps = 30;
    public const int DurationSeconds = 30;
    public const int FrameCount = Fps * DurationSeconds;
    public const int CodeBits = 12;

    /// <summary>Source time of the camera's first frame (project <c>camera.startOffset</c>).</summary>
    public const double CameraStartOffsetSeconds = 0.2;

    /// <summary>The camera clip started 0.2 s after the screen clip: screen frame N pairs with camera frame N − 6.</summary>
    public const int CameraFrameLag = 6;

    public static readonly ClipSpec Screen = new("screen.mp4", 2560, 1440, CodeX: 256, CodeY: 64, CodeCell: 64, HasAudio: true, PatchX: 256, PatchY: 600, PatchSize: 96, PatchPitch: 128);

    /// <summary>The strip sits in the centre of the frame so it survives the square crop and circle mask.</summary>
    public static readonly ClipSpec Camera = new("camera.mp4", 1280, 720, CodeX: 352, CodeY: 312, CodeCell: 48, HasAudio: false, PatchX: 448, PatchY: 440, PatchSize: 64, PatchPitch: 96);

    /// <summary>
    /// Flat colour patches drawn into every frame of both clips (ffmpeg colour name, nominal RGB).
    /// They are for comparing how each decode path converts YUV to RGB; the reference values are
    /// what ffmpeg decodes from the finished file, not these nominal numbers.
    /// </summary>
    public static readonly (string Name, Rgb Nominal)[] PatchColors =
    {
        ("red", new Rgb(255, 0, 0)),
        ("lime", new Rgb(0, 255, 0)),
        ("blue", new Rgb(0, 0, 255)),
        ("gray", new Rgb(128, 128, 128)),
    };

    /// <summary>Length of the tone burst at the start of every second of the screen clip's audio.</summary>
    public const double BurstSeconds = 0.05;

    /// <summary>Burst pitch encodes the second it belongs to, so a burst can be identified after trims.</summary>
    public static double BurstFrequency(int second) => 400 + (100 * second);

    public static string Directory(string root) => Path.Combine(root, "media");

    public static bool Exists(string root) =>
        File.Exists(Path.Combine(Directory(root), Screen.FileName)) && File.Exists(Path.Combine(Directory(root), Camera.FileName));

    public static void Generate(string root, Report report, bool force)
    {
        var directory = Directory(root);
        System.IO.Directory.CreateDirectory(directory);
        foreach (var clip in new[] { Screen, Camera })
        {
            var path = Path.Combine(directory, clip.FileName);
            if (File.Exists(path) && !force)
            {
                report.Line($"{clip.FileName}: already present ({new FileInfo(path).Length / 1024.0 / 1024.0:0.0} MB), use --force to regenerate");
                continue;
            }

            var watch = Stopwatch.StartNew();
            RunFfmpeg(BuildArguments(clip, path), report);
            report.Line($"{clip.FileName}: generated in {watch.Elapsed.TotalSeconds:0.0} s ({new FileInfo(path).Length / 1024.0 / 1024.0:0.0} MB)");
        }
    }

    private static List<string> BuildArguments(ClipSpec clip, string path)
    {
        var c = CultureInfo.InvariantCulture;
        var filter = new StringBuilder();
        filter.Append("[0:v]");
        if (!clip.HasAudio)
        {
            // Tint the camera clip so the two sources are easy to tell apart in a composite.
            filter.Append("hue=h=120,");
        }

        var stripWidth = CodeBits * clip.CodeCell;
        filter.Append(c, $"drawbox=x={clip.CodeX}:y={clip.CodeY}:w={stripWidth}:h={clip.CodeCell * 2}:color=black:t=fill,");
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

        var fontSize = clip.HasAudio ? 110 : 84;
        var label = clip.HasAudio ? "SCREEN" : "CAM";
        var textX = clip.HasAudio ? clip.CodeX.ToString(c) : "(w-text_w)/2";
        var textY = clip.HasAudio ? (clip.CodeY + (clip.CodeCell * 2) + 60).ToString(c) : (clip.CodeY - 150).ToString(c);
        filter.Append(c, $"drawtext=fontfile='C\\:/Windows/Fonts/consolab.ttf':text='{label} %{{frame_num}}':fontsize={fontSize}:fontcolor=white:box=1:boxcolor=black@0.8:boxborderw=18:x={textX}:y={textY},");
        if (clip.HasAudio)
        {
            filter.Append(c, $"drawtext=fontfile='C\\:/Windows/Fonts/consolab.ttf':text='%{{pts\\:hms}}':fontsize=80:fontcolor=white:box=1:boxcolor=black@0.8:boxborderw=18:x={clip.CodeX}:y={clip.CodeY + (clip.CodeCell * 2) + 240},");
        }

        filter.Append("scale=out_color_matrix=bt709:out_range=tv,format=yuv420p[v]");

        // Same shape as a TinyClips recording from MfSinkWriterEncoder: H.264 High, constant bitrate
        // of width*height*fps/10, a two second GOP and no B-frames.
        var bitrate = Math.Clamp((long)clip.Width * clip.Height * Fps / 10, 2_000_000, 24_000_000);
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", string.Create(c, $"testsrc2=size={clip.Width}x{clip.Height}:rate={Fps}:duration={DurationSeconds}"),
        };

        if (clip.HasAudio)
        {
            var burst = string.Create(c, $"0.5*sin(2*PI*(400+100*floor(t))*t)*lt(mod(t,1),{BurstSeconds})");
            args.AddRange(new[] { "-f", "lavfi", "-i", string.Create(c, $"aevalsrc=exprs='{burst}|{burst}':channel_layout=stereo:sample_rate=48000:duration={DurationSeconds}") });
        }

        args.AddRange(new[] { "-filter_complex", filter.ToString(), "-map", "[v]" });
        if (clip.HasAudio)
        {
            args.AddRange(new[] { "-map", "1:a", "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2" });
        }
        else
        {
            args.Add("-an");
        }

        args.AddRange(new[]
        {
            "-c:v", "libx264", "-profile:v", "high", "-preset", "veryfast", "-pix_fmt", "yuv420p",
            "-b:v", bitrate.ToString(c), "-maxrate", bitrate.ToString(c), "-bufsize", (bitrate * 2).ToString(c),
            "-g", (Fps * 2).ToString(c), "-keyint_min", (Fps * 2).ToString(c), "-sc_threshold", "0", "-bf", "0",
            "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "tv",
            "-r", Fps.ToString(c), "-movflags", "+faststart",
            path,
        });
        return args;
    }

    public static void RunFfmpeg(IEnumerable<string> arguments, Report report) => RunTool("ffmpeg", arguments, report);

    /// <summary>Runs ffmpeg/ffprobe from PATH and returns stdout. Throws with stderr on failure.</summary>
    public static string RunTool(string tool, IEnumerable<string> arguments, Report? report)
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

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {tool}.");
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        var errors = stderr.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{tool} exited with {process.ExitCode}: {errors}");
        }

        if (!string.IsNullOrWhiteSpace(errors))
        {
            report?.Line($"  [{tool}] {errors.Trim()}");
        }

        return stdout;
    }

    /// <summary>Runs a tool and returns its raw stdout bytes (decoded frames, PCM).</summary>
    public static byte[] RunToolBinary(string tool, IEnumerable<string> arguments)
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

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {tool}.");
        var stderr = process.StandardError.ReadToEndAsync();
        using var buffer = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(buffer);
        process.WaitForExit();
        var errors = stderr.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{tool} exited with {process.ExitCode}: {errors}");
        }

        return buffer.ToArray();
    }
}

/// <summary>Geometry of one synthetic clip, its code strip and its colour patches (source pixels).</summary>
internal sealed record ClipSpec(string FileName, int Width, int Height, int CodeX, int CodeY, int CodeCell, bool HasAudio, int PatchX, int PatchY, int PatchSize, int PatchPitch)
{
    public int CodeWidth => TestMedia.CodeBits * CodeCell;

    public int CodeHeight => CodeCell * 2;
}
