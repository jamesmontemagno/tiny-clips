using TinyClips.App.Models.Studio;
using TinyClips.Core.Models;
using TinyClips.Core.Studio;

namespace TinyClips.App.ViewModels.Studio;

// The values behind the Captions panel of the inspector. What an edit does is decided in the
// session. The text of the selected cue goes in when the box is left, so that typing is one undo
// step and one new list of rows: until then it is kept here, and whatever is about to read the
// project, or change the selection, puts it in first.
public sealed partial class StudioViewModel
{
    private const string CaptionActivityId = "StudioCaptions";

    private static readonly StudioCaptionRow[] NoCaptionRows = [];
    private static readonly StudioCaptionFinding[] NoCaptionFindings = [];

    // What changes when another cue is selected and the project stays as it is.
    private static readonly string[] CaptionSelectionPropertyNames =
    [
        nameof(SelectedCaptionRow),
        nameof(HasSelectedCaption),
        nameof(SelectedCaptionText),
        nameof(SelectedCaptionTimeText),
    ];

    private StudioCaptionRow[] _captionRows = NoCaptionRows;
    private StudioCaptionCue[]? _captionRowsList;
    private StudioProject? _captionStateProject;
    private StudioCaptionFinding[] _captionFindings = NoCaptionFindings;
    private bool _hasResolvedCaptions;
    private string? _pendingCaptionText;
    private bool _isChoosingCaptionFile;

    /// <summary>Asks for a caption file to read. Null when none was chosen.</summary>
    public Func<Task<string?>>? ChooseCaptionFile { get; set; }

    /// <summary>Asks where to write captions, from a folder and a file name. Null when nowhere was chosen.</summary>
    public Func<string, string, StudioCaptionFormat, Task<string?>>? ChooseCaptionExportFile { get; set; }

    /// <summary>The cues in time order. The same list for as long as no cue changes.</summary>
    public IReadOnlyList<StudioCaptionRow> CaptionRows
    {
        get
        {
            if (_session.Model is not { } model)
            {
                return NoCaptionRows;
            }

            var cues = model.Project.Captions.Cues;
            if (!ReferenceEquals(cues, _captionRowsList))
            {
                _captionRowsList = cues;
                _captionRows = model.GetCaptionIndexesInOrder().Select(index => StudioCaptionRow.From(index, cues[index])).ToArray();
            }

            return _captionRows;
        }
    }

    public string CaptionCountText => StudioCaptionText.GetCountText(Project?.Captions.Cues.Length ?? 0);

    public bool IsCaptionEmptyHintVisible => IsReady && (Project?.Captions.Cues.Length ?? 0) == 0;

    public string CaptionEmptyHint => StudioCaptionText.EmptyHint;

    public string CaptionExportNote => StudioCaptionText.ExportNote;

    /// <summary>What the last import, fix or export came to, for those who do not hear it said.</summary>
    public string CaptionStatus { get; private set; } = string.Empty;

    public bool HasCaptionStatus => CaptionStatus.Length > 0;

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

    public string SelectedCaptionText => _session.SelectedCaption?.Text ?? string.Empty;

    public string SelectedCaptionTimeText =>
        _session.SelectedCaption is { } cue ? StudioCaptionText.GetRangeText(cue) : string.Empty;

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

    /// <summary>Keeps what is typed for the selected cue until the box is left.</summary>
    public void SetPendingCaptionText(string text) => _pendingCaptionText = text;

    /// <summary>Puts what was typed for the selected cue into the project.</summary>
    public void CommitCaptionText()
    {
        if (_pendingCaptionText is { } text)
        {
            _pendingCaptionText = null;
            _session.SetSelectedCaptionText(text);
        }
    }

    public void RemoveSelectedCaption()
    {
        _pendingCaptionText = null;
        _session.RemoveSelectedCaption();
    }

    /// <summary>Makes the safe fixes, and says what came of them.</summary>
    public void FixCaptions()
    {
        CommitCaptionText();
        ReportCaptions(StudioCaptionText.GetFixedMessage(_session.FixCaptions()));
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
                ShowError(StudioCaptionText.NothingToImport);
                return;
            }

            ReportCaptions(StudioCaptionText.GetImportedMessage(result.Count, result.Skipped));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ShowError($"The captions could not be read: {ex.Message}");
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
            ReportCaptions(StudioCaptionText.ExportedMessage);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError($"The captions could not be written: {ex.Message}");
        }
        finally
        {
            SetChoosingCaptionFile(false);
        }
    }

    private void ReportCaptions(string message)
    {
        CaptionStatus = message;
        OnPropertyChanged(nameof(CaptionStatus));
        OnPropertyChanged(nameof(HasCaptionStatus));
        Announce(message, CaptionActivityId, StudioAnnouncementKind.Completed);
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
