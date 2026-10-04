using System.Diagnostics;
using System.Globalization;
using Vortice.MediaFoundation;

namespace TinyClips.Tools.StudioRenderCheck;

/// <summary>
/// Headless check of the Studio renderer and exporter. It makes its own source clips, renders
/// and exports from them through the public API, and reads every result back from pixels and
/// decoded samples. One line per check; the exit code is the number of failed checks, capped at 1.
/// </summary>
internal static class Program
{
    private static readonly string[] Groups = ["sources", "recorder", "renderer", "zoom", "exports", "encoders", "color", "robustness", "poster", "service", "warp", "speed"];

    private static async Task<int> Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        List<string>? only = null;
        var keep = false;
        string? root = null;
        string? match = null;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--only" when index + 1 < args.Length:
                    only = [.. args[++index].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
                    break;
                case "--match" when index + 1 < args.Length:
                    match = args[++index];
                    break;
                case "--keep":
                    keep = true;
                    break;
                case "--out" when index + 1 < args.Length:
                    root = Path.GetFullPath(args[++index]);
                    keep = true;
                    break;
                default:
                    Console.WriteLine("StudioRenderCheck [--only group,group] [--match text] [--keep] [--out folder]");
                    Console.WriteLine("  groups: " + string.Join(", ", Groups));
                    Console.WriteLine("  --match run only the checks whose name contains the text");
                    Console.WriteLine("  --keep  keep the clips and exports (they are always kept when a check fails)");
                    Console.WriteLine("  --out   put them in this folder, and keep them");
                    return args[index] is "--help" or "-h" or "/?" ? 0 : 2;
            }
        }

        if (only?.FirstOrDefault(group => !Groups.Contains(group, StringComparer.OrdinalIgnoreCase)) is { } unknown)
        {
            Console.WriteLine($"Unknown group '{unknown}'. Groups: {string.Join(", ", Groups)}");
            return 2;
        }

        root ??= Path.Combine(Path.GetTempPath(), "TinyClipsStudioRenderCheck", DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(root);
        var harness = new Harness(root, only, match) { KeepsFiles = keep };
        var watch = Stopwatch.StartNew();

        MediaFactory.MFStartup(true).CheckError();
        try
        {
            using (var graphics = TinyClips.Core.Studio.Rendering.StudioGraphicsDevice.CreateHardware())
            {
                Console.WriteLine($"Graphics: {graphics.AdapterName}{(graphics.IsSoftware ? " (software)" : string.Empty)}, Direct3D video support {(graphics.VideoSupport ? "yes" : "no")}");
            }

            Console.WriteLine($"H.264 encoders: hardware [{string.Join(", ", Media.Encoders(hevc: false, hardware: true))}], software [{string.Join(", ", Media.Encoders(hevc: false, hardware: false))}]");
            Console.WriteLine($"HEVC encoders: hardware [{string.Join(", ", Media.Encoders(hevc: true, hardware: true))}], software [{string.Join(", ", Media.Encoders(hevc: true, hardware: false))}]");
            Console.WriteLine();



            await SourceChecks.Run(harness).ConfigureAwait(false);
            await RecorderChecks.Run(harness).ConfigureAwait(false);
            await RendererChecks.Run(harness).ConfigureAwait(false);
            await ZoomChecks.Run(harness).ConfigureAwait(false);
            await ExportChecks.Run(harness).ConfigureAwait(false);
            await OtherChecks.Run(harness).ConfigureAwait(false);
            await SamplingChecks.Run(harness).ConfigureAwait(false);
            await SpeedChecks.Run(harness).ConfigureAwait(false);
        }
        finally
        {
            MediaFactory.MFShutdown();
        }

        Console.WriteLine();
        if (harness.Measurements.Count > 0)
        {
            Console.WriteLine("Measured:");
            foreach (var line in harness.Measurements)
            {
                Console.WriteLine("  " + line);
            }

            Console.WriteLine();
        }

        foreach (var line in harness.Skipped)
        {
            Console.WriteLine("Not run: " + line);
        }

        foreach (var line in harness.Known)
        {
            Console.WriteLine("Known limitation: " + line);
        }

        foreach (var line in harness.Failures)
        {
            Console.WriteLine(line);
        }

        var failed = harness.Failures.Count;
        if (failed == 0 && !keep)
        {
            TryDelete(root);
        }
        else
        {
            Console.WriteLine($"Files kept in {root}");
        }

        var seconds = watch.Elapsed.TotalSeconds.ToString("0", CultureInfo.InvariantCulture);
        Console.WriteLine(failed == 0
            ? $"RESULT: all {harness.Passed} checks passed{(harness.Known.Count > 0 ? $", {harness.Known.Count} known limitation{(harness.Known.Count == 1 ? string.Empty : "s")} measured" : string.Empty)}{(harness.Skipped.Count > 0 ? $", {harness.Skipped.Count} not run" : string.Empty)} ({seconds} s)"
            : $"RESULT: {failed} of {harness.Passed + failed} checks failed ({seconds} s)");
        return failed == 0 ? 0 : 1;
    }

    private static void TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Could not remove {folder}: {ex.Message}");
        }
    }
}
