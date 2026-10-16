using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using TinyClips.App.Settings;
using TinyClips.Core.Models;
using TinyClips.Core.Services;

namespace TinyClips.App;

/// <summary>
/// Shown once per content revision to introduce Studio and offer its saved preview switch.
/// New installations see onboarding instead. Closing by any route marks this revision as seen.
/// </summary>
public sealed partial class WhatsNewWindow : Window
{
    private const int MinimumWidthDip = 480;
    private const int MinimumHeightDip = 560;

    private readonly ICaptureSettings _settings;
    private readonly WindowChromeController _chromeController;

    public WhatsNewViewModel ViewModel { get; }

    public WhatsNewWindow()
    {
        _settings = App.Services.GetRequiredService<ICaptureSettings>();
        ViewModel = new WhatsNewViewModel(_settings);

        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        AppWindowPlacement.CenterInCurrentWorkAreaAtDipSize(AppWindow, hwnd, 760, 900);
        _chromeController = new WindowChromeController(this, RootGrid, MinimumWidthDip, MinimumHeightDip);

        RootGrid.RequestedTheme = _settings.Theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        var version = AppVersionInfo.GetCurrentVersion();
        VersionText.Text = $"Version {version.Major}.{version.Minor}{(version.Build > 0 ? $".{version.Build}" : string.Empty)}";

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Activated += OnActivated;
        Closed += (_, _) =>
        {
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            Activated -= OnActivated;
            MarkSeen();
        };
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            ViewModel.IsStudioEnabled = _settings.StudioPreviewEnabled;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(WhatsNewViewModel.IsStudioEnabled))
        {
            ((App)Application.Current).RefreshStudioPreviewSetting();
        }
    }

    /// <summary>
    /// Bump this whenever the window's content is rewritten for a release. The window is shown
    /// only to users who have not yet dismissed this revision, so shipping versions without new
    /// notes never re-shows stale ones, and the release's actual version number never needs to be
    /// known here (the displayed version comes from the package at runtime).
    /// </summary>
    internal const int ContentRevision = WhatsNewViewModel.ContentRevision;

    /// <summary>
    /// True when this content revision has not been dismissed yet. First-run users see onboarding
    /// instead; <see cref="MarkSeenForCurrentVersion"/> keeps them from also getting this window
    /// on their second launch. Legacy values (a bare version string from before revisions existed)
    /// count as "seen nothing" — which is correct, since those users predate every revision.
    /// </summary>
    public static bool ShouldShow(ICaptureSettings settings) => WhatsNewViewModel.ShouldShow(settings);

    /// <summary>Records the current content revision (and version, for diagnostics) as seen.</summary>
    public static void MarkSeenForCurrentVersion(ICaptureSettings settings) =>
        WhatsNewViewModel.MarkSeen(settings, AppVersionInfo.GetCurrentVersionText());

    private void MarkSeen() => MarkSeenForCurrentVersion(_settings);

    private void OnDoneClicked(object sender, RoutedEventArgs e) => Close();

    private void OnOpenStudioSettingsClicked(object sender, RoutedEventArgs e)
    {
        ((App)Application.Current).OpenSettingsWindow(SettingsSectionKind.Studio);
        Close();
    }
}
