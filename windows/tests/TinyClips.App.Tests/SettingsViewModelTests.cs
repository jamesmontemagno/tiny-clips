using TinyClips.App;
using TinyClips.App.Settings;
using TinyClips.Core.Models;
using TinyClips.Core.Services;

namespace TinyClips.App.Tests;

public sealed class SettingsViewModelTests
{
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

        public SettingsViewModel CreateViewModel(ITeleprompterTranscriptSaveScheduler? scheduler = null) => new(
            Settings, new FakeHotKeys(), LaunchAtLogin, Audio, Webcams, new FakeStorage(),
            Analytics, Credentials, dispatcherQueue: null, transcriptSaveScheduler: scheduler);
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
