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
    Undo,
    Redo,
    Export,
    CancelExport,
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
/// <param name="IsReady">Whether the project is open (<see cref="StudioEditorSession.IsReady"/>).</param>
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

    /// <summary>Whether a zoom is selected. Delete removes it, and without one is left alone.</summary>
    public bool HasSelectedZoom { get; init; }

    /// <summary>Whether a cut is selected. Delete removes it, which puts its stretch back.</summary>
    public bool HasSelectedCut { get; init; }

    /// <summary>
    /// True when the focused control is a scene on the scene lane. Delete then removes the scene
    /// the playhead is in, and not the selected zoom or cut.
    /// </summary>
    public bool IsSceneFocused { get; init; }
}

/// <summary>
/// The Studio editor's keyboard model, the same as on the Mac: Space plays or pauses, Left and
/// Right step a frame, I and O set the trim at the playhead, 1 to 4 choose the layout, S splits the
/// scene at the playhead, Z adds a zoom there and X a cut, Delete removes the selected zoom or
/// cut, or the current scene while a scene on the lane has the focus, and Ctrl+Z, Ctrl+Y or
/// Ctrl+Shift+Z, and Ctrl+E undo, redo and export. Esc stops a running export.
/// </summary>
/// <remarks>
/// The window only asks about a key that the focused control did not use, so a focused slider
/// keeps its arrow keys and a focused button keeps Space.
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
            return input is { IsExporting: true, IsControlDown: false, IsShiftDown: false }
                ? StudioShortcutAction.CancelExport
                : StudioShortcutAction.None;
        }

        // Text being edited keeps every key, including its own Undo and Redo.
        if (input.IsTextInputFocused || !input.IsReady || input.IsExporting)
        {
            return StudioShortcutAction.None;
        }

        if (input.IsControlDown)
        {
            return ResolveControlKey(input);
        }

        return input.IsTypeToSearchFocused ? StudioShortcutAction.None : ResolvePlainKey(input);
    }

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
            StudioShortcutKey.S => StudioShortcutAction.SplitScene,
            StudioShortcutKey.X => StudioShortcutAction.AddCut,
            StudioShortcutKey.Z => StudioShortcutAction.AddZoom,
            StudioShortcutKey.Delete when input.IsSceneFocused => StudioShortcutAction.RemoveCurrentScene,
            StudioShortcutKey.Delete when input.HasSelectedCut => StudioShortcutAction.RemoveSelectedCut,
            StudioShortcutKey.Delete when input.HasSelectedZoom => StudioShortcutAction.RemoveSelectedZoom,
            StudioShortcutKey.Digit1 => StudioShortcutAction.ShowScreenLayout,
            StudioShortcutKey.Digit2 => StudioShortcutAction.ShowBubbleLayout,
            StudioShortcutKey.Digit3 => StudioShortcutAction.ShowSideBySideLayout,
            StudioShortcutKey.Digit4 => StudioShortcutAction.ShowCameraLayout,
            _ => StudioShortcutAction.None,
        };
    }
}
