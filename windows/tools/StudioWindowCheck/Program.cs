using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using TinyClips.Tools.StudioPreviewCheck;
using TinyClips.Tools.StudioPreviewCheck.Media;
using TinyClips.Tools.StudioWindowCheck.Checks;
using TinyClips.Tools.StudioWindowCheck.Host;

namespace TinyClips.Tools.StudioWindowCheck;

internal static class Program
{
    // STA because XAML starts on this thread. The checks run on a thread of their own.
    [STAThread]
    private static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        CheckOptions options;
        try
        {
            options = CheckOptions.Parse(args);
            if (options.Unknown("only", "skip", "out", "media", "held-up", "memory", "help") is { Length: > 0 } unknown)
            {
                throw new ArgumentException($"Unknown option --{unknown[0]}.");
            }

            foreach (var name in options.Names("only").Concat(options.Names("skip")))
            {
                if (!WindowChecks.Groups.Contains(name, StringComparer.OrdinalIgnoreCase))
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

        var tool = FindToolDirectory();
        var output = Path.GetFullPath(options.Text("out", Path.Combine(tool, "out")));
        var media = Path.GetFullPath(options.Text("media", DefaultMediaDirectory(tool, output)));
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        using var report = new Report(output, stamp);
        using var foreground = new ForegroundWatch();
        try
        {
            // First of all, and on this thread, which becomes the UI thread: from here on nothing
            // in this process can bring a window to the front.
            ForegroundGuard.Install();
            report.Line($"StudioWindowCheck, {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            report.Line($"{Environment.OSVersion}, .NET {Environment.Version}, {Environment.ProcessorCount} logical processors");
            report.Line($"Projects, exports and everything else the tool writes while it checks: {TestFolder.Root} (deleted at the end). Settings are kept in memory.");
            report.Line($"Reports and screenshots: {output}");
            report.Line("The preview is forced mute. Windows are shown without activation, behind the other windows. No input is sent.");
            report.Line("Nothing in this process can take the keyboard focus or the foreground: the functions that do it are switched off, and every call to one is counted.");
            report.Section("Test clips");
            TestMedia.Ensure(media, report);

            // The XAML compiler's own Main is switched off (DISABLE_XAML_GENERATED_MAIN), so this is it.
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(_ =>
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                var app = new CheckApp(report, options, media, output, foreground);
                GC.KeepAlive(app);
            });
        }
        catch (Exception ex)
        {
            report.Check("the tool ran to the end", false, ex.ToString());
        }

        report.Section("The person at the machine");
        var own = foreground.OwnWindowsInFront();
        report.Check(
            "no window of the tool was ever the foreground window",
            own.Length == 0,
            own.Length == 0 ? foreground.Describe() : string.Join("; ", own));
        report.Note($"the foreground window during the run: {string.Join(", then ", foreground.Seen())}");
        var refused = ForegroundGuard.Events();
        report.Note(refused.Length == 0
            ? "nothing in the process tried to take the keyboard focus or to activate a window"
            : $"{refused.Length} attempts to take the keyboard focus or to activate a window were refused: {string.Join(", ", refused.GroupBy(e => e.What).Select(g => $"{g.Count()} {g.Key}"))}. Each is in the timeline with the step that caused it");
        var timeline = Path.Combine(output, $"timeline-{stamp}.txt");
        Timeline.Save(timeline, foreground.Seen());
        report.Line($"Timeline: {timeline}");

        report.Section("Cleaning up");
        report.Check("the temp folder of this run is gone", TestFolder.DeleteRoot(), TestFolder.Root);
        return report.Finish();
    }

    /// <summary>The folder that holds the project file, found from the build output the tool runs from.</summary>
    private static string FindToolDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "StudioWindowCheck.csproj")))
            {
                return directory.FullName;
            }
        }

        return AppContext.BaseDirectory;
    }

    /// <summary>StudioPreviewCheck's clips when it has made them, so they are not generated twice; otherwise a folder of the tool's own.</summary>
    private static string DefaultMediaDirectory(string tool, string output)
    {
        var shared = Path.Combine(tool, "..", "StudioPreviewCheck", "out", "media");
        return File.Exists(Path.Combine(shared, "stamp.txt")) ? shared : Path.Combine(output, "media");
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            $"""
            StudioWindowCheck [--only a,b] [--skip a,b] [--out <folder>] [--media <folder>] [--held-up] [--memory]

              --only a,b        run only these groups of checks
              --skip a,b        leave these groups out
              --out <folder>    where reports and screenshots go (default: out next to the project)
              --media <folder>  where the test clips are, or are generated (default: StudioPreviewCheck's
                                out\media when it has them, otherwise media in the out folder)
              --held-up         in the zoom group, play through a zoom that moves in a second time
                                while the tool makes the garbage collector stop every thread of the
                                process a few times, as happens to an app on a busy PC
              --memory          run none of the groups: open and close windows in several ways, and
                                say which are still in memory afterwards

            Groups: {string.Join(' ', WindowChecks.Groups)}

            Needs ffmpeg and ffprobe on PATH: to check the test clips, to generate them when they are
            missing, and to decode what the editor exports.
            The exit code is 0 when every check passed, 1 when one failed (one FAILED line each), 2 for a usage error.
            """);
    }
}
