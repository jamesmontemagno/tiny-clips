using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TinyClips.Core.Services;

namespace TinyClips.App.Settings.Sections;

/// <summary>App version, update messaging, and GitHub links.</summary>
public sealed partial class AboutSettingsSection : UserControl
{
    private const string WingetUpgradeCommand = "winget upgrade Refractored.TinyClips";

    private readonly IDisposable _realizationScope;
    private readonly IAppUpdateService _updateService;
    private Uri? _latestReleaseUri;
    private string _appVersion = "1.0.0";

    public SettingsViewModel ViewModel { get; }

    public bool IsStoreBuild => BuildFlavor.IsStoreBuild;
    public bool IsDirectBuild => BuildFlavor.IsDirectBuild;

    public AboutSettingsSection(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        _realizationScope = viewModel.BeginSectionRealization();
        _updateService = App.Services.GetRequiredService<IAppUpdateService>();
        InitializeComponent();
        SectionLifecycle.HookFirstLoad(this, viewModel, _realizationScope);

        ApplyBuildFlavorVisibility();
        UpdateAboutInfo();
        ApplyUpdateCheckResult(_updateService.LastResult);
    }

    private void ApplyBuildFlavorVisibility()
    {
        DirectBuildUpdatesCard.Visibility = IsDirectBuild ? Visibility.Visible : Visibility.Collapsed;
        StoreBuildUpdatesCard.Visibility = IsStoreBuild ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateAboutInfo()
    {
        _appVersion = QuickBugReport.GetAppVersion();
        AboutVersionText.Text = $"Version {_appVersion}";
        AboutDetailedIssueLink.NavigateUri = QuickBugReport.BuildDetailedIssueRequestUri(_appVersion);
        AboutCopyrightText.Text = $"© {DateTime.Now.Year} Refractored LLC";
    }

    private void ApplyUpdateCheckResult(AppUpdateCheckResult? result)
    {
        if (!IsDirectBuild)
        {
            return;
        }

        if (result is null)
        {
            UpdateStatusText.Text = "Check for updates to see whether a newer version is available.";
            CopyWingetCommandButton.Visibility = Visibility.Collapsed;
            OpenLatestReleaseButton.Visibility = Visibility.Collapsed;
            _latestReleaseUri = null;
            return;
        }

        _latestReleaseUri = result.ReleaseUri;
        switch (result.Status)
        {
            case AppUpdateStatus.UpToDate:
                UpdateStatusText.Text = $"You're up to date (v{result.CurrentVersion}).";
                CopyWingetCommandButton.Visibility = Visibility.Collapsed;
                OpenLatestReleaseButton.Visibility = Visibility.Collapsed;
                break;
            case AppUpdateStatus.UpdateAvailable:
                UpdateStatusText.Text = $"Update available: v{result.LatestVersion} (current v{result.CurrentVersion}).";
                CopyWingetCommandButton.Visibility = Visibility.Visible;
                OpenLatestReleaseButton.Visibility = result.ReleaseUri is not null ? Visibility.Visible : Visibility.Collapsed;
                break;
            default:
                UpdateStatusText.Text = $"Couldn't check for updates: {result.Message ?? "Unknown error."}";
                CopyWingetCommandButton.Visibility = Visibility.Collapsed;
                OpenLatestReleaseButton.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private async void OnCheckForUpdatesClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            CheckForUpdatesButton.IsEnabled = false;
            UpdateStatusText.Text = "Checking for updates...";

            var result = await _updateService.CheckForUpdatesAsync(AppVersionInfo.GetCurrentVersion());
            ApplyUpdateCheckResult(result);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Check for updates failed unexpectedly: {ex}");
            ApplyUpdateCheckResult(AppUpdateCheckResult.Failed(AppVersionInfo.GetCurrentVersion(), "Unexpected error while checking for updates."));
        }
        finally
        {
            CheckForUpdatesButton.IsEnabled = true;
        }
    }

    private async void OnCopyWingetCommandClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            await ClipboardService.CopyTextAsync(WingetUpgradeCommand);
            UpdateStatusText.Text = "Copied: winget upgrade command. Run it in Terminal to update.";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Copy winget command failed: {ex}");
            UpdateStatusText.Text = "Couldn't copy the winget command.";
        }
    }

    private void OnOpenLatestReleaseClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var target = _latestReleaseUri ?? new Uri("https://github.com/jamesmontemagno/tiny-clips/releases/latest");
            Process.Start(new ProcessStartInfo(target.ToString())
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Open latest release failed: {ex}");
            UpdateStatusText.Text = "Couldn't open the latest release page.";
        }
    }

    private async void OnFileBugClick(object sender, RoutedEventArgs e)
    {
        await OpenQuickBugReportAsync();
    }

    private Task OpenQuickBugReportAsync() =>
        QuickBugReport.ShowQuickBugDialogAndOpenAsync(
            XamlRoot,
            _appVersion,
            QuickBugReport.GetDistributionChannel());
}
