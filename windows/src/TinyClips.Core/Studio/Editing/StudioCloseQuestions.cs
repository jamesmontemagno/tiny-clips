using TinyClips.Core.Editing;

namespace TinyClips.Core.Studio.Editing;

/// <summary>How the user asked for the Studio window to close.</summary>
public enum StudioCloseRequest
{
    /// <summary>The close button, Alt+F4, or a close from the system.</summary>
    CloseButton,

    /// <summary>Esc, once the keys have made it a request to close (<see cref="StudioShortcutAction.RequestClose"/>).</summary>
    Escape,
}

/// <summary>What the user is asked before the Studio window closes.</summary>
public enum StudioCloseQuestion
{
    /// <summary>Nothing. The window closes.</summary>
    None,

    /// <summary>An export is running: keep exporting, or stop and close.</summary>
    RunningExport,

    /// <summary>The project is being saved as a folder: keep saving, or stop and close.</summary>
    RunningProjectSave,

    /// <summary>The project has no saved project folder: keep it as a draft, or delete it.</summary>
    Draft,

    /// <summary>The saved project has changed since its last save: save, don't save, or cancel.</summary>
    UnsavedChanges,

    /// <summary>Whether Esc was meant. Only Esc asks this, and only of a project that is open, where nothing else is asked.</summary>
    Escape,
}

/// <summary>
/// What the Studio window asks before it closes. The close button and Esc both come here, and
/// to nothing else, so that Esc can never close what the close button would have asked about.
/// The window shows the question. It is the Mac's rule.
/// </summary>
public static class StudioCloseQuestions
{
    /// <summary>
    /// Each request takes the first of these that applies. What closing asks of itself is asked,
    /// however the close was asked for and whatever the setting says: while an export runs, the
    /// question about the export, while the project is being saved as a folder, the question
    /// about that save, of a project without a saved folder, whether to keep it as a draft or
    /// delete it, and of a saved project that has changed, whether to save the changes. That
    /// question is the confirmation, and Esc adds none to it. Where closing asks nothing, the
    /// close button asks nothing. Esc asks whether it was meant, as it does in the other editors
    /// and behind the same setting, of a project that is open, which is then a saved project
    /// without changes. A window whose project is not open, because it is still being opened or
    /// cannot be shown, closes on Esc as it does by its close button, whatever the setting says.
    /// </summary>
    /// <param name="request">How the close was asked for.</param>
    /// <param name="prompt">What closing asks of itself (<see cref="StudioEditorSession.GetClosePrompt"/>).</param>
    /// <param name="isProjectOpen">Whether the project is open (<see cref="StudioEditorSession.IsReady"/>).</param>
    /// <param name="confirmEscape">The setting that guards the editors against a stray Esc.</param>
    public static StudioCloseQuestion Resolve(
        StudioCloseRequest request,
        StudioClosePrompt prompt,
        bool isProjectOpen,
        bool confirmEscape)
    {
        switch (prompt)
        {
            case StudioClosePrompt.ExportRunning:
                return StudioCloseQuestion.RunningExport;
            case StudioClosePrompt.ProjectSaveRunning:
                return StudioCloseQuestion.RunningProjectSave;
            case StudioClosePrompt.Draft:
                return StudioCloseQuestion.Draft;
            case StudioClosePrompt.UnsavedChanges:
                return StudioCloseQuestion.UnsavedChanges;
        }

        // A saved project without changes has nothing to ask about.
        return request == StudioCloseRequest.Escape
            && isProjectOpen
            && EditorEscape.ResolvePrompt(confirmEscape, hasUnsavedChanges: false) is not null
                ? StudioCloseQuestion.Escape
                : StudioCloseQuestion.None;
    }
}
