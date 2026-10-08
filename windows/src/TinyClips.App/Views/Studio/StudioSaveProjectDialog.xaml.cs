using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.App.Views.Studio;

/// <summary>
/// Asks where a project is saved as a folder: a name, suggested from the project's, and the
/// folder it is made in, which starts where the last project went. Save looks at what is
/// there before the dialog closes, and where a project cannot be saved the dialog stays and
/// says why, under the name. A saved project that would be replaced is not asked about here:
/// the dialog closes with it as its target, and the window asks.
/// </summary>
/// <remarks>
/// The system's save picker was the other way. It makes an empty file where it is pointed,
/// which a folder of that name then cannot take, and it asks its own "replace?" of a file,
/// where the question here is about a folder and has three answers.
/// </remarks>
public sealed partial class StudioSaveProjectDialog : ContentDialog
{
    private readonly Func<string?, string?, StudioSaveTarget> _check;
    private readonly Func<Task<string?>> _chooseFolder;
    private string _place;
    private bool _isChoosingFolder;

    /// <param name="folderName">The name the box starts with.</param>
    /// <param name="place">The folder the project's folder is made in, until another is chosen.</param>
    /// <param name="check">Says what is at a place and a name (<see cref="StudioProjectFolderText.CheckSaveTarget"/>).</param>
    /// <param name="chooseFolder">Shows the folder picker. Returns null when none was chosen.</param>
    public StudioSaveProjectDialog(
        string folderName,
        string place,
        Func<string?, string?, StudioSaveTarget> check,
        Func<Task<string?>> chooseFolder)
    {
        _check = check;
        _chooseFolder = chooseFolder;
        _place = place;
        InitializeComponent();

        NameBox.Text = folderName;
        NameBox.SelectAll();
        ShowPlace();
        PrimaryButtonClick += OnSaveClick;
    }

    /// <summary>Where the project is to be saved, once Save was pressed on a place that can take it. Null otherwise.</summary>
    public StudioSaveTarget? Target { get; private set; }

    /// <summary>What the name box holds.</summary>
    public string FolderName => NameBox.Text;

    /// <summary>The folder the project's folder is made in.</summary>
    public string Place => _place;

    /// <summary>What the dialog says cannot be done, or empty.</summary>
    public string Problem => ProblemText.Visibility == Visibility.Visible ? ProblemText.Text : string.Empty;

    private void OnSaveClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var target = _check(_place, NameBox.Text);
        if (target.CanSave)
        {
            Target = target;
            return;
        }

        // The dialog stays, with the reason under the name, and the focus where it is mended.
        args.Cancel = true;
        ShowProblem(target.Message);
        if (target.Kind == StudioSaveTargetKind.NoPlace)
        {
            ChooseFolderButton.Focus(FocusState.Programmatic);
        }
        else
        {
            NameBox.Focus(FocusState.Programmatic);
            NameBox.SelectAll();
        }
    }

    private async void OnChooseFolderClick(object sender, RoutedEventArgs e)
    {
        if (_isChoosingFolder)
        {
            return;
        }

        _isChoosingFolder = true;
        try
        {
            if (await _chooseFolder() is { Length: > 0 } folder)
            {
                _place = folder;
                ShowPlace();
                ShowProblem(string.Empty);
            }
        }
        catch (Exception ex)
        {
            ShowProblem($"The folder could not be chosen: {ex.Message}");
        }
        finally
        {
            _isChoosingFolder = false;
        }
    }

    // What was said about the old name says nothing about the new one.
    private void OnNameChanged(object sender, TextChangedEventArgs e) => ShowProblem(string.Empty);

    private void ShowPlace()
    {
        PlaceText.Text = _place;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PlaceText, $"In {_place}");
    }

    private void ShowProblem(string message)
    {
        if (ProblemText.Text == message && (message.Length > 0) == (ProblemText.Visibility == Visibility.Visible))
        {
            return;
        }

        ProblemText.Text = message;
        ProblemText.Visibility = message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (message.Length > 0)
        {
            // A live region says its text when it is told that the text changed.
            var peer = FrameworkElementAutomationPeer.FromElement(ProblemText)
                ?? FrameworkElementAutomationPeer.CreatePeerForElement(ProblemText);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }
}
