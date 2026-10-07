namespace TinyClips.Core.Studio.Editing;

/// <summary>The keys the Studio editor listens for. Anything else is <see cref="Other"/>.</summary>
public enum StudioShortcutKey
{
    Other,
    Space,
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
    Undo,
    Redo,
    Export,
    CancelExport,

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
}

/// <summary>
/// The Studio editor's keyboard model, the same as on the Mac: Space plays or pauses, Left and
/// Right step a frame, I and O set the trim at the playhead, 1 to 4 choose the layout, S splits the
/// scene at the playhead, Z adds a zoom there, X a cut and R a speed change, Delete removes the
/// selected zoom, cut or speed change, or the current scene while a scene on the lane has the
/// focus, and Ctrl+Z, Ctrl+Y or Ctrl+Shift+Z, and Ctrl+E undo, redo and export. Esc stops a running
/// export, and otherwise asks the window to close; held down, it does neither. While something
/// is being dragged, only Space and the arrow keys act.
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
        if (input.IsTextInputFocused || !input.IsReady || input.IsExporting)
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
    /// answered would ask it again. While an export runs it stops the export, and that is all.
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

        return input.IsTextInputFocused || input.IsDropDownOpen || input.IsDragging
            ? StudioShortcutAction.None
            : StudioShortcutAction.RequestClose;
    }

    private static bool MovesOnlyThePlayhead(StudioShortcutAction action) => action
        is StudioShortcutAction.TogglePlayback
        or StudioShortcutAction.PreviousFrame
        or StudioShortcutAction.NextFrame;

    private static StudioShortcutAction ResolveControlKey(StudioShortcutInput input) => input.Key switch
    {
        StudioShortcutKey.Z => input.IsShiftDown ? StudioShortcutAction.Redo : StudioShortcutAction.Undo,
        StudioShortcutKey.Y when !input.IsShiftDown => StudioShortcutAction.Redo,
        StudioShortcutKey.E when !input.IsShiftDown && !input.IsRepeat => StudioShortcutAction.Export,
        _ => StudioShortcutAction.None,
    };

    private static StudioShortcutAction ResolvePlainKey(StudioShortcutInput input)
    {
        if (input.IsShiftDown)
        {
            return StudioShortcutAction.None;
        }

        // Holding an arrow key keeps stepping through frames. The other keys act once per press.
        switch (input.Key)
        {
            case StudioShortcutKey.Left:
                return StudioShortcutAction.PreviousFrame;
            case StudioShortcutKey.Right:
                return StudioShortcutAction.NextFrame;
        }

        if (input.IsRepeat)
        {
            return StudioShortcutAction.None;
        }

        return input.Key switch
        {
            StudioShortcutKey.Space => StudioShortcutAction.TogglePlayback,
            StudioShortcutKey.I => StudioShortcutAction.SetTrimStartAtPlayhead,
            StudioShortcutKey.O => StudioShortcutAction.SetTrimEndAtPlayhead,
            StudioShortcutKey.R => StudioShortcutAction.AddSpeed,
            StudioShortcutKey.S => StudioShortcutAction.SplitScene,
            StudioShortcutKey.X => StudioShortcutAction.AddCut,
            StudioShortcutKey.Z => StudioShortcutAction.AddZoom,
            StudioShortcutKey.Delete when input.IsSceneFocused => StudioShortcutAction.RemoveCurrentScene,
            StudioShortcutKey.Delete when input.HasSelectedCut => StudioShortcutAction.RemoveSelectedCut,
            StudioShortcutKey.Delete when input.HasSelectedSpeed => StudioShortcutAction.RemoveSelectedSpeed,
            StudioShortcutKey.Delete when input.HasSelectedZoom => StudioShortcutAction.RemoveSelectedZoom,
            StudioShortcutKey.Digit1 => StudioShortcutAction.ShowScreenLayout,
            StudioShortcutKey.Digit2 => StudioShortcutAction.ShowBubbleLayout,
            StudioShortcutKey.Digit3 => StudioShortcutAction.ShowSideBySideLayout,
            StudioShortcutKey.Digit4 => StudioShortcutAction.ShowCameraLayout,
            _ => StudioShortcutAction.None,
        };
    }
}
