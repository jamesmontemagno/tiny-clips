using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace TinyClips.Tools.StudioPreviewCheck.Windowed;

/// <summary>The XAML application object behind the window checks: one window, the checks, then exit.</summary>
public sealed partial class CheckApp : Application
{
    private readonly Report _report;
    private readonly CheckOptions _options;
    private readonly string _media;
    private readonly string _output;
    private CheckWindow? _window;
    private WindowChecks? _checks;

    internal CheckApp(Report report, CheckOptions options, string mediaDirectory, string outputDirectory)
    {
        _report = report;
        _options = options;
        _media = mediaDirectory;
        _output = outputDirectory;
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            // Without this the process dies with a stowed exception and no text.
            _report.Check("window: no unhandled exception on the UI thread", false, $"0x{e.Exception.HResult:X8} {e.Exception}");
            e.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new CheckWindow();
            var window = _window;
            var handle = WindowNative.GetWindowHandle(window);

            // Someone is working on this machine. The window is never activated, never on top, and
            // not in the taskbar or the Alt+Tab list: it opens behind everything else.
            var appWindow = window.AppWindow;
            appWindow.IsShownInSwitchers = false;
            var work = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            var width = Math.Min(1400, work.Width - 40);
            var height = Math.Min(900, work.Height - 40);
            appWindow.MoveAndResize(new RectInt32(work.X + 20, work.Y + 20, width, height));
            NativeMethods.SendToBack(handle);
            appWindow.Show(activateWindow: false);
            NativeMethods.SendToBack(handle);

            _checks = new WindowChecks(_report, _options, _media, _output, window, handle, () =>
            {
                window.Close();
                Exit();
            });
            var checks = _checks;
            var content = (FrameworkElement)window.Content;
            if (content.IsLoaded)
            {
                checks.Start();
            }
            else
            {
                content.Loaded += (_, _) => checks.Start();
            }

            // A window that is never activated might never load its content. Say so instead of hanging.
            var timer = window.DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(10);
            timer.IsRepeating = false;
            timer.Tick += (_, _) =>
            {
                if (!checks.Started)
                {
                    _report.Check("window: the window's content loaded without the window being activated", false, "not loaded after 10 s");
                    window.Close();
                    Exit();
                }
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            _report.Check("window: the window could be created", false, $"0x{ex.HResult:X8} {ex}");
            Exit();
        }
    }
}
