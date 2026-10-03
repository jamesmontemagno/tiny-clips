using System.Globalization;
using StudioEngineSpike.Engine;
using StudioEngineSpike.Modes;

namespace StudioEngineSpike;

internal static class Program
{
    // STA because the `present` mode starts XAML on this thread. The headless modes run on their
    // own MTA thread (see RunHeadless), which is how a tray app's background work would call them.
    [STAThread]
    private static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        var options = SpikeOptions.Parse(args);
        try
        {
            switch (options.Mode)
            {
                case "media":
                    return RunHeadless(options, MediaMode.Run);
                case "info":
                    return RunHeadless(options, InfoMode.Run);
                case "preview":
                    return RunHeadless(options, PreviewMode.Run);
                case "export":
                    return RunHeadless(options, ExportMode.Run);
                case "encoders":
                    return RunHeadless(options, EncodersMode.Run);
                case "present":
                    return PresentMode.Run(options);
                case "help":
                    PrintUsage();
                    return 0;
                default:
                    Console.Error.WriteLine(options.Mode.Length == 0 ? "No mode given." : $"Unknown mode '{options.Mode}'.");
                    PrintUsage();
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAILED: {ex}");
            return 2;
        }
    }

    private static int RunHeadless(SpikeOptions options, Func<SpikeOptions, int> run)
    {
        var result = 0;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = run(options);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            Name = "Spike." + options.Mode,
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            Console.Error.WriteLine($"FAILED: {failure}");
            return 2;
        }

        return result;
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            StudioEngineSpike <mode> [options]

              media      Generate the synthetic screen/camera clips with ffmpeg and self-check them.
              info       Print the adapter, the Media Foundation encoders/decoders and runtime flavour.
              preview    Q1: two frame-server MediaPlayers on one MediaTimelineController, Direct2D composite (headless).
              present    Q2: WinUI 3 window, CanvasSwapChainPanel vs SwapChainPanel + ISwapChainPanelNative.
              export     Q3: source readers -> Direct2D -> sink writer, with audio.
              encoders   Q4: one vs two real-time sink writers.

            Common options:
              --root <dir>   Spike folder holding media\ and out\ (default: found from the exe location).
              --tag <name>   Suffix for the output folder, e.g. --tag aot writes out\<mode>-aot\.
              --force        (media) regenerate clips that already exist.

            See README.md for the per-mode options.
            """);
    }
}

/// <summary>"mode --key value --flag" command line, with the spike folder resolved once.</summary>
internal sealed class SpikeOptions
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public string Mode { get; private set; } = string.Empty;

    public string Root { get; private set; } = string.Empty;

    public string Tag => GetString("tag", string.Empty);

    /// <summary>True when this process was compiled ahead of time (no JIT available).</summary>
    public static bool IsNativeAot => !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported;

    public static string RuntimeFlavor => IsNativeAot ? "NativeAOT" : "JIT (CoreCLR)";

    public static SpikeOptions Parse(string[] args)
    {
        var options = new SpikeOptions();
        var index = 0;
        if (args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
        {
            options.Mode = args[0].ToLowerInvariant();
            index = 1;
        }

        while (index < args.Length)
        {
            var key = args[index];
            if (!key.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected argument '{key}'.");
            }

            key = key[2..];
            if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                options._values[key] = args[index + 1];
                index += 2;
            }
            else
            {
                options._values[key] = "true";
                index++;
            }
        }

        options.Root = options._values.TryGetValue("root", out var root) ? Path.GetFullPath(root) : FindRoot();
        return options;
    }

    public bool Has(string key) => _values.ContainsKey(key);

    public bool Flag(string key) => _values.TryGetValue(key, out var value) && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);

    public string GetString(string key, string fallback) => _values.TryGetValue(key, out var value) ? value : fallback;

    public int GetInt(string key, int fallback) =>
        _values.TryGetValue(key, out var value) ? int.Parse(value, CultureInfo.InvariantCulture) : fallback;

    public double GetDouble(string key, double fallback) =>
        _values.TryGetValue(key, out var value) ? double.Parse(value, CultureInfo.InvariantCulture) : fallback;

    /// <summary>Parses "WxH" (e.g. 1920x1080).</summary>
    public (int Width, int Height) GetSize(string key, int fallbackWidth, int fallbackHeight)
    {
        if (!_values.TryGetValue(key, out var value))
        {
            return (fallbackWidth, fallbackHeight);
        }

        var parts = value.Split('x', 'X');
        return (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    /// <summary><c>out\&lt;mode&gt;[-tag]\</c> under the spike folder.</summary>
    public string OutputDirectory(string? mode = null)
    {
        var name = mode ?? Mode;
        if (Tag.Length > 0)
        {
            name += "-" + Tag;
        }

        var directory = Path.Combine(Root, "out", name);
        Directory.CreateDirectory(directory);
        return directory;
    }

    public Report OpenReport(string? mode = null) => new(Path.Combine(OutputDirectory(mode), "report.txt"));

    /// <summary>
    /// Walks up from the executable until it finds the project file, so the same command works for
    /// `dotnet run`, bin\...\StudioEngineSpike.exe and the NativeAOT publish folder.
    /// </summary>
    private static string FindRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "StudioEngineSpike.csproj")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        return Environment.CurrentDirectory;
    }
}
