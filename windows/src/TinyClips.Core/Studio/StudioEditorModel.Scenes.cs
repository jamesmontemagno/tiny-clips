namespace TinyClips.Core.Studio;

/// <summary>What an edit to the scenes did.</summary>
/// <param name="Changed">Whether the project changed. False when there was nothing to do or the edit is not possible.</param>
/// <param name="Index">
/// The scene the edit is about, as the scenes are now: the new scene after a split, and after a
/// delete the scene that has taken over the deleted scene's time.
/// </param>
public readonly record struct StudioSceneEditResult(bool Changed, int Index);

/// <summary>Why a scene cannot be split at a time.</summary>
public enum StudioSceneSplitObstacle
{
    /// <summary>Nothing: the scene can be split there.</summary>
    None,

    /// <summary>The recording has no camera, so every layout is the screen alone.</summary>
    NoCamera,

    /// <summary>One of the two scenes would be shorter than <see cref="StudioEditorModel.MinimumSceneDuration"/>.</summary>
    TooShort,

    /// <summary>The scene's layers are still moving into place there.</summary>
    StillMoving,
}

// Scenes. The stored list is kept in time order with the first scene at 0, so a scene's index
// identifies it between two edits and the scene a time is in is the last one that has started.
public sealed partial class StudioEditorModel
{
    /// <summary>The shortest scene the editor makes, in seconds.</summary>
    public const double MinimumSceneDuration = 0.3;

    /// <summary>
    /// The shortest move into a scene that can be set, in seconds. Section 6.9 allows none at all,
    /// which is what a cut is for.
    /// </summary>
    public const double MinimumSceneTransitionDuration = 0.1;

    /// <summary>The longest move into a scene, in seconds.</summary>
    public const double MaximumSceneTransitionDuration = 2;

    /// <summary>
    /// Where the playhead is, in source time, as the owner last said. It picks the current scene.
    /// It is not part of what undo restores.
    /// </summary>
    public double SceneTime { get; set; }

    /// <summary>The scene the playhead is in.</summary>
    public int CurrentSceneIndex => GetSceneIndexAt(SceneTime);

    /// <summary>The scene a time is in (section 6.1): the last one that has started.</summary>
    public int GetSceneIndexAt(double sourceTime)
    {
        var scenes = Project.Scenes;
        var index = 0;
        for (var i = 0; i < scenes.Length; i++)
        {
            if (scenes[i].Start <= sourceTime)
            {
                index = i;
            }
        }

        return index;
    }

    /// <summary>
    /// When a scene starts and ends. It ends where the next one starts, or with the recording.
    /// Null when there is no such scene.
    /// </summary>
    public (double Start, double End)? GetSceneRange(int index)
    {
        var scenes = Project.Scenes;
        if (index < 0 || index >= scenes.Length)
        {
            return null;
        }

        var end = index + 1 < scenes.Length ? scenes[index + 1].Start : SourceDuration;
        return (scenes[index].Start, Math.Max(scenes[index].Start, end));
    }

    /// <summary>
    /// How long the layers take to move into a scene, as the layout applies it (section 6.9): 0
    /// for a cut and for the first scene, and never longer than the scene.
    /// </summary>
    public double GetSceneTransitionLength(int index) => StudioLayoutPlan.TransitionLength(Project.Scenes, index);

    /// <summary>
    /// A time at which a scene has been entered, to show it at: the end of its move, kept a frame
    /// inside the scene, which contains its start and not its end.
    /// </summary>
    public double? GetSceneLookTime(int index)
    {
        if (GetSceneRange(index) is not { } range)
        {
            return null;
        }

        var time = Math.Max(range.Start, Math.Min(range.Start + GetSceneTransitionLength(index), range.End - FrameDuration));
        return ClampSourceTime(time);
    }

    /// <summary>What stands in the way of splitting the scene a time is in.</summary>
    public StudioSceneSplitObstacle GetSplitSceneObstacle(double sourceTime)
    {
        if (!HasCamera)
        {
            return StudioSceneSplitObstacle.NoCamera;
        }

        if (!double.IsFinite(sourceTime))
        {
            return StudioSceneSplitObstacle.TooShort;
        }

        var time = ClampSourceTime(sourceTime);
        var index = GetSceneIndexAt(time);
        if (GetSceneRange(index) is not { } range
            || !(time - range.Start >= MinimumSceneDuration)
            || !(range.End - time >= MinimumSceneDuration))
        {
            return StudioSceneSplitObstacle.TooShort;
        }

        return time >= range.Start + GetSceneTransitionLength(index) ? StudioSceneSplitObstacle.None : StudioSceneSplitObstacle.StillMoving;
    }

    public bool CanSplitScene(double sourceTime) => GetSplitSceneObstacle(sourceTime) == StudioSceneSplitObstacle.None;

    /// <summary>
    /// Starts a new scene at a time: a copy of the scene that time is in, entered by moving. Both
    /// halves have to last <see cref="MinimumSceneDuration"/>, and the scene has to be at rest there.
    /// </summary>
    public StudioSceneEditResult SplitScene(double sourceTime)
    {
        var time = ClampSourceTime(sourceTime);
        var index = GetSceneIndexAt(time);
        if (!CanSplitScene(sourceTime))
        {
            return new StudioSceneEditResult(false, index);
        }

        var scenes = Project.Scenes.ToList();
        scenes.Insert(index + 1, scenes[index] with
        {
            Start = time,
            Transition = new StudioTransition { Kind = StudioTransitionKind.Morph },
        });
        Mutate(project => project with { Scenes = [.. scenes] });
        return new StudioSceneEditResult(true, index + 1);
    }

    /// <summary>Whether a scene can be deleted: any scene but the only one.</summary>
    public bool CanRemoveScene(int index) => Project.Scenes.Length > 1 && index >= 0 && index < Project.Scenes.Length;

    /// <summary>
    /// Deletes a scene. The scene before it then lasts until the next one; deleting the first
    /// hands its time to the second, which then starts at 0.
    /// </summary>
    public StudioSceneEditResult RemoveScene(int index)
    {
        if (!CanRemoveScene(index))
        {
            return Unchanged(index);
        }

        var scenes = Project.Scenes.ToList();
        scenes.RemoveAt(index);
        if (scenes[0].Start != 0)
        {
            scenes[0] = scenes[0] with { Start = 0 };
        }

        Mutate(project => project with { Scenes = [.. scenes] });
        return new StudioSceneEditResult(true, Math.Max(0, index - 1));
    }

    /// <summary>
    /// Moves where a scene starts. It stays <see cref="MinimumSceneDuration"/> after the start of
    /// the scene before it and as long before its own end. The first scene always starts at 0.
    /// </summary>
    public StudioSceneEditResult SetSceneStart(int index, double sourceTime)
    {
        if (index < 1 || !double.IsFinite(sourceTime) || GetSceneRange(index) is not { } range)
        {
            return Unchanged(index);
        }

        var earliest = Project.Scenes[index - 1].Start + MinimumSceneDuration;
        var latest = range.End - MinimumSceneDuration;
        if (!(earliest <= latest))
        {
            return Unchanged(index);
        }

        var start = Math.Min(Math.Max(sourceTime, earliest), latest);
        if (start == Project.Scenes[index].Start)
        {
            return Unchanged(index);
        }

        Mutate(project => WithScene(project, index, scene => scene with { Start = start }));
        return new StudioSceneEditResult(true, index);
    }

    /// <summary>
    /// Sets whether a scene is cut to or entered by moving. The first scene is entered at once
    /// whatever it says, so it is left alone.
    /// </summary>
    public StudioSceneEditResult SetSceneTransitionKind(int index, StudioTransitionKind kind) =>
        EditSceneTransition(index, transition => transition with { Kind = kind });

    /// <summary>
    /// Sets how long the move into a scene takes, between <see cref="MinimumSceneTransitionDuration"/>
    /// and <see cref="MaximumSceneTransitionDuration"/>.
    /// </summary>
    public StudioSceneEditResult SetSceneTransitionDuration(int index, double seconds) =>
        double.IsFinite(seconds)
            ? EditSceneTransition(index, transition => transition with
            {
                Duration = Clamp(seconds, MinimumSceneTransitionDuration, MaximumSceneTransitionDuration),
            })
            : Unchanged(index);

    // Changes how a scene is entered. Not the first scene, which is entered at once.
    private StudioSceneEditResult EditSceneTransition(int index, Func<StudioTransition, StudioTransition> change)
    {
        if (index < 1 || index >= Project.Scenes.Length)
        {
            return Unchanged(index);
        }

        var current = Project.Scenes[index].Transition;
        var edited = change(current);
        if (edited.Kind == current.Kind && edited.Duration == current.Duration)
        {
            return new StudioSceneEditResult(false, index);
        }

        Mutate(project => WithScene(project, index, scene => scene with { Transition = edited }));
        return new StudioSceneEditResult(true, index);
    }

    private StudioSceneEditResult Unchanged(int index) =>
        new(false, Math.Min(Math.Max(0, index), Project.Scenes.Length - 1));

    // Puts the stored scenes in the order section 6.1 of the format reads them in, and drops those
    // that start at or after the end of the recording, which are never shown.
    private static StudioScene[] NormalizeStoredScenes(StudioScene[] scenes, double duration) =>
        [.. StudioLayoutResolver.NormalizeScenes(scenes).Where((scene, index) => index == 0 || scene.Start < duration)];
}
