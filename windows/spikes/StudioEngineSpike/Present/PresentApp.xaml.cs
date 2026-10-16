using Microsoft.UI.Xaml;
using StudioEngineSpike.Engine;

namespace StudioEngineSpike.Present;

/// <summary>The XAML application object behind the `present` mode: one window, then exit.</summary>
public sealed partial class PresentApp : Application
{
    private readonly SpikeOptions _options;
    private PresentWindow? _window;

    internal PresentApp(SpikeOptions options)
    {
        _options = options;
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            Console.Error.WriteLine($"FAILED (XAML unhandled exception): 0x{e.Exception.HResult:X8} {e.Message}");
            Console.Error.WriteLine(e.Exception);
            ExitCode = 2;
        };
    }

    /// <summary>What <c>Main</c> returns once the window has closed.</summary>
    internal static int ExitCode { get; set; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new PresentWindow(_options);
            _window.Activate();
        }
        catch (Exception ex)
        {
            // Without this the process dies with a stowed exception and no text.
            Console.Error.WriteLine($"FAILED to create the window: 0x{ex.HResult:X8} {ex}");
            ExitCode = 2;
            Exit();
        }
    }
}
