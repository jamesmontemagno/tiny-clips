using TinyClips.App.Models.Studio;
using TinyClips.Core.Models;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;
using TinyClips.Core.Studio.Transcription;

namespace TinyClips.App.ViewModels.Studio;

// The values behind the Captions panel of the inspector. What an edit does is decided in the
// session. The text of the selected cue goes in when the box is left, so that typing is one undo
// step and one new list of rows: until then it is kept here, and whatever is about to read the
// project, or change the selection, puts it in first.
public sealed partial class StudioViewModel
{
    private const string CaptionActivityId = "StudioCaptions";

    /// <summary>How far the nudge buttons move one end of a cue, in seconds.</summary>
    public const double CaptionNudgeStep = 0.1;

    private static readonly StudioCaptionRow[] NoCaptionRows = [];
    private static readonly StudioCaptionCue[] NoOrderedCaptions = [];
    private static readonly StudioCaptionFinding[] NoCaptionFindings = [];

    // What changes when another cue is selected and the project stays as it is.
    private static readonly string[] CaptionSelectionPropertyNames =
    [
        nameof(SelectedCaptionRow),
        nameof(HasSelectedCaption),
        nameof(SelectedCaptionText),
        nameof(SelectedCaptionTimeText),
        nameof(SelectedCaptionStartText),
        nameof(SelectedCaptionEndText),
        nameof(SelectedCaptionProblemText),
        nameof(HasSelectedCaptionProblems),
        nameof(CanEditSelectedCaption),
        nameof(CanSplitSelectedCaption),
        nameof(CanMergeSelectedCaption),
    ];

    // What changes when what the panel says changes.
    private static readonly string[] CaptionNoticePropertyNames =
    [
        nameof(CaptionNotice),
        nameof(IsCaptionNoticeOpen),
        nameof(CaptionNoticeKind),
        nameof(CaptionNoticeTitle),
        nameof(HasCaptionNoticeTitle),
        nameof(CaptionNoticeMessage),
        nameof(IsCaptionNoticeRetryVisible),
    ];

    // What changes when what is searched for changes.
    private static readonly string[] CaptionSearchPropertyNames =
    [
        nameof(CaptionRows),
        nameof(SelectedCaptionRow),
        nameof(IsSearchingCaptions),
        nameof(CaptionSearchCountText),
        nameof(IsCaptionNoMatchesVisible),
        nameof(IsCaptionListVisible),
        nameof(CaptionNoMatchesText),
    ];

    private StudioCaptionRow[] _allCaptionRows = NoCaptionRows;
    private StudioCaptionCue[] _orderedCaptions = NoOrderedCaptions;
    private StudioCaptionCue[]? _captionRowsList;
    private StudioCaptionRow[] _captionRows = NoCaptionRows;
    private StudioCaptionRow[]? _captionRowsSource;
    private string _captionRowsQuery = string.Empty;
    private string _captionSearchQuery = string.Empty;
    private StudioCaptionRow? _currentCaptionRow;
    private bool _couldSplitSelectedCaption;
    private StudioProject? _captionStateProject;
    private StudioCaptionFinding[] _captionFindings = NoCaptionFindings;
    private bool _hasResolvedCaptions;
    private string? _pendingCaptionText;
    private bool _isChoosingCaptionFile;
    private StudioCaptionNotice? _captionNotice;
    private ITimer? _captionNoticeTimer;

    /// <summary>Asks for a caption file to read. Null when none was chosen.</summary>
    public Func<Task<string?>>? ChooseCaptionFile { get; set; }

    /// <summary>Asks where to write captions, from a folder and a file name. Null when nowhere was chosen.</summary>
    public Func<string, string, StudioCaptionFormat, Task<string?>>? ChooseCaptionExportFile { get; set; }

    /// <summary>
    /// Whether the window is the one in use. When generating ends while it is not, a notification
    /// says what came of it instead of a screen reader.
    /// </summary>
    public Func<bool>? IsWindowInFront { get; set; }

    /// <summary>Raised when generating has ended while the window was not in front.</summary>
    public event EventHandler<StudioProjectNotificationEventArgs>? CaptionNotificationRequested;

    /// <summary>
    /// The cues in time order that have what is searched for: all of them when nothing is. The
    /// same list for as long as no cue changes and the search stays as it is.
    /// </summary>
    public IReadOnlyList<StudioCaptionRow> CaptionRows
    {
        get
        {
            RefreshCaptionRows();
            if (!ReferenceEquals(_captionRowsSource, _allCaptionRows) || _captionRowsQuery != _captionSearchQuery)
            {
                _captionRowsSource = _allCaptionRows;
                _captionRowsQuery = _captionSearchQuery;
                _captionRows = FilterCaptionRows(_allCaptionRows, _captionSearchQuery);
            }

            return _captionRows;
        }
    }

    /// <summary>The row of the cue on show at the playhead, which the list keeps in view while nothing is selected.</summary>
    public StudioCaptionRow? CurrentCaptionRow
    {
        get
        {
            RefreshCaptionRows();
            return _currentCaptionRow;
        }
    }

    public string CaptionCountText => StudioCaptionText.GetCountText(Project?.Captions.Cues.Length ?? 0);

    public bool IsCaptionEmptyHintVisible => IsReady && (Project?.Captions.Cues.Length ?? 0) == 0;

    public string CaptionEmptyHint => StudioCaptionText.GenerateHint;

    public string CaptionExportNote => StudioCaptionText.ExportNote;

    // What the panel says

    /// <summary>What the last import, fix, export or generating came to. Null when there is nothing to say.</summary>
    public StudioCaptionNotice? CaptionNotice => _captionNotice;

    /// <summary>Whether there is something to say. The banner's close button turns this off.</summary>
    public bool IsCaptionNoticeOpen
    {
        get => _captionNotice is not null;
        set
        {
            if (!value)
            {
                DismissCaptionNotice();
            }

            ResyncIfDifferent(value, IsCaptionNoticeOpen);
        }
    }

    public StudioCaptionNoticeKind CaptionNoticeKind => _captionNotice?.Kind ?? StudioCaptionNoticeKind.Info;

    public string CaptionNoticeTitle => _captionNotice?.Title ?? string.Empty;

    public bool HasCaptionNoticeTitle => CaptionNoticeTitle.Length > 0;

    public string CaptionNoticeMessage => _captionNotice?.Message ?? string.Empty;

    /// <summary>Whether it offers to try another model: after hearing nobody speak.</summary>
    public bool IsCaptionNoticeRetryVisible => _captionNotice?.Kind == StudioCaptionNoticeKind.NoSpeech && CanGenerateCaptions;

    public void DismissCaptionNotice()
    {
        _captionNoticeTimer?.Dispose();
        _captionNoticeTimer = null;
        if (_captionNotice is not null)
        {
            _captionNotice = null;
            Raise(CaptionNoticePropertyNames);
        }
    }

    // Searching

    public bool IsSearchingCaptions => _captionSearchQuery.Length > 0;

    /// <summary>How many cues have what is searched for: "3 of 12". Nothing while nothing is.</summary>
    public string CaptionSearchCountText => IsSearchingCaptions
        ? StudioCaptionPanelText.GetSearchCountText(CaptionRows.Count, _allCaptionRows.Length)
        : string.Empty;

    public bool IsCaptionNoMatchesVisible => IsSearchingCaptions && HasCaptions && CaptionRows.Count == 0;

    /// <summary>Whether the list has a row to show: there are captions, and the search finds one.</summary>
    public bool IsCaptionListVisible => CaptionRows.Count > 0;

    public string CaptionNoMatchesText => StudioCaptionPanelText.GetNoMatchesText(_captionSearchQuery);

    /// <summary>Shows only the cues that have <paramref name="text"/>, and marks it in them.</summary>
    public void SetCaptionSearchText(string text)
    {
        var query = StudioCaptionSearch.Normalize(text);
        if (query != _captionSearchQuery)
        {
            _captionSearchQuery = query;
            Raise(CaptionSearchPropertyNames);
        }
    }

    /// <summary>The selected cue's place in <see cref="CaptionRows"/>, or -1.</summary>
    public int SelectedCaptionRow
    {
        get
        {
            if (_session.SelectedCaptionIndex is not { } index)
            {
                return -1;
            }

            var rows = CaptionRows;
            for (var row = 0; row < rows.Count; row++)
            {
                if (rows[row].Index == index)
                {
                    return row;
                }
            }

            return -1;
        }
    }

    public bool HasSelectedCaption => _session.SelectedCaptionIndex is not null;

    // What is typed wins over the project: whatever refreshes the box while someone types in it
    // must not put the old text back.
    public string SelectedCaptionText => _pendingCaptionText ?? _session.SelectedCaption?.Text ?? string.Empty;

    /// <summary>When the selected cue starts and ends, as a screen reader says it.</summary>
    public string SelectedCaptionTimeText =>
        _session.SelectedCaption is { } cue ? StudioCaptionText.GetRangeText(cue) : string.Empty;

    public string SelectedCaptionStartText =>
        _session.SelectedCaption is { } cue ? StudioCaptionPanelText.GetClockText(cue.Start) : string.Empty;

    public string SelectedCaptionEndText =>
        _session.SelectedCaption is { } cue ? StudioCaptionPanelText.GetClockText(cue.End) : string.Empty;

    /// <summary>What the checks found in the selected cue, or nothing.</summary>
    public string SelectedCaptionProblemText
    {
        get
        {
            if (_session.SelectedCaptionIndex is not { } index)
            {
                return string.Empty;
            }

            RefreshCaptionRows();
            return Array.Find(_allCaptionRows, row => row.Index == index)?.ProblemText ?? string.Empty;
        }
    }

    public bool HasSelectedCaptionProblems => SelectedCaptionProblemText.Length > 0;

    /// <summary>Whether the selected cue can be retimed or deleted.</summary>
    public bool CanEditSelectedCaption => IsEditable && HasSelectedCaption;

    /// <summary>Whether the selected cue can be split at the playhead: it is there, and has two words or two lines.</summary>
    public bool CanSplitSelectedCaption => _session.CanSplitSelectedCaption;

    public bool CanMergeSelectedCaption => _session.CanMergeSelectedCaption;

    /// <summary>The words on the Generate button: an invitation until there are captions.</summary>
    public string CaptionGenerateButtonText => StudioCaptionPanelText.GetGenerateButtonText(HasCaptions);

    public bool CanImportCaptions => IsEditable && !_isChoosingCaptionFile;

    public bool CanExportCaptions
    {
        get
        {
            RefreshCaptionState();
            return IsReady && !_isChoosingCaptionFile && _hasResolvedCaptions;
        }
    }

    public bool CanFixCaptions
    {
        get
        {
            RefreshCaptionState();
            return IsEditable && _captionFindings.Any(static finding => finding.CanFix);
        }
    }

    public bool HasCaptions => (Project?.Captions.Cues.Length ?? 0) > 0;

    /// <summary>Whether the checks find something in the captions there are.</summary>
    public bool HasCaptionProblems
    {
        get
        {
            RefreshCaptionState();
            return _captionFindings.Length > 0;
        }
    }

    public bool AreCaptionsClean => HasCaptions && !HasCaptionProblems;

    /// <summary>What the checks find, such as "2 long lines, 1 too short".</summary>
    public string CaptionCheckText
    {
        get
        {
            RefreshCaptionState();
            return _captionFindings.Length == 0 ? StudioCaptionText.NoProblems : StudioCaptionQuality.GetSummary(_captionFindings);
        }
    }

    /// <summary>Selects the cue on a row of the list, and moves the playhead to where it starts.</summary>
    public void SelectCaptionRow(int row)
    {
        CommitCaptionText();
        var rows = CaptionRows;
        _session.SelectCaption(row >= 0 && row < rows.Count ? rows[row].Index : null);
    }

    /// <summary>
    /// Keeps what is typed for the selected cue until the box is left. The box saying what the cue
    /// says already, as it does each time it is refreshed, is nothing typed: kept, it would hide
    /// what an undo or a redo puts there next.
    /// </summary>
    public void SetPendingCaptionText(string text) =>
        _pendingCaptionText = _session.SelectedCaption is { } cue && !IsSameCaptionText(text, cue.Text) ? text : null;

    // A text box ends its lines with \r, so the cue it shows is told apart only by its words.
    private static bool IsSameCaptionText(string shown, string text) =>
        string.Equals(shown.ReplaceLineEndings("\n"), text.ReplaceLineEndings("\n"), StringComparison.Ordinal);

    /// <summary>Puts what was typed for the selected cue into the project.</summary>
    public void CommitCaptionText()
    {
        if (_pendingCaptionText is { } text)
        {
            _pendingCaptionText = null;
            _session.SetSelectedCaptionText(text);
        }
    }

    /// <summary>Lets go of the selected cue, keeping what was typed for it.</summary>
    public void DeselectCaption()
    {
        CommitCaptionText();
        _session.SelectCaption(null);
    }

    public void RemoveSelectedCaption()
    {
        if (!CanEditSelectedCaption)
        {
            return;
        }

        _pendingCaptionText = null;
        _session.RemoveSelectedCaption();
        Announce(StudioCaptionPanelText.DeletedMessage, CaptionActivityId, StudioAnnouncementKind.Completed);
    }

    /// <summary>Splits the selected cue in two at the playhead, and selects the second part.</summary>
    public void SplitSelectedCaption()
    {
        CommitCaptionText();
        if (_session.SplitSelectedCaptionAtPlayhead())
        {
            Announce(StudioCaptionPanelText.SplitMessage, CaptionActivityId, StudioAnnouncementKind.Completed);
        }
    }

    /// <summary>Puts the selected cue together with the one after it.</summary>
    public void MergeSelectedCaption()
    {
        CommitCaptionText();
        if (_session.MergeSelectedCaptionWithNext())
        {
            Announce(StudioCaptionPanelText.MergedMessage, CaptionActivityId, StudioAnnouncementKind.Completed);
        }
    }

    public void NudgeCaptionStartEarlier() => NudgeSelectedCaption(StudioCaptionEdge.Start, -CaptionNudgeStep);

    public void NudgeCaptionStartLater() => NudgeSelectedCaption(StudioCaptionEdge.Start, CaptionNudgeStep);

    public void NudgeCaptionEndEarlier() => NudgeSelectedCaption(StudioCaptionEdge.End, -CaptionNudgeStep);

    public void NudgeCaptionEndLater() => NudgeSelectedCaption(StudioCaptionEdge.End, CaptionNudgeStep);

    public void MoveCaptionStartToPlayhead() => RetimeSelectedCaption(StudioCaptionEdge.Start, _session.Playhead);

    public void MoveCaptionEndToPlayhead() => RetimeSelectedCaption(StudioCaptionEdge.End, _session.Playhead);

    /// <summary>Makes the safe fixes, and says what came of them and how many problems are left.</summary>
    public void FixCaptions()
    {
        CommitCaptionText();
        var applied = _session.FixCaptions();
        RefreshCaptionState();
        ShowCaptionNotice(new StudioCaptionNotice(StudioCaptionNoticeKind.Done, StudioCaptionText.GetFixedMessage(applied, _captionFindings.Length)));
    }

    /// <summary>Whether the checks found a problem that Next problem can go to.</summary>
    public bool CanSelectNextCaptionProblem => IsEditable && HasCaptionProblems;

    /// <summary>Said with the checks when Auto Fix cannot fix every problem they found, or empty when it can.</summary>
    public string CaptionManualFixNote
    {
        get
        {
            RefreshCaptionState();
            var fixable = _captionFindings.Count(static finding => finding.CanFix);
            return StudioCaptionText.GetManualFixNote(fixable, _captionFindings.Length) ?? string.Empty;
        }
    }

    public bool HasCaptionManualFixNote => CaptionManualFixNote.Length > 0;

    /// <summary>
    /// Selects the next cue, in time order, that the checks found a problem in: after the selected
    /// one, or the first when none is selected, going round after the last.
    /// </summary>
    public void SelectNextCaptionProblem()
    {
        CommitCaptionText();
        RefreshCaptionRows();
        var rows = _allCaptionRows;
        int? current = null;
        if (_session.SelectedCaptionIndex is { } index && Array.FindIndex(rows, row => row.Index == index) is var place and >= 0)
        {
            current = place;
        }

        if (StudioCaptionEditing.GetNextProblem(rows.Length, current, row => rows[row].HasProblems) is { } next)
        {
            _session.SelectCaption(rows[next].Index);
        }
    }

    /// <summary>Asks for a caption file and puts its cues in place of the captions.</summary>
    public async Task ImportCaptionsAsync(StudioCaptionTiming timing)
    {
        if (!CanImportCaptions || ChooseCaptionFile is null)
        {
            return;
        }

        CommitCaptionText();
        Pause();
        SetChoosingCaptionFile(true);
        try
        {
            if (await ChooseCaptionFile() is not { } path)
            {
                return;
            }

            var text = await Task.Run(() => StudioCaptionFiles.ReadFile(path));
            if (_session.ImportCaptions(text, timing) is not { } result)
            {
                return;
            }

            if (result.Count == 0)
            {
                ShowCaptionProblem(StudioCaptionText.NothingToImport);
                return;
            }

            ShowCaptionNotice(new StudioCaptionNotice(
                StudioCaptionNoticeKind.Done,
                StudioCaptionText.GetImportedMessage(result.Count, result.Skipped)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ShowCaptionProblem($"The captions could not be read: {ex.Message}");
        }
        finally
        {
            SetChoosingCaptionFile(false);
        }
    }

    /// <summary>Asks where to write the captions the video has, and writes them there.</summary>
    public async Task ExportCaptionsAsync(StudioCaptionFormat format)
    {
        if (!CanExportCaptions || ChooseCaptionExportFile is null)
        {
            return;
        }

        CommitCaptionText();
        Pause();
        SetChoosingCaptionFile(true);
        try
        {
            var saved = SavedProjectFile;
            var folder = saved is not null && Path.GetDirectoryName(saved) is { } parent && Directory.Exists(parent)
                ? parent
                : _storage.OutputDirectory(CaptureType.Video);
            var name = StudioProjectFolder.FolderName(ClipName) + StudioCaptionFiles.GetExtension(format);
            if (await ChooseCaptionExportFile(folder, name, format) is not { } path)
            {
                return;
            }

            var text = StudioCaptionFiles.Write(StudioCaptionFiles.GetFormat(path) ?? format, _session.GetResolvedCaptions());
            await Task.Run(() => StudioCaptionFiles.ReplaceFile(path, text));
            ShowCaptionNotice(new StudioCaptionNotice(StudioCaptionNoticeKind.Done, StudioCaptionText.ExportedMessage));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowCaptionProblem($"The captions could not be written: {ex.Message}");
        }
        finally
        {
            SetChoosingCaptionFile(false);
        }
    }

    // Drawing them into the picture

    public bool AreCaptionsDrawn
    {
        get => Project?.Overlays.Captions.Enabled ?? false;
        set
        {
            if (value != AreCaptionsDrawn)
            {
                _session.SetCaptionsDrawn(value);
            }

            ResyncIfDifferent(value, AreCaptionsDrawn);
        }
    }

    public int CaptionSizeIndex
    {
        get => (int)(Project?.Overlays.Captions.Size ?? StudioCaptionSize.Medium);
        set
        {
            if (value is >= 0 and <= (int)StudioCaptionSize.Large && value != CaptionSizeIndex)
            {
                _session.SetCaptionSize((StudioCaptionSize)value);
            }

            ResyncIfDifferent(value, CaptionSizeIndex);
        }
    }

    public int CaptionPositionIndex
    {
        get => (int)(Project?.Overlays.Captions.Position ?? StudioCaptionPosition.Bottom);
        set
        {
            if (value is >= 0 and <= (int)StudioCaptionPosition.Top && value != CaptionPositionIndex)
            {
                _session.SetCaptionPosition((StudioCaptionPosition)value);
            }

            ResyncIfDifferent(value, CaptionPositionIndex);
        }
    }

    public int CaptionAppearanceIndex
    {
        get => (int)(Project?.Overlays.Captions.Appearance ?? StudioCaptionAppearance.ClassicOutline);
        set
        {
            if (value is >= 0 and <= (int)StudioCaptionAppearance.CleanShadow && value != CaptionAppearanceIndex)
            {
                _session.SetCaptionAppearance((StudioCaptionAppearance)value);
            }

            ResyncIfDifferent(value, CaptionAppearanceIndex);
        }
    }

    public string CaptionDrawnNote => StudioCaptionText.DrawnNote;

    // Generating

    private static readonly IReadOnlyList<StudioCaptionModelOption> AllCaptionModelOptions =
        StudioCaptionModels.All.Select(static model => new StudioCaptionModelOption(model)).ToArray();

    // What changes as generating goes on, and the project stays as it is.
    private static readonly string[] CaptionGenerationPropertyNames =
    [
        nameof(IsGeneratingCaptions),
        nameof(IsCaptionGenerateVisible),
        nameof(CaptionProgressPercent),
        nameof(CaptionProgressText),
        nameof(IsStoppingCaptionGeneration),
        nameof(CanCancelCaptionGeneration),
    ];

    public IReadOnlyList<StudioCaptionModelOption> CaptionModelOptions => AllCaptionModelOptions;

    private StudioCaptionModel SelectedCaptionModel =>
        StudioCaptionModels.Find(_settings.StudioCaptionModelId) ?? StudioCaptionModels.Default;

    /// <summary>The chosen model's place in <see cref="CaptionModelOptions"/>. Kept in the settings.</summary>
    public int SelectedCaptionModelIndex
    {
        get
        {
            var chosen = SelectedCaptionModel;
            for (var index = 0; index < AllCaptionModelOptions.Count; index++)
            {
                if (ReferenceEquals(AllCaptionModelOptions[index].Model, chosen))
                {
                    return index;
                }
            }

            return 0;
        }
        set
        {
            if (value >= 0 && value < AllCaptionModelOptions.Count && value != SelectedCaptionModelIndex)
            {
                _settings.StudioCaptionModelId = AllCaptionModelOptions[value].Model.Id;
                OnPropertyChanged(nameof(SelectedCaptionModelIndex));
                OnPropertyChanged(nameof(CaptionModelNote));
                OnPropertyChanged(nameof(CaptionGenerateActionText));
            }

            ResyncIfDifferent(value, SelectedCaptionModelIndex);
        }
    }

    public string CaptionModelNote =>
        StudioCaptionText.GetModelNote(SelectedCaptionModel, _session.IsCaptionModelInstalled(SelectedCaptionModel));

    public string CaptionGenerateActionText =>
        StudioCaptionText.GetGenerateActionText(SelectedCaptionModel, _session.IsCaptionModelInstalled(SelectedCaptionModel));

    public string CaptionPrivacyNote => StudioCaptionText.PrivacyNote;

    public string CaptionReplaceNote => StudioCaptionText.ReplaceNote;

    public bool CanGenerateCaptions => _session.CanGenerateCaptions;

    public bool IsGeneratingCaptions => _session.IsGeneratingCaptions;

    public bool IsCaptionGenerateVisible => !IsGeneratingCaptions;

    public double CaptionProgressPercent => _session.CaptionGenerationProgress.Fraction * 100;

    public string CaptionProgressText => IsStoppingCaptionGeneration
        ? StudioCaptionText.StoppingMessage
        : StudioCaptionText.GetProgressText(_session.CaptionGenerationProgress);

    public bool IsStoppingCaptionGeneration => _session.IsStoppingCaptionGeneration;

    public bool CanCancelCaptionGeneration => IsGeneratingCaptions && !IsStoppingCaptionGeneration;

    public void CancelCaptionGeneration()
    {
        if (CanCancelCaptionGeneration)
        {
            _session.CancelCaptionGeneration();
            Announce(StudioCaptionText.StoppingMessage, CaptionActivityId, StudioAnnouncementKind.Information);
        }
    }

    /// <summary>Hears the recording with the chosen model, and says what came of it.</summary>
    public async Task GenerateCaptionsAsync()
    {
        if (!CanGenerateCaptions)
        {
            return;
        }

        CommitCaptionText();
        DismissCaptionNotice();
        var model = SelectedCaptionModel;
        var generation = _session.GenerateCaptionsAsync(model);
        Announce(StudioCaptionText.GeneratingMessage, CaptionActivityId, StudioAnnouncementKind.Information);
        var result = await generation;

        // A model it downloaded is on this computer now, whatever came of the rest.
        OnPropertyChanged(nameof(CaptionModelNote));
        OnPropertyChanged(nameof(CaptionGenerateActionText));
        if (_session.IsClosed)
        {
            return;
        }

        var notice = result.Outcome switch
        {
            StudioCaptionGenerationOutcome.Generated => new StudioCaptionNotice(
                StudioCaptionNoticeKind.Done,
                StudioCaptionText.GetGeneratedMessage(result.Count)),
            StudioCaptionGenerationOutcome.NoSpeech => new StudioCaptionNotice(
                StudioCaptionNoticeKind.NoSpeech,
                StudioCaptionPanelText.GetNoSpeechMessage(model.IsEnglishOnly, HasCaptions),
                StudioCaptionPanelText.NoSpeechTitle),
            StudioCaptionGenerationOutcome.Failed => new StudioCaptionNotice(
                StudioCaptionNoticeKind.Problem,
                result.Message ?? StudioCaptionPanelText.NotGeneratedMessage),
            _ => new StudioCaptionNotice(StudioCaptionNoticeKind.Info, StudioCaptionText.CancelledMessage),
        };
        if (result.Outcome == StudioCaptionGenerationOutcome.Generated)
        {
            _pendingCaptionText = null;
        }

        ShowCaptionNotice(notice, notifyWhenBehind: true);
    }

    // Says what came of something: in the panel, and to a screen reader, or in a notification
    // when generating ended while the window was not in front. Done and Info go after a while.
    private void ShowCaptionNotice(StudioCaptionNotice notice, bool notifyWhenBehind = false)
    {
        _captionNoticeTimer?.Dispose();
        _captionNoticeTimer = null;
        _captionNotice = notice;
        Raise(CaptionNoticePropertyNames);
        if (notice.GoesByItself)
        {
            _captionNoticeTimer = _timeProvider.CreateTimer(
                _ => _post(() =>
                {
                    if (ReferenceEquals(_captionNotice, notice))
                    {
                        DismissCaptionNotice();
                    }
                }),
                null,
                StudioCaptionNotice.ShownFor,
                Timeout.InfiniteTimeSpan);
        }

        if (notifyWhenBehind && notice.Kind != StudioCaptionNoticeKind.Info && IsWindowInFront?.Invoke() == false)
        {
            CaptionNotificationRequested?.Invoke(this, new StudioProjectNotificationEventArgs(
                ProjectId,
                StudioCaptionPanelText.GetNotificationTitle(notice),
                StudioCaptionPanelText.GetNotificationMessage(notice, ClipName)));
            return;
        }

        Announce(notice.SpokenText, CaptionActivityId, notice.Kind switch
        {
            StudioCaptionNoticeKind.Done or StudioCaptionNoticeKind.NoSpeech => StudioAnnouncementKind.Completed,
            StudioCaptionNoticeKind.Info => StudioAnnouncementKind.Stopped,
            _ => StudioAnnouncementKind.Information,
        });
    }

    private void ShowCaptionProblem(string message) =>
        ShowCaptionNotice(new StudioCaptionNotice(StudioCaptionNoticeKind.Problem, message));

    private void NudgeSelectedCaption(StudioCaptionEdge edge, double step)
    {
        if (_session.SelectedCaption is { } cue)
        {
            RetimeSelectedCaption(edge, (edge == StudioCaptionEdge.Start ? cue.Start : cue.End) + step);
        }
    }

    // Moves one end of the selected cue, and says where it is now.
    private void RetimeSelectedCaption(StudioCaptionEdge edge, double time)
    {
        CommitCaptionText();
        if (_session.RetimeSelectedCaption(edge, time) && _session.SelectedCaption is { } cue)
        {
            Announce(StudioCaptionText.GetRangeText(cue), CaptionActivityId, StudioAnnouncementKind.Information);
        }
    }

    // The rows of all the cues, made again when a cue changes, with what the checks found in each
    // and which one is at the playhead.
    private void RefreshCaptionRows()
    {
        if (_session.Model is not { } model)
        {
            _captionRowsList = null;
            _allCaptionRows = NoCaptionRows;
            _orderedCaptions = NoOrderedCaptions;
            _currentCaptionRow = null;
            return;
        }

        var cues = model.Project.Captions.Cues;
        if (ReferenceEquals(cues, _captionRowsList))
        {
            return;
        }

        RefreshCaptionState();
        _captionRowsList = cues;
        var order = model.GetCaptionIndexesInOrder();
        var problems = _captionFindings.ToLookup(static finding => finding.Cue, static finding => finding.Kind);
        _orderedCaptions = order.Select(index => cues[index]).ToArray();
        _allCaptionRows = order.Select((index, place) => new StudioCaptionRow(index, cues[index], problems[place])).ToArray();
        _currentCaptionRow = null;
        UpdateCurrentCaptionRow();
    }

    // Marks the row at the playhead. True when it is another one than before.
    private bool UpdateCurrentCaptionRow()
    {
        var row = StudioCaptionEditing.GetPositionAt(_orderedCaptions, _session.Playhead) is { } place ? _allCaptionRows[place] : null;
        if (ReferenceEquals(row, _currentCaptionRow))
        {
            return false;
        }

        if (_currentCaptionRow is not null)
        {
            _currentCaptionRow.IsCurrent = false;
        }

        if (row is not null)
        {
            row.IsCurrent = true;
        }

        _currentCaptionRow = row;
        return true;
    }

    private static StudioCaptionRow[] FilterCaptionRows(StudioCaptionRow[] rows, string query)
    {
        var found = new List<StudioCaptionRow>(rows.Length);
        foreach (var row in rows)
        {
            var ranges = StudioCaptionSearch.GetRanges(row.Text, query);
            row.Highlights = ranges;
            if (query.Length == 0 || ranges.Length > 0)
            {
                found.Add(row);
            }
        }

        return found.Count == rows.Length ? rows : [.. found];
    }

    /// <summary>
    /// The row at the playhead, and whether the selected cue can be split there, follow the
    /// playhead. Each is reported when it has changed, and not for every frame.
    /// </summary>
    private void RaiseCaptionStateAtPlayhead()
    {
        RefreshCaptionRows();
        if (UpdateCurrentCaptionRow())
        {
            OnPropertyChanged(nameof(CurrentCaptionRow));
        }

        var canSplit = CanSplitSelectedCaption;
        if (canSplit != _couldSplitSelectedCaption)
        {
            _couldSplitSelectedCaption = canSplit;
            OnPropertyChanged(nameof(CanSplitSelectedCaption));
        }
    }

    private void RememberCaptionStateAtPlayhead()
    {
        RefreshCaptionRows();
        UpdateCurrentCaptionRow();
        _couldSplitSelectedCaption = CanSplitSelectedCaption;
    }

    private void SetChoosingCaptionFile(bool isChoosing)
    {
        _isChoosingCaptionFile = isChoosing;
        OnPropertyChanged(nameof(CanImportCaptions));
        OnPropertyChanged(nameof(CanExportCaptions));
    }

    // Worked out once for each state of the project, because several bindings ask.
    private void RefreshCaptionState()
    {
        var project = Project;
        if (ReferenceEquals(project, _captionStateProject))
        {
            return;
        }

        _captionStateProject = project;
        _captionFindings = project is null
            ? NoCaptionFindings
            : StudioCaptionQuality.Check(project.Captions.Cues, project.Sources.Screen.Duration);
        _hasResolvedCaptions = project is not null && StudioCaptionResolver.Resolve(project).Length > 0;
    }
}
