using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using TinyClips.App.Settings;
using TinyClips.Core.Models;
using TinyClips.Core.Services;

namespace TinyClips.App;

/// <summary>
/// Shown once after an update that changes how the app behaves (first time: the GPU recording
/// pipeline and low-latency encoder becoming the defaults). Highlights the new features and
/// points at the Video settings that revert to the previous behaviour. Dismissing records the
/// current version in <see cref="ICaptureSettings.LastSeenWhatsNewVersion"/>.
/// </summary>
public sealed partial class WhatsNewWindow : Window
{
    private const int MinimumWidthDip = 480;
    private const int MinimumHeightDip = 560;

    private readonly ICaptureSettings _settings;
    private readonly WindowChromeController _chromeController;

    public WhatsNewWindow()
    {
        _settings = App.Services.GetRequiredService<ICaptureSettings>();

        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        AppWindowPlacement.CenterInCurrentWorkAreaAtDipSize(AppWindow, hwnd, 720, 820);
        _chromeController = new WindowChromeController(this, RootGrid, MinimumWidthDip, MinimumHeightDip);

        RootGrid.RequestedTheme = _settings.Theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        var version = AppVersionInfo.GetCurrentVersion();
        VersionText.Text = $"Version {version.Major}.{version.Minor}{(version.Build > 0 ? $".{version.Build}" : string.Empty)}";

        Closed += (_, _) => MarkSeen();
    }

    /// <summary>
    /// Bump this whenever the window's content is rewritten for a release. The window is shown
    /// only to users who have not yet dismissed this revision, so shipping versions without new
    /// notes never re-shows stale ones, and the release's actual version number never needs to be
    /// known here (the displayed version comes from the package at runtime).
    /// </summary>
    internal const int ContentRevision = 1;

    /// <summary>
    /// True when this content revision has not been dismissed yet. First-run users see onboarding
    /// instead; <see cref="MarkSeenForCurrentVersion"/> keeps them from also getting this window
    /// on their second launch. Legacy values (a bare version string from before revisions existed)
    /// count as "seen nothing" — which is correct, since those users predate every revision.
    /// </summary>
    public static bool ShouldShow(ICaptureSettings settings) => ParseSeenRevision(settings.LastSeenWhatsNewVersion) < ContentRevision;

    /// <summary>Records the current content revision (and version, for diagnostics) as seen.</summary>
    public static void MarkSeenForCurrentVersion(ICaptureSettings settings) =>
        settings.LastSeenWhatsNewVersion = $"rev{ContentRevision};{AppVersionInfo.GetCurrentVersionText()}";

    internal static int ParseSeenRevision(string? stored)
    {
        if (string.IsNullOrEmpty(stored) || !stored.StartsWith("rev", StringComparison.Ordinal))
        {
            return 0;
        }

        var end = stored.IndexOf(';');
        var number = end > 3 ? stored[3..end] : stored[3..];
        return int.TryParse(number, out var revision) ? revision : 0;
    }

    private void MarkSeen() => MarkSeenForCurrentVersion(_settings);

    private void OnDoneClicked(object sender, RoutedEventArgs e) => Close();

    private void OnOpenVideoSettingsClicked(object sender, RoutedEventArgs e)
    {
        ((App)Application.Current).OpenSettingsWindow(SettingsSectionKind.Video);
        Close();
    }
}
