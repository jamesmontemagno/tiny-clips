namespace TinyClips.Core.Studio.Editing;

// Scenes. The current scene is the one the playhead is in, so there is nothing to select: the
// layout controls, the bubble in the preview and the keys 1 to 4 change that scene. A change of
// current scene is reported with StudioEditorChanges.Scene, whatever caused it: a seek, playback
// reaching the next scene, an edit, or undo.
public sealed partial class StudioEditorSession
{
    private int _raisedSceneIndex;

    /// <summary>The scene the playhead is in. 0 until the project has been read.</summary>
    public int CurrentSceneIndex => Model?.CurrentSceneIndex ?? 0;

    /// <summary>How many scenes the project has. 0 until the project has been read.</summary>
    public int SceneCount => Model?.Project.Scenes.Length ?? 0;

    /// <summary>Whether Split would start a new scene at the playhead.</summary>
    public bool CanSplitSceneAtPlayhead => IsEditable && Model is { } model && model.CanSplitScene(Playhead);

    /// <summary>Why a scene cannot be split at the playhead, in words, or null when it can be.</summary>
    public string? SplitSceneExplanation =>
        Model is { } model ? StudioEditorText.GetSplitSceneExplanation(model.GetSplitSceneObstacle(Playhead)) : null;

    /// <summary>Whether the current scene can be deleted: not the only one.</summary>
    public bool CanRemoveCurrentScene => IsEditable && Model is { } model && model.CanRemoveScene(model.CurrentSceneIndex);

    /// <summary>
    /// Starts a new scene at the playhead, a copy of the current one that is entered by moving.
    /// At its first instant a scene still looks like the one before, so a paused playhead then
    /// moves to where the new scene has been entered, which shows what is changed in it next.
    /// </summary>
    public StudioSceneEditResult SplitSceneAtPlayhead()
    {
        var time = Playhead;
        var result = EditScene(model => model.SplitScene(time));
        if (result.Changed && !IsPlaying && Model?.GetSceneLookTime(result.Index) is { } look)
        {
            Scrub(look);
        }

        return result;
    }

    /// <summary>Deletes a scene. The scene before it then lasts until the next one.</summary>
    public StudioSceneEditResult RemoveScene(int index) => EditScene(model => model.RemoveScene(index));

    /// <summary>Deletes the scene the playhead is in.</summary>
    public StudioSceneEditResult RemoveCurrentScene() => EditScene(model => model.RemoveScene(model.CurrentSceneIndex));

    /// <summary>
    /// Moves where a scene starts, from the lane or a step button, and shows the picture there:
    /// the playhead follows the start as it does a trim handle.
    /// </summary>
    public StudioSceneEditResult SetSceneStart(int index, double sourceTime)
    {
        var result = EditScene(model => model.SetSceneStart(index, sourceTime));
        if (index >= 1 && IsEditable && Model?.GetSceneRange(index) is { } range)
        {
            Scrub(range.Start);
        }

        return result;
    }

    /// <summary>Makes a scene start at the playhead. The playhead stays where it is.</summary>
    public StudioSceneEditResult SetSceneStartAtPlayhead(int index)
    {
        var time = Playhead;
        return EditScene(model => model.SetSceneStart(index, time));
    }

    /// <summary>Sets whether a scene is cut to or entered by moving. Not the first scene.</summary>
    public StudioSceneEditResult SetSceneTransitionKind(int index, StudioTransitionKind kind) =>
        EditScene(model => model.SetSceneTransitionKind(index, kind));

    /// <summary>Sets how long the move into a scene takes. Not the first scene.</summary>
    public StudioSceneEditResult SetSceneTransitionDuration(int index, double seconds) =>
        EditScene(model => model.SetSceneTransitionDuration(index, seconds));

    /// <summary>
    /// Moves the playhead to where the scene after the current one has been entered. False when
    /// the playhead is in the last scene.
    /// </summary>
    public bool ShowNextScene() => ShowScene(CurrentSceneIndex + 1);

    /// <summary>
    /// Moves the playhead to where the scene before the current one has been entered. False when
    /// the playhead is in the first scene.
    /// </summary>
    public bool ShowPreviousScene() => ShowScene(CurrentSceneIndex - 1);

    /// <summary>Moves the playhead to where a scene has been entered. False when there is no such scene.</summary>
    public bool ShowScene(int index)
    {
        if (!IsEditable || Model?.GetSceneLookTime(index) is not { } time)
        {
            return false;
        }

        Scrub(time);
        return true;
    }

    // An edit to the scenes. Refused, because the project cannot be edited just now, it says
    // nothing changed and names the scene the playhead is in.
    private StudioSceneEditResult EditScene(Func<StudioEditorModel, StudioSceneEditResult> change) =>
        Edit(change, new StudioSceneEditResult(false, CurrentSceneIndex));
}