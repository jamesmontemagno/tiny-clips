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

    /// <summary>The recording was never exported: export it, keep it as a draft, or delete it.</summary>
    Draft,

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
    /// about that save, and of a recording that was never exported, what to do with
    /// it. That question is the confirmation, and Esc adds none to it. Where closing asks
    /// nothing, the close button asks nothing. Esc asks whether it was meant, as it does in the
    /// other editors and behind the same setting, of a project that is open, which is then one
    /// that has been exported. A window whose project is not open, because it is still being
    /// opened or cannot be shown, closes on Esc as it does by its close button, whatever the
    /// setting says: the question says that the edits are saved, which is no sentence for a
    /// window that opened nothing.
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
            case StudioClosePrompt.NeverExported:
                return StudioCloseQuestion.Draft;
        }

        // Studio saves every edit as it is made, so there is never anything unsaved to ask about.
        return request == StudioCloseRequest.Escape
            && isProjectOpen
            && EditorEscape.ResolvePrompt(confirmEscape, hasUnsavedChanges: false) is not null
                ? StudioCloseQuestion.Escape
                : StudioCloseQuestion.None;
    }
}
