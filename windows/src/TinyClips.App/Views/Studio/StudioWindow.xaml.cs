using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TinyClips.App.Controls.Studio;
using TinyClips.App.Services.Studio;
using TinyClips.App.ViewModels.Studio;
using TinyClips.Core.Editing;
using TinyClips.Core.Models;
using TinyClips.Core.Services;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;
using Windows.UI.Core;
using ActivatedHandler = Windows.Foundation.TypedEventHandler<object, Microsoft.UI.Xaml.WindowActivatedEventArgs>;
using VirtualKey = Windows.System.VirtualKey;

namespace TinyClips.App.Views.Studio;

/// <summary>
/// The Studio editor for one project: header, preview, inspector and timeline. The window shows
/// what <see cref="StudioViewModel"/> reports, asks the questions that closing needs, and passes
/// the keys on. What any of it means is decided in <see cref="StudioEditorSession"/>.
/// </summary>
public sealed partial class StudioWindow : Window
{
    private const int DefaultWidthDip = 1180;
    private const int DefaultHeightDip = 760;

    // Below this the header's controls and the 320 wide inspector crowd the preview out.
    private const int MinimumWidthDip = 980;
    private const int MinimumHeightDip = 640;

    private readonly IStudioPreviewViewFactory _previewViews;
    private readonly ICaptureSettings _settings;
    private readonly WindowChromeController _chromeController;
    private readonly StudioTimeline _timeline;
    private readonly Action<StudioWindow, Task> _onClosed;
    private FrameworkElement? _previewView;
    private Task? _teardown;
    private FocusRequest _focusRequest;
    private bool _isActive;
    private bool _wasExporting;
    private bool _isPromptOpen;
    private bool _closeConfirmed;
    private bool _deleteOnClose;
    private bool _isClosed;

    /// <param name="viewModel">The editor for the project this window shows.</param>
    /// <param name="previewViews">Makes the element the preview is drawn in.</param>
    /// <param name="settings">Where the app theme comes from, and whether Esc asks before it closes the window.</param>
    /// <param name="onClosed">
    /// Called once when the window is closing for good, with a task that finishes when the
    /// project's files have been let go of and, if the user chose to delete the project, it is
    /// gone.
    /// </param>
    public StudioWindow(
        StudioViewModel viewModel,
        IStudioPreviewViewFactory previewViews,
        ICaptureSettings settings,
        Action<StudioWindow, Task> onClosed)
    {
        ViewModel = viewModel;
        _previewViews = previewViews;
        _settings = settings;
        _onClosed = onClosed;

        InitializeComponent();

        foreach (var aspect in Enum.GetValues<StudioCanvasAspect>())
        {
            CanvasChoice.Items.Add(StudioEditorModel.GetAspectName(aspect));
        }

        InspectorHost.Child = new StudioInspector(viewModel);
        _timeline = new StudioTimeline(viewModel);
        TimelineHost.Child = _timeline;
        CanvasHost.Children.Add(new StudioPreviewOverlay(viewModel));

        // The message bar reads its message out when it opens, but only when it already has an
        // automation peer, which it otherwise gets when a screen reader happens to look at it.
        _ = FrameworkElementAutomationPeer.CreatePeerForElement(ErrorBar);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        AppWindowPlacement.CenterInCurrentWorkAreaAtDipSize(AppWindow, hwnd, DefaultWidthDip, DefaultHeightDip);

        // WindowChromeController owns the icon, the minimum size in effective pixels, and keeping
        // that minimum right when the window moves to a display with another scale.
        _chromeController = new WindowChromeController(this, RootGrid, MinimumWidthDip, MinimumHeightDip);

        RootGrid.RequestedTheme = settings.Theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        AppTitleBar.Loaded += OnDecoratedControlLoaded;
        LoadingRing.Loaded += OnDecoratedControlLoaded;
        ErrorBar.SizeChanged += OnErrorBarSizeChanged;
        ViewModel.StateChanged += OnStateChanged;
        ViewModel.Announced += OnAnnounced;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Activated += OnActivated;
        RootGrid.KeyDown += OnRootKeyDown;
        AppWindow.Closing += OnAppWindowClosing;
        Closed += OnClosed;

        // Queued, so the window is on screen saying "Opening" before the project is read.
        DispatcherQueue.TryEnqueue(() => _ = ViewModel.LoadAsync());
    }

    /// <summary>Where the keyboard focus is waiting to be put.</summary>
    private enum FocusRequest
    {
        None,

        /// <summary>The window has opened: on Play, or on the message when the project cannot be shown.</summary>
        Opened,

        /// <summary>An export started or ended: on Cancel while it runs, on Export afterwards.</summary>
        Export,
    }

    public StudioViewModel ViewModel { get; }

    /// <summary>True once the user chose to delete the project. The window is closing by then.</summary>
    public bool IsDeletingProject => _deleteOnClose;

    /// <summary>
    /// True from the moment the window is closing for good. What goes wrong from then on, the
    /// last save among it, has no message bar to be shown in.
    /// </summary>
    public bool IsClosing => _isClosed;

    /// <summary>
    /// Closes the window without asking anything, for when the app is exiting. The edits are saved
    /// and a running export is stopped by the time this returns; the task finishes when the
    /// project's files have been let go of.
    /// </summary>
    public Task CloseForExit()
    {
        var teardown = TearDown();
        CloseWithoutAsking();
        return teardown;
    }

    private void CloseWithoutAsking()
    {
        _closeConfirmed = true;
        Close();
    }

    // State

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (_isClosed)
        {
            return;
        }

        if (ViewModel.IsReady)
        {
            AttachPreviewView();

            // Space and the arrow keys start from Play.
            RequestFocus(FocusRequest.Opened);
            return;
        }

        DetachPreviewView();
        if (ViewModel.IsUnavailable)
        {
            // The focused element is what a screen reader reads, also in a window that has only
            // just opened, where a notice sent this early would not arrive.
            RequestFocus(FocusRequest.Opened);
        }
    }

    // Focus

    private void OnActivated(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs args)
    {
        _isActive = args.WindowActivationState != WindowActivationState.Deactivated;
        if (args.WindowActivationState == WindowActivationState.PointerActivated)
        {
            // The click that activated the window says where the focus goes.
            _focusRequest = FocusRequest.None;
        }
        else if (_isActive)
        {
            PlaceFocusLater();
        }
    }

    /// <summary>
    /// Asks for the keyboard focus to be put where the editor's state calls for.
    /// </summary>
    /// <remarks>
    /// Moving the focus also asks Windows for the keyboard focus, and Windows answers that by
    /// activating the window: it comes in front of the app's other windows, and of other apps
    /// when Windows lets it. A project that has finished opening or an export that has ended is
    /// no reason for that. So a window that is not the active one keeps the request, and the
    /// focus is put there when the user comes back to it.
    /// </remarks>
    private void RequestFocus(FocusRequest request)
    {
        _focusRequest = request;
        PlaceFocusLater();
    }

    // Later, so that what has only just become visible or enabled is laid out and can take focus.
    private void PlaceFocusLater()
    {
        if (_isActive && _focusRequest != FocusRequest.None)
        {
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, PlaceFocus);
        }
    }

    private void PlaceFocus()
    {
        if (_isClosed || !_isActive || _focusRequest == FocusRequest.None)
        {
            return;
        }

        var request = _focusRequest;
        _focusRequest = FocusRequest.None;
        if (ViewModel.IsUnavailable)
        {
            // The message has only just become visible, and has to be laid out to take focus.
            RootGrid.UpdateLayout();
            UnavailablePanel.Focus(FocusState.Programmatic);
        }
        else if (ViewModel.IsExporting)
        {
            // The editor under the overlay is disabled, so focus goes to the one thing that works.
            CancelExportButton.Focus(FocusState.Programmatic);
        }
        else if (ViewModel.IsEditable
            && (request != FocusRequest.Export || !ExportButton.Focus(FocusState.Programmatic)))
        {
            _timeline.FocusPlayButton();
        }
    }

    private void AttachPreviewView()
    {
        if (_previewView is not null || ViewModel.Preview is not { } preview)
        {
            return;
        }

        try
        {
            _previewView = _previewViews.Create(preview);

            // Under the overlay that holds the camera handle.
            CanvasHost.Children.Insert(0, _previewView);
        }
        catch (Exception ex)
        {
            _previewView = null;
            ViewModel.ShowError($"The preview could not be shown: {ex.Message}");
        }
    }

    /// <summary>The preview element leaves the tree before the preview it draws is disposed.</summary>
    private void DetachPreviewView()
    {
        if (_previewView is { } view)
        {
            _previewView = null;
            CanvasHost.Children.Remove(view);

            // Not left to the Unloaded event, which comes later, and not at all once the window
            // has closed.
            (view as StudioPreviewPanel)?.Detach();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_isClosed || ViewModel.IsExporting == _wasExporting)
        {
            return;
        }

        // Focus goes to Cancel while the export runs, and back to where an export is started
        // when it is over.
        _wasExporting = ViewModel.IsExporting;
        RequestFocus(FocusRequest.Export);
    }

    private void OnClipNameTrimmedChanged(TextBlock sender, IsTextTrimmedChangedEventArgs args) =>
        ToolTipService.SetToolTip(sender, sender.IsTextTrimmed ? sender.Text : null);

    private void OnExportClick(object sender, RoutedEventArgs e) => _ = ViewModel.ExportAsync();

    // Never fails: what came of it is shown under the button and read out.
    private void OnSaveScreenRecordingClick(object sender, RoutedEventArgs e) => _ = ViewModel.SaveScreenRecordingAsync();

    // Screen readers

    /// <summary>
    /// The title bar shows the app icon, and the progress ring its animation, as an image without
    /// a name: something a screen reader stops on and can say nothing about. Both are decoration,
    /// so they are taken out of what a screen reader walks.
    /// </summary>
    private void OnDecoratedControlLoaded(object sender, RoutedEventArgs e)
    {
        var control = (FrameworkElement)sender;
        control.Loaded -= OnDecoratedControlLoaded;
        HideWhatSaysNothing(control);
    }

    /// <summary>
    /// The message bar has a text for a title, which this window does not use, and which is then
    /// an empty text to a screen reader. The bar builds its parts when it is first shown.
    /// </summary>
    private void OnErrorBarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ErrorBar.SizeChanged -= OnErrorBarSizeChanged;
        HideWhatSaysNothing(ErrorBar);
    }

    /// <summary>Takes the images and texts without a name that a control is built of out of what a screen reader walks.</summary>
    private static void HideWhatSaysNothing(DependencyObject parent)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);

            // Told by what it is to a screen reader, not by its type: that needs no cast, and a
            // cast can fail on a part read back from a template when the app is compiled ahead
            // of time.
            if (child is UIElement element
                && FrameworkElementAutomationPeer.CreatePeerForElement(element) is { } peer
                && peer.GetAutomationControlType() is AutomationControlType.Image or AutomationControlType.Text
                && string.IsNullOrEmpty(peer.GetName()))
            {
                AutomationProperties.SetAccessibilityView(element, AccessibilityView.Raw);
            }

            HideWhatSaysNothing(child);
        }
    }

    private void OnAnnounced(object? sender, StudioAnnouncementEventArgs e)
    {
        var kind = e.Kind switch
        {
            StudioAnnouncementKind.Completed => AutomationNotificationKind.ActionCompleted,
            StudioAnnouncementKind.Stopped => AutomationNotificationKind.ActionAborted,
            _ => AutomationNotificationKind.Other,
        };
        Announce(e.Message, e.ActivityId, kind, AutomationNotificationProcessing.MostRecent);
    }

    private void Announce(
        string message,
        string activityId,
        AutomationNotificationKind kind,
        AutomationNotificationProcessing processing)
    {
        if (_isClosed)
        {
            return;
        }

        // Raised from the title bar, which is there in every state of the window.
        var peer = FrameworkElementAutomationPeer.FromElement(AppTitleBar)
            ?? FrameworkElementAutomationPeer.CreatePeerForElement(AppTitleBar);
        peer?.RaiseNotificationEvent(kind, processing, message, activityId);
    }

    // Keys

    /// <summary>
    /// Keys arrive here only after the focused control has passed on them, so a focused slider
    /// keeps its arrow keys and a focused button keeps Space.
    /// </summary>
    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var key = MapKey(e.Key);
        if (key != StudioShortcutKey.Other
            && RunShortcut(key, IsKeyDown(VirtualKey.Control), IsKeyDown(VirtualKey.Shift), e.KeyStatus.IsMenuKeyDown, e.KeyStatus.WasKeyDown) != StudioShortcutAction.None)
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// Does what a key means in this window as it is now, and returns what that was:
    /// <see cref="StudioShortcutAction.None"/> for a key that is left alone.
    /// </summary>
    /// <param name="isRepeat">True when the key is being held and this is not its first press.</param>
    internal StudioShortcutAction RunShortcut(StudioShortcutKey key, bool isControlDown, bool isShiftDown, bool isAltDown, bool isRepeat)
    {
        var focused = RootGrid.XamlRoot is { } root ? FocusManager.GetFocusedElement(root) : null;
        var input = new StudioShortcutInput(
            key,
            IsControlDown: isControlDown,
            IsShiftDown: isShiftDown,
            IsAltDown: isAltDown,
            IsRepeat: isRepeat,
            IsTextInputFocused: focused is TextBox or RichEditBox or PasswordBox,
            IsReady: ViewModel.IsReady,
            IsExporting: ViewModel.IsExporting)
        {
            IsTypeToSearchFocused = focused is ComboBox or ComboBoxItem,

            // Esc is a drop-down's only while its list is open. An open list closes on Esc itself
            // and marks the key as handled, so that press should never come here: this is for
            // the case that it does.
            IsDropDownOpen = focused is ComboBoxItem or ComboBox { IsDropDownOpen: true },
            HasSelectedZoom = ViewModel.HasSelectedZoom,
            HasSelectedCut = ViewModel.HasSelectedCut,
            HasSelectedSpeed = ViewModel.HasSelectedSpeed,

            // With the focus on the scene lane, Delete is about the scene the playhead is in.
            IsSceneFocused = focused is StudioSceneLane,

            // A drag keeps what it holds: Delete or a layout key in the middle of one would take
            // it away from under the pointer.
            IsDragging = ViewModel.IsInGesture,
        };

        var action = StudioShortcuts.Resolve(input);
        if (action == StudioShortcutAction.RequestClose)
        {
            // The one key the editor cannot act on itself: it has no window to close.
            return RequestCloseByEscape() ? action : StudioShortcutAction.None;
        }

        if (action != StudioShortcutAction.None)
        {
            ViewModel.Run(action);
        }

        return action;
    }

    internal static StudioShortcutKey MapKey(VirtualKey key) => key switch
    {
        VirtualKey.Space => StudioShortcutKey.Space,
        VirtualKey.Left => StudioShortcutKey.Left,
        VirtualKey.Right => StudioShortcutKey.Right,
        VirtualKey.Escape => StudioShortcutKey.Escape,
        VirtualKey.I => StudioShortcutKey.I,
        VirtualKey.O => StudioShortcutKey.O,
        VirtualKey.R => StudioShortcutKey.R,
        VirtualKey.S => StudioShortcutKey.S,
        VirtualKey.X => StudioShortcutKey.X,
        VirtualKey.Z => StudioShortcutKey.Z,
        VirtualKey.Y => StudioShortcutKey.Y,
        VirtualKey.E => StudioShortcutKey.E,
        VirtualKey.Delete => StudioShortcutKey.Delete,

        // The number row, not the number pad.
        VirtualKey.Number1 => StudioShortcutKey.Digit1,
        VirtualKey.Number2 => StudioShortcutKey.Digit2,
        VirtualKey.Number3 => StudioShortcutKey.Digit3,
        VirtualKey.Number4 => StudioShortcutKey.Digit4,
        _ => StudioShortcutKey.Other,
    };

    private static bool IsKeyDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    // Closing

    /// <summary>How the user asked for the window to close.</summary>
    private enum CloseRequest
    {
        /// <summary>The close button, Alt+F4, or a close from the system: anything that raises the AppWindow's Closing event.</summary>
        CloseButton,

        /// <summary>Esc.</summary>
        Escape,
    }

    /// <summary>What the user is asked before the window closes.</summary>
    private enum CloseQuestion
    {
        /// <summary>Nothing. The window closes.</summary>
        None,

        /// <summary>An export is running: keep exporting, or stop and close.</summary>
        RunningExport,

        /// <summary>The recording was never exported: export it, keep it as a draft, or delete it.</summary>
        Draft,

        /// <summary>Whether Esc was meant. Only Esc asks this, and only of a project that is open, where nothing else is asked.</summary>
        Escape,
    }

    /// <summary>
    /// What has to be asked when the user asks the window to close. The close button and Esc
    /// both come here, and to nothing else, so that Esc can never close what the close button
    /// would have asked about.
    /// </summary>
    /// <remarks>
    /// Esc differs in one place, the same as on the Mac: a project that is open and has been
    /// exported. Closing asks nothing there, so Esc asks whether it was meant, as it does in the
    /// other editors and behind the same setting. Where closing has a question of its own, that
    /// question is the confirmation, whatever the setting says, and Esc adds none to it. A
    /// window whose project is not open, because it is still being opened or cannot be shown,
    /// closes on Esc as it does by its close button, whatever the setting says: the question
    /// says that the edits are saved, which is no sentence for a window that opened nothing.
    /// </remarks>
    private CloseQuestion QuestionBeforeClosing(CloseRequest request)
    {
        switch (ViewModel.GetClosePrompt())
        {
            case StudioClosePrompt.ExportRunning:
                return CloseQuestion.RunningExport;
            case StudioClosePrompt.NeverExported:
                return CloseQuestion.Draft;
        }

        // Studio saves every edit as it is made, so there is never anything unsaved to ask about.
        return request == CloseRequest.Escape
            && ViewModel.IsReady
            && EditorEscape.ResolvePrompt(_settings.ConfirmEditorEscape, hasUnsavedChanges: false) is not null
                ? CloseQuestion.Escape
                : CloseQuestion.None;
    }

    private Task AskBeforeClosingAsync(CloseQuestion question) => question switch
    {
        CloseQuestion.RunningExport => AskAboutRunningExportAsync(),
        CloseQuestion.Draft => AskAboutDraftAsync(),
        CloseQuestion.Escape => AskWhetherEscapeWasMeantAsync(),
        _ => Task.CompletedTask,
    };

    /// <summary>
    /// Guards the close button, Alt+F4 and a close from the system: anything that raises the
    /// AppWindow's Closing event.
    /// </summary>
    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeConfirmed)
        {
            return;
        }

        // No window closes from under a question. While one is open the close button waits for
        // its answer, also where closing by now asks nothing: Esc's question is asked there,
        // and an export can end behind the question about it.
        var question = QuestionBeforeClosing(CloseRequest.CloseButton);
        if (question == CloseQuestion.None && !_isPromptOpen)
        {
            return;
        }

        args.Cancel = true;
        if (_isPromptOpen)
        {
            return;
        }

        await AskBeforeClosingAsync(question);
    }

    /// <summary>
    /// Esc asks the window to close, as its close button does. Returns false when the key is not
    /// the window's to act on: a question is open, and Esc belongs to that.
    /// </summary>
    /// <remarks>
    /// Window.Close does not raise the AppWindow's Closing event, so a key that called it would
    /// close past every question. This asks them itself.
    /// </remarks>
    private bool RequestCloseByEscape()
    {
        if (_isClosed || _isPromptOpen)
        {
            return false;
        }

        var question = QuestionBeforeClosing(CloseRequest.Escape);
        if (question == CloseQuestion.None)
        {
            CloseWithoutAsking();
        }
        else
        {
            _ = AskBeforeClosingAsync(question);
        }

        return true;
    }

    /// <summary>Shows one question at a time: a window can hold only one dialog.</summary>
    private async Task<ContentDialogResult> ShowPromptAsync(ContentDialog dialog)
    {
        _isPromptOpen = true;
        try
        {
            dialog.XamlRoot = RootGrid.XamlRoot;
            dialog.RequestedTheme = RootGrid.RequestedTheme;
            return await dialog.ShowAsync();
        }
        finally
        {
            _isPromptOpen = false;
        }
    }

    private async Task AskAboutRunningExportAsync()
    {
        var dialog = new ContentDialog
        {
            Title = "An export is still running.",
            Content = "Closing the window stops the export. Your edits are kept.",
            PrimaryButtonText = "Stop and close",
            CloseButtonText = "Keep exporting",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await ShowPromptAsync(dialog) == ContentDialogResult.Primary && !_isClosed)
        {
            // Closing the session stops the export.
            CloseWithoutAsking();
        }
    }

    /// <summary>
    /// The confirmation the other editors ask before Esc closes them, with Studio's words. The
    /// window closes only on its primary button.
    /// </summary>
    private async Task AskWhetherEscapeWasMeantAsync()
    {
        ViewModel.Pause();
        _isPromptOpen = true;
        bool confirmed;
        try
        {
            confirmed = await EditorEscapeConfirmation.ConfirmAsync(RootGrid, EditorEscapePrompt.CloseWithoutChanges, EditorEscapeSurface.Studio);
        }
        finally
        {
            _isPromptOpen = false;
        }

        // This question stands in only where closing asks nothing. Should closing have come to
        // need a question of its own while this one was open, the window stays.
        if (confirmed && !_isClosed && ViewModel.GetClosePrompt() == StudioClosePrompt.None)
        {
            CloseWithoutAsking();
        }
    }

    private async Task AskAboutDraftAsync()
    {
        ViewModel.Pause();
        var dialog = new StudioClosePromptDialog(ViewModel.CanExport);
        await ShowPromptAsync(dialog);
        if (_isClosed)
        {
            return;
        }

        switch (dialog.Choice)
        {
            case StudioClosePromptChoice.Export:
                // The window closes once the video is written. If the export fails or is
                // cancelled, it stays open.
                if (await ViewModel.ExportAsync() == StudioExportOutcome.Exported && !_isClosed)
                {
                    CloseWithoutAsking();
                }

                break;
            case StudioClosePromptChoice.KeepAsDraft:
                CloseWithoutAsking();
                break;
            case StudioClosePromptChoice.Delete:
                _deleteOnClose = true;
                CloseWithoutAsking();
                break;
        }
    }

    private void OnClosed(object sender, WindowEventArgs args) => TearDown();

    /// <summary>
    /// Takes the window's compiled bindings off its Activated event, so that the window can
    /// leave memory once it is closed.
    /// </summary>
    /// <remarks>
    /// The code the XAML compiler writes for a window that uses x:Bind adds a handler to the
    /// window's Activated event and has nothing that takes it off again. The window holds the
    /// handler and the handler holds the window, and the garbage collector cannot undo that:
    /// every editor that was closed stayed in memory, with everything it showed and the project
    /// behind it, and made each later run of the garbage collector longer. The handler is a
    /// method of a class the compiler writes in its second pass, which this file cannot name,
    /// because it is compiled in the first pass as well. So the method is found by its name.
    /// If the compiler ever calls it something else, nothing is taken off and nothing fails.
    /// </remarks>
    private void LetGoOfBindings()
    {
        if (Bindings is { } bindings
            && Delegate.CreateDelegate(typeof(ActivatedHandler), bindings, "Activated", ignoreCase: false, throwOnBindFailure: false) is ActivatedHandler listening)
        {
            Activated -= listening;
        }
    }

    /// <summary>Ends the editor, once. The window is closed, or about to be.</summary>
    private Task TearDown()
    {
        if (_teardown is { } started)
        {
            return started;
        }

        _isClosed = true;
        Closed -= OnClosed;
        Activated -= OnActivated;
        AppTitleBar.Loaded -= OnDecoratedControlLoaded;
        LoadingRing.Loaded -= OnDecoratedControlLoaded;
        ErrorBar.SizeChanged -= OnErrorBarSizeChanged;
        AppWindow.Closing -= OnAppWindowClosing;
        RootGrid.KeyDown -= OnRootKeyDown;
        ViewModel.StateChanged -= OnStateChanged;
        ViewModel.Announced -= OnAnnounced;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;

        LetGoOfBindings();
        DetachPreviewView();
        var teardown = ViewModel.CloseAsync(_deleteOnClose);
        _teardown = teardown;
        _onClosed(this, teardown);
        return teardown;
    }
}
