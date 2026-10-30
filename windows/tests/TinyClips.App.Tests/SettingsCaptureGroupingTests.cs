using System.Xml.Linq;

namespace TinyClips.App.Tests;

public sealed class SettingsCaptureGroupingTests
{
    private const string Id = "AutomationProperties.AutomationId";

    [Fact]
    public void Screenshot_GroupsEveryControlByQualityAndCaptureStage()
    {
        var groups = Groups("ScreenshotSettingsSection.xaml", "Screenshot");

        Assert.Equal(["Image & quality", "Before capture", "After capture", "Advanced"], groups.Keys);
        Assert.Equal(["ScreenshotFormatComboBox", "JpegQualitySlider", "ScreenshotScaleSlider"], groups["Image & quality"]);
        Assert.Equal(
            ["ShowScreenshotCapturePickerToggle", "ScreenshotCountdownDurationNumberBox", "ScreenshotCountdownToggle"],
            groups["Before capture"]);
        Assert.Equal(
            ["ShowScreenshotCapturePickerAfterCaptureToggle", "ShowScreenshotEditorToggle", "ScreenshotEditorDefaultToolComboBox", "CopyScreenshotToggle"],
            groups["After capture"]);
        Assert.Equal(["ScreenshotLiveCaptureToggle"], groups["Advanced"]);

        var advanced = SettingsMarkup.Elements("ScreenshotSettingsSection.xaml")
            .Single(element => element.Attr(Id) == "AdvancedScreenshotSettingsExpander");
        Assert.Equal("False", advanced.Attr("IsExpanded"));
        Assert.Equal("Advanced screenshot settings", advanced.Attr("AutomationProperties.Name"));
    }

    [Fact]
    public void Gif_GroupsEveryControlByQualityAndRecordingStage()
    {
        var groups = Groups("GifSettingsSection.xaml", "GIF");

        Assert.Equal(["GIF quality", "Before recording", "After recording"], groups.Keys);
        Assert.Equal(["GifFrameRateNumberBox", "GifMaxWidthNumberBox"], groups["GIF quality"]);
        Assert.Equal(
            ["ShowGifCapturePickerToggle", "GifCountdownDurationNumberBox", "GifCountdownToggle"],
            groups["Before recording"]);
        Assert.Equal(
            ["ShowGifCapturePickerAfterCaptureToggle", "ShowGifTrimmerToggle", "CopyGifToggle"],
            groups["After recording"]);
    }

    [Theory]
    [InlineData("Screenshot")]
    [InlineData("Gif")]
    public void CountdownAndRepeatPicker_KeepTheirEnabledBindings(string kind)
    {
        var controls = SettingsMarkup.Elements(kind + "SettingsSection.xaml")
            .Where(element => element.Attr(Id) is not null)
            .ToDictionary(element => element.Attr(Id)!);

        Assert.Equal(
            $"{{x:Bind ViewModel.{kind}CountdownEnabled, Mode=OneWay}}",
            controls[kind + "CountdownDurationNumberBox"].Attr("IsEnabled"));
        Assert.Equal(
            $"{{x:Bind ViewModel.{kind}CapturePickerAfterCaptureEnabled, Mode=OneWay}}",
            controls["Show" + kind + "CapturePickerAfterCaptureToggle"].Attr("IsEnabled"));
    }

    private static Dictionary<string, string[]> Groups(string page, string title)
    {
        var column = SettingsMarkup.Elements(page).First(element => element.Name.LocalName == "StackPanel");
        var children = column.Elements().ToList();
        Assert.Equal(title, children[0].Attr("Text"));
        Assert.Equal("Level1", children[0].Attr("AutomationProperties.HeadingLevel"));

        var groups = new Dictionary<string, string[]>();
        for (var index = 1; index < children.Count; index++)
        {
            var heading = children[index];
            Assert.Equal("Level2", heading.Attr("AutomationProperties.HeadingLevel"));
            XElement content;
            string name;
            if (heading.Name.LocalName == "SettingsExpander")
            {
                name = heading.Attr("Header")!;
                content = heading;
            }
            else
            {
                Assert.Equal("TextBlock", heading.Name.LocalName);
                name = heading.Attr("Text")!;
                content = children[++index];
                Assert.Equal("StackPanel", content.Name.LocalName);
                Assert.Equal("2", content.Attr("Spacing"));
                Assert.All(content.Elements(), card => Assert.Equal("SettingsCard", card.Name.LocalName));
            }

            var controls = content.Descendants()
                .Where(element => element.Name.LocalName is "ComboBox" or "Slider" or "NumberBox" or "ToggleSwitch")
                .ToList();
            Assert.NotEmpty(controls);
            Assert.All(controls, control => Assert.False(string.IsNullOrWhiteSpace(control.Attr("AutomationProperties.Name"))));
            groups.Add(name, controls.Select(control => control.Attr(Id)!).ToArray());
        }

        var ids = groups.Values.SelectMany(ids => ids).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        return groups;
    }
}
