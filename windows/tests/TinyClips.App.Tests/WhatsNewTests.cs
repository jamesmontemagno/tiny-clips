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
    [InlineData("rev3;2.0.0", true)]
    [InlineData("rev4;2.1.0", false)]
    [InlineData("rev5;2.2.0", false)]
    [InlineData("revnot-a-number", true)]
    [InlineData("rev-1", true)]
    public void ContentRevision_NotPackageVersion_DecidesWhetherToShow(string seen, bool shouldShow)
    {
        var settings = new CaptureSettings(new MemorySettings()) { LastSeenWhatsNewVersion = seen };

        Assert.Equal(shouldShow, WhatsNewViewModel.ShouldShow(settings));
    }

    [Fact]
    public void DismissalStampsTheCurrentContentRevision()
    {
        var settings = new CaptureSettings(new MemorySettings());

        WhatsNewViewModel.MarkSeen(settings, "2.1.0");

        Assert.Equal($"rev{WhatsNewViewModel.ContentRevision};2.1.0", settings.LastSeenWhatsNewVersion);
        Assert.False(WhatsNewViewModel.ShouldShow(settings));
    }

    [Fact]
    public void MarkupIntroducesAgentControlWithoutAStudioPromotion()
    {
        const string markup = "WhatsNewWindow.xaml";
        var elements = SettingsMarkup.Elements(markup).ToList();
        var text = string.Join(" ", elements.Select(element => element.Value));

        Assert.DoesNotContain(elements, element => element.Name.LocalName == "ToggleSwitch");
        Assert.DoesNotContain("Studio preview", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Enable Tiny Clips Studio", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WhatsNewDoneButton", SettingsMarkup.AutomationIds(markup));
        Assert.Contains("WhatsNewChangelogLink", SettingsMarkup.AutomationIds(markup));

        var diagram = Assert.Single(elements, element => element.Attr("AutomationProperties.AutomationId") == "WhatsNewAgentDiagram");
        Assert.Equal("AccessibleFigure", diagram.Name.LocalName);
        Assert.Contains("Agent control", diagram.Attr("AutomationProperties.Name"));

        var settingsButton = Assert.Single(elements, element => element.Attr("AutomationProperties.AutomationId") == "WhatsNewOpenAgentSettingsButton");
        Assert.Equal("OnOpenAgentSettingsClicked", settingsButton.Attr("Click"));
    }

    private sealed class MemorySettings : ISettingsService
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

        public AppTheme Theme { get; set; }
        public string SaveDirectory { get; set; } = string.Empty;

        public T Get<T>(string key, T defaultValue) =>
            _values.TryGetValue(key, out var value) && value is T typed ? typed : defaultValue;

        public void Set<T>(string key, T value) => _values[key] = value;
    }
}
