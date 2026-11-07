namespace TinyClips.Core.Studio.Editing;

/// <summary>The keys the Studio editor listens for. Anything else is <see cref="Other"/>.</summary>
public enum StudioShortcutKey
{
    Other,
    Space,
    A,
    C,
    T,
    Left,
    Right,
    Escape,
    I,
    O,
    R,
    S,
    X,
    Z,
    Y,
    E,
    H,
    B,
    M,
    D,
    Home,
    End,

    /// <summary>Delete, and Backspace, which does the same.</summary>
    Delete,

    /// <summary>The 1 key of the number row, whatever it types on the current keyboard layout.</summary>
    Digit1,
    Digit2,
    Digit3,
    Digit4,
}

/// <summary>What a key press does in the Studio editor.</summary>
public enum StudioShortcutAction
{
    /// <summary>Nothing. The key is left for whatever else wants it.</summary>
    None,
    TogglePlayback,
    PreviousFrame,
    NextFrame,

    /// <summary>Moves the playhead one second back, or to the start of the video if that is nearer.</summary>
    BackOneSecond,

    /// <summary>Moves the playhead one second on, or to the end of the video if that is nearer.</summary>
    ForwardOneSecond,

    /// <summary>Moves the playhead to the start of the trimmed video.</summary>
    GoToStart,

    /// <summary>Moves the playhead to the end of the trimmed video.</summary>
    GoToEnd,
    SetTrimStartAtPlayhead,
    SetTrimEndAtPlayhead,
    ShowScreenLayout,
    ShowBubbleLayout,
    ShowSideBySideLayout,
    ShowCameraLayout,
    AddZoom,
    RemoveSelectedZoom,
    SplitScene,
    RemoveCurrentScene,
    AddCut,
    RemoveSelectedCut,
    AddSpeed,
    RemoveSelectedSpeed,
    AddArrow,
    AddCallout,
    AddText,
    AddRectangle,
    AddEllipse,
    AddRedaction,
    AddEmoji,
    RemoveSelectedAnnotation,

    /// <summary>Copies the selected annotation, a little right and down, and selects the copy. Ctrl+D.</summary>
    DuplicateSelectedAnnotation,
    Undo,
    Redo,
    Export,
    CancelExport,

    /// <summary>
    /// Asks for a project that was saved as a folder, to open it. The window acts on this one
    /// and on the next itself: both ask the user something first, which an editor cannot.
    /// </summary>
    OpenProject,

    /// <summary>Asks where to save the project as a folder, and saves it there. Ctrl+S.</summary>
    SaveProject,
    SaveProjectAs,

    /// <summary>Stops the save of the project as a folder that is under way.</summary>
    CancelProjectSave,

    /// <summary>
    /// Asks the window to close, as its close button does. The window acts on this one itself,
    /// with whatever closing asks first: an editor cannot close its window.
    /// </summary>
    RequestClose,
}

/// <summary>One key press, and what the editor was doing when it arrived.</summary>
/// <param name="Key">The key that went down.</param>
/// <param name="IsControlDown">Whether Ctrl was held.</param>
/// <param name="IsShiftDown">Whether Shift was held.</param>
/// <param name="IsAltDown">Whether Alt was held.</param>
/// <param name="IsRepeat">True when the key is being held and this is not its first press.</param>
/// <param name="IsTextInputFocused">
/// True when the focused control is text being edited. It keeps every key, including its own Undo
/// and Redo.
/// </param>
/// <param name="IsReady">
/// Whether the project is open (<see cref="StudioEditorSession.IsReady"/>). Esc is the one key
/// this makes no difference to: a window that cannot show its project closes on Esc too.
/// </param>
/// <param name="IsExporting">Whether an export is running.</param>
public readonly record struct StudioShortcutInput(
    StudioShortcutKey Key,
    bool IsControlDown,
    bool IsShiftDown,
    bool IsAltDown,
    bool IsRepeat,
    bool IsTextInputFocused,
    bool IsReady,
    bool IsExporting)
{
    /// <summary>
    /// True when the focused control is a list that searches as you type, such as a drop-down. It
    /// keeps the keys that type something, and leaves the Ctrl shortcuts to the editor.
    /// </summary>
    public bool IsTypeToSearchFocused { get; init; }

    /// <summary>
    /// True while the list of a drop-down is open. Esc is then the list's, which closes on it. A
    /// drop-down that only has the focus, with its list closed, does not keep Esc: the focus
    /// stays on a drop-down after a choice is made from it, and Esc would otherwise do nothing
    /// in the window until the focus was moved.
    /// </summary>
    public bool IsDropDownOpen { get; init; }

    /// <summary>Whether a zoom is selected. Delete removes it, and without one is left alone.</summary>
    public bool HasSelectedZoom { get; init; }

    /// <summary>Whether a cut is selected. Delete removes it, which puts its stretch back.</summary>
    public bool HasSelectedCut { get; init; }

    /// <summary>
    /// Whether a speed change is selected. Delete removes it, so its stretch plays at the
    /// recording's own speed again.
    /// </summary>
    public bool HasSelectedSpeed { get; init; }

    /// <summary>Whether an annotation is selected. Delete removes it.</summary>
    public bool HasSelectedAnnotation { get; init; }

    /// <summary>
    /// True when the focused control is a scene on the scene lane. Delete then removes the scene
    /// the playhead is in, and not the selected zoom, cut or speed change.
    /// </summary>
    public bool IsSceneFocused { get; init; }

    /// <summary>
    /// True while a pointer is dragging something in the editor: a slider, the camera, a trim
    /// handle, or a block on a lane (<see cref="StudioEditorSession.IsInGesture"/>). A key that
    /// changes the project, and Ctrl+E, then does nothing until the pointer lets go. A layout key
    /// in the middle of a drag of the camera would take the camera away from under the pointer,
    /// and Delete would take away the zoom, cut or speed change the pointer is holding, after
    /// which the rest of the drag moves the one next to it. Space and the arrow keys only move
    /// the playhead, and still do. Esc does not ask the window to close in the middle of a drag
    /// either.
    /// </summary>
    public bool IsDragging { get; init; }

    /// <summary>
    /// True while the project is being saved as a folder
    /// (<see cref="StudioEditorSession.IsSavingProjectFolder"/>). The editor takes no edits
    /// then, so no key acts but Esc, which stops the save as it stops an export.
    /// </summary>
    public bool IsSavingProject { get; init; }
}

/// <summary>
/// The Studio editor's keyboard model, the same as on the Mac, where Ctrl is Command:
/// <list type="bullet">
/// <item>Playback: Space plays or pauses, Left and Right step a frame, Shift+Left and
/// Shift+Right a second, and Home and End go to the start and the end of the trimmed video.</item>
/// <item>Edits at the playhead: I and O set the trim, S splits the scene, Z adds a zoom, X a cut,
/// R a speed change, and 1 to 4 choose the layout.</item>
/// <item>Annotations: A an arrow, C a callout, T text, H a rectangle, E an ellipse, B a redaction
/// and M an emoji.</item>
/// <item>Selection: Delete or Backspace removes the selected zoom, cut, speed change or
/// annotation, or the current scene while a scene on the lane has the focus, and Ctrl+D
/// duplicates the selected annotation.</item>
/// <item>Ctrl+Z, Ctrl+Y or Ctrl+Shift+Z, and Ctrl+E undo, redo and export. Ctrl+O opens a project
/// that was saved as a folder and Ctrl+S saves this one as a folder (Ctrl+Shift+S somewhere
/// else), where the Mac has Command-O and Shift-Command-S.</item>
/// <item>Esc stops a running export or a save of the project that is under way, and otherwise
/// asks the window to close; held down, it does none of them.</item>
/// </list>
/// While something is being dragged, only the keys that move the playhead act.
/// </summary>
/// <remarks>
/// The window only asks about a key that the focused control did not use, so a focused slider
/// keeps its arrow keys and a focused button keeps Space. The Windows window makes one
/// exception: Space on the chosen item of the inspector's rail, which it asks about before
/// the list has the key, because the list would keep it and do nothing with it there.
/// </remarks>
public static class StudioShortcuts
{
    public static StudioShortcutAction Resolve(StudioShortcutInput input)
    {
        if (input.IsAltDown)
        {
            return StudioShortcutAction.None;
        }

        if (input.Key == StudioShortcutKey.Escape)
        {
            return ResolveEscape(input);
        }

        // Text being edited keeps every key, including its own Undo and Redo.
        if (input.IsTextInputFocused || !input.IsReady || input.IsExporting || input.IsSavingProject)
        {
            return StudioShortcutAction.None;
        }

        var action = input.IsControlDown
            ? ResolveControlKey(input)
            : input.IsTypeToSearchFocused ? StudioShortcutAction.None : ResolvePlainKey(input);

        // A drag keeps what it holds until the pointer lets go.
        return input.IsDragging && !MovesOnlyThePlayhead(action) ? StudioShortcutAction.None : action;
    }

    /// <summary>
    /// Esc. Each press takes the first of these that applies. With Ctrl or Shift held it is the
    /// system's, as it is with Alt, which <see cref="Resolve"/> has dealt with by now. A key
    /// that is being held does nothing at all: the press it belongs to has done what there was
    /// to do, and may have done it somewhere else. Held a little too long, the Esc that answered
    /// "Keep exporting" would otherwise stop the export it was asked to keep, the Esc that
    /// stopped an export would go on to close the window, and the Esc whose question was
    /// answered would ask it again. While an export runs it stops the export, and that is all;
    /// while the project is being saved as a folder it stops that save, and that is all.
    /// In text that is being edited, and while the list of a drop-down is open, the key belongs
    /// to that control. In the middle of a drag it waits, like every key that does more than
    /// move the playhead. Otherwise it asks the window to close.
    /// </summary>
    /// <remarks>
    /// Whether the project is open does not come into it: an editor that cannot show its
    /// project closes on Esc as it does by its close button. What is selected does not either:
    /// Esc lets go of nothing first. Nor does a drop-down that has the focus while its list is
    /// closed (<see cref="StudioShortcutInput.IsTypeToSearchFocused"/>), as in the other editors.
    /// </remarks>
    private static StudioShortcutAction ResolveEscape(StudioShortcutInput input)
    {
        if (input.IsControlDown || input.IsShiftDown || input.IsRepeat)
        {
            return StudioShortcutAction.None;
        }

        if (input.IsExporting)
        {
            return StudioShortcutAction.CancelExport;
        }

        if (input.IsSavingProject)
        {
            return StudioShortcutAction.CancelProjectSave;
        }

        return input.IsTextInputFocused || input.IsDropDownOpen || input.IsDragging
            ? StudioShortcutAction.None
            : StudioShortcutAction.RequestClose;
    }

    private static bool MovesOnlyThePlayhead(StudioShortcutAction action) => action
        is StudioShortcutAction.TogglePlayback
        or StudioShortcutAction.PreviousFrame
        or StudioShortcutAction.NextFrame
        or StudioShortcutAction.BackOneSecond
        or StudioShortcutAction.ForwardOneSecond
        or StudioShortcutAction.GoToStart
        or StudioShortcutAction.GoToEnd;

    private static StudioShortcutAction ResolveControlKey(StudioShortcutInput input) => input.Key switch
    {
        StudioShortcutKey.Z => input.IsShiftDown ? StudioShortcutAction.Redo : StudioShortcutAction.Undo,
        StudioShortcutKey.Y when !input.IsShiftDown => StudioShortcutAction.Redo,
        StudioShortcutKey.E when !input.IsShiftDown && !input.IsRepeat => StudioShortcutAction.Export,

        // Without an annotation selected Ctrl+D is left for whatever else wants it.
        StudioShortcutKey.D when !input.IsShiftDown && !input.IsRepeat && input.HasSelectedAnnotation && !input.IsTypeToSearchFocused => StudioShortcutAction.DuplicateSelectedAnnotation,

        // Plain O sets the trim end and plain S splits the scene. With Ctrl they are about
        // the project as a whole. Each asks something first, so a key that is held asks once.
        //
        // Saving is Ctrl+S on Windows, not the Mac's key with Shift: Ctrl+Shift+S is the
        // app's own global hotkey for Stop recording, which Windows hands to the app and
        // not to the window that has the keyboard, so in the app it never arrives here.
        // Where it does arrive, because the hotkey could not be registered, it saves too.
        StudioShortcutKey.O when !input.IsShiftDown && !input.IsRepeat => StudioShortcutAction.OpenProject,
        StudioShortcutKey.S when !input.IsRepeat => input.IsShiftDown ? StudioShortcutAction.SaveProjectAs : StudioShortcutAction.SaveProject,
        _ => StudioShortcutAction.None,
    };

    private static StudioShortcutAction ResolvePlainKey(StudioShortcutInput input)
    {
        // Holding an arrow key keeps stepping, through frames, or seconds with Shift. The other
        // keys act once per press, and with Shift not at all.
        switch (input.Key)
        {
            case StudioShortcutKey.Left:
                return input.IsShiftDown ? StudioShortcutAction.BackOneSecond : StudioShortcutAction.PreviousFrame;
            case StudioShortcutKey.Right:
                return input.IsShiftDown ? StudioShortcutAction.ForwardOneSecond : StudioShortcutAction.NextFrame;
        }

        if (input.IsShiftDown)
        {
            return StudioShortcutAction.None;
        }

        if (input.IsRepeat)
        {
            return StudioShortcutAction.None;
        }

        return input.Key switch
        {
            StudioShortcutKey.Space => StudioShortcutAction.TogglePlayback,
            StudioShortcutKey.A => StudioShortcutAction.AddArrow,
            StudioShortcutKey.C => StudioShortcutAction.AddCallout,
            StudioShortcutKey.T => StudioShortcutAction.AddText,
            StudioShortcutKey.H => StudioShortcutAction.AddRectangle,
            StudioShortcutKey.E => StudioShortcutAction.AddEllipse,
            StudioShortcutKey.B => StudioShortcutAction.AddRedaction,
            StudioShortcutKey.M => StudioShortcutAction.AddEmoji,
            StudioShortcutKey.Home => StudioShortcutAction.GoToStart,
            StudioShortcutKey.End => StudioShortcutAction.GoToEnd,
            StudioShortcutKey.I => StudioShortcutAction.SetTrimStartAtPlayhead,
            StudioShortcutKey.O => StudioShortcutAction.SetTrimEndAtPlayhead,
            StudioShortcutKey.R => StudioShortcutAction.AddSpeed,
            StudioShortcutKey.S => StudioShortcutAction.SplitScene,
            StudioShortcutKey.X => StudioShortcutAction.AddCut,
            StudioShortcutKey.Z => StudioShortcutAction.AddZoom,
            StudioShortcutKey.Delete when input.IsSceneFocused => StudioShortcutAction.RemoveCurrentScene,
            StudioShortcutKey.Delete when input.HasSelectedCut => StudioShortcutAction.RemoveSelectedCut,
            StudioShortcutKey.Delete when input.HasSelectedSpeed => StudioShortcutAction.RemoveSelectedSpeed,
            StudioShortcutKey.Delete when input.HasSelectedAnnotation => StudioShortcutAction.RemoveSelectedAnnotation,
            StudioShortcutKey.Delete when input.HasSelectedZoom => StudioShortcutAction.RemoveSelectedZoom,
            StudioShortcutKey.Digit1 => StudioShortcutAction.ShowScreenLayout,
            StudioShortcutKey.Digit2 => StudioShortcutAction.ShowBubbleLayout,
            StudioShortcutKey.Digit3 => StudioShortcutAction.ShowSideBySideLayout,
            StudioShortcutKey.Digit4 => StudioShortcutAction.ShowCameraLayout,
            _ => StudioShortcutAction.None,
        };
    }
}
