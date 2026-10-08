using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using TinyClips.Tools.StudioPreviewCheck;
using TinyClips.Tools.StudioPreviewCheck.Media;
using TinyClips.Tools.StudioWindowCheck.Checks;

namespace TinyClips.Tools.StudioWindowCheck.Host;

/// <summary>The XAML application object: it builds the services, lets the checks open Studio windows, and exits when they are done.</summary>
public sealed partial class CheckApp : Application
{
    private readonly Report _report;
    private readonly CheckOptions _options;
    private readonly string _media;
    private readonly string _output;
    private readonly ForegroundWatch _foreground;
    private WindowChecks? _checks;

    internal CheckApp(Report report, CheckOptions options, string mediaDirectory, string outputDirectory, ForegroundWatch foreground)
    {
        _report = report;
        _options = options;
        _media = mediaDirectory;
        _output = outputDirectory;
        _foreground = foreground;
        InitializeComponent();

        // Windows come and go during a run, and the last one closing must not end it.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        UnhandledException += (_, e) =>
        {
            // Without this the process dies with a stowed exception and no text.
            _report.Check("no unhandled exception on the UI thread", false, $"0x{e.Exception.HResult:X8} {e.Exception}");
            e.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var services = new ToolServices(TestFolder.Root);
            _checks = new WindowChecks(_report, _options, _media, _output, services, _foreground, DispatcherQueue.GetForCurrentThread(), Exit);
            _checks.Start();
        }
        catch (Exception ex)
        {
            _report.Check("the tool could start", false, $"0x{ex.HResult:X8} {ex}");
            Exit();
        }
    }
}
