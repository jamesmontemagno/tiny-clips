using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TinyClips.App.ViewModels.Studio;
using TinyClips.Core.Studio;

namespace TinyClips.App.Settings.Sections;

/// <summary>
/// Tiny Clips Studio's page of Settings: the switch that turns Studio on while it is a preview,
/// and, while it is on, how a Studio recording is started, project storage with its cleanup
/// rules, and the recordings that only their project holds. It comes after Video in the
/// navigation.
/// </summary>
public sealed partial class StudioSettingsSection : UserControl, ISettingsSectionLifecycle
{
    private readonly IDisposable _realizationScope;
    private bool _closed;

    public SettingsViewModel ViewModel { get; }

    public StudioSettingsSection(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        _realizationScope = viewModel.BeginSectionRealization(SettingsSectionKind.Studio);
        InitializeComponent();
        SectionLifecycle.HookFirstLoad(this, viewModel, _realizationScope);

        // Never faults. With Studio switched off it reads only what the line under the switch
        // says. The projects are read when this page is first shown, and not when Settings opens.
        _ = viewModel.EnsureStudioStorageInitializedAsync();
    }

    public void NotifyWindowClosed() => _closed = true;

    private async void OnCleanUpStudioProjects(object sender, RoutedEventArgs e)
    {
        await ViewModel.CleanUpStudioProjectsAsync();
        if (_closed)
        {
            return;
        }

        // The status line is a live region, so screen readers need to be told its text changed.
        var peer = FrameworkElementAutomationPeer.FromElement(StudioCleanupStatusText)
            ?? FrameworkElementAutomationPeer.CreatePeerForElement(StudioCleanupStatusText);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void OnOpenStudioDraft(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: StudioDraftItem draft } && Application.Current is App app)
        {
            app.OpenStudioWindow(draft.Id);
        }
    }

    /// <summary>
    /// Shows one of this section's dialogs, and returns what was chosen. Returns null when it
    /// could not be shown: the section is no longer on a window, or another dialog is open in
    /// it. See <see cref="SettingsDialog"/> for why that has to be answered and not thrown.
    /// </summary>
    private Task<ContentDialogResult?> TryShowAsync(ContentDialog dialog) =>
        _closed ? Task.FromResult<ContentDialogResult?>(null) : SettingsDialog.TryShowAsync(dialog, XamlRoot);

    /// <summary>
    /// Saves a row's screen recording as an ordinary video. From there it is a saved video like
    /// any other, and the app announces it as one. A failure is said in a dialog, or in a
    /// notification where no dialog can be shown.
    /// </summary>
    /// <remarks>
    /// The button stays as it is while the recording is copied, so that it keeps the keyboard
    /// focus, and a press then saves nothing more. A screen reader is told that the recording
    /// is being saved at the press that starts the copy and at every press while it runs, so
    /// that no press goes without an answer.
    /// </remarks>
    private async void OnSaveStudioDraftRecording(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: StudioDraftItem draft } button)
        {
            return;
        }

        var saving = ViewModel.SaveStudioScreenRecordingAsync(draft);
        if (draft.IsSavingRecording && !_closed)
        {
            TellScreenReader(button, StudioScreenRecording.SavingMessage, "StudioDraftRecordingSaving");
        }

        var (path, error) = await saving;
        if (path is not null)
        {
            // Also when Settings was closed while the recording was being copied: the video is there.
            (Application.Current as App)?.AnnounceStudioVideoSaved(path);
            return;
        }

        if (error is null)
        {
            return;
        }

        var failure = new ContentDialog
        {
            Title = "The screen recording was not saved",
            Content = error,
            CloseButtonText = "OK",
        };
        if (await TryShowAsync(failure) is null)
        {
            // Settings was closed while the recording was being copied, or is asking something
            // else. The failure is said all the same.
            App.ShowMessageNotification($"The screen recording was not saved. {error}");
        }
    }

    /// <summary>
    /// Says a sentence through a screen reader, from the control it is about. Never fails: it
    /// is called from handlers that are <c>async void</c>.
    /// </summary>
    private static void TellScreenReader(FrameworkElement element, string message, string activityId)
    {
        try
        {
            var peer = FrameworkElementAutomationPeer.FromElement(element)
                ?? FrameworkElementAutomationPeer.CreatePeerForElement(element);
            peer?.RaiseNotificationEvent(
                AutomationNotificationKind.Other,
                AutomationNotificationProcessing.MostRecent,
                message,
                activityId);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Studio settings could not tell a screen reader \"{message}\": {ex}");
        }
    }

    private async void OnDeleteStudioDraft(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: StudioDraftItem draft })
        {
            return;
        }

        var confirmation = new ContentDialog
        {
            Title = "Delete this draft?",
            Content = $"\"{draft.Name}\" and its recordings will be removed. This cannot be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        // Not shown, or not answered with Delete: nothing is deleted.
        if (await TryShowAsync(confirmation) != ContentDialogResult.Primary || _closed)
        {
            return;
        }

        var index = ViewModel.StudioDrafts.IndexOf(draft);
        var error = await ViewModel.DeleteStudioDraftAsync(draft);
        if (error is null)
        {
            if (!_closed)
            {
                FocusAfterDraftRemoved(index);
            }

            return;
        }

        var failure = new ContentDialog
        {
            Title = "The draft was not deleted",
            Content = error,
            CloseButtonText = "OK",
        };
        if (await TryShowAsync(failure) is null)
        {
            App.ShowMessageNotification($"The draft was not deleted. {error}");
        }
    }

    /// <summary>
    /// The button that had the keyboard focus went away with its row. Focus goes to the row that
    /// took its place, to the last row when that was the last one, or to Clean up now when no
    /// draft is left, so that it is not lost.
    /// </summary>
    private void FocusAfterDraftRemoved(int index)
    {
        // The list has just been filled again. Its rows are there once it has been laid out.
        StudioDraftsRepeater.UpdateLayout();
        var count = ViewModel.StudioDrafts.Count;
        var row = count > 0 ? StudioDraftsRepeater.TryGetElement(Math.Clamp(index, 0, count - 1)) : null;
        Control target = FindFirstButton(row) ?? StudioCleanUpNowButton;
        target.Focus(FocusState.Programmatic);
    }

    private static Button? FindFirstButton(DependencyObject? root)
    {
        if (root is null)
        {
            return null;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);

            // A row does not show every button: one that cannot be opened has no Open.
            if (child is Button { Visibility: Visibility.Visible, IsEnabled: true } button)
            {
                return button;
            }

            if (FindFirstButton(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}
