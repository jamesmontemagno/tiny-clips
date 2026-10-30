using TinyClips.Core.Models;
using TinyClips.Core.Services;

namespace TinyClips.App.Tests;

public sealed class WhatsNewTests
{
    [Theory]
    [InlineData("", true)]
    [InlineData("1.8.0", true)]
    [InlineData("rev1;1.8.0", true)]
    [InlineData("rev2", true)]
    [InlineData("rev2;1.8.0", true)]
    [InlineData("rev3;1.9.0", false)]
    [InlineData("rev4;2.1.0", false)]
    [InlineData("revnot-a-number", true)]
    [InlineData("rev-1", true)]
    public void ContentRevision_NotPackageVersion_DecidesWhetherToShow(string seen, bool shouldShow)
    {
        var settings = new CaptureSettings(new MemorySettings()) { LastSeenWhatsNewVersion = seen };

        Assert.Equal(shouldShow, WhatsNewViewModel.ShouldShow(settings));
    }

    [Fact]
    public void DismissalOrFirstRunStamp_PreventsAnotherIntroductionAndKeepsStudioDefaultOn()
    {
        var saved = new MemorySettings();
        var settings = new CaptureSettings(saved);
        WhatsNewViewModel.MarkSeen(settings, "1.9.0");

        var reloaded = new CaptureSettings(saved);
        Assert.Equal($"rev{WhatsNewViewModel.ContentRevision};1.9.0", reloaded.LastSeenWhatsNewVersion);
        Assert.False(WhatsNewViewModel.ShouldShow(reloaded));
        Assert.True(reloaded.StudioPreviewEnabled);
        Assert.Equal(0, saved.StudioWrites);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LaunchWithNoStudioPreference_EnablesStudioWithoutSavingAChoice(bool returningUser)
    {
        var saved = new MemorySettings();
        var settings = new CaptureSettings(saved) { HasCompletedOnboarding = returningUser };
        if (returningUser)
        {
            settings.LastSeenWhatsNewVersion = "rev2;1.9.0";
            Assert.True(WhatsNewViewModel.ShouldShow(settings));
        }
        else
        {
            WhatsNewViewModel.MarkSeen(settings, "2.0.0");
            Assert.False(WhatsNewViewModel.ShouldShow(settings));
        }

        Assert.True(new WhatsNewViewModel(settings).IsStudioEnabled);
        Assert.Equal(0, saved.StudioWrites);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpeningAndDismissingIntroduction_PreservesTheSavedChoice(bool enabled)
    {
        var saved = new MemorySettings();
        var settings = new CaptureSettings(saved) { StudioPreviewEnabled = enabled };
        var writes = saved.StudioWrites;

        var viewModel = new WhatsNewViewModel(settings);

        Assert.Equal(enabled, viewModel.IsStudioEnabled);
        WhatsNewViewModel.MarkSeen(settings, "2.0.0");
        Assert.Equal(enabled, new CaptureSettings(saved).StudioPreviewEnabled);
        Assert.Equal(writes, saved.StudioWrites);
    }

    [Fact]
    public void Toggle_PersistsBothWays_AndDoesNotChangeOrdinaryRecording()
    {
        var saved = new MemorySettings();
        var settings = new CaptureSettings(saved);
        var showTrimmer = settings.ShowTrimmer;
        var viewModel = new WhatsNewViewModel(settings);
        var notifications = new List<string?>();
        viewModel.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        Assert.True(viewModel.IsStudioEnabled);
        viewModel.IsStudioEnabled = false;
        Assert.False(new CaptureSettings(saved).StudioPreviewEnabled);
        Assert.False(new WhatsNewViewModel(new CaptureSettings(saved)).IsStudioEnabled);
        viewModel.IsStudioEnabled = false;
        Assert.Equal(1, saved.StudioWrites);

        viewModel.IsStudioEnabled = true;
        Assert.True(new CaptureSettings(saved).StudioPreviewEnabled);
        Assert.Equal(showTrimmer, settings.ShowTrimmer);
        Assert.Equal(2, saved.StudioWrites);
        Assert.Equal([nameof(WhatsNewViewModel.IsStudioEnabled), nameof(WhatsNewViewModel.IsStudioEnabled)], notifications);
    }

    [Fact]
    public void FailedPersistence_DoesNotReportAnUnsavedChoice()
    {
        var saved = new MemorySettings { FailStudioWrites = true };
        var viewModel = new WhatsNewViewModel(new CaptureSettings(saved));

        Assert.Throws<IOException>(() => viewModel.IsStudioEnabled = false);
        Assert.True(viewModel.IsStudioEnabled);
    }

    [Fact]
    public void Markup_BindsAnAccessibleToggle_AndKeepsDismissalAndChangelogActions()
    {
        const string markup = "WhatsNewWindow.xaml";
        var elements = SettingsMarkup.Elements(markup);
        var toggle = Assert.Single(elements, element => element.Name.LocalName == "ToggleSwitch");
        Assert.Equal("Enable Tiny Clips Studio preview", toggle.Attr("AutomationProperties.Name"));
        Assert.False(string.IsNullOrWhiteSpace(toggle.Attr("AutomationProperties.HelpText")));
        Assert.Equal(["IsStudioEnabled"], SettingsMarkup.TwoWayViewModelProperties(markup));
        Assert.Contains("WhatsNewDoneButton", SettingsMarkup.AutomationIds(markup));
        Assert.Contains("WhatsNewChangelogLink", SettingsMarkup.AutomationIds(markup));
        var settings = Assert.Single(elements, element => element.Attr("AutomationProperties.AutomationId") == "WhatsNewOpenStudioSettingsButton");
        Assert.Equal("OnOpenStudioSettingsClicked", settings.Attr("Click"));
        var illustration = Assert.Single(elements, element => element.Name.LocalName == "Image");
        Assert.False(string.IsNullOrWhiteSpace(illustration.Attr("AutomationProperties.Name")));
        Assert.Contains(elements, element => element.Name.LocalName == "SvgImageSource"
            && element.Attr("UriSource") == "ms-appx:///Assets/Studio/whats-new-studio.svg");
    }

    private sealed class MemorySettings : ISettingsService
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);
        public AppTheme Theme { get; set; }
        public string SaveDirectory { get; set; } = string.Empty;
        public int StudioWrites { get; private set; }
        public bool FailStudioWrites { get; init; }

        public T Get<T>(string key, T defaultValue) =>
            _values.TryGetValue(key, out var value) && value is T typed ? typed : defaultValue;

        public void Set<T>(string key, T value)
        {
            if (key == "studioPreviewEnabled")
            {
                if (FailStudioWrites)
                {
                    throw new IOException("Cannot persist Studio setting.");
                }
                StudioWrites++;
            }
            _values[key] = value;
        }
    }
}
