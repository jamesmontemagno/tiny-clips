namespace TinyClips.Core.Studio.Editing;

/// <summary>Where a <see cref="StudioEditorSession"/> is in opening its project.</summary>
public enum StudioEditorLoadState
{
    /// <summary>The project is being opened.</summary>
    Loading,

    /// <summary>The project is open and can be edited, played and exported.</summary>
    Ready,

    /// <summary>The project cannot be shown. <see cref="StudioEditorSession.UnavailableMessage"/> says why.</summary>
    Unavailable,
}

/// <summary>What a <see cref="StudioEditorSession"/> needs asked before its window closes.</summary>
public enum StudioClosePrompt
{
    /// <summary>Nothing. Close without asking.</summary>
    None,

    /// <summary>An export is running: keep exporting, or stop and close.</summary>
    ExportRunning,

    /// <summary>The project was never exported: export it, keep it as a draft, or delete it.</summary>
    NeverExported,
}

/// <summary>How <see cref="StudioEditorSession.ExportAsync"/> ended.</summary>
public enum StudioExportOutcome
{
    /// <summary>
    /// Nothing was rendered: there was nothing to export, an export was already running, or the
    /// edits could not be saved first.
    /// </summary>
    NotStarted,

    /// <summary>The video was written and its link is recorded in the project.</summary>
    Exported,

    /// <summary>The export was stopped. Nothing was written and nothing is reported.</summary>
    Cancelled,

    /// <summary>The export failed. The error has been reported.</summary>
    Failed,
}

/// <summary>The parts of a <see cref="StudioEditorSession"/> that can change.</summary>
[Flags]
public enum StudioEditorChanges
{
    None = 0,

    /// <summary>Loading, ready or unavailable.</summary>
    State = 1,

    /// <summary>The project and what follows from it: undo, redo, the export size, the trim.</summary>
    Project = 2,

    /// <summary>The playhead, and whether the preview is playing.</summary>
    Playback = 4,

    /// <summary>Whether an export is running, and how far it is.</summary>
    Export = 8,

    /// <summary>Which zoom is selected.</summary>
    Selection = 16,

    /// <summary>Which scene the playhead is in. The layout controls show and change that scene.</summary>
    Scene = 32,

    All = State | Project | Playback | Export | Selection | Scene,
}

public sealed class StudioEditorChangedEventArgs(StudioEditorChanges changes) : EventArgs
{
    public StudioEditorChanges Changes { get; } = changes;

    public bool Includes(StudioEditorChanges change) => (Changes & change) != 0;
}

public sealed class StudioExportedEventArgs(string projectId, string path) : EventArgs
{
    public string ProjectId { get; } = projectId;

    /// <summary>The full path of the video that was written.</summary>
    public string Path { get; } = path;
}

/// <summary>What a <see cref="StudioEditorSession"/> was doing when something went wrong.</summary>
public enum StudioEditorErrorKind
{
    /// <summary>Saving the edits. The edits are still unsaved, and the next save tries again.</summary>
    Save,

    /// <summary>Rendering the video.</summary>
    Export,

    /// <summary>Deleting the project.</summary>
    Delete,
}

public sealed class StudioEditorErrorEventArgs(StudioEditorErrorKind kind, string message) : EventArgs
{
    public StudioEditorErrorKind Kind { get; } = kind;

    /// <summary>A sentence that can be shown to the user.</summary>
    public string Message { get; } = message;
}
