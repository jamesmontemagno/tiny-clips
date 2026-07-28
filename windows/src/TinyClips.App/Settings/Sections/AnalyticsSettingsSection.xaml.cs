using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace TinyClips.App.Settings.Sections;

/// <summary>Capture-history chart, totals, insights, and analytics reset/copy actions.</summary>
public sealed partial class AnalyticsSettingsSection : UserControl
{
    private readonly IDisposable _realizationScope;
    private readonly nint _settingsWindowHandle;

    // Retained so the (internally try/catch-wrapped, never-faulting) analytics load task is
    // observed rather than fire-and-forget. Exposed for tests and diagnostics.
    private readonly Task _analyticsInitialization;

    public SettingsViewModel ViewModel { get; }

    public Task AnalyticsInitialization => _analyticsInitialization;

    public AnalyticsSettingsSection(SettingsViewModel viewModel, nint settingsWindowHandle)
    {
        ViewModel = viewModel;
        _settingsWindowHandle = settingsWindowHandle;
        _realizationScope = viewModel.BeginSectionRealization();
        InitializeComponent();
        SectionLifecycle.HookFirstLoad(this, viewModel, _realizationScope);

        _analyticsInitialization = viewModel.EnsureAnalyticsInitializedAsync();
    }

    /// <summary>Used by the InfoBar's x:Bind to show/hide based on whether an error message is set.</summary>
    public static bool HasError(string? message) => !string.IsNullOrEmpty(message);

    private async void OnResetAnalytics(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Reset capture analytics?",
            Content = "This clears all local screenshot, video, and GIF counts — including lifetime totals. This can't be undone.",
            PrimaryButtonText = "Reset",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.ResetAnalytics();
        }
    }

    private async void OnCopyAnalyticsSummary(object sender, RoutedEventArgs e)
    {
        await ViewModel.CopyAnalyticsSummaryAsync();

        var originalContent = CopyAnalyticsSummaryButton.Content;
        CopyAnalyticsSummaryButton.Content = "Copied!";
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        CopyAnalyticsSummaryButton.Content = originalContent;
    }

    private void OnShareAnalyticsSummary(object sender, RoutedEventArgs e)
    {
        var summary = ViewModel.BuildAnalyticsSummaryText();
        var dataTransferManager = DataTransferManagerInterop.GetForWindow(_settingsWindowHandle);

        TypedEventHandler<DataTransferManager, DataRequestedEventArgs>? handler = null;
        handler = (_, args) =>
        {
            args.Request.Data.Properties.Title = "My TinyClips capture activity";
            args.Request.Data.SetText(summary);
        };

        dataTransferManager.DataRequested += handler;
        try
        {
            DataTransferManager.ShowShareUI();
        }
        finally
        {
            dataTransferManager.DataRequested -= handler;
        }
    }
}
