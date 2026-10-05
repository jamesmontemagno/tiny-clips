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
    private static readonly string[] HeadlessGroups = ["open", "seek", "step", "seekplay", "position", "editor", "play", "pause", "stalls", "zoom", "cuts", "scenes", "end", "update", "camera", "mute", "surface", "dispose", "device", "software"];
    private static readonly string[] WindowGroups = ["window-exact", "window-playback", "window-resize", "window-scale", "window-blocked", "window-reload", "window-engines", "window-devicelost"];

    // STA because the window checks start XAML on this thread. Everything else runs on a thread of
    // its own in the multithreaded apartment, the way the app's background work calls the engine.
    [STAThread]
    private static int Main(string[] args)
    {
        // A second copy of the tool, started by the first to hold it up (see Freezer). It writes
        // no report and touches nothing else.
        if (args.Length > 0 && args[0] == Freezer.Argument)
        {
            return Freezer.Run(args);
        }

        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        CheckOptions options;
        try
        {
            options = CheckOptions.Parse(args);
            if (options.Unknown("quick", "only", "skip", "no-window", "no-headless", "out", "help", "trust-first-frames", "investigate", "count", "seconds", "device", "camera", "scenario", "alternate", "keep-first-frames", "stalls", "stall-ms", "no-pauses", "snap", "fps", "discard-late", "believe-positions", "fetch-every-rest", "no-quiet-rule", "stop-noted-late", "judge-limits", "trace-lines", "keep-traces", "from", "stall-gap", "until", "lead") is { Length: > 0 } unknown)
            {
                throw new ArgumentException($"Unknown option --{unknown[0]}.");
            }

            if (!options.Flag("investigate"))
            {
                // Says so when a kind of hold-up is named that there is none of.
                HoldUps.Parse(options.Text("stalls", string.Empty));
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
        using var report = new Report(output, DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        try
        {
            report.Line($"StudioPreviewCheck, {DateTime.Now:yyyy-MM-dd HH:mm:ss}, {(options.Flag("investigate") ? "an investigation" : options.Flag("quick") ? "quick run" : "full run")}");
            if (options.Flag("trust-first-frames") && !options.Flag("investigate"))
            {
                report.Line("--trust-first-frames: every preview without a window is opened the way the engine opened before, believing what the players hand over first. Checks of the first picture are expected to fail.");
            }

            if (options.Flag("believe-positions") && !options.Flag("investigate"))
            {
                report.Line("--believe-positions: every preview without a window takes each frame of playback for the one its player's position names, as the engine did before. The checks made while the process is held up are expected to fail.");
            }

            if (options.Flag("fetch-every-rest") && !options.Flag("investigate"))
            {
                report.Line("--fetch-every-rest: every preview without a window fetches the frame anew that the clock stops on, as the engine does by itself when the frame's number rests on an inference. With --believe-positions the pause checks are expected to fail for the moment Pause() returns and to hold at rest.");
            }

            if (options.Flag("no-quiet-rule") && !options.Flag("investigate"))
            {
                report.Line("--no-quiet-rule: no preview without a window holds its picture during the first position change after playing. The check of Pause() followed at once by Seek is expected to fail now and then.");
            }

            if (options.Flag("stop-noted-late") && !options.Flag("investigate"))
            {
                report.Line("--stop-noted-late: every preview without a window notes when its clock stopped only once the clock has been told and the thread has got on, as the engine did before. A frame a player hands over in between is then taken for a frame of playback. The pause checks with the stopping thread held up (--stalls afterstop) are expected to fail.");
            }

            if (options.Flag("judge-limits") && !options.Flag("investigate"))
            {
                report.Line("--judge-limits: every frame that was given a number its pixels do not show is judged, also the two kinds the engine says it cannot rule out: a number from rule 3, which takes a hand-over the garbage collector kept waiting for the frame next in line, and one given while the whole process was stopped from outside in the middle of the hand-over. The checks made while the process is held up are expected to fail now and then.");
            }

            report.Line($"{Environment.OSVersion}, .NET {Environment.Version}, {Environment.ProcessorCount} logical processors, {AdapterName()}");
            report.Line("Every player is muted and at volume zero for the whole run. The one exception is named in the mute checks.");
            report.Section("Test clips");
            OnWorkerThread(() => TestMedia.Ensure(media, report));

            if (options.Flag("investigate"))
            {
                OnWorkerThread(() => new HeadlessChecks(report, options, media, output).Investigate());
            }
            else if (!options.Flag("no-headless") && HeadlessGroups.Any(options.Wants))
            {
                OnWorkerThread(() => new HeadlessChecks(report, options, media, output).Run());
            }

            if (!options.Flag("investigate") && !options.Flag("no-window") && WindowGroups.Any(options.Wants))
            {
                RunWindow(report, options, media, output);
            }
        }
        catch (Exception ex)
        {
            report.Check("the tool ran to the end", false, ex.ToString());
        }

        report.Section("Previews that needed a second attempt to open");
        Session.ReportSecondAttempts(report, Path.Combine(output, "failures", report.Stamp));

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
            StudioPreviewCheck --investigate opens|cycles|decoding|players|names|reopens|starts|waits|tails [...]

              --quick         fewer repetitions (a smoke run; the numbers in the docs come from a full run)
              --only a,b      run only these groups of checks
              --skip a,b      leave these groups out
              --no-window     leave out the checks that open a window
              --no-headless   leave out the checks that need no window
              --out <folder>  where the test clips, reports and screenshots go (default: out next to the project)
              --trust-first-frames
                              open every preview without a window the way the engine opened before it
                              stopped believing the players' first frames, to see which checks notice
              --believe-positions
                              take every frame of playback for the one its player's position names, as
                              the engine did before, to see which checks notice (stalls, zoom, scenes)
              --fetch-every-rest
                              fetch the frame anew that the clock stops on, every time. With
                              --believe-positions: what that fetch does for a number that is wrong
              --no-quiet-rule do not hold the picture during the first position change after playing,
                              to see how often the pause check then sees a frame nobody asked for
              --stop-noted-late
                              note when the clock stopped only after it has, as the engine did before,
                              to see which checks notice (stalls with --stalls afterstop)
              --judge-limits  judge every wrong number: also one from the rule for a hand-over the
                              garbage collector kept waiting, of which a check may show one, or one
                              in 20,000, and one given while the whole process was stopped in the
                              middle of the frame's hand-over. The checks count both apart otherwise
              --stalls a,b    what holds the process up in the groups stalls, zoom, cuts and end:
                              none, collector, draw, stopped, busy, loaded, gpu, afterstop, all
                              (default: the first four in turn, and afterstop as well in stalls;
                              through the editor session, none and collector; in end, collector
                              and draw)
              --stall-ms N    about how long one hold-up lasts (default 60)
              --stall-gap N   in zoom: the shortest time between two collections, in milliseconds
                              (default 150); the longest is three times that
              --count N       in stalls: pauses for each kind of hold-up; in zoom: plays; in cuts:
                              crossings of the cut; in end: plays into the end for each kind
              --camera none   in zoom: a project without a camera
              --from N        in zoom: the frame the plays start from
              --keep-traces   in zoom: keep the engine's trace of every play, and the frames the
                              players handed over with the frame each really was, in out\traces
              --trace-lines N how many lines of an engine's trace are kept for a failure (default 600)

              --investigate   an experiment instead of the checks; it prints what it measures (see README.md):
                opens      [--device software|hardware] [--count N] [--camera late|start|none|mixed]
                           [--trust-first-frames | --alternate] [--keep-first-frames]
                cycles     [--device hardware|software] [--count N] [--trust-first-frames]
                decoding   [--device software|hardware] [--seconds N]
                players    [--device software|hardware|both] [--count N] [--scenario first-copy|second-device|
                           while-opening|both-ready|settle|before-source|reopen]
                names      [--stalls none|gc|gate|freeze|both|all] [--fps 30|60] [--count N] [--seconds N]
                           [--no-pauses] [--snap] [--discard-late]
                reopens    [--scenario after-close|while-closing|two-alive|two-playing|pile|undecodable]
                           [--count N]
                starts     [--count N] [--stall-ms N] [--until N]
                waits      [--count N] [--stall-ms N]
                tails      [--count N] [--stall-ms N] [--lead N]

            Groups without a window: open seek step seekplay position editor play pause stalls zoom cuts scenes
                                     end update camera mute surface dispose device software
            Groups with a window:    window-exact window-playback window-resize window-scale window-blocked
                                     window-reload window-engines window-devicelost

            Needs ffmpeg and ffprobe on PATH to generate the test clips (once; they are kept in the out folder).
            The exit code is 0 when every check passed, 1 when one failed (one FAILED line each), 2 for a usage error.
            """);
    }
}
