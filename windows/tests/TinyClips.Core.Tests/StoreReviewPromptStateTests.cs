using TinyClips.Core.Models;
using TinyClips.Core.Services;

namespace TinyClips.Core.Tests;

public sealed class StoreReviewPromptStateTests
{
    [Fact]
    public void NewState_StartsAtZeroWithoutLifetimeAnalyticsBackfill()
    {
        var settings = new TestSettingsService();
        settings.Set("captureAnalyticsLifetimeV1", """{"screenshotCount":47,"videoCount":2,"gifCount":8}""");

        var state = new StoreReviewPromptState(settings);

        Assert.Equal(0, state.CompletedCaptureCount);
        Assert.False(state.HasClaimedPrompt);
    }

    [Fact]
    public void RecordCompletedCapture_BecomesEligibleOnlyAtThirty()
    {
        var state = new StoreReviewPromptState(new TestSettingsService());

        for (var index = 0; index < 29; index++)
        {
            state.RecordCompletedCapture();
        }

        Assert.Equal(29, state.CompletedCaptureCount);
        Assert.False(state.IsEligible);
        Assert.False(state.TryClaimPromptAfterEditorWindowClosed(
            isCaptureInProgress: false,
            hasOpenCaptureEditor: false));

        state.RecordCompletedCapture();

        Assert.Equal(StoreReviewPromptState.Threshold, state.CompletedCaptureCount);
        Assert.True(state.IsEligible);
        Assert.False(state.HasClaimedPrompt);
        Assert.False(state.TryClaimPromptAfterEditorWindowClosed(
            isCaptureInProgress: true,
            hasOpenCaptureEditor: false));
        Assert.False(state.TryClaimPromptAfterEditorWindowClosed(
            isCaptureInProgress: false,
            hasOpenCaptureEditor: true));
        Assert.True(state.TryClaimPromptAfterEditorWindowClosed(
            isCaptureInProgress: false,
            hasOpenCaptureEditor: false));
    }

    [Fact]
    public void State_PersistsCountAndClaim()
    {
        var settings = new TestSettingsService();
        var state = new StoreReviewPromptState(settings);

        for (var index = 0; index < StoreReviewPromptState.Threshold; index++)
        {
            state.RecordCompletedCapture();
        }

        Assert.True(state.TryClaimPromptAfterEditorWindowClosed(
            isCaptureInProgress: false,
            hasOpenCaptureEditor: false));

        var reloaded = new StoreReviewPromptState(settings);
        Assert.Equal(StoreReviewPromptState.Threshold, reloaded.CompletedCaptureCount);
        Assert.True(reloaded.HasClaimedPrompt);
    }

    [Fact]
    public void CaptureCount_CanAccumulatePastThresholdWithoutClaiming()
    {
        var state = new StoreReviewPromptState(new TestSettingsService());
        for (var index = 0; index < 35; index++)
        {
            state.RecordCompletedCapture();
        }

        Assert.Equal(35, state.CompletedCaptureCount);
        Assert.True(state.IsEligible);
        Assert.False(state.HasClaimedPrompt);
    }

    [Fact]
    public void Claim_IsOneTime()
    {
        var settings = new TestSettingsService();
        var state = new StoreReviewPromptState(settings);
        for (var index = 0; index < StoreReviewPromptState.Threshold; index++)
        {
            state.RecordCompletedCapture();
        }

        Assert.True(state.TryClaimPromptAfterEditorWindowClosed(
            isCaptureInProgress: false,
            hasOpenCaptureEditor: false));
        Assert.False(state.TryClaimPromptAfterEditorWindowClosed(
            isCaptureInProgress: false,
            hasOpenCaptureEditor: false));

        state.RecordCompletedCapture();
        Assert.Equal(StoreReviewPromptState.Threshold, state.CompletedCaptureCount);
        Assert.Equal(1, settings.SetCount(StoreReviewPromptState.PromptClaimedKey));
    }

    [Fact]
    public void State_SurvivesAnalyticsAndResetToDefaults()
    {
        var settings = new TestSettingsService();
        var state = new StoreReviewPromptState(settings);
        var analytics = new ClipAnalyticsService(settings, storeReviewPromptState: state);

        analytics.RecordCapture(CaptureType.Screenshot);
        state.RecordCompletedCapture();
        analytics.Clear();
        new CaptureSettings(settings, analytics).ResetToDefaults();

        var reloaded = new StoreReviewPromptState(settings);
        Assert.Equal(2, reloaded.CompletedCaptureCount);
        Assert.False(reloaded.HasClaimedPrompt);
    }

    [Fact]
    public void ClipAnalyticsService_ReportsEverySupportedCompletionExactlyOnce()
    {
        var settings = new TestSettingsService();
        var state = new StoreReviewPromptState(settings);
        var analytics = new ClipAnalyticsService(settings, storeReviewPromptState: state);

        for (var index = 0; index < StoreReviewPromptState.Threshold; index++)
        {
            analytics.RecordCapture((index % 3) switch
            {
                0 => CaptureType.Screenshot,
                1 => CaptureType.Video,
                _ => CaptureType.Gif,
            });
        }

        Assert.Equal(StoreReviewPromptState.Threshold, state.CompletedCaptureCount);
        Assert.True(state.IsEligible);
        Assert.Equal(StoreReviewPromptState.Threshold, analytics.GetLifetimeTotals().TotalCount);
    }

    [Fact]
    public void ClipAnalyticsService_WithoutStoreObserver_DoesNotAdvanceReviewCount()
    {
        var settings = new TestSettingsService();
        var analytics = new ClipAnalyticsService(settings);

        for (var index = 0; index < StoreReviewPromptState.Threshold; index++)
        {
            analytics.RecordCapture(CaptureType.Screenshot);
        }

        var unobservedState = new StoreReviewPromptState(settings);
        Assert.Equal(0, unobservedState.CompletedCaptureCount);
        Assert.Equal(StoreReviewPromptState.Threshold, analytics.GetLifetimeTotals().TotalCount);
    }

    private sealed class TestSettingsService : ISettingsService
    {
        private readonly Dictionary<string, object> _values = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _setCounts = new(StringComparer.OrdinalIgnoreCase);

        public AppTheme Theme { get; set; }
        public string SaveDirectory { get; set; } = string.Empty;

        public T Get<T>(string key, T defaultValue) =>
            _values.TryGetValue(key, out var value) && value is T typedValue
                ? typedValue
                : defaultValue;

        public void Set<T>(string key, T value)
        {
            _values[key] = value is null ? string.Empty : value;
            _setCounts[key] = _setCounts.GetValueOrDefault(key) + 1;
        }

        public int SetCount(string key) => _setCounts.GetValueOrDefault(key);
    }
}
