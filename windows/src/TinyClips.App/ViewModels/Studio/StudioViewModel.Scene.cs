using System.Globalization;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.App.ViewModels.Studio;

// The values behind the scene lane and the Scene section of the inspector. A scene is not
// selected: the current scene is the one the playhead is in, and the editor says when that is
// another one. What an edit does is decided in the session, and what is said about it in
// StudioEditorText.
public sealed partial class StudioViewModel
{
    private const string SceneActivityId = "StudioScene";
    private const double SceneTimeStep = 0.1;
    private const string SplitSceneHint = "Start a new scene at the playhead (S)";

    // What changes when the playhead comes into another scene and the project stays as it is: the
    // Scene section, and the layout and camera controls, which show and change the current scene.
    private static readonly string[] ScenePropertyNames =
    [
        nameof(CurrentSceneIndex),
        nameof(ScenePositionText),
        nameof(SceneRangeText),
        nameof(CanShowPreviousScene),
        nameof(CanShowNextScene),
        nameof(IsFirstSceneExplanationVisible),
        nameof(AreSceneEntryControlsVisible),
        nameof(SceneStartText),
        nameof(SceneStartAccessibleName),
        nameof(SceneEntryIndex),
        nameof(IsSceneMoveDurationVisible),
        nameof(SceneMoveDuration),
        nameof(SceneMoveDurationText),
        nameof(SceneMoveLimitedText),
        nameof(HasSceneMoveLimitedText),
        nameof(LayoutIndex),
        nameof(IsCameraHiddenNoteVisible),
        nameof(AreBubbleControlsVisible),
        nameof(IsCameraCornerRadiusVisible),
        nameof(AreSideBySideControlsVisible),
        nameof(AreCameraStyleControlsVisible),
        nameof(CameraBubbleSize),
        nameof(CameraBubbleSizeText),
        nameof(CameraAnchorIndex),
        nameof(CameraOffsetX),
        nameof(CameraOffsetXText),
        nameof(CameraOffsetY),
        nameof(CameraOffsetYText),
        nameof(CameraSideIndex),
        nameof(CameraShare),
        nameof(CameraShareText),
        nameof(PreviewDescription),
        nameof(BubbleRect),
    ];

    private static readonly StudioScene[] NoScenes = [];

    // What was last reported of the values that follow the playhead inside a scene, so that a
    // playhead that moves thirty times a second only refreshes them when one has changed.
    private bool _couldSplitScene;
    private string _splitSceneExplanation = string.Empty;
    private bool _wasSplitSceneExplanationVisible;

    // The lane

    /// <summary>
    /// The project's scenes, in time order. It is the same list for as long as no scene changes,
    /// so whoever draws them can tell by reference that there is nothing to draw again.
    /// </summary>
    public IReadOnlyList<StudioScene> Scenes => Project?.Scenes ?? NoScenes;

    /// <summary>The place in <see cref="Scenes"/> of the scene the playhead is in.</summary>
    public int CurrentSceneIndex => _session.CurrentSceneIndex;

    /// <summary>When a scene starts and ends, in source time, or null when there is no such scene.</summary>
    public (double Start, double End)? GetSceneRange(int index) => _session.Model?.GetSceneRange(index);

    /// <summary>The name of a scene's layout, such as "Side by side".</summary>
    public string GetSceneLayoutName(int index) => index >= 0 && index < Scenes.Count
        ? StudioEditorModel.GetLayoutName(HasCamera ? Scenes[index].Layout : StudioLayout.Screen)
        : string.Empty;

    /// <summary>A scene as a screen reader says it, such as "Scene 2 of 3, Side by side, 12.0 to 30.5 seconds".</summary>
    public string GetSceneDescription(int index) =>
        _session.Model is { } model ? StudioEditorText.GetSceneDescription(model, index) : string.Empty;

    // Stepping through the scenes

    public bool CanShowPreviousScene => IsEditable && CurrentSceneIndex > 0;

    public bool CanShowNextScene => IsEditable && CurrentSceneIndex + 1 < Scenes.Count;

    /// <summary>Which scene the playhead is in, such as "Scene 2 of 3".</summary>
    public string ScenePositionText =>
        Scenes.Count > 0 ? StudioEditorText.GetScenePositionText(CurrentSceneIndex, Scenes.Count) : string.Empty;

    /// <summary>When the current scene starts and ends.</summary>
    public string SceneRangeText =>
        _session.Model is { } model ? StudioEditorText.GetSceneRangeText(model, CurrentSceneIndex) : string.Empty;

    // Splitting

    public bool CanSplitSceneAtPlayhead => _session.CanSplitSceneAtPlayhead;

    /// <summary>Why the scene cannot be split at the playhead. Empty where it can be.</summary>
    public string SplitSceneExplanation => IsReady ? _session.SplitSceneExplanation ?? string.Empty : string.Empty;

    /// <summary>
    /// The reason is shown while the preview is paused. While it plays, the playhead comes past a
    /// place where no scene can be split at every start of a scene, and a sentence that came and
    /// went there would push the rest of the inspector down and up again.
    /// </summary>
    public bool IsSplitSceneExplanationVisible => !IsPlaying && SplitSceneExplanation.Length > 0;

    /// <summary>What Split does, or why it cannot.</summary>
    public string SplitSceneHelpText => SplitSceneExplanation is { Length: > 0 } explanation ? explanation : SplitSceneHint;

    public bool IsOneSceneExplanationVisible => IsReady && Scenes.Count == 1;

    public string OneSceneExplanation => StudioEditorText.OneSceneExplanation;

    public bool IsFirstSceneExplanationVisible => Scenes.Count > 1 && CurrentSceneIndex == 0;

    public string FirstSceneExplanation => StudioEditorText.FirstSceneExplanation;

    // The current scene

    /// <summary>
    /// True for every scene but the first, which starts with the recording and has nothing to
    /// move from: the others have a start and a way they are entered.
    /// </summary>
    public bool AreSceneEntryControlsVisible => CurrentSceneIndex >= 1;

    public string SceneStartText => StudioEditorModel.GetSecondsText(Scene.Start);

    public string SceneStartAccessibleName => $"Scene start {SceneStartText}";

    /// <summary>0 for a scene that is cut to, 1 for one that is entered by moving.</summary>
    public int SceneEntryIndex
    {
        get => Scene.Transition.Kind == StudioTransitionKind.Morph ? 1 : 0;
        set
        {
            var index = CurrentSceneIndex;
            if (value is 0 or 1 && index >= 1 && value != SceneEntryIndex)
            {
                _session.SetSceneTransitionKind(index, value == 1 ? StudioTransitionKind.Morph : StudioTransitionKind.Cut);
            }

            ResyncIfDifferent(value, SceneEntryIndex);
        }
    }

    public bool IsSceneMoveDurationVisible =>
        AreSceneEntryControlsVisible && Scene.Transition.Kind == StudioTransitionKind.Morph;

    /// <summary>How long the screen and the camera take to move into the current scene, in seconds.</summary>
    public double SceneMoveDuration
    {
        get => Math.Clamp(
            Scene.Transition.Duration,
            StudioEditorModel.MinimumSceneTransitionDuration,
            StudioEditorModel.MaximumSceneTransitionDuration);
        set
        {
            var index = CurrentSceneIndex;
            if (index >= 1)
            {
                _session.SetSceneTransitionDuration(index, value);
            }
        }
    }

    /// <summary>The move in hundredths of a second, which is how finely it is set: "0.35 seconds".</summary>
    public string SceneMoveDurationText =>
        string.Create(CultureInfo.InvariantCulture, $"{SceneMoveDuration:0.00} seconds");

    /// <summary>How long the move really is, in a scene that is shorter than the time asked for. Empty otherwise.</summary>
    public string SceneMoveLimitedText => _session.Model is { } model
        ? StudioEditorText.GetSceneMoveLimitedText(model, CurrentSceneIndex) ?? string.Empty
        : string.Empty;

    public bool HasSceneMoveLimitedText => IsSceneMoveDurationVisible && SceneMoveLimitedText.Length > 0;

    /// <summary>The only scene cannot be deleted, so with one scene there is no button for it.</summary>
    public bool IsDeleteSceneVisible => Scenes.Count > 1;

    public bool CanRemoveCurrentScene => _session.CanRemoveCurrentScene;

    // Commands

    /// <summary>Moves the playhead to where the scene before the current one has been entered.</summary>
    public bool ShowPreviousScene() => _session.ShowPreviousScene();

    /// <summary>Moves the playhead to where the scene after the current one has been entered.</summary>
    public bool ShowNextScene() => _session.ShowNextScene();

    /// <summary>Moves the playhead to where a scene has been entered.</summary>
    public bool ShowScene(int index) => _session.ShowScene(index);

    /// <summary>The Previous button of the Scene section. The scene it lands on is read out.</summary>
    public void StepToPreviousScene()
    {
        if (ShowPreviousScene())
        {
            AnnounceCurrentScene();
        }
    }

    /// <summary>The Next button of the Scene section. The scene it lands on is read out.</summary>
    public void StepToNextScene()
    {
        if (ShowNextScene())
        {
            AnnounceCurrentScene();
        }
    }

    /// <summary>Starts a new scene at the playhead and says so, or says why it cannot.</summary>
    public void SplitSceneAtPlayhead()
    {
        if (!IsEditable)
        {
            return;
        }

        if (_session.SplitSceneAtPlayhead().Changed)
        {
            Announce(StudioEditorText.SceneSplitMessage, SceneActivityId, StudioAnnouncementKind.Completed);
        }
        else if (_session.SplitSceneExplanation is { } explanation)
        {
            Announce(explanation, SceneActivityId, StudioAnnouncementKind.Information);
        }
    }

    /// <summary>Deletes the scene the playhead is in and says so, or says that the only scene stays.</summary>
    public void RemoveCurrentScene()
    {
        if (!IsEditable)
        {
            return;
        }

        if (_session.RemoveCurrentScene().Changed)
        {
            Announce(StudioEditorText.SceneDeletedMessage, SceneActivityId, StudioAnnouncementKind.Completed);
        }
        else if (Scenes.Count == 1)
        {
            Announce(StudioEditorText.OnlySceneExplanation, SceneActivityId, StudioAnnouncementKind.Information);
        }
    }

    /// <summary>
    /// Moves where a scene starts, for a drag on the lane. The playhead follows the start, as it
    /// follows a trim handle.
    /// </summary>
    public StudioSceneEditResult SetSceneStart(int index, double sourceTime) => _session.SetSceneStart(index, sourceTime);

    public void StepSceneStartEarlier() => StepSceneStart(-SceneTimeStep);

    public void StepSceneStartLater() => StepSceneStart(SceneTimeStep);

    public void SetSceneStartAtPlayhead()
    {
        var index = CurrentSceneIndex;
        if (index >= 1)
        {
            _session.SetSceneStartAtPlayhead(index);
            AnnounceSceneTime();
        }
    }

    private void StepSceneStart(double seconds)
    {
        var index = CurrentSceneIndex;
        if (index >= 1 && index < Scenes.Count)
        {
            _session.SetSceneStart(index, Scenes[index].Start + seconds);
            AnnounceSceneTime();
        }
    }

    // Previous and Next say nothing of where they land, and the text between them is not read
    // when it changes. On the lane the arrow keys need none of this: there the list says which
    // of its items is now the selected one.
    private void AnnounceCurrentScene() =>
        Announce(GetSceneDescription(CurrentSceneIndex), "StudioSceneSelection", StudioAnnouncementKind.Information);

    // A button that moves the start says nothing by itself, and the time is written elsewhere.
    // The time it ended up with is said, which is also how a step that a neighbor stopped is heard.
    private void AnnounceSceneTime() =>
        Announce(SceneStartAccessibleName, "StudioSceneTime", StudioAnnouncementKind.Information);

    private void RaiseSceneChanged()
    {
        Raise(ScenePropertyNames);
        RaiseSceneStateAtPlayhead();
    }

    /// <summary>
    /// Whether the scene can be split, and why not, follow the playhead, and whether the reason
    /// shows follows the playing. Each is reported when it has changed, and not for every frame.
    /// </summary>
    private void RaiseSceneStateAtPlayhead()
    {
        var canSplitChanged = CanSplitSceneAtPlayhead != _couldSplitScene;
        var explanationChanged = SplitSceneExplanation != _splitSceneExplanation;
        var isVisibleChanged = IsSplitSceneExplanationVisible != _wasSplitSceneExplanationVisible;
        RememberSceneStateAtPlayhead();
        if (canSplitChanged)
        {
            OnPropertyChanged(nameof(CanSplitSceneAtPlayhead));
        }

        if (explanationChanged)
        {
            OnPropertyChanged(nameof(SplitSceneExplanation));
            OnPropertyChanged(nameof(SplitSceneHelpText));
        }

        if (isVisibleChanged)
        {
            OnPropertyChanged(nameof(IsSplitSceneExplanationVisible));
        }
    }

    private void RememberSceneStateAtPlayhead()
    {
        _couldSplitScene = CanSplitSceneAtPlayhead;
        _splitSceneExplanation = SplitSceneExplanation;
        _wasSplitSceneExplanationVisible = IsSplitSceneExplanationVisible;
    }
}
