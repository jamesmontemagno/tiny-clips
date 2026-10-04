using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using TinyClips.Tools.StudioPreviewCheck.Checks;
using TinyClips.Tools.StudioPreviewCheck.Media;
using TinyClips.Tools.StudioPreviewCheck.Windowed;
using Vortice.DXGI;

namespace TinyClips.Tools.StudioPreviewCheck;

internal static class Program
{
    private static readonly string[] HeadlessGroups = ["open", "seek", "step", "seekplay", "position", "editor", "play", "pause", "end", "update", "camera", "mute", "surface", "dispose", "device", "software"];
    private static readonly string[] WindowGroups = ["window-exact", "window-playback", "window-resize", "window-scale", "window-blocked", "window-reload", "window-engines", "window-devicelost"];

    // STA because the window checks start XAML on this thread. Everything else runs on a thread of
    // its own in the multithreaded apartment, the way the app's background work calls the engine.
    [STAThread]
    private static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        CheckOptions options;
        try
        {
            options = CheckOptions.Parse(args);
            if (options.Unknown("quick", "only", "skip", "no-window", "no-headless", "out", "help") is { Length: > 0 } unknown)
            {
                throw new ArgumentException($"Unknown option --{unknown[0]}.");
            }

            foreach (var name in options.Names("only").Concat(options.Names("skip")))
            {
                if (!HeadlessGroups.Contains(name, StringComparer.OrdinalIgnoreCase) && !WindowGroups.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    throw new ArgumentException($"There is no group of checks called '{name}'.");
                }
            }
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            PrintUsage();
            return 2;
        }

        if (options.Flag("help"))
        {
            PrintUsage();
            return 0;
        }

        var output = Path.GetFullPath(options.Text("out", Path.Combine(FindToolDirectory(), "out")));
        var media = Path.Combine(output, "media");
        using var report = new Report(Path.Combine(output, $"report-{DateTime.Now:yyyyMMdd-HHmmss}.txt"));
        try
        {
            report.Line($"StudioPreviewCheck, {DateTime.Now:yyyy-MM-dd HH:mm:ss}, {(options.Flag("quick") ? "quick" : "full")} run");
            report.Line($"{Environment.OSVersion}, .NET {Environment.Version}, {Environment.ProcessorCount} logical processors, {AdapterName()}");
            report.Line("Every player is muted and at volume zero for the whole run. The one exception is named in the mute checks.");
            report.Section("Test clips");
            OnWorkerThread(() => TestMedia.Ensure(media, report));

            if (!options.Flag("no-headless") && HeadlessGroups.Any(options.Wants))
            {
                OnWorkerThread(() => new HeadlessChecks(report, options, media, output).Run());
            }

            if (!options.Flag("no-window") && WindowGroups.Any(options.Wants))
            {
                RunWindow(report, options, media, output);
            }
        }
        catch (Exception ex)
        {
            report.Check("the tool ran to the end", false, ex.ToString());
        }

        report.Section("Cleaning up");
        report.Check("the temp project folders of this run are gone", TestFolder.DeleteRoot(), TestFolder.Root);
        return report.Finish();
    }

    private static void RunWindow(Report report, CheckOptions options, string media, string output)
    {
        // The XAML compiler's own Main is switched off (DISABLE_XAML_GENERATED_MAIN), so this is it.
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            var app = new CheckApp(report, options, media, output);
            GC.KeepAlive(app);
        });
    }

    private static void OnWorkerThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            Name = "StudioPreviewCheck.Headless",
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException(failure.Message, failure);
        }
    }

    /// <summary>The folder that holds the project file, found from the build output the tool runs from.</summary>
    private static string FindToolDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "StudioPreviewCheck.csproj")))
            {
                return directory.FullName;
            }
        }

        return AppContext.BaseDirectory;
    }

    private static string AdapterName()
    {
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            if (factory.EnumAdapters1(0, out var adapter).Success && adapter is not null)
            {
                using (adapter)
                {
                    return adapter.Description1.Description;
                }
            }
        }
        catch (Exception)
        {
            // Only for the report's first lines.
        }

        return "unknown graphics adapter";
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            StudioPreviewCheck [--quick] [--only a,b] [--skip a,b] [--no-window] [--no-headless] [--out <folder>]

              --quick         fewer repetitions (a smoke run; the numbers in the docs come from a full run)
              --only a,b      run only these groups of checks
              --skip a,b      leave these groups out
              --no-window     leave out the checks that open a window
              --no-headless   leave out the checks that need no window
              --out <folder>  where the test clips, reports and screenshots go (default: out next to the project)

            Groups without a window: open seek step seekplay position editor play pause end update camera mute
                                     surface dispose device software
            Groups with a window:    window-exact window-playback window-resize window-scale window-blocked
                                     window-reload window-engines window-devicelost

            Needs ffmpeg and ffprobe on PATH to generate the test clips (once; they are kept in the out folder).
            The exit code is 0 when every check passed, 1 when one failed (one FAILED line each), 2 for a usage error.
            """);
    }
}
