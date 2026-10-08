using System.Text.RegularExpressions;
using System.Xml.Linq;
using TinyClips.App.Settings;

namespace TinyClips.App.Tests;

/// <summary>
/// The Studio page of Settings, read from its markup and its code: what is on it, where it is
/// in the navigation, and that the General page, where all of it was until 7 October 2026, has
/// nothing of Studio left. What the page's values do is in <see cref="SettingsViewModelStudioTests"/>.
/// None of this shows that the page looks right or can be used: nobody has opened it.
/// </summary>
public sealed partial class SettingsStudioPageTests
{
    private const string StudioPage = "StudioSettingsSection.xaml";
    private const string GeneralPage = "GeneralSettingsSection.xaml";
    private const string Id = "AutomationProperties.AutomationId";

    // What the General page's Studio card held, in its order. The last three are one a row.
    private static readonly string[] MovedIds =
    [
        "StudioKeptNoteText",
        "StudioPreviewToggle",
        "StudioStorageSummaryText",
        "StudioUninstallNoteText",
        "StudioCleanupStatusText",
        "StudioCleanUpNowButton",
        "StudioSourceRetentionDaysNumberBox",
        "StudioStorageCapNumberBox",
        "StudioDraftsHeading",
        "StudioDraftsList",
        "{x:Bind OpenButtonId, Mode=OneTime}",
        "{x:Bind SaveRecordingButtonId, Mode=OneTime}",
        "{x:Bind DeleteButtonId, Mode=OneTime}",
    ];

    // ---- Where the page is

    [Fact]
    public void TheNavigation_HasOneItemForEverySection_AndStudioRightAfterVideo()
    {
        var items = SettingsMarkup.Elements("SettingsWindow.xaml")
            .Where(element => element.Name.LocalName == "NavigationViewItem")
            .ToList();
        var tags = items.Select(item => item.Attr("Tag")).ToList();

        // The window finds a section by its item's tag, which is the section's name.
        Assert.Equal(Enum.GetNames<SettingsSectionKind>().Order(StringComparer.Ordinal), tags.Order(StringComparer.Ordinal));
        Assert.Equal(tags.IndexOf("Video") + 1, tags.IndexOf("Studio"));

        var studio = items[tags.IndexOf("Studio")];
        Assert.Equal("Studio", studio.Attr("Content"));
        Assert.Equal("StudioNavigationItem", studio.Attr(Id));
    }

    [Fact]
    public void TheWindow_MakesAPageForEverySection()
    {
        var code = SettingsMarkup.Read("SettingsWindow.xaml.cs");

        // A section without one ends the app when its item is chosen.
        Assert.All(
            Enum.GetNames<SettingsSectionKind>(),
            kind => Assert.Contains($"SettingsSectionKind.{kind} => ", code, StringComparison.Ordinal));
        Assert.Contains("SettingsSectionKind.Studio => new StudioSettingsSection(ViewModel)", code, StringComparison.Ordinal);
    }

    // ---- What is on it

    [Fact]
    public void TheStudioPage_HasEveryStudioControlOnce_InTheOrderItHad_AndNoOtherPageHasOne()
    {
        var ids = SettingsMarkup.AutomationIds(StudioPage);

        Assert.All(MovedIds, id => Assert.Single(ids, candidate => candidate == id));
        Assert.Equal(MovedIds, ids.Where(id => MovedIds.Contains(id)));

        // Moved, not copied.
        Assert.Contains(GeneralPage, SettingsMarkup.Pages);
        foreach (var page in SettingsMarkup.Pages.Where(page => page != StudioPage))
        {
            var markup = SettingsMarkup.Read(page);
            Assert.All(MovedIds, id => Assert.DoesNotContain(id, markup, StringComparison.Ordinal));
        }

        Assert.DoesNotContain("Studio", SettingsMarkup.Read(GeneralPage), StringComparison.Ordinal);
        Assert.DoesNotContain("Studio", SettingsMarkup.Read(GeneralPage + ".cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheStudioPage_ShowsTheSwitchAlways_AndEverythingElseOnlyWhileStudioIsOn()
    {
        var column = SettingsMarkup.Elements(StudioPage).First(element => element.Name.LocalName == "StackPanel");
        var parts = column.Elements().ToList();

        // The page's name, the first heading, the card with the switch, and one panel for the rest.
        Assert.Equal(["TextBlock", "TextBlock", "SettingsCard", "StackPanel"], parts.Select(part => part.Name.LocalName));
        var switchCard = parts[2];
        var whileOn = parts[3];
        Assert.Null(switchCard.Attr("Visibility"));
        Assert.Contains(switchCard.Descendants(), element => element.Attr(Id) == "StudioPreviewToggle");
        Assert.Equal("{x:Bind ViewModel.StudioPreviewVisibility, Mode=OneWay}", whileOn.Attr("Visibility"));

        // The line that says what is kept while Studio is off is with the switch, where it can show.
        Assert.Contains(switchCard.Descendants(), element => element.Attr(Id) == "StudioKeptNoteText");

        var others = Controls(column).Where(control => control.Attr(Id) != "StudioPreviewToggle").ToList();
        Assert.NotEmpty(others);
        Assert.All(others, control => Assert.Contains(whileOn, control.Ancestors()));
    }

    [Fact]
    public void TheStudioPage_HasTheMacsSections_AsHeadings()
    {
        var headings = SettingsMarkup.Elements(StudioPage)
            .Where(element => element.Name.LocalName == "TextBlock" && element.Attr("AutomationProperties.HeadingLevel") is not null)
            .Select(element => (Text: element.Attr("Text"), Level: element.Attr("AutomationProperties.HeadingLevel")))
            .ToList();

        // Projects, between Recording and Storage as on the Mac, holds Open project (#429).
        Assert.Equal(
            [("Studio", "Level1"), ("Tiny Clips Studio", "Level2"), ("Recording", "Level2"), ("Projects", "Level2"), ("Storage", "Level2"), ("Drafts", "Level2")],
            headings);

        // No text that looks like a heading is only made to look like one.
        var styled = SettingsMarkup.Elements(StudioPage)
            .Where(element => element.Name.LocalName == "TextBlock"
                && element.Attr("Style") is "{StaticResource SubtitleTextBlockStyle}" or "{StaticResource BodyStrongTextBlockStyle}")
            .ToList();
        Assert.Equal(headings.Count, styled.Count);
        Assert.All(styled, element => Assert.NotNull(element.Attr("AutomationProperties.HeadingLevel")));
    }

    [Fact]
    public void TheStudioPage_SaysHowAStudioRecordingIsStarted_WithoutAControl()
    {
        var elements = SettingsMarkup.Elements(StudioPage);
        var recording = elements.Single(element => element.Attr("Text") == "Recording");

        // Under the heading, up to the next one: one card, with words and nothing to press.
        var under = recording.ElementsAfterSelf()
            .TakeWhile(element => element.Attr("AutomationProperties.HeadingLevel") is null)
            .ToList();
        var card = Assert.Single(under);
        Assert.Equal("SettingsCard", card.Name.LocalName);
        Assert.Equal("Choose Studio in the tray menu", card.Attr("Header"));
        Assert.Contains("Record video, from the tray menu or with its hotkey, is always an ordinary recording.", card.Attr("Description"));
        Assert.Empty(card.Elements());
        Assert.Null(card.Attr("IsClickEnabled"));
        Assert.Null(card.Attr("Click"));
    }

    [Fact]
    public void TheStudioPage_HasOpenProjectUnderProjects_AButtonCalledByItsWords_AndSaysHowAProjectOutlivesAnUninstall()
    {
        var elements = SettingsMarkup.Elements(StudioPage);
        var projects = elements.Single(element => element.Attr("Text") == "Projects");

        // Under the heading, up to the next one: one card with one button.
        var under = projects.ElementsAfterSelf()
            .TakeWhile(element => element.Attr("AutomationProperties.HeadingLevel") is null)
            .ToList();
        var card = Assert.Single(under);
        Assert.Equal("SettingsCard", card.Name.LocalName);
        Assert.Equal("Open a saved project", card.Attr("Header"));
        Assert.Contains("The project is copied into Studio as a new draft, and the folder is only read.", card.Attr("Description"));
        var button = Assert.Single(card.Elements());
        Assert.Equal("Button", button.Name.LocalName);
        Assert.Equal("StudioOpenProjectButton", button.Attr(Id));
        Assert.Equal("Open project\u2026", button.Attr("AutomationProperties.Name"));
        Assert.Equal("OnOpenStudioProject", button.Attr("Click"));

        // Its picture and its word are the button's content, and say nothing of their own to
        // a screen reader, which reads the button's name: the words alone.
        var inside = button.Descendants().Where(element => element.Name.LocalName is "FontIcon" or "TextBlock").ToList();
        Assert.Equal(["FontIcon", "TextBlock"], inside.Select(element => element.Name.LocalName));
        Assert.All(inside, element => Assert.Equal("Raw", element.Attr("AutomationProperties.AccessibilityView")));
        Assert.Equal("Open project\u2026", inside[1].Attr("Text"));

        // Shown only while Studio is on, like everything else below the switch.
        Assert.Contains(card.Ancestors(), element => element.Attr("Visibility") == "{x:Bind ViewModel.StudioPreviewVisibility, Mode=OneWay}");

        // The page asks the app, which owns the picker and says why a project was not opened.
        var code = SettingsMarkup.Read(StudioPage + ".cs");
        Assert.Contains("app.ChooseAndOpenStudioProjectFromSettingsAsync()", code, StringComparison.Ordinal);
        Assert.Contains("Title = \"The project was not opened\"", code, StringComparison.Ordinal);

        // Where the page says that uninstalling deletes the projects, it says how to keep one.
        var note = elements.Single(element => element.Attr(Id) == "StudioUninstallNoteText").Attr("Text");
        Assert.Equal(
            "Projects are stored with the app. Uninstalling Tiny Clips, or resetting it in Windows Settings, deletes all of them, drafts included. To keep one past that, save it as a folder with Save project in the editor.",
            note);
    }

    [Fact]
    public void TheStudioPage_NamesEveryControl_AndSetsNoColourOfItsOwn()
    {
        var column = SettingsMarkup.Elements(StudioPage).First(element => element.Name.LocalName == "StackPanel");

        Assert.All(Controls(column), control =>
        {
            Assert.False(string.IsNullOrWhiteSpace(control.Attr(Id)), $"A {control.Name.LocalName} has no AutomationId.");
            if (control.Name.LocalName != "ItemsRepeater")
            {
                Assert.False(string.IsNullOrWhiteSpace(control.Attr("AutomationProperties.Name")), $"{control.Attr(Id)} has no accessible name.");
            }
        });

        // Colours come from the theme, so that dark and contrast themes are right.
        var markup = SettingsMarkup.Read(StudioPage);
        Assert.DoesNotMatch(HexColour(), markup);
        Assert.All(
            column.DescendantsAndSelf().SelectMany(element => element.Attributes())
                .Where(attribute => attribute.Name.LocalName is "Foreground" or "Background" or "BorderBrush" or "Fill"),
            attribute => Assert.StartsWith("{ThemeResource ", attribute.Value, StringComparison.Ordinal));
    }

    // ---- How it is made

    [Fact]
    public void TheStudioPage_KeepsItsValuesFromBeingSaved_BeforeItsControlsAreMade_AndReadsTheProjectsItself()
    {
        var code = SettingsMarkup.Read(StudioPage + ".cs");
        var begins = code.IndexOf("viewModel.BeginSectionRealization(SettingsSectionKind.Studio)", StringComparison.Ordinal);
        var controls = code.IndexOf("InitializeComponent();", StringComparison.Ordinal);
        var completes = code.IndexOf("SectionLifecycle.HookFirstLoad(this, viewModel, _realizationScope);", StringComparison.Ordinal);
        var reads = code.IndexOf("viewModel.EnsureStudioStorageInitializedAsync()", StringComparison.Ordinal);

        // A control bound both ways writes its first value back when it is made. That is not an
        // edit only if the page has said beforehand that it is being made.
        Assert.True(begins >= 0, "The Studio page does not say that it is being made.");
        Assert.True(begins < controls, "The Studio page makes its controls before it says that it is being made.");
        Assert.True(controls < completes, "The Studio page is not told when it has been shown.");

        // The projects are read by this page, when it is first chosen. General, which is made
        // whenever Settings opens, reads none: its code has nothing of Studio (see above).
        Assert.True(completes < reads, "The Studio page does not read the projects when it is made.");
    }

    // The things on the page that take the keyboard focus, or hold what does.
    private static IEnumerable<XElement> Controls(XElement within) =>
        within.Descendants().Where(element => element.Name.LocalName is "Button" or "NumberBox" or "ToggleSwitch" or "ItemsRepeater");

    [GeneratedRegex("\"#[0-9A-Fa-f]{3,8}\"", RegexOptions.CultureInvariant)]
    private static partial Regex HexColour();
}
