using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TinyClips.App.Views.Studio;

/// <summary>What the user decided about a recording that was never exported.</summary>
public enum StudioClosePromptChoice
{
    /// <summary>Keep the window open. Esc and the Cancel button mean this.</summary>
    Cancel,

    /// <summary>Export the recording, and close the window when that has worked.</summary>
    Export,

    /// <summary>Close the window and keep the project to finish later.</summary>
    KeepAsDraft,

    /// <summary>Close the window and delete the project.</summary>
    Delete,
}

/// <summary>
/// Asked when a Studio window closes on a recording that was never exported: export it, keep it as
/// a draft, delete it, or cancel and keep editing. Export is the default; Delete never is.
/// </summary>
/// <remarks>
/// A dialog gives its first focus to the first control in its content that can take it, and only
/// when there is none to its default button. Delete is in the content, and Enter must not be one
/// key press away from deleting a recording. So Delete starts outside the tab order, the dialog
/// puts the focus on its default button, and after that Delete can be reached with Tab.
/// </remarks>
public sealed partial class StudioClosePromptDialog : ContentDialog
{
    /// <param name="canExport">False when there is nothing to export, which takes that choice away.</param>
    public StudioClosePromptDialog(bool canExport)
    {
        InitializeComponent();

        if (!canExport)
        {
            IsPrimaryButtonEnabled = false;
            DefaultButton = ContentDialogButton.Secondary;
        }

        PrimaryButtonClick += (_, _) => Choice = StudioClosePromptChoice.Export;
        SecondaryButtonClick += (_, _) => Choice = StudioClosePromptChoice.KeepAsDraft;
        DeleteButton.Loaded += OnDeleteButtonLoaded;
    }

    /// <summary>What was chosen. <see cref="StudioClosePromptChoice.Cancel"/> until a button says otherwise.</summary>
    public StudioClosePromptChoice Choice { get; private set; } = StudioClosePromptChoice.Cancel;

    /// <summary>
    /// The dialog gives its first focus when its root has loaded, which is in the same round of
    /// loading as this button or an earlier one. Once that round is over, Delete joins the tab
    /// order.
    /// </summary>
    private void OnDeleteButtonLoaded(object sender, RoutedEventArgs e)
    {
        DeleteButton.Loaded -= OnDeleteButtonLoaded;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => DeleteButton.IsTabStop = true);
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        Choice = StudioClosePromptChoice.Delete;
        Hide();
    }
}
