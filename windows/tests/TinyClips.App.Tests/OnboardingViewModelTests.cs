using System.Xml.Linq;
using TinyClips.Core.Models;
using TinyClips.Core.Services;

namespace TinyClips.App.Tests;

public sealed class OnboardingViewModelTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ScreenshotOutputs_RefreshAllChoicesAfterExternalChanges(bool transitionAccepted)
    {
        var fixture = new Fixture();
        fixture.Settings.SaveScreenshotToDisk = false;
        fixture.Settings.ShowScreenshotEditor = transitionAccepted;

        fixture.ViewModel.CopyScreenshotToClipboard = false;

        Assert.False(fixture.ViewModel.SaveScreenshotToDisk);
        Assert.Equal(!transitionAccepted, fixture.ViewModel.CopyScreenshotToClipboard);
        Assert.Equal(transitionAccepted, fixture.ViewModel.ShowScreenshotEditor);
        Assert.Equal(!transitionAccepted, fixture.ViewModel.HasScreenshotOutputWarning);
        Assert.Equal(fixture.Settings.SaveScreenshotToDisk, fixture.ViewModel.SaveScreenshotToDisk);
        Assert.Equal(fixture.Settings.CopyScreenshotToClipboard, fixture.ViewModel.CopyScreenshotToClipboard);
        Assert.Equal(fixture.Settings.ShowScreenshotEditor, fixture.ViewModel.ShowScreenshotEditor);
    }

    [Theory]
    [InlineData(ScreenshotOutput.SaveToDisk)]
    [InlineData(ScreenshotOutput.CopyToClipboard)]
    [InlineData(ScreenshotOutput.OpenEditor)]
    public void ScreenshotOutputs_AllowValidTransitionsAndRejectTheLastOutput(ScreenshotOutput output)
    {
        var bit = output switch
        {
            ScreenshotOutput.SaveToDisk => 1,
            ScreenshotOutput.CopyToClipboard => 2,
            ScreenshotOutput.OpenEditor => 4,
            _ => throw new InvalidOperationException(),
        };
        var propertyName = output switch
        {
            ScreenshotOutput.SaveToDisk => nameof(OnboardingViewModel.SaveScreenshotToDisk),
            ScreenshotOutput.CopyToClipboard => nameof(OnboardingViewModel.CopyScreenshotToClipboard),
            ScreenshotOutput.OpenEditor => nameof(OnboardingViewModel.ShowScreenshotEditor),
            _ => throw new InvalidOperationException(),
        };

        foreach (var mask in Enumerable.Range(1, 7))
        {
            foreach (var enabled in new[] { false, true })
            {
                var store = new MemorySettings();
                var settings = new CaptureSettings(store)
                {
                    SaveScreenshotToDisk = (mask & 1) != 0,
                    CopyScreenshotToClipboard = (mask & 2) != 0,
                    ShowScreenshotEditor = (mask & 4) != 0,
                };
                var vm = new OnboardingViewModel(new FakeLaunchAtLogin(), settings);
                var writes = store.Writes;
                var notifications = new List<string?>();
                vm.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
                var allowed = enabled || (mask & ~bit) != 0;
                var expectedMask = !allowed ? mask : enabled ? mask | bit : mask & ~bit;

                SetScreenshotOutput(vm, output, enabled);

                Assert.Equal(expectedMask, ScreenshotMask(vm.SaveScreenshotToDisk, vm.CopyScreenshotToClipboard, vm.ShowScreenshotEditor));
                Assert.Equal(expectedMask, ScreenshotMask(settings.SaveScreenshotToDisk, settings.CopyScreenshotToClipboard, settings.ShowScreenshotEditor));
                Assert.Equal(!allowed, vm.HasScreenshotOutputWarning);
                Assert.Equal(writes + (allowed && ((mask & bit) != 0) != enabled ? 1 : 0), store.Writes);
                if (!allowed)
                {
                    Assert.Contains(propertyName, notifications);
                    Assert.Equal(ScreenshotOutputPolicy.LastOutputExplanation, vm.ScreenshotOutputExplanation);
                }
            }
        }
    }

    [Fact]
    public void ScreenshotOutputWarning_ClearsWhenAnotherOptionIsEnabledAndPersistsOnReopen()
    {
        var fixture = new Fixture();
        fixture.Settings.CopyScreenshotToClipboard = false;
        fixture.Settings.ShowScreenshotEditor = false;
        fixture.ViewModel.RefreshPreferences();

        fixture.ViewModel.SaveScreenshotToDisk = false;
        Assert.True(fixture.ViewModel.SaveScreenshotToDisk);
        Assert.True(fixture.ViewModel.HasScreenshotOutputWarning);
        fixture.ViewModel.ShowScreenshotEditor = true;
        Assert.False(fixture.ViewModel.HasScreenshotOutputWarning);
        fixture.ViewModel.SaveScreenshotToDisk = false;

        var reopened = new OnboardingViewModel(fixture.LaunchAtLogin, fixture.Settings);
        Assert.False(reopened.SaveScreenshotToDisk);
        Assert.False(reopened.CopyScreenshotToClipboard);
        Assert.True(reopened.ShowScreenshotEditor);
        Assert.False(reopened.HasScreenshotOutputWarning);
    }

    [Fact]
    public async Task BeforeTheOsAnswers_ToggleIsLocked_ThenOffByDefaultWithoutChangingAnything()
    {
        var fixture = new Fixture();
        var read = new TaskCompletionSource<LaunchAtLoginState>();
        fixture.LaunchAtLogin.ReadResult = read.Task;

        var pending = fixture.ViewModel.RefreshAsync();

        Assert.False(fixture.ViewModel.CanChangeLaunchAtLogin);
        read.SetResult(LaunchAtLoginState.Disabled);
        await pending;

        Assert.False(fixture.ViewModel.LaunchAtLogin);
        Assert.True(fixture.ViewModel.CanChangeLaunchAtLogin);
        Assert.False(fixture.ViewModel.HasLaunchAtLoginNote);
        Assert.Empty(fixture.LaunchAtLogin.Requests);
    }

    [Fact]
    public async Task AlreadyEnabled_IsShownOnWithoutRequestingAChange()
    {
        var fixture = new Fixture();
        fixture.LaunchAtLogin.ReadResult = Task.FromResult(LaunchAtLoginState.Enabled);

        await fixture.ViewModel.RefreshAsync();

        Assert.True(fixture.ViewModel.LaunchAtLogin);
        Assert.True(fixture.Settings.LaunchAtLogin);
        Assert.Empty(fixture.LaunchAtLogin.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Choosing_AppliesToTheOs_AndUpdatesTheSavedMirror(bool startedOn)
    {
        var fixture = new Fixture();
        fixture.LaunchAtLogin.ReadResult = Task.FromResult(startedOn ? LaunchAtLoginState.Enabled : LaunchAtLoginState.Disabled);
        await fixture.ViewModel.RefreshAsync();

        fixture.ViewModel.LaunchAtLogin = !startedOn;
        await fixture.ViewModel.Pending;

        Assert.Equal([!startedOn], fixture.LaunchAtLogin.Requests);
        Assert.Equal(!startedOn, fixture.ViewModel.LaunchAtLogin);
        Assert.Equal(!startedOn, fixture.Settings.LaunchAtLogin);
    }

    [Fact]
    public async Task WindowsRefusesTheChange_ToggleShowsItsAnswerAndExplains()
    {
        var fixture = new Fixture();
        await fixture.ViewModel.RefreshAsync();
        fixture.LaunchAtLogin.NextSetResult = Task.FromResult(LaunchAtLoginState.DisabledByUser);
        var changes = new List<string?>();
        fixture.ViewModel.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        fixture.ViewModel.LaunchAtLogin = true;
        await fixture.ViewModel.Pending;

        Assert.False(fixture.ViewModel.LaunchAtLogin);
        Assert.False(fixture.Settings.LaunchAtLogin);
        Assert.False(fixture.ViewModel.CanChangeLaunchAtLogin);
        Assert.Equal(LaunchAtLoginState.DisabledByUser.Describe(), fixture.ViewModel.LaunchAtLoginNote);
        Assert.True(fixture.ViewModel.HasLaunchAtLoginNote);
        Assert.Equal(2, changes.Count(name => name == nameof(OnboardingViewModel.LaunchAtLogin)));
        Assert.Equal([true], fixture.LaunchAtLogin.Requests);
    }

    [Theory]
    [InlineData(LaunchAtLoginState.DisabledByUser, false)]
    [InlineData(LaunchAtLoginState.DisabledByPolicy, false)]
    [InlineData(LaunchAtLoginState.EnabledByPolicy, true)]
    [InlineData(LaunchAtLoginState.Unavailable, false)]
    public async Task StatesWindowsOwns_LockTheToggleAndExplainWhy(LaunchAtLoginState state, bool isOn)
    {
        var fixture = new Fixture();
        fixture.LaunchAtLogin.ReadResult = Task.FromResult(state);

        await fixture.ViewModel.RefreshAsync();

        Assert.Equal(isOn, fixture.ViewModel.LaunchAtLogin);
        Assert.False(fixture.ViewModel.CanChangeLaunchAtLogin);
        Assert.True(fixture.ViewModel.HasLaunchAtLoginNote);
    }

    [Fact]
    public async Task QuickChanges_ReachTheOsInOrder_AndOnlyTheLastIsShown()
    {
        var fixture = new Fixture();
        await fixture.ViewModel.RefreshAsync();
        var first = new TaskCompletionSource<LaunchAtLoginState>();
        fixture.LaunchAtLogin.NextSetResult = first.Task;

        fixture.ViewModel.LaunchAtLogin = true;
        fixture.ViewModel.LaunchAtLogin = false;

        Assert.Equal([true], fixture.LaunchAtLogin.Requests);
        first.SetResult(LaunchAtLoginState.Enabled);
        await fixture.ViewModel.Pending;

        Assert.Equal([true, false], fixture.LaunchAtLogin.Requests);
        Assert.False(fixture.ViewModel.LaunchAtLogin);
        Assert.False(fixture.Settings.LaunchAtLogin);
    }

    [Fact]
    public async Task Refresh_WaitsForAChangeInProgress()
    {
        var fixture = new Fixture();
        await fixture.ViewModel.RefreshAsync();
        var change = new TaskCompletionSource<LaunchAtLoginState>();
        fixture.LaunchAtLogin.NextSetResult = change.Task;
        fixture.ViewModel.LaunchAtLogin = true;
        var readsBefore = fixture.LaunchAtLogin.Reads;

        var refresh = fixture.ViewModel.RefreshAsync();

        Assert.Equal(readsBefore, fixture.LaunchAtLogin.Reads);
        fixture.LaunchAtLogin.ReadResult = Task.FromResult(LaunchAtLoginState.Enabled);
        change.SetResult(LaunchAtLoginState.Enabled);
        await refresh;

        Assert.Equal(readsBefore + 1, fixture.LaunchAtLogin.Reads);
        Assert.True(fixture.ViewModel.LaunchAtLogin);
    }

    [Fact]
    public async Task FailedChange_ReturnsToTheLastConfirmedState_AndALaterChangeStillRuns()
    {
        var fixture = new Fixture();
        await fixture.ViewModel.RefreshAsync();
        fixture.LaunchAtLogin.NextSetResult = Task.FromException<LaunchAtLoginState>(new InvalidOperationException("Unavailable"));

        fixture.ViewModel.LaunchAtLogin = true;
        await fixture.ViewModel.Pending;

        Assert.False(fixture.ViewModel.LaunchAtLogin);
        Assert.False(fixture.Settings.LaunchAtLogin);
        Assert.Contains("Couldn't", fixture.ViewModel.LaunchAtLoginNote);
        Assert.True(fixture.ViewModel.CanChangeLaunchAtLogin);

        fixture.ViewModel.LaunchAtLogin = true;
        await fixture.ViewModel.Pending;

        Assert.True(fixture.ViewModel.LaunchAtLogin);
        Assert.False(fixture.ViewModel.HasLaunchAtLoginNote);
    }

    [Fact]
    public async Task FailedFirstRead_KeepsTheToggleLocked()
    {
        var fixture = new Fixture();
        fixture.LaunchAtLogin.ReadResult = Task.FromException<LaunchAtLoginState>(new InvalidOperationException("Unavailable"));

        await fixture.ViewModel.RefreshAsync();

        Assert.False(fixture.ViewModel.CanChangeLaunchAtLogin);
        Assert.Contains("Couldn't", fixture.ViewModel.LaunchAtLoginNote);
    }

    [Fact]
    public void Markup_HasAStepAndDotForEachPage_AndAnAccessibleBoundToggle()
    {
        const string markup = "OnboardingWindow.xaml";
        var elements = SettingsMarkup.Elements(markup);
        string?[] names = [.. elements.Select(element => element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value)];

        Assert.Equal(["Step0", "Step1", "Step2", "Step3", "Step4"], names.Where(name => name?.StartsWith("Step", StringComparison.Ordinal) == true));
        Assert.Equal(["Dot0", "Dot1", "Dot2", "Dot3", "Dot4"], names.Where(name => name?.StartsWith("Dot", StringComparison.Ordinal) == true));

        var toggle = Assert.Single(elements, element => element.Attr("AutomationProperties.AutomationId") == "OnboardingLaunchAtLoginToggle");
        Assert.Equal("OnboardingLaunchAtLoginToggle", toggle.Attr("AutomationProperties.AutomationId"));
        Assert.Equal("Launch at login", toggle.Attr("AutomationProperties.Name"));
        Assert.Equal("{x:Bind ViewModel.CanChangeLaunchAtLogin, Mode=OneWay}", toggle.Attr("IsEnabled"));
        Assert.Equal(
            ["LaunchAtLogin", "RecordingCountdown", "ShowSaveNotifications"],
            SettingsMarkup.TwoWayViewModelProperties(markup).Order());
        var countdown = Assert.Single(elements, element =>
            element.Attr("AutomationProperties.Name") == "Countdown before video and GIF recording");
        Assert.Equal("ToggleSwitch", countdown.Name.LocalName);
        Assert.Equal("{x:Bind ViewModel.RecordingCountdown, Mode=TwoWay}", countdown.Attr("IsOn"));
    }

    [Fact]
    public void ScreenshotOutputMarkup_UsesNamedNativeSwitchesAndAnAccessibleLiveWarning()
    {
        var elements = SettingsMarkup.Elements("OnboardingWindow.xaml");
        foreach (var (id, property) in new[]
        {
            ("OnboardingSaveScreenshotToDiskToggle", "SaveScreenshotToDisk"),
            ("OnboardingCopyScreenshotToggle", "CopyScreenshotToClipboard"),
            ("OnboardingShowScreenshotEditorToggle", "ShowScreenshotEditor"),
        })
        {
            var toggle = Assert.Single(elements, element => element.Attr("AutomationProperties.AutomationId") == id);
            Assert.Equal("ToggleSwitch", toggle.Name.LocalName);
            Assert.False(string.IsNullOrWhiteSpace(toggle.Attr("AutomationProperties.Name")));
            Assert.Contains("must stay on", toggle.Attr("AutomationProperties.HelpText"));
            Assert.Equal($"{{x:Bind ViewModel.{property}, Mode=OneWay}}", toggle.Attr("IsOn"));
            Assert.Equal("OnScreenshotOutputToggled", toggle.Attr("Toggled"));
        }

        var notice = Assert.Single(elements, element => element.Attr("AutomationProperties.AutomationId") == "OnboardingScreenshotOutputStatus");
        Assert.Equal("InfoBar", notice.Name.LocalName);
        Assert.Equal("Polite", notice.Attr("AutomationProperties.LiveSetting"));
        Assert.Equal("{x:Bind ViewModel.HasScreenshotOutputWarning, Mode=OneWay}", notice.Attr("IsOpen"));
        Assert.Equal("{x:Bind ViewModel.ScreenshotOutputExplanation, Mode=OneWay}", notice.Attr("AutomationProperties.Name"));
    }

    [Fact]
    public void CompletionPage_ShowsTrayGuidanceBeforeAlternativeKeyBindings()
    {
        var elements = SettingsMarkup.Elements("OnboardingWindow.xaml").ToList();
        var title = elements.FindIndex(element => element.Attr("Text") == "You're all set");
        var illustration = elements.FindIndex(element =>
            element.Attr("AutomationProperties.Name") == "Windows taskbar illustration. Tiny Clips is in the system tray near the clock.");
        var alternative = elements.FindIndex(element => element.Attr("Text") == "Or use a key binding");
        var bindings = elements.FindIndex(element =>
            element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value == "ScreenshotShortcut");

        Assert.True(title >= 0 && title < illustration);
        Assert.True(illustration < alternative && alternative < bindings);
    }

    [Fact]
    public void RefreshingPreferences_PreservesMixedCountdownAndDoesNotWrite()
    {
        var storage = new MemorySettings();
        var settings = new CaptureSettings(storage);
        settings.VideoCountdownEnabled = true;
        settings.GifCountdownEnabled = false;
        settings.CopyScreenshotToClipboard = false;
        var writesBefore = storage.Writes;
        var model = new OnboardingViewModel(new FakeLaunchAtLogin(), settings);

        Assert.False(model.RecordingCountdown);
        Assert.Equal("Custom", model.RecordingCountdownOffContent);
        Assert.False(model.CopyScreenshotToClipboard);
        Assert.True(model.SaveScreenshotToDisk);
        model.SaveScreenshotToDisk = model.SaveScreenshotToDisk;
        model.CopyScreenshotToClipboard = model.CopyScreenshotToClipboard;
        model.RecordingCountdown = model.RecordingCountdown;
        model.RefreshPreferences();

        Assert.Equal(writesBefore, storage.Writes);
        Assert.True(settings.VideoCountdownEnabled);
        Assert.False(settings.GifCountdownEnabled);
        Assert.Equal("Custom", model.RecordingCountdownOffContent);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CountdownChoice_UpdatesBothAndPreservesSavedDurations(bool enabled)
    {
        var fixture = new Fixture();
        fixture.Settings.VideoCountdownEnabled = true;
        fixture.Settings.GifCountdownEnabled = false;
        fixture.Settings.VideoCountdownDuration = 8;
        fixture.Settings.GifCountdownDuration = 5;
        fixture.Settings.ScreenshotCountdownEnabled = true;
        fixture.ViewModel.RefreshPreferences();

        fixture.ViewModel.RecordingCountdown = true;
        fixture.ViewModel.RecordingCountdown = enabled;

        Assert.Equal(enabled, fixture.Settings.VideoCountdownEnabled);
        Assert.Equal(enabled, fixture.Settings.GifCountdownEnabled);
        Assert.Equal(8, fixture.Settings.VideoCountdownDuration);
        Assert.Equal(5, fixture.Settings.GifCountdownDuration);
        Assert.True(fixture.Settings.ScreenshotCountdownEnabled);
        Assert.Equal(enabled, fixture.ViewModel.RecordingCountdown);
        Assert.Equal("Off", fixture.ViewModel.RecordingCountdownOffContent);
    }

    [Fact]
    public void ExplicitPreferences_PersistImmediatelyAndReopenWithoutChangingOtherCaptureOptions()
    {
        var fixture = new Fixture();
        fixture.ViewModel.CopyScreenshotToClipboard = false;
        fixture.ViewModel.ShowScreenshotEditor = false;
        fixture.ViewModel.ShowSaveNotifications = true;

        var reopened = new OnboardingViewModel(fixture.LaunchAtLogin, fixture.Settings);
        Assert.False(reopened.CopyScreenshotToClipboard);
        Assert.False(reopened.ShowScreenshotEditor);
        Assert.True(reopened.ShowSaveNotifications);
        Assert.True(fixture.Settings.ShowTrimmer);
        Assert.True(fixture.Settings.ShowGifTrimmer);
        Assert.False(fixture.Settings.CopyVideoToClipboard);
        Assert.False(fixture.Settings.CopyGifToClipboard);

        fixture.Settings.CopyScreenshotToClipboard = true;
        reopened.RefreshPreferences();
        Assert.True(reopened.CopyScreenshotToClipboard);
    }

    private static int ScreenshotMask(bool save, bool copy, bool editor) =>
        (save ? 1 : 0) | (copy ? 2 : 0) | (editor ? 4 : 0);

    private static void SetScreenshotOutput(OnboardingViewModel vm, ScreenshotOutput output, bool enabled)
    {
        switch (output)
        {
            case ScreenshotOutput.SaveToDisk:
                vm.SaveScreenshotToDisk = enabled;
                break;
            case ScreenshotOutput.CopyToClipboard:
                vm.CopyScreenshotToClipboard = enabled;
                break;
            case ScreenshotOutput.OpenEditor:
                vm.ShowScreenshotEditor = enabled;
                break;
        }
    }

    private sealed class Fixture
    {
        public FakeLaunchAtLogin LaunchAtLogin { get; } = new();
        public CaptureSettings Settings { get; } = new(new MemorySettings());
        public OnboardingViewModel ViewModel { get; }

        public Fixture() => ViewModel = new OnboardingViewModel(LaunchAtLogin, Settings);
    }

    private sealed class FakeLaunchAtLogin : ILaunchAtLoginService
    {
        public int Reads { get; private set; }
        public List<bool> Requests { get; } = [];
        public Task<LaunchAtLoginState>? NextSetResult { get; set; }
        public Task<LaunchAtLoginState> ReadResult { get; set; } = Task.FromResult(LaunchAtLoginState.Disabled);

        public Task<LaunchAtLoginState> GetStateAsync()
        {
            Reads++;
            return ReadResult;
        }

        public Task<LaunchAtLoginState> SetEnabledAsync(bool enabled)
        {
            Requests.Add(enabled);
            if (NextSetResult is { } result)
            {
                NextSetResult = null;
                return result;
            }

            return Task.FromResult(enabled ? LaunchAtLoginState.Enabled : LaunchAtLoginState.Disabled);
        }
    }

    private sealed class MemorySettings : ISettingsService
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);
        public int Writes { get; private set; }
        public AppTheme Theme { get; set; }
        public string SaveDirectory { get; set; } = string.Empty;

        public T Get<T>(string key, T defaultValue) =>
            _values.TryGetValue(key, out var value) && value is T typed ? typed : defaultValue;

        public void Set<T>(string key, T value)
        {
            Writes++;
            _values[key] = value;
        }
    }
}
