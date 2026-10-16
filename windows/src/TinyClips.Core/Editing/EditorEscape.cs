namespace TinyClips.Core.Editing;

/// <summary>What Esc does in the screenshot editor. Each press takes only the first step that applies.</summary>
public enum ScreenshotEditorEscapeAction
{
    /// <summary>Esc in a text input belongs to that input; it never closes the editor.</summary>
    LeaveToTextInput,
    ClearCropSelection,
    Close,
}

/// <summary>What an editor or trimmer has to confirm before Esc closes it.</summary>
public enum EditorEscapePrompt
{
    DiscardChanges,
    CloseWithoutChanges,
}

public enum EditorEscapeSurface
{
    ScreenshotEditor,
    VideoTrimmer,
    GifTrimmer,

    /// <summary>
    /// The Studio editor. It saves every edit as it is made, so the only thing it ever asks here
    /// is <see cref="EditorEscapePrompt.CloseWithoutChanges"/>. What else Esc does in its window
    /// is decided in <c>StudioShortcuts</c>.
    /// </summary>
    Studio,
}

/// <summary>
/// Esc-to-close decisions shared by the screenshot editor and the trimmers. There is no
/// "discard the capture" prompt: a capture is written to its save folder before any of these
/// windows open, so closing only ever loses edits.
/// </summary>
public static class EditorEscape
{
    public static ScreenshotEditorEscapeAction ResolveEditorAction(bool textInputHasFocus, bool hasCropSelection)
    {
        if (textInputHasFocus)
        {
            return ScreenshotEditorEscapeAction.LeaveToTextInput;
        }

        return hasCropSelection
            ? ScreenshotEditorEscapeAction.ClearCropSelection
            : ScreenshotEditorEscapeAction.Close;
    }

    /// <summary>Returns the prompt to show, or null when Esc closes straight away.</summary>
    public static EditorEscapePrompt? ResolvePrompt(bool confirmOnEscape, bool hasUnsavedChanges)
    {
        if (!confirmOnEscape)
        {
            return null;
        }

        return hasUnsavedChanges ? EditorEscapePrompt.DiscardChanges : EditorEscapePrompt.CloseWithoutChanges;
    }

    public static bool IsDestructive(EditorEscapePrompt prompt) => prompt == EditorEscapePrompt.DiscardChanges;

    public static string Title(EditorEscapePrompt prompt, EditorEscapeSurface surface) => prompt switch
    {
        EditorEscapePrompt.DiscardChanges => "Discard changes?",
        _ when surface == EditorEscapeSurface.Studio => "Close Studio?",
        _ => surface == EditorEscapeSurface.ScreenshotEditor ? "Close the editor?" : "Close the trimmer?",
    };

    public static string Message(EditorEscapePrompt prompt, EditorEscapeSurface surface)
    {
        if (surface == EditorEscapeSurface.Studio)
        {
            return "Your edits are saved with the project, and you can reopen it from the Clips Library. You can turn off this confirmation in General settings.";
        }

        if (surface == EditorEscapeSurface.ScreenshotEditor)
        {
            return prompt == EditorEscapePrompt.DiscardChanges
                ? "You have unsaved annotations. Close anyway?"
                : "This screenshot has no unsaved changes. You can turn off this confirmation in General settings.";
        }

        var media = surface == EditorEscapeSurface.GifTrimmer ? "GIF" : "recording";
        return prompt == EditorEscapePrompt.DiscardChanges
            ? $"Your changes in the trimmer have not been saved. Closing discards them and keeps the original {media}."
            : $"This {media} is saved and has no unsaved changes. You can turn off this confirmation in General settings.";
    }

    public static string ConfirmButtonText(EditorEscapePrompt prompt, EditorEscapeSurface surface)
    {
        if (surface == EditorEscapeSurface.Studio)
        {
            return "Close Studio";
        }

        if (surface == EditorEscapeSurface.ScreenshotEditor)
        {
            return prompt == EditorEscapePrompt.DiscardChanges ? "Discard" : "Close editor";
        }

        return prompt == EditorEscapePrompt.DiscardChanges ? "Discard changes" : "Close trimmer";
    }
}
