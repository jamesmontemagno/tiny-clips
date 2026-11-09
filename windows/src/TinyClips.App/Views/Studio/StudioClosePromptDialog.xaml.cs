using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.App.Views.Studio;

/// <summary>What the user decided about a recording that has no saved project folder.</summary>
public enum StudioClosePromptChoice
{
    /// <summary>Keep the window open. Esc and the Cancel button mean this.</summary>
    Cancel,

    /// <summary>Close the window and keep the project to finish later.</summary>
    KeepAsDraft,

    /// <summary>Close the window and delete the project.</summary>
    Delete,
}

/// <summary>
/// Asked when a Studio window closes on a project that has no saved project folder: keep it as
/// a draft, delete it, or cancel and keep editing. Keep as draft is the default; Delete never is.
/// </summary>
/// <remarks>
/// A dialog gives its first focus to the first control in its content that can take it, and only
/// when there is none to its default button. Delete is in the content, and Enter must not be one
/// key press away from deleting a recording. So Delete starts outside the tab order, the dialog
/// puts the focus on its default button, and after that Delete can be reached with Tab.
/// </remarks>
public sealed partial class StudioClosePromptDialog : ContentDialog
{
    public StudioClosePromptDialog()
    {
        InitializeComponent();
        DeleteIcon.Glyph = StudioGlyphs.Delete;

        PrimaryButtonClick += (_, _) => Choice = StudioClosePromptChoice.KeepAsDraft;
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
