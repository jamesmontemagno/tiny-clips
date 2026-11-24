namespace TinyClips.Core.Services;

/// <summary>
/// Persistent progress for the Microsoft Store review prompt. The count intentionally starts
/// independently of the lifetime analytics history and is claimed before the prompt is shown.
/// </summary>
public sealed class StoreReviewPromptState
{
    public const int Threshold = 30;
    public const string CaptureCountKey = "storeReviewCaptureCountV1";
    public const string PromptClaimedKey = "storeReviewPromptClaimedV1";

    private readonly ISettingsService _settings;
    private readonly object _gate = new();
    private int _completedCaptureCount;
    private bool _hasClaimedPrompt;

    public StoreReviewPromptState(ISettingsService settings)
    {
        _settings = settings;
        _completedCaptureCount = Math.Max(0, _settings.Get(CaptureCountKey, 0));
        _hasClaimedPrompt = _settings.Get(PromptClaimedKey, false);
    }

    public int CompletedCaptureCount
    {
        get
        {
            lock (_gate)
            {
                return _completedCaptureCount;
            }
        }
    }

    public bool HasClaimedPrompt
    {
        get
        {
            lock (_gate)
            {
                return _hasClaimedPrompt;
            }
        }
    }

    public bool IsEligible
    {
        get
        {
            lock (_gate)
            {
                return !_hasClaimedPrompt && _completedCaptureCount >= Threshold;
            }
        }
    }

    /// <summary>Counts one capture reported by the successful-completion analytics source.</summary>
    public void RecordCompletedCapture()
    {
        lock (_gate)
        {
            if (_hasClaimedPrompt)
            {
                return;
            }

            _completedCaptureCount++;
            _settings.Set(CaptureCountKey, _completedCaptureCount);
        }
    }

    /// <summary>
    /// Atomically marks an eligible prompt as claimed. Persisting happens before the UI is shown.
    /// </summary>
    public bool TryClaimPromptAfterEditorWindowClosed(
        bool isCaptureInProgress,
        bool hasOpenCaptureEditor)
    {
        lock (_gate)
        {
            if (isCaptureInProgress ||
                hasOpenCaptureEditor ||
                _hasClaimedPrompt ||
                _completedCaptureCount < Threshold)
            {
                return false;
            }

            _hasClaimedPrompt = true;
            _settings.Set(PromptClaimedKey, true);
            return true;
        }
    }
}
