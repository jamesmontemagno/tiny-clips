using TinyClips.Core.Studio.Transcription;

namespace TinyClips.Core.Studio.Editing;

/// <summary>What came of importing a caption file.</summary>
/// <param name="Count">How many cues the project has now.</param>
/// <param name="Skipped">How many blocks of the file could not be used.</param>
public readonly record struct StudioCaptionImportResult(int Count, int Skipped);

/// <summary>How generating captions ended.</summary>
public enum StudioCaptionGenerationOutcome
{
    /// <summary>It could not start: the project cannot be edited just now, or it is already running.</summary>
    NotStarted,
    Generated,
    Cancelled,
    Failed,
}

/// <summary>What came of generating captions.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Count">How many cues the project has now, when it worked.</param>
/// <param name="Message">Why it failed, for the user.</param>
public readonly record struct StudioCaptionGenerationResult(StudioCaptionGenerationOutcome Outcome, int Count = 0, string? Message = null);

// Captions. One cue can be selected for its text to be edited. That is apart from the timeline's
// selection: captions have no lane. Selecting one lets go of a selected zoom, cut, or speed change,
// so that Delete does not take away something that is no longer on show. An index is a cue's place
// in Project.Captions.Cues, and it lasts for as long as that list does: undo, redo, and an edit
// from elsewhere each make another list, and let go of the selection.
public sealed partial class StudioEditorSession
{
    private int? _selectedCaptionIndex;
    private StudioCaptionCue[]? _selectedCaptionList;
    private readonly IStudioCaptionGenerator? _captionGenerator;
    private CancellationTokenSource? _captionCancellation;
    private Task _captionGeneration = Task.CompletedTask;
    private int _captionGenerationNumber;

    /// <summary>The cue whose text the inspector shows, or null.</summary>
    public int? SelectedCaptionIndex =>
        _selectedCaptionIndex is { } index
        && Model is { } model
        && ReferenceEquals(model.Project.Captions.Cues, _selectedCaptionList)
        && index >= 0
        && index < model.Project.Captions.Cues.Length
            ? index
            : null;

    public StudioCaptionCue? SelectedCaption => SelectedCaptionIndex is { } index ? Model?.Project.Captions.Cues[index] : null;

    /// <summary>The captions the video has, in output time, as the files and the drawn captions get them.</summary>
    public StudioResolvedCaption[] GetResolvedCaptions() =>
        Model is { } model ? StudioCaptionResolver.Resolve(model.Project) : [];

    /// <summary>What the checks find in the captions now.</summary>
    public StudioCaptionFinding[] CheckCaptions() =>
        Model is { } model ? StudioCaptionQuality.Check(model.Project.Captions.Cues, model.SourceDuration) : [];

    /// <summary>
    /// Selects a cue and moves the playhead to where it starts, or lets go with null. False when
    /// there is no such cue.
    /// </summary>
    public bool SelectCaption(int? index)
    {
        if (Model is not { } model)
        {
            return false;
        }

        var cues = model.Project.Captions.Cues;
        if (index is not { } place || place < 0 || place >= cues.Length)
        {
            _selectedCaptionIndex = null;
            _selectedCaptionList = null;
            RaiseChanged(StudioEditorChanges.Selection);
            return false;
        }

        _selectedCaptionIndex = place;
        _selectedCaptionList = cues;
        SelectNothing();
        RaiseChanged(StudioEditorChanges.Selection);
        Scrub(cues[place].Start);
        return true;
    }

    public void SetCaptionsDrawn(bool isDrawn) => Edit(model => model.SetCaptionsDrawn(isDrawn));

    public void SetCaptionSize(StudioCaptionSize size) => Edit(model => model.SetCaptionSize(size));

    public void SetCaptionPosition(StudioCaptionPosition position) => Edit(model => model.SetCaptionPosition(position));

    public void SetCaptionAppearance(StudioCaptionAppearance appearance) => Edit(model => model.SetCaptionAppearance(appearance));

    /// <summary>Changes what the selected cue says. It stays selected.</summary>
    public void SetSelectedCaptionText(string text)
    {
        if (SelectedCaptionIndex is not { } index)
        {
            return;
        }

        Edit(model =>
        {
            if (model.SetCaptionText(index, text))
            {
                _selectedCaptionList = model.Project.Captions.Cues;
            }
        });
    }

    /// <summary>Takes the selected cue out.</summary>
    public void RemoveSelectedCaption()
    {
        if (SelectedCaptionIndex is { } index)
        {
            _selectedCaptionIndex = null;
            _selectedCaptionList = null;
            Edit(model => model.RemoveCaption(index));
        }
    }

    /// <summary>
    /// Puts the cues of a caption file in place of the captions, as one edit. A file with no cues
    /// changes nothing.
    /// </summary>
    /// <returns>What came of it, or null when the project cannot be edited just now.</returns>
    public StudioCaptionImportResult? ImportCaptions(string text, StudioCaptionTiming timing)
    {
        if (!IsEditable || Model is not { } model)
        {
            return null;
        }

        var contents = StudioCaptionFiles.Read(text, timing, model.TimeMap);
        if (contents.Cues.Length > 0)
        {
            _selectedCaptionIndex = null;
            _selectedCaptionList = null;
            Edit(editing => editing.ReplaceCaptions(contents.Cues));
        }

        return new StudioCaptionImportResult(contents.Cues.Length, contents.SkippedBlocks);
    }

    /// <summary>Makes the safe fixes as one edit, and says how many things changed.</summary>
    public int FixCaptions()
    {
        var applied = 0;
        _selectedCaptionIndex = null;
        _selectedCaptionList = null;
        Edit(model => applied = model.FixCaptions());
        return applied;
    }

    // Generating. It runs beside the editor: the cues are in source time, so editing the video
    // meanwhile changes nothing about them. What was heard goes in as one edit at the end.

    /// <summary>True while captions are being generated.</summary>
    public bool IsGeneratingCaptions { get; private set; }

    /// <summary>How far generating captions is. Means nothing unless <see cref="IsGeneratingCaptions"/>.</summary>
    public StudioCaptionProgress CaptionGenerationProgress { get; private set; }

    /// <summary>Whether this session can generate captions now.</summary>
    public bool CanGenerateCaptions => IsEditable && !IsGeneratingCaptions && _captionGenerator is not null;

    /// <summary>Whether the model is on this computer. A model that is not is downloaded first.</summary>
    public bool IsCaptionModelInstalled(StudioCaptionModel model) => _captionGenerator?.IsModelInstalled(model) ?? false;

    /// <summary>
    /// Hears the recording and puts what was heard in place of the captions, as one edit. Whoever
    /// calls this has told the user about a download the model needs. The task itself never fails.
    /// </summary>
    public Task<StudioCaptionGenerationResult> GenerateCaptionsAsync(StudioCaptionModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!CanGenerateCaptions || Model is not { } editor || _paths is not { } paths)
        {
            return Task.FromResult(new StudioCaptionGenerationResult(StudioCaptionGenerationOutcome.NotStarted));
        }

        var task = GenerateCaptionsCoreAsync(_captionGenerator!, editor.Project, paths, model);
        _captionGeneration = task;
        return task;
    }

    /// <summary>
    /// Whether generating was asked to stop and has not yet. Speech recognition stops between
    /// stretches of work, so this can last for seconds, and longer with a large model.
    /// </summary>
    public bool IsStoppingCaptionGeneration { get; private set; }

    /// <summary>Stops generating captions. The captions stay as they were.</summary>
    public void CancelCaptionGeneration()
    {
        if (_captionCancellation is not { } cancellation || IsStoppingCaptionGeneration)
        {
            return;
        }

        IsStoppingCaptionGeneration = true;
        cancellation.Cancel();
        RaiseChanged(StudioEditorChanges.Captions);
    }

    private async Task<StudioCaptionGenerationResult> GenerateCaptionsCoreAsync(
        IStudioCaptionGenerator generator,
        StudioProject project,
        StudioProjectPaths paths,
        StudioCaptionModel model)
    {
        using var cancellation = new CancellationTokenSource();
        var generation = ++_captionGenerationNumber;
        _captionCancellation = cancellation;
        IsGeneratingCaptions = true;
        CaptionGenerationProgress = default;
        RaiseChanged(StudioEditorChanges.All);

        StudioGeneratedCaptions? heard = null;
        var result = new StudioCaptionGenerationResult(StudioCaptionGenerationOutcome.Cancelled);
        try
        {
            heard = await generator
                .GenerateAsync(project, paths, model, new CaptionProgressRelay(this, generation), cancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (StudioCaptionException ex)
        {
            result = new StudioCaptionGenerationResult(StudioCaptionGenerationOutcome.Failed, 0, ex.Message);
        }
        catch (Exception ex)
        {
            result = new StudioCaptionGenerationResult(StudioCaptionGenerationOutcome.Failed, 0, $"Captions could not be generated: {ex.Message}");
        }

        await PostAsync(() =>
        {
            _captionCancellation = null;
            IsGeneratingCaptions = false;
            IsStoppingCaptionGeneration = false;
            if (heard is not null && !_isClosed && !cancellation.IsCancellationRequested)
            {
                if (IsEditable)
                {
                    _selectedCaptionIndex = null;
                    _selectedCaptionList = null;
                    Edit(editing => editing.SetGeneratedCaptions(heard.Language, heard.Cues));
                    result = new StudioCaptionGenerationResult(StudioCaptionGenerationOutcome.Generated, heard.Cues.Length);
                }
                else
                {
                    result = new StudioCaptionGenerationResult(StudioCaptionGenerationOutcome.Failed, 0, "The captions were generated while the project could not be changed. Generate them again.");
                }
            }

            RaiseChanged(StudioEditorChanges.All);
        }).ConfigureAwait(false);
        return result;
    }

    private void ApplyCaptionProgress(int generation, StudioCaptionProgress progress)
    {
        if (generation == _captionGenerationNumber && IsGeneratingCaptions && !_isClosed)
        {
            CaptionGenerationProgress = progress;
            RaiseChanged(StudioEditorChanges.Captions);
        }
    }

    // Progress comes from the work's own thread and is put on the session's, with no more than
    // one report waiting: a model's download says a lot more than a screen can show.
    private sealed class CaptionProgressRelay(StudioEditorSession session, int generation) : IProgress<StudioCaptionProgress>
    {
        private readonly object _gate = new();
        private StudioCaptionProgress _latest;
        private bool _isPosted;

        public void Report(StudioCaptionProgress value)
        {
            lock (_gate)
            {
                _latest = value;
                if (_isPosted)
                {
                    return;
                }

                _isPosted = true;
            }

            session._post(() =>
            {
                StudioCaptionProgress latest;
                lock (_gate)
                {
                    _isPosted = false;
                    latest = _latest;
                }

                session.ApplyCaptionProgress(generation, latest);
            });
        }
    }
}
