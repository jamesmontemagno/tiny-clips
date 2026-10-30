using TinyClips.App;
using TinyClips.App.Settings;
using TinyClips.Core.Editing;
using TinyClips.Core.Models;
using TinyClips.Core.Services;
using TinyClips.Core.Tests;

namespace TinyClips.App.Tests;

public sealed class SettingsViewModelTests
{
    [Fact]
    public async Task TemporaryFiles_SlowScanDoesNotBlockOrRepeatOnBindingReads()
    {
        var fixture = new Fixture();
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scans = 0;
        var vm = fixture.CreateViewModel(getTemporaryFilesSummary: () =>
        {
            Interlocked.Increment(ref scans);
            started.SetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            return new TemporaryFilesSummary(1, 1024);
        });

        Assert.Contains("Loading", vm.TempFolderSummary);
        Assert.Equal(0, scans);
        var initialization = vm.EnsureTemporaryFilesInitializedAsync();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.False(initialization.IsCompleted);
            Assert.Same(initialization, vm.EnsureTemporaryFilesInitializedAsync());
            Assert.Contains("Loading", vm.TempFolderSummary);
        }
        finally
        {
            release.Set();
        }

        await initialization;
        Assert.StartsWith("1 file,", vm.TempFolderSummary);
        Assert.Same(initialization, vm.EnsureTemporaryFilesInitializedAsync());
        Assert.Equal(1, scans);
    }

    [Fact]
    public async Task TemporaryFiles_ClosingDuringScanDiscardsLateSummary()
    {
        using var release = new ManualResetEventSlim();
        var vm = new Fixture().CreateViewModel(getTemporaryFilesSummary: () =>
        {
            Assert.True(release.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            return new TemporaryFilesSummary(7, 42);
        });
        var initialization = vm.EnsureTemporaryFilesInitializedAsync();
        vm.NotifyClosed();
        var notifications = 0;
        vm.PropertyChanged += (_, _) => notifications++;
        release.Set();
        await initialization;

        Assert.Equal(0, notifications);
        Assert.Contains("Loading", vm.TempFolderSummary);
        Assert.True(vm.EnsureTemporaryFilesInitializedAsync().IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TemporaryFiles_UnavailableDirectoryShowsAnError(bool accessDenied)
    {
        var vm = new Fixture().CreateViewModel(getTemporaryFilesSummary: () =>
            throw (accessDenied ? (Exception)new UnauthorizedAccessException() : new IOException()));

        await vm.EnsureTemporaryFilesInitializedAsync();

        Assert.Contains("Couldn't read temporary files", vm.TempFolderSummary);
    }

    [Fact]
    public async Task TemporaryFiles_PurgeRunsOffThreadAndRefreshesCachedSummary()
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scans = 0;
        var vm = new Fixture().CreateViewModel(
            getTemporaryFilesSummary: () => new TemporaryFilesSummary(
                Interlocked.Increment(ref scans) == 1 ? 3 : 1, 42),
            purgeTemporaryFiles: () =>
            {
                started.SetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
                return new TemporaryFilesPurgeResult(2, 1);
            });
        var purge = vm.PurgeTempFilesAsync();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.False(purge.IsCompleted);
            Assert.StartsWith("3 files,", vm.TempFolderSummary);
        }
        finally
        {
            release.Set();
        }

        Assert.Equal(new TemporaryFilesPurgeResult(2, 1), await purge);
        Assert.StartsWith("1 file,", vm.TempFolderSummary);
        Assert.Equal(2, scans);
    }

    [Fact]
    public async Task TemporaryFiles_ClosingDuringPurgeDoesNotRescanOrPublish()
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scans = 0;
        var vm = new Fixture().CreateViewModel(
            getTemporaryFilesSummary: () => new TemporaryFilesSummary(Interlocked.Increment(ref scans), 42),
            purgeTemporaryFiles: () =>
            {
                started.SetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
                return new TemporaryFilesPurgeResult(1, 0);
            });
        var purge = vm.PurgeTempFilesAsync();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            vm.NotifyClosed();
        }
        finally
        {
            release.Set();
        }
        var notifications = 0;
        vm.PropertyChanged += (_, _) => notifications++;

        Assert.Equal(new TemporaryFilesPurgeResult(1, 0), await purge);
        Assert.Equal(0, notifications);
        Assert.Equal(1, scans);
    }

    [Fact]
    public void IncludeTinyClips_LoadsWithGeneral_PersistsAndResetsWithoutRealizationWrites()
    {
        var fixture = new Fixture();
        fixture.Settings.IncludeTinyClipsInCapture = true;
        var vm = fixture.CreateViewModel();
        var writes = fixture.Store.Writes;
        var general = vm.BeginSectionRealization(SettingsSectionKind.General);
        vm.IncludeTinyClipsInCapture = false;
        vm.CompleteSectionRealization(general);

        Assert.True(vm.IncludeTinyClipsInCapture);
        Assert.True(fixture.Settings.IncludeTinyClipsInCapture);
        Assert.Equal(writes, fixture.Store.Writes);
        vm.IncludeTinyClipsInCapture = false;
        Assert.False(fixture.Settings.IncludeTinyClipsInCapture);
        vm.IncludeTinyClipsInCapture = true;
        Assert.True(fixture.Settings.IncludeTinyClipsInCapture);
        vm.ResetAllSettings();
        Assert.False(vm.IncludeTinyClipsInCapture);
        Assert.False(fixture.Settings.IncludeTinyClipsInCapture);
    }

    [Fact]
    public void IncludeTinyClipsToggle_HasAccessibleNameAndTwoWayBinding()
    {
        var toggle = Assert.Single(SettingsMarkup.Elements("GeneralSettingsSection.xaml"),
            element => element.Attr("AutomationProperties.AutomationId") == "IncludeTinyClipsInCaptureToggle");
        Assert.Equal("ToggleSwitch", toggle.Name.LocalName);
        Assert.Equal("Include Tiny Clips in captures", toggle.Attr("AutomationProperties.Name"));
        Assert.Equal("{x:Bind ViewModel.IncludeTinyClipsInCapture, Mode=TwoWay}", toggle.Attr("IsOn"));
    }

    [Fact]
    public void ScreenshotEditorDefaultTool_LoadsMoveSelect_PersistsAndRoundTrips()
    {
        var fixture = new Fixture();
        var vm = fixture.CreateViewModel();
        Realize(vm, SettingsSectionKind.Screenshot);

        Assert.Equal(0, vm.ScreenshotEditorDefaultToolIndex);
        Assert.Equal(EditTool.Select, fixture.Settings.ScreenshotEditorDefaultTool);

        vm.ScreenshotEditorDefaultToolIndex = 4;
        Assert.Equal(EditTool.Arrow, fixture.Settings.ScreenshotEditorDefaultTool);

        var reopened = fixture.CreateViewModel();
        Realize(reopened, SettingsSectionKind.Screenshot);
        Assert.Equal(4, reopened.ScreenshotEditorDefaultToolIndex);
    }

    [Fact]
    public void ScreenshotEditorDefaultToolCombo_HasAccessibleNameAndFriendlyToolChoices()
    {
        var combo = Assert.Single(SettingsMarkup.Elements("ScreenshotSettingsSection.xaml"),
            element => element.Attr("AutomationProperties.AutomationId") == "ScreenshotEditorDefaultToolComboBox");

        Assert.Equal("ComboBox", combo.Name.LocalName);
        Assert.Equal("Default screenshot editor tool", combo.Attr("AutomationProperties.Name"));
        Assert.Equal(
            "{x:Bind ViewModel.ScreenshotEditorDefaultToolIndex, Mode=TwoWay}",
            combo.Attr("SelectedIndex"));
        Assert.Equal(
            ["Move / select", "Crop", "Rectangle", "Ellipse", "Arrow", "Line", "Pen", "Text", "Counter", "Emoji", "Redact"],
            combo.Elements().Select(item => item.Attr("Content")));
    }

    [Fact]
    public void FirstLoadAndUnrelatedRealizations_DoNotReadExternalState()
    {
        var fixture = new Fixture();
        var vm = fixture.CreateViewModel();

        foreach (var kind in Enum.GetValues<SettingsSectionKind>()
                     .Except([SettingsSectionKind.Uploadcare, SettingsSectionKind.Teleprompter]))
        {
            Realize(vm, kind);
        }

        Assert.Equal(0, fixture.Credentials.Lookups);
        Assert.Equal(0, fixture.Store.TranscriptReads);
        Assert.Equal(0, fixture.Audio.Lookups);
        Assert.Equal(0, fixture.Webcams.Lookups);
        Assert.Equal(0, fixture.Analytics.Lookups);
        Assert.Equal(1, fixture.LaunchAtLogin.Reads);
    }

    [Fact]
    public void FirstRealization_RepairsBindingWriteBacksWithoutPersistingThem()
    {
        var fixture = new Fixture();
        fixture.Settings.Theme = AppTheme.Dark;
        fixture.Settings.FileNameTemplate = "Synthetic {date}";
        fixture.Settings.ImageFormat = ImageFormat.Webp;
        fixture.Settings.ScreenshotScale = 75;
        var vm = fixture.CreateViewModel();
        var writes = fixture.Store.Writes;

        var general = vm.BeginSectionRealization(SettingsSectionKind.General);
        vm.ThemeIndex = -1;
        vm.FileNameTemplate = string.Empty;
        vm.CompleteSectionRealization(general);
        var screenshot = vm.BeginSectionRealization(SettingsSectionKind.Screenshot);
        vm.ScreenshotFormatIndex = -1;
        vm.ScreenshotScale = 0;
        vm.CompleteSectionRealization(screenshot);

        Assert.Equal(writes, fixture.Store.Writes);
        Assert.Equal(2, vm.ThemeIndex);
        Assert.Equal("Synthetic {date}", vm.FileNameTemplate);
        Assert.Equal(2, vm.ScreenshotFormatIndex);
        Assert.Equal(75, vm.ScreenshotScale);
        Assert.Equal(AppTheme.Dark, fixture.Settings.Theme);
        Assert.Equal(ImageFormat.Webp, fixture.Settings.ImageFormat);
        Assert.Equal(75, fixture.Settings.ScreenshotScale);
    }

    [Fact]
    public void Realization_RestoresOnlyItsOwnScalarSettings()
    {
        var fixture = new Fixture();
        var vm = fixture.CreateViewModel();
        fixture.Store.Reads.Clear();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        var scope = vm.BeginSectionRealization(SettingsSectionKind.Gif);
        vm.GifMaxWidth = 1;
        vm.CompleteSectionRealization(scope);

        Assert.DoesNotContain("videoFrameRate", fixture.Store.Reads);
        Assert.DoesNotContain("fileNameTemplate", fixture.Store.Reads);
        Assert.DoesNotContain("uploadcarePublicKey", fixture.Store.Reads);
        Assert.DoesNotContain(nameof(SettingsViewModel.ThemeIndex), changed);
        Assert.Equal(0, fixture.Credentials.Lookups);
        Assert.Equal(0, fixture.Store.TranscriptReads);
    }

    [Fact]
    public void RelevantState_IsReadOnceAndMutationsDoNotEnumerateAgain()
    {
        var fixture = new Fixture();
        fixture.Credentials.HasKey = true;
        fixture.Settings.TeleprompterTranscript = "Synthetic script";
        var vm = fixture.CreateViewModel();

        Realize(vm, SettingsSectionKind.Uploadcare);
        Realize(vm, SettingsSectionKind.Teleprompter);
        Assert.True(vm.HasUploadcareSecretKey);
        Assert.Equal("Synthetic script", vm.TeleprompterTranscript);

        vm.ClearUploadcareSecretKey();
        Realize(vm, SettingsSectionKind.Uploadcare);
        Assert.False(vm.HasUploadcareSecretKey);
        vm.SaveUploadcareSecretKey("synthetic-fixture-only");
        vm.ImportTeleprompterTranscript("Imported synthetic script");
        Realize(vm, SettingsSectionKind.Uploadcare);
        Realize(vm, SettingsSectionKind.Teleprompter);
        Realize(vm, SettingsSectionKind.Screenshot);

        Assert.True(vm.HasUploadcareSecretKey);
        Assert.Equal("Imported synthetic script", vm.TeleprompterTranscript);
        Assert.Equal("Imported synthetic script", fixture.Settings.TeleprompterTranscript);
        Assert.Equal(1, fixture.Credentials.Lookups);
        // The assertion above explicitly reads the persisted text a second time.
        Assert.Equal(2, fixture.Store.TranscriptReads);
        Assert.Equal(1, fixture.Store.EditingTranscriptReads);
        Assert.Equal(1, fixture.Credentials.Saves);
        Assert.Equal(1, fixture.Credentials.Removals);
    }

    [Fact]
    public void OverlappingRealizations_KeepPersistenceSuppressedUntilBothComplete()
    {
        var fixture = new Fixture();
        fixture.Settings.GifMaxWidth = 640;
        fixture.Settings.ScreenshotScale = 75;
        var vm = fixture.CreateViewModel();
        var writes = fixture.Store.Writes;

        var screenshot = vm.BeginSectionRealization(SettingsSectionKind.Screenshot);
        var gif = vm.BeginSectionRealization(SettingsSectionKind.Gif);
        vm.ScreenshotScale = 1;
        vm.GifMaxWidth = 1;
        vm.CompleteSectionRealization(gif);
        vm.ScreenshotScale = 2;
        Assert.Equal(writes, fixture.Store.Writes);
        vm.CompleteSectionRealization(screenshot);
        vm.CompleteSectionRealization(gif);

        Assert.Equal(640, vm.GifMaxWidth);
        Assert.Equal(75, vm.ScreenshotScale);
        Assert.Equal(writes, fixture.Store.Writes);
        vm.ScreenshotScale = 50;
        Assert.Equal(50, fixture.Settings.ScreenshotScale);
    }

    [Fact]
    public void CachedTranscriptEdits_SurviveUnrelatedNavigationAndRelevantRestoration()
    {
        var fixture = new Fixture();
        fixture.Settings.TeleprompterTranscript = "Initial synthetic script";
        var vm = fixture.CreateViewModel();
        Realize(vm, SettingsSectionKind.Teleprompter);
        vm.TeleprompterTranscript = "Edited synthetic script";

        foreach (var kind in new[] { SettingsSectionKind.General, SettingsSectionKind.Video, SettingsSectionKind.Analytics })
        {
            Realize(vm, kind);
            Assert.Equal("Edited synthetic script", vm.TeleprompterTranscript);
        }

        var teleprompter = vm.BeginSectionRealization(SettingsSectionKind.Teleprompter);
        vm.TeleprompterTranscript = string.Empty;
        vm.CompleteSectionRealization(teleprompter);

        Assert.Equal("Edited synthetic script", vm.TeleprompterTranscript);
        Assert.Equal(1, fixture.Store.TranscriptReads);
    }

    [Fact]
    public void ConfirmEditorEscape_LoadsWithGeneralAndPersistsChanges()
    {
        var fixture = new Fixture();
        var vm = fixture.CreateViewModel();
        Realize(vm, SettingsSectionKind.General);

        Assert.True(vm.ConfirmEditorEscape);

        vm.ConfirmEditorEscape = false;
        Assert.False(fixture.Settings.ConfirmEditorEscape);

        vm.ResetAllSettings();
        Assert.True(vm.ConfirmEditorEscape);
        Assert.True(fixture.Settings.ConfirmEditorEscape);
    }

    [Fact]
    public void Reset_DoesNotReadExternalStateOrRestoreOldTranscript()
    {
        var fixture = new Fixture();
        fixture.Credentials.HasKey = true;
        fixture.Settings.TeleprompterTranscript = "Synthetic script";
        var vm = fixture.CreateViewModel();
        Realize(vm, SettingsSectionKind.Teleprompter);
        Realize(vm, SettingsSectionKind.Uploadcare);
        vm.TeleprompterTranscript = "Edited script";

        vm.ResetAllSettings();
        Realize(vm, SettingsSectionKind.Teleprompter);
        Realize(vm, SettingsSectionKind.Uploadcare);

        Assert.Empty(vm.TeleprompterTranscript);
        Assert.False(vm.HasUploadcareSecretKey);
        Assert.Equal(1, fixture.Credentials.Lookups);
        Assert.Equal(1, fixture.Store.TranscriptReads);
        Assert.Equal(1, fixture.LaunchAtLogin.Sets);
    }

    [Fact]
    public void Reopening_ReadsCurrentSettingsAndExternalState()
    {
        var fixture = new Fixture();
        var first = fixture.CreateViewModel();
        Realize(first, SettingsSectionKind.Uploadcare);
        Realize(first, SettingsSectionKind.Teleprompter);
        first.NotifyClosed();
        fixture.Settings.Theme = AppTheme.Light;
        fixture.Settings.TeleprompterTranscript = "Replacement synthetic script";
        fixture.Credentials.HasKey = true;

        var reopened = fixture.CreateViewModel();
        Assert.Equal(1, reopened.ThemeIndex);
        Realize(reopened, SettingsSectionKind.Uploadcare);
        Realize(reopened, SettingsSectionKind.Teleprompter);

        Assert.True(reopened.HasUploadcareSecretKey);
        Assert.Equal("Replacement synthetic script", reopened.TeleprompterTranscript);
        Assert.Equal(2, fixture.Credentials.Lookups);
        Assert.Equal(2, fixture.Store.TranscriptReads);
    }

    [Fact]
    public void ExternalLoadFailures_AreExplicitAndDoNotPersistEmptyDefaults()
    {
        var fixture = new Fixture();
        fixture.Store.FailTranscriptRead = true;
        fixture.Credentials.FailLookup = true;
        var vm = fixture.CreateViewModel();
        var writes = fixture.Store.Writes;

        Realize(vm, SettingsSectionKind.Uploadcare);
        Realize(vm, SettingsSectionKind.Teleprompter);

        Assert.NotNull(vm.UploadcareLoadError);
        Assert.Equal(vm.UploadcareLoadError, vm.UploadcareSecretKeyStatus);
        Assert.NotNull(vm.TeleprompterTranscriptLoadError);
        Assert.False(vm.IsTeleprompterTranscriptLoaded);
        Assert.False(vm.TeleprompterTranscriptEditorEnabled);
        Assert.Equal(writes, fixture.Store.Writes);
        vm.SaveUploadcareSecretKey("synthetic-fixture-only");
        vm.ImportTeleprompterTranscript("Imported recovery script");
        Assert.Null(vm.UploadcareLoadError);
        Assert.Null(vm.TeleprompterTranscriptLoadError);
        Assert.True(vm.IsTeleprompterTranscriptLoaded);
    }

    [Fact]
    public async Task ClosingDuringRealizationAndDelayedLaunchRead_DiscardsLateWork()
    {
        var fixture = new Fixture();
        var launchCompletion = new TaskCompletionSource<LaunchAtLoginState>();
        fixture.LaunchAtLogin.ReadResult = launchCompletion.Task;
        var vm = fixture.CreateViewModel();
        var uploadcare = vm.BeginSectionRealization(SettingsSectionKind.Uploadcare);
        var teleprompter = vm.BeginSectionRealization(SettingsSectionKind.Teleprompter);
        var notifications = 0;
        vm.PropertyChanged += (_, _) => notifications++;
        vm.NotifyClosed();
        var writes = fixture.Store.Writes;

        vm.CompleteSectionRealization(teleprompter);
        vm.CompleteSectionRealization(uploadcare);
        launchCompletion.SetResult(LaunchAtLoginState.Enabled);
        await vm.LaunchAtLoginInitialization;
        await vm.EnsureAnalyticsInitializedAsync();
        await vm.EnsureMediaDevicesInitializedAsync();

        Assert.Equal(0, notifications);
        Assert.Equal(writes, fixture.Store.Writes);
        Assert.Equal(0, fixture.Credentials.Lookups);
        Assert.Equal(0, fixture.Store.TranscriptReads);
        vm.TeleprompterTranscript = "Late import";
        vm.ThemeIndex = 2;
        vm.SaveUploadcareSecretKey("late-synthetic-fixture");
        Assert.Equal(writes, fixture.Store.Writes);
        Assert.Equal(0, fixture.Credentials.Saves);
    }

    [Fact]
    public async Task MediaAndAnalytics_RemainLazyAndIdempotent()
    {
        var fixture = new Fixture();
        var vm = fixture.CreateViewModel();
        await vm.EnsureAnalyticsInitializedAsync();
        var analyticsLookups = fixture.Analytics.Lookups;
        await vm.EnsureAnalyticsInitializedAsync();
        await vm.EnsureMediaDevicesInitializedAsync();
        await vm.EnsureMediaDevicesInitializedAsync();
        Realize(vm, SettingsSectionKind.Video);

        Assert.True(analyticsLookups > 0);
        Assert.Equal(analyticsLookups, fixture.Analytics.Lookups);
        Assert.Equal(1, fixture.Audio.Lookups);
        Assert.Equal(1, fixture.Webcams.Lookups);
        Assert.Equal(0, fixture.Credentials.Lookups);
        Assert.Equal(0, fixture.Store.TranscriptReads);
    }

    [Fact]
    public void PendingTranscript_SurvivesNavigationAndRestorationWithoutDiskRereads()
    {
        var fixture = new Fixture();
        fixture.Settings.TeleprompterTranscript = "Initial synthetic script";
        var scheduler = new ManualTranscriptSaveScheduler();
        var vm = fixture.CreateViewModel(scheduler);
        Realize(vm, SettingsSectionKind.Teleprompter);
        var writes = fixture.Store.Writes;
        vm.TeleprompterTranscript = "Newest synthetic edit";
        Realize(vm, SettingsSectionKind.General);
        Realize(vm, SettingsSectionKind.Video);
        var teleprompter = vm.BeginSectionRealization(SettingsSectionKind.Teleprompter);
        vm.TeleprompterTranscript = string.Empty;
        vm.CompleteSectionRealization(teleprompter);

        Assert.Equal("Newest synthetic edit", vm.TeleprompterTranscript);
        Assert.Equal(writes, fixture.Store.Writes);
        Assert.Equal(1, fixture.Store.TranscriptReads);
        scheduler.Fire();
        Assert.Equal(writes + 1, fixture.Store.Writes);
        Assert.Equal("Newest synthetic edit", fixture.Store.Get("teleprompterTranscript", string.Empty));
    }

    [Fact]
    public void ResetAndClose_CancelDebounceAndNeverRestoreOldText()
    {
        var fixture = new Fixture();
        var scheduler = new ManualTranscriptSaveScheduler();
        var vm = fixture.CreateViewModel(scheduler);
        Realize(vm, SettingsSectionKind.Teleprompter);
        vm.TeleprompterTranscript = "Pending synthetic edit";
        vm.ResetAllSettings();
        var writes = fixture.Store.Writes;
        scheduler.Fire();
        vm.NotifyClosed();

        Assert.Equal(writes, fixture.Store.Writes);
        Assert.Empty(vm.TeleprompterTranscript);
        Assert.Empty(fixture.Store.Get("teleprompterTranscript", string.Empty));
    }

    [Fact]
    public void Closing_FlushesOnlyAcceptedPendingTranscriptOnce()
    {
        var fixture = new Fixture();
        var scheduler = new ManualTranscriptSaveScheduler();
        var vm = fixture.CreateViewModel(scheduler);
        Realize(vm, SettingsSectionKind.Teleprompter);
        vm.TeleprompterTranscript = "Accepted synthetic edit";
        var scope = vm.BeginSectionRealization(SettingsSectionKind.Teleprompter);
        vm.TeleprompterTranscript = string.Empty;
        var writes = fixture.Store.Writes;

        vm.NotifyClosed();
        vm.CompleteSectionRealization(scope);
        vm.NotifyClosed();
        scheduler.Fire();
        vm.ImportTeleprompterTranscript("Late synthetic import");

        Assert.Equal(writes + 1, fixture.Store.Writes);
        Assert.Equal("Accepted synthetic edit", fixture.Store.Get("teleprompterTranscript", string.Empty));
    }

    [Fact]
    public void CachedSectionEdits_AreNotDroppedDuringUnrelatedRealization()
    {
        var fixture = new Fixture();
        var scheduler = new ManualTranscriptSaveScheduler();
        var vm = fixture.CreateViewModel(scheduler);
        Realize(vm, SettingsSectionKind.General);
        Realize(vm, SettingsSectionKind.Teleprompter);
        var themes = 0;
        vm.ThemeChanged += () => themes++;
        var video = vm.BeginSectionRealization(SettingsSectionKind.Video);

        vm.ThemeIndex = 2;
        vm.FileNameTemplate = "Edited synthetic template";
        vm.TeleprompterTranscript = "Edit during rapid navigation";
        vm.VideoFrameRate = 0;
        vm.CompleteSectionRealization(video);
        scheduler.Fire();

        Assert.Equal(AppTheme.Dark, fixture.Settings.Theme);
        Assert.Equal("Edited synthetic template", fixture.Settings.FileNameTemplate);
        Assert.Equal("Edit during rapid navigation", fixture.Store.Get("teleprompterTranscript", string.Empty));
        Assert.Equal(30, fixture.Settings.VideoFrameRate);
        Assert.Equal(1, themes);
        Assert.Equal(1, fixture.Store.TranscriptReads);
        Assert.Equal(0, fixture.Credentials.Lookups);
    }

    [Fact]
    public void SameSectionOverlappingScopes_RemainReferenceCounted()
    {
        var fixture = new Fixture();
        var vm = fixture.CreateViewModel();
        var first = vm.BeginSectionRealization(SettingsSectionKind.Screenshot);
        var second = vm.BeginSectionRealization(SettingsSectionKind.Screenshot);
        var writes = fixture.Store.Writes;

        vm.ScreenshotScale = 1;
        vm.CompleteSectionRealization(first);
        vm.ScreenshotScale = 2;
        Assert.Equal(writes, fixture.Store.Writes);
        vm.CompleteSectionRealization(second);
        second.Dispose();
        first.Dispose();
        vm.ScreenshotScale = 75;

        Assert.Equal(75, fixture.Settings.ScreenshotScale);
        Assert.Equal(writes + 1, fixture.Store.Writes);
    }

    [Fact]
    public void ImportDuringFirstRealization_IsAnIntentionalMutation()
    {
        var fixture = new Fixture();
        var scheduler = new ManualTranscriptSaveScheduler();
        var vm = fixture.CreateViewModel(scheduler);
        var scope = vm.BeginSectionRealization(SettingsSectionKind.Teleprompter);

        vm.ImportTeleprompterTranscript("Imported synthetic script");
        vm.TeleprompterTranscript = string.Empty;
        vm.CompleteSectionRealization(scope);
        scheduler.Fire();

        Assert.Equal("Imported synthetic script", vm.TeleprompterTranscript);
        Assert.Equal("Imported synthetic script", fixture.Store.Get("teleprompterTranscript", string.Empty));
        Assert.Equal(0, fixture.Store.TranscriptReads);
    }

    [Fact]
    public void ScalarRestoreFailure_ReleasesItsRealizationScope()
    {
        var fixture = new Fixture();
        var vm = fixture.CreateViewModel();
        var scope = vm.BeginSectionRealization(SettingsSectionKind.Gif);
        fixture.Store.FailReadKey = "gifMaxWidth";

        Assert.Throws<IOException>(() => vm.CompleteSectionRealization(scope));
        fixture.Store.FailReadKey = null;
        vm.GifMaxWidth = 640;

        Assert.Equal(640, fixture.Settings.GifMaxWidth);
        Realize(vm, SettingsSectionKind.Gif);
    }

    [Fact]
    public void NestedRestoration_PreservesTheOuterLoadingGuard()
    {
        var fixture = new Fixture();
        var vm = fixture.CreateViewModel();
        var general = vm.BeginSectionRealization(SettingsSectionKind.General);
        var screenshot = vm.BeginSectionRealization(SettingsSectionKind.Screenshot);
        vm.ThemeIndex = -1;
        vm.ScreenshotScale = 1;
        var writes = fixture.Store.Writes;
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SettingsViewModel.ThemeIndex))
            {
                vm.CompleteSectionRealization(screenshot);
                vm.GifFrameRate = 20;
            }
        };

        vm.CompleteSectionRealization(general);

        Assert.Equal(writes, fixture.Store.Writes);
        Assert.Equal(10, fixture.Settings.GifFrameRate);
    }

    [Fact]
    public async Task ClosingDuringMediaEnumeration_DiscardsAllLateUpdates()
    {
        var fixture = new Fixture();
        var audio = new TaskCompletionSource<IReadOnlyList<AudioInputDevice>>();
        var webcams = new TaskCompletionSource<IReadOnlyList<WebcamDeviceInfo>>();
        fixture.Audio.ResultTask = audio.Task;
        fixture.Webcams.ResultTask = webcams.Task;
        var vm = fixture.CreateViewModel();
        var initialization = vm.EnsureMediaDevicesInitializedAsync();
        await fixture.Audio.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        vm.NotifyClosed();
        var notifications = 0;
        var writes = fixture.Store.Writes;
        vm.PropertyChanged += (_, _) => notifications++;

        audio.SetResult([new("synthetic-mic", "Fixture microphone")]);
        webcams.SetResult([new("synthetic-camera", "Fixture camera")]);
        await initialization;

        Assert.Equal(0, notifications);
        Assert.Equal(writes, fixture.Store.Writes);
        Assert.Empty(vm.Microphones);
        Assert.Empty(vm.Webcams);
    }

    [Fact]
    public async Task LateInitialLaunchState_DoesNotOverwriteANewerToggleMutation()
    {
        var fixture = new Fixture();
        var initialState = new TaskCompletionSource<LaunchAtLoginState>();
        fixture.LaunchAtLogin.ReadResult = initialState.Task;
        var vm = fixture.CreateViewModel();
        Realize(vm, SettingsSectionKind.General);
        vm.LaunchAtLogin = true;

        initialState.SetResult(LaunchAtLoginState.DisabledByPolicy);
        await vm.LaunchAtLoginInitialization;

        Assert.True(vm.LaunchAtLogin);
        Assert.True(vm.LaunchAtLoginToggleEnabled);
        Assert.True(fixture.Settings.LaunchAtLogin);
        Assert.Empty(vm.LaunchAtLoginNote);
    }

    [Fact]
    public async Task LaunchReconciliationFailure_IsExplicit()
    {
        var fixture = new Fixture();
        fixture.LaunchAtLogin.ReadResult = Task.FromException<LaunchAtLoginState>(
            new InvalidOperationException("Synthetic launch-at-login failure."));
        var vm = fixture.CreateViewModel();
        await vm.LaunchAtLoginInitialization;

        Assert.Contains("Couldn't", vm.LaunchAtLoginNote);
    }

    [Fact]
    public async Task OverlappingLaunchMutations_AreAppliedInRequestedOrder()
    {
        var fixture = new Fixture();
        var firstMutation = new TaskCompletionSource<LaunchAtLoginState>();
        fixture.LaunchAtLogin.NextSetResult = firstMutation.Task;
        var vm = fixture.CreateViewModel();
        vm.LaunchAtLogin = true;
        vm.LaunchAtLogin = false;

        Assert.Equal([true], fixture.LaunchAtLogin.Requests);
        firstMutation.SetResult(LaunchAtLoginState.Enabled);
        await vm.LaunchAtLoginMutation;

        Assert.Equal([true, false], fixture.LaunchAtLogin.Requests);
        Assert.False(vm.LaunchAtLogin);
        Assert.False(fixture.Settings.LaunchAtLogin);
    }

    [Fact]
    public async Task Closing_DiscardsQueuedLaunchMutations()
    {
        var fixture = new Fixture();
        var firstMutation = new TaskCompletionSource<LaunchAtLoginState>();
        fixture.LaunchAtLogin.NextSetResult = firstMutation.Task;
        var vm = fixture.CreateViewModel();
        vm.LaunchAtLogin = true;
        vm.LaunchAtLogin = false;
        vm.NotifyClosed();
        var writes = fixture.Store.Writes;

        firstMutation.SetResult(LaunchAtLoginState.Enabled);
        await vm.LaunchAtLoginMutation;

        Assert.Equal([true], fixture.LaunchAtLogin.Requests);
        Assert.Equal(writes, fixture.Store.Writes);
    }

    [Fact]
    public async Task AnalyticsChart_FiltersRescaleBarsWithoutChangingTotalsOrSelectedDayDetails()
    {
        var fixture = new Fixture();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero));
        var analytics = new ClipAnalyticsService(fixture.Store, clock);
        analytics.RecordCapture(CaptureType.Screenshot);
        analytics.RecordCapture(CaptureType.Screenshot);
        analytics.RecordCapture(CaptureType.Video);
        analytics.RecordCapture(CaptureType.Gif);
        clock.Advance(TimeSpan.FromDays(1));
        analytics.RecordCapture(CaptureType.Video);
        var vm = fixture.CreateViewModel(analytics: analytics);
        await vm.EnsureAnalyticsInitializedAsync();
        vm.SelectedAnalyticsDay = vm.AnalyticsDays[^2];
        var details = vm.SelectedAnalyticsDaySummary;
        var selectedLabel = vm.SelectedAnalyticsDay.FullDateLabel;
        var weekday = vm.SelectedWeekday;
        var hour = vm.SelectedHour;

        Assert.Equal(7, vm.AnalyticsDays.Count);
        Assert.Equal(4, vm.AnalyticsChartMaximum);
        var peak = vm.AnalyticsDays[^2];
        Assert.Equal(200, peak.ScreenshotHeight + peak.VideoHeight + peak.GifHeight);
        Assert.Equal(4, peak.TotalCount);
        Assert.Equal(5, vm.AnalyticsCaptureTotal);

        vm.ShowScreenshotsInChart = false;

        Assert.Equal(2, vm.AnalyticsChartMaximum);
        Assert.Equal(0, vm.SelectedAnalyticsDay!.ScreenshotHeight);
        Assert.Equal(200, vm.SelectedAnalyticsDay.VideoHeight + vm.SelectedAnalyticsDay.GifHeight);
        Assert.Equal(selectedLabel, vm.SelectedAnalyticsDay.FullDateLabel);
        Assert.Equal(details, vm.SelectedAnalyticsDaySummary);
        Assert.Equal((2, 2, 1, 5), (vm.AnalyticsScreenshotTotal, vm.AnalyticsVideoTotal, vm.AnalyticsGifTotal, vm.AnalyticsCaptureTotal));
        Assert.Same(weekday, vm.SelectedWeekday);
        Assert.Same(hour, vm.SelectedHour);

        vm.ShowVideosInChart = false;
        vm.ShowGifsInChart = false;

        Assert.True(vm.ShowGifsInChart);
        Assert.Equal(1, vm.AnalyticsChartMaximum);
        Assert.Equal(200, vm.SelectedAnalyticsDay!.GifHeight);
        Assert.Equal(details, vm.SelectedAnalyticsDaySummary);
    }

    [Fact]
    public async Task AnalyticsChart_RangeChangesPreserveSelectionOrFallBackToToday_AndUseSparseLabels()
    {
        var fixture = new Fixture();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 6, 20, 9, 0, 0, TimeSpan.Zero));
        var analytics = new ClipAnalyticsService(fixture.Store, clock);
        analytics.RecordCapture(CaptureType.Gif);
        clock.Advance(TimeSpan.FromDays(24));
        analytics.RecordCapture(CaptureType.Screenshot);
        var vm = fixture.CreateViewModel(analytics: analytics);
        await vm.EnsureAnalyticsInitializedAsync();
        var todayLabel = vm.SelectedAnalyticsDay!.FullDateLabel;
        vm.SelectedAnalyticsDay = vm.AnalyticsDays[1];
        var selectedLabel = vm.SelectedAnalyticsDay.FullDateLabel;

        vm.AnalyticsRangeIndex = 1;

        Assert.Equal(30, vm.AnalyticsDays.Count);
        Assert.Equal(2, vm.AnalyticsCaptureTotal);
        Assert.Equal(selectedLabel, vm.SelectedAnalyticsDay!.FullDateLabel);
        Assert.Equal([0, 5, 10, 15, 20, 25, 29], vm.AnalyticsDays
            .Select((day, index) => (day, index))
            .Where(item => !string.IsNullOrEmpty(item.day.DateLabel))
            .Select(item => item.index));
        Assert.All(vm.AnalyticsDays, day => Assert.Contains(day.FullDateLabel, day.AccessibilitySummary));

        vm.SelectedAnalyticsDay = vm.AnalyticsDays[0];
        vm.AnalyticsRangeIndex = 0;

        Assert.Equal(7, vm.AnalyticsDays.Count);
        Assert.Equal(1, vm.AnalyticsCaptureTotal);
        Assert.Equal(todayLabel, vm.SelectedAnalyticsDay!.FullDateLabel);
        Assert.All(vm.AnalyticsDays, day => Assert.Equal(3, day.DateLabel.Length));
    }

    [Fact]
    public async Task AnalyticsChart_InsightSelectionsSurviveRangeChangeAndReset_WithoutStaleDetails()
    {
        var fixture = new Fixture();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero));
        var analytics = new ClipAnalyticsService(fixture.Store, clock);
        analytics.RecordCapture(CaptureType.Screenshot);
        clock.Advance(TimeSpan.FromHours(5));
        analytics.RecordCapture(CaptureType.Video);
        var vm = fixture.CreateViewModel(analytics: analytics);
        await vm.EnsureAnalyticsInitializedAsync();
        vm.SelectedWeekday = vm.WeekdayBreakdown.Single(day => day.FullDayLabel == "Monday");
        vm.SelectedHour = vm.HourlyBreakdown[14];
        var dayLabel = vm.SelectedWeekday.FullDayLabel;
        var hourLabel = vm.SelectedHour.HourLabel;

        Assert.Equal(7, vm.WeekdayBreakdown.Count);
        Assert.Equal(24, vm.HourlyBreakdown.Count);
        Assert.Equal(["00", "06", "12", "18", "23"], vm.HourlyBreakdown
            .Select(hour => hour.AxisLabel).Where(label => label.Length > 0));
        Assert.Equal(100, vm.SelectedWeekday.Height);
        Assert.Equal(100, vm.SelectedHour.Height);
        Assert.Contains("1 capture all-time", vm.SelectedHourSummary);

        vm.AnalyticsRangeIndex = 1;
        Assert.Equal(dayLabel, vm.SelectedWeekday!.FullDayLabel);
        Assert.Equal(hourLabel, vm.SelectedHour!.HourLabel);
        Assert.Contains("1 capture all-time", vm.SelectedHourSummary);

        vm.ResetAnalytics();

        Assert.Equal(0, vm.AnalyticsCaptureTotal);
        Assert.Equal(0, vm.LifetimeCaptureTotal);
        Assert.Equal(0, vm.AnalyticsChartMaximum);
        Assert.Equal(dayLabel, vm.SelectedWeekday!.FullDayLabel);
        Assert.Equal(hourLabel, vm.SelectedHour!.HourLabel);
        Assert.Contains("0 captures", vm.SelectedAnalyticsDaySummary);
        Assert.Contains("0 captures", vm.SelectedWeekdaySummary);
        Assert.Contains("0 captures all-time", vm.SelectedHourSummary);
        Assert.All(vm.AnalyticsDays, day => Assert.Equal(0, day.ScreenshotHeight + day.VideoHeight + day.GifHeight));
        Assert.All(vm.WeekdayBreakdown, day => Assert.Equal(0, day.Height));
        Assert.All(vm.HourlyBreakdown, hour => Assert.Equal(0, hour.Height));
    }

    [Fact]
    public async Task AnalyticsChart_SelectionChangesNotifyDetailBindings_AndEmptyDataHasFiniteHeights()
    {
        var fixture = new Fixture();
        var analytics = new ClipAnalyticsService(fixture.Store,
            new ManualTimeProvider(new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero)));
        var vm = fixture.CreateViewModel(analytics: analytics);
        await vm.EnsureAnalyticsInitializedAsync();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.SelectedAnalyticsDay = vm.AnalyticsDays[0];
        vm.SelectedWeekday = vm.WeekdayBreakdown[1];
        vm.SelectedHour = vm.HourlyBreakdown[1];

        Assert.Contains(nameof(SettingsViewModel.SelectedAnalyticsDaySummary), changed);
        Assert.Contains(nameof(SettingsViewModel.SelectedWeekdaySummary), changed);
        Assert.Contains(nameof(SettingsViewModel.SelectedHourSummary), changed);
        Assert.Equal("No captures for the shown types.", vm.AnalyticsChartScaleLabel);
        Assert.All(vm.AnalyticsDays, day => Assert.Equal(0, day.ScreenshotHeight + day.VideoHeight + day.GifHeight));
        Assert.All(vm.WeekdayBreakdown, day => Assert.Equal(0, day.Height));
        Assert.All(vm.HourlyBreakdown, hour => Assert.Equal(0, hour.Height));
        Assert.Contains("0 captures", vm.SelectedAnalyticsDaySummary);
    }

    [Fact]
    public void AnalyticsCharts_StretchWithoutHorizontalScrolling_AndExposeSelectableDetails()
    {
        var elements = SettingsMarkup.Elements("AnalyticsSettingsSection.xaml");
        var charts = elements.Where(element => element.Name.LocalName == "AnalyticsChart").ToList();
        Assert.Equal(3, charts.Count);
        Assert.Equal(
            ["DailyCapturesChart", "WeekdayCapturesChart", "HourlyCapturesChart"],
            charts.Select(chart => chart.Attr("AutomationProperties.AutomationId")));
        Assert.All(charts, chart =>
        {
            Assert.Equal("{StaticResource AnalyticsChartStyle}", chart.Attr("Style"));
            Assert.NotNull(chart.Attr("AutomationProperties.Name"));
            Assert.Contains("arrow", chart.Attr("AutomationProperties.HelpText")!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Mode=TwoWay", chart.Attr("SelectedItem")!);
            Assert.DoesNotContain(chart.Descendants(), element => element.Attr("Width") is not null);
            Assert.DoesNotContain(chart.Ancestors(), element => element.Attr("MaxWidth") is not null);
        });
        Assert.Single(elements, element => element.Name.LocalName == "AnalyticsChartPanel");
        Assert.DoesNotContain(elements, element => element.Name.LocalName == "ScrollViewer");
        Assert.Contains(elements, element =>
            element.Attr("Property") == "ScrollViewer.HorizontalScrollMode" && element.Attr("Value") == "Disabled");
        Assert.Contains(elements, element =>
            element.Attr("Property") == "HorizontalContentAlignment" && element.Attr("Value") == "Stretch");
        Assert.All(
            new[] { "DailyCaptureDetails", "WeekdayCaptureDetails", "HourlyCaptureDetails" },
            id =>
            {
                var details = Assert.Single(elements, element => element.Attr("AutomationProperties.AutomationId") == id);
                Assert.Contains("Summary, Mode=OneWay", details.Attr("Text")!);
                Assert.Equal("Wrap", details.Attr("TextWrapping"));
            });
    }

    [Fact]
    public void ForeignAndInvalidScopes_AreRejectedWithoutReleasingTheOwnersGuard()
    {
        var fixture = new Fixture();
        var first = fixture.CreateViewModel();
        var second = fixture.CreateViewModel();
        var scope = first.BeginSectionRealization(SettingsSectionKind.Gif);
        var writes = fixture.Store.Writes;

        Assert.Throws<ArgumentException>(() => second.CompleteSectionRealization(scope));
        Assert.Throws<ArgumentOutOfRangeException>(() => first.BeginSectionRealization((SettingsSectionKind)(-1)));
        first.GifMaxWidth = 1;
        Assert.Equal(writes, fixture.Store.Writes);
        first.CompleteSectionRealization(scope);
        first.GifMaxWidth = 640;
        Assert.Equal(640, fixture.Settings.GifMaxWidth);
    }

    [Fact]
    public void VideoSettingsSection_GroupsRecordingAndAdvancedControlsInOrder()
    {
        const string HeadingLevel = "AutomationProperties.HeadingLevel";
        var elements = SettingsMarkup.Elements("VideoSettingsSection.xaml");
        var headings = elements
            .Where(element => element.Attr(HeadingLevel) is not null)
            .Select(element => (
                Text: element.Attr("Text") ?? element.Attr("Header"),
                Level: element.Attr(HeadingLevel)))
            .ToList();

        Assert.Equal(
            [
                ("Video", "Level1"),
                ("Video quality", "Level2"),
                ("Audio", "Level2"),
                ("Webcam overlay", "Level2"),
                ("Before recording", "Level2"),
                ("During recording", "Level2"),
                ("After recording", "Level2"),
                ("Advanced", "Level2"),
            ],
            headings);

        Assert.Equal(
            ["VideoFrameRateNumberBox", "VideoCodecCombo"],
            AutomationIdsInGroup(elements, "Video quality"));
        Assert.Equal(
            ["ShowVideoCapturePickerToggle", "VideoCountdownDurationNumberBox", "VideoCountdownToggle"],
            AutomationIdsInGroup(elements, "Before recording"));
        Assert.Equal(
            ["KeepDisplayAwakeToggle", "VideoTimeLimitNumberBox"],
            AutomationIdsInGroup(elements, "During recording"));
        Assert.Equal(
            ["ShowVideoCapturePickerAfterCaptureToggle", "ShowVideoTrimmerToggle", "CopyVideoToggle"],
            AutomationIdsInGroup(elements, "After recording"));

        var advanced = Assert.Single(elements, element =>
            element.Name.LocalName == "SettingsExpander"
            && element.Attr("AutomationProperties.AutomationId") == "AdvancedVideoSettingsExpander");
        Assert.Equal("Advanced video settings", advanced.Attr("AutomationProperties.Name"));
        Assert.Equal("False", advanced.Attr("IsExpanded"));
        Assert.Equal(
            ["UseGpuRecordingPipelineToggle", "VideoEncoderBackendCombo", "AudioOffsetNumberBox", "AudioOffsetResetButton"],
            advanced.Descendants()
                .Select(element => element.Attr("AutomationProperties.AutomationId"))
                .OfType<string>()
                .ToList());

        var automationIds = SettingsMarkup.AutomationIds("VideoSettingsSection.xaml");
        Assert.Equal(25, automationIds.Count);
        Assert.Equal(automationIds.Count, automationIds.Distinct(StringComparer.Ordinal).Count());
    }

    private static IReadOnlyList<string> AutomationIdsInGroup(
        IReadOnlyList<System.Xml.Linq.XElement> elements,
        string headingText)
    {
        const string HeadingLevel = "AutomationProperties.HeadingLevel";
        var heading = elements.Single(element =>
            element.Name.LocalName == "TextBlock"
            && element.Attr("Text") == headingText);
        return
        [
            .. heading.ElementsAfterSelf()
                .TakeWhile(element => element.Attr(HeadingLevel) is null)
                .SelectMany(element => element.DescendantsAndSelf())
                .Select(element => element.Attr("AutomationProperties.AutomationId"))
                .OfType<string>(),
        ];
    }

    private static void Realize(SettingsViewModel vm, SettingsSectionKind kind)
        => vm.CompleteSectionRealization(vm.BeginSectionRealization(kind));

    private sealed class Fixture
    {
        public CountingSettingsStore Store { get; } = new();
        public CountingCredentials Credentials { get; } = new();
        public FakeLaunchAtLogin LaunchAtLogin { get; } = new();
        public CountingAudio Audio { get; } = new();
        public CountingWebcams Webcams { get; } = new();
        public CountingAnalytics Analytics { get; } = new();
        public CaptureSettings Settings { get; }

        public Fixture() => Settings = new CaptureSettings(Store);

        public SettingsViewModel CreateViewModel(
            ITeleprompterTranscriptSaveScheduler? scheduler = null,
            IClipAnalyticsService? analytics = null,
            Func<TemporaryFilesSummary>? getTemporaryFilesSummary = null,
            Func<TemporaryFilesPurgeResult>? purgeTemporaryFiles = null) => new(
            Settings, new FakeHotKeys(), LaunchAtLogin, Audio, Webcams, new FakeStorage(),
            analytics ?? Analytics, Credentials, dispatcherQueue: null, transcriptSaveScheduler: scheduler,
            getTemporaryFilesSummary: getTemporaryFilesSummary, purgeTemporaryFiles: purgeTemporaryFiles);
    }

    private sealed class CountingSettingsStore : ISettingsService, ILargeTextSettingsService
    {
        private readonly Dictionary<string, object?> _values = new();
        private AppTheme _theme;
        public List<string> Reads { get; } = [];
        public int Writes { get; private set; }
        public int TranscriptReads { get; private set; }
        public int EditingTranscriptReads { get; private set; }
        public bool FailTranscriptRead { get; set; }
        public string? FailReadKey { get; set; }
        public AppTheme Theme
        {
            get => _theme;
            set { _theme = value; Writes++; }
        }
        public string SaveDirectory { get; set; } = string.Empty;
        public T Get<T>(string key, T defaultValue)
        {
            Reads.Add(key);
            if (key == FailReadKey)
            {
                throw new IOException("Synthetic scalar read failure.");
            }

            if (!_values.TryGetValue(key, out var value))
            {
                return defaultValue;
            }

            return value is string text && typeof(T).IsEnum
                ? (T)Enum.Parse(typeof(T), text)
                : (T)value!;
        }
        public void Set<T>(string key, T value) { _values[key] = value; Writes++; }
        public string GetLargeText(string key, string defaultValue) => ReadLargeText(key, defaultValue, fail: false);
        public string GetLargeTextForEditing(string key, string defaultValue)
        {
            EditingTranscriptReads++;
            return ReadLargeText(key, defaultValue, FailTranscriptRead);
        }
        private string ReadLargeText(string key, string defaultValue, bool fail)
        {
            TranscriptReads++;
            if (fail)
            {
                throw new IOException("Synthetic read failure.");
            }

            return Get(key, defaultValue);
        }
        public void SetLargeText(string key, string value) => Set(key, value);
    }

    private sealed class CountingCredentials : IUploadcareCredentialStore
    {
        public int Lookups { get; private set; }
        public int Saves { get; private set; }
        public int Removals { get; private set; }
        public bool HasKey { get; set; }
        public bool FailLookup { get; set; }
        public string? GetSecretKey() => throw new InvalidOperationException("Settings must not retrieve secret contents.");
        public bool HasSecretKey()
        {
            Lookups++;
            if (FailLookup)
            {
                throw new InvalidOperationException("Synthetic credential lookup failure.");
            }
            return HasKey;
        }
        public void SaveSecretKey(string secretKey) { Saves++; HasKey = true; }
        public void RemoveSecretKey() { Removals++; HasKey = false; }
    }

    private sealed class FakeLaunchAtLogin : ILaunchAtLoginService
    {
        public int Reads { get; private set; }
        public int Sets { get; private set; }
        public List<bool> Requests { get; } = [];
        public Task<LaunchAtLoginState>? NextSetResult { get; set; }
        public Task<LaunchAtLoginState> ReadResult { get; set; } = Task.FromResult(LaunchAtLoginState.Disabled);
        public Task<LaunchAtLoginState> GetStateAsync() { Reads++; return ReadResult; }
        public Task<LaunchAtLoginState> SetEnabledAsync(bool enabled)
        {
            Sets++;
            Requests.Add(enabled);
            if (NextSetResult is { } result)
            {
                NextSetResult = null;
                return result;
            }

            return Task.FromResult(enabled ? LaunchAtLoginState.Enabled : LaunchAtLoginState.Disabled);
        }
    }

    private sealed class CountingAudio : IAudioDeviceService
    {
        public int Lookups { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<AudioInputDevice>>? ResultTask { get; set; }
        public IReadOnlyList<AudioInputDevice> GetMicrophones()
        {
            Lookups++;
            Started.TrySetResult();
            return ResultTask?.GetAwaiter().GetResult() ?? [];
        }
    }

    private sealed class CountingWebcams : IWebcamDeviceEnumerator
    {
        public int Lookups { get; private set; }
        public Task<IReadOnlyList<WebcamDeviceInfo>>? ResultTask { get; set; }
        public Task<IReadOnlyList<WebcamDeviceInfo>> GetWebcamDevicesAsync()
        {
            Lookups++;
            return ResultTask ?? Task.FromResult<IReadOnlyList<WebcamDeviceInfo>>([]);
        }
    }

    private sealed class CountingAnalytics : IClipAnalyticsService
    {
        public int Lookups { get; private set; }
        public void RecordCapture(CaptureType type) => throw new NotSupportedException();
        public IReadOnlyList<DailyCaptureAnalytics> GetDailyCounts(int days) { Lookups++; return []; }
        public LifetimeCaptureAnalytics GetLifetimeTotals() { Lookups++; return new(0, 0, 0); }
        public IReadOnlyList<WeekdayCaptureTotal> GetWeekdayTotals(int days) { Lookups++; return []; }
        public WeekdayCaptureTotal? GetBusiestWeekday(int days) { Lookups++; return null; }
        public IReadOnlyList<HourCaptureTotal> GetHourlyTotals() { Lookups++; return []; }
        public HourCaptureTotal? GetMostActiveHour() { Lookups++; return null; }
        public void Clear() { }
    }

    private sealed class FakeStorage : IClipStorageService
    {
        public string FileExtensionFor(CaptureType type) => ".png";
        public string GenerateFilePath(CaptureType type, string? fileExtension = null, string? stemSuffix = null)
            => throw new NotSupportedException();
        public string OutputDirectory(CaptureType type) => "Synthetic fixture directory";
    }

    private sealed class FakeHotKeys : IHotKeyService
    {
        public HotKeyDefinition GetBinding(HotKeyAction action) => new(HotKeyModifiers.Control, 53);
        public HotKeyDefinition GetStopBinding() => new(HotKeyModifiers.Control, 83);
        public string StopRecordingDisplayString => "Ctrl+S";
        public void SetBinding(HotKeyAction action, HotKeyDefinition binding) { }
        public HotKeyDefinition DefaultFor(HotKeyAction action) => GetBinding(action);
        public HotKeyValidationResult ValidateBinding(HotKeyAction action, HotKeyDefinition binding) => throw new NotSupportedException();
    }

    private sealed class ManualTranscriptSaveScheduler : ITeleprompterTranscriptSaveScheduler
    {
        private Action? _save;
        public void Restart(Action save) => _save = save;
        public void Stop() => _save = null;
        public void Fire()
        {
            var save = _save;
            _save = null;
            save?.Invoke();
        }
    }
}
