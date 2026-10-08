using TinyClips.Core.Studio.Editing;

namespace TinyClips.Core.Tests;

/// <summary>
/// The panels of the Studio inspector's rail, and the glyphs on the editor's buttons. The tests
/// down to the one of a button that adds what a panel edits are the Mac's tests of the same rules
/// (<c>StudioInspectorPanelTests.swift</c>); the others hold the glyphs and the words to what
/// Windows shows.
/// </summary>
public sealed class StudioInspectorPanelTests
{
    private static readonly StudioInspectorPanel[] EveryPanel = Enum.GetValues<StudioInspectorPanel>();

    [Fact]
    public void ARecordingWithACamera_HasEveryPanelInRailOrder()
    {
        Assert.Equal(
            new[]
            {
                StudioInspectorPanel.Scene,
                StudioInspectorPanel.Background,
                StudioInspectorPanel.Screen,
                StudioInspectorPanel.Camera,
                StudioInspectorPanel.Zoom,
                StudioInspectorPanel.Cut,
                StudioInspectorPanel.Speed,
                StudioInspectorPanel.Audio,
                StudioInspectorPanel.Project,
            },
            StudioInspectorPanels.GetAvailable(hasCamera: true));
        Assert.Equal(EveryPanel.ToHashSet(), StudioInspectorPanels.GetAvailable(hasCamera: true).ToHashSet());
        Assert.Equal(StudioInspectorPanels.GetAvailable(hasCamera: true), StudioInspectorPanels.All);
    }

    [Fact]
    public void ARecordingWithoutACamera_HasNoSceneOrCameraPanel()
    {
        Assert.Equal(
            new[]
            {
                StudioInspectorPanel.Background,
                StudioInspectorPanel.Screen,
                StudioInspectorPanel.Zoom,
                StudioInspectorPanel.Cut,
                StudioInspectorPanel.Speed,
                StudioInspectorPanel.Audio,
                StudioInspectorPanel.Project,
            },
            StudioInspectorPanels.GetAvailable(hasCamera: false));
    }

    [Fact]
    public void TheRailGroups_LookThenTimelineEditsThenTheRest()
    {
        var timelineEdits = new[] { StudioInspectorPanel.Zoom, StudioInspectorPanel.Cut, StudioInspectorPanel.Speed };
        var rest = new[] { StudioInspectorPanel.Audio, StudioInspectorPanel.Project };

        var withCamera = StudioInspectorPanels.GetGroups(hasCamera: true);
        Assert.Equal(3, withCamera.Count);
        Assert.Equal(
            new[] { StudioInspectorPanel.Scene, StudioInspectorPanel.Background, StudioInspectorPanel.Screen, StudioInspectorPanel.Camera },
            withCamera[0]);
        Assert.Equal(timelineEdits, withCamera[1]);
        Assert.Equal(rest, withCamera[2]);

        var withoutCamera = StudioInspectorPanels.GetGroups(hasCamera: false);
        Assert.Equal(3, withoutCamera.Count);
        Assert.Equal(new[] { StudioInspectorPanel.Background, StudioInspectorPanel.Screen }, withoutCamera[0]);
        Assert.Equal(timelineEdits, withoutCamera[1]);
        Assert.Equal(rest, withoutCamera[2]);

        // The groups, one after the other, are the panels of the rail.
        foreach (var hasCamera in new[] { true, false })
        {
            Assert.Equal(
                StudioInspectorPanels.GetAvailable(hasCamera),
                StudioInspectorPanels.GetGroups(hasCamera).SelectMany(group => group));
        }
    }

    [Fact]
    public void AProjectOpensOnAPanelItHas()
    {
        Assert.Equal(StudioInspectorPanel.Scene, StudioInspectorPanels.GetInitial(hasCamera: true));
        Assert.Equal(StudioInspectorPanel.Background, StudioInspectorPanels.GetInitial(hasCamera: false));
    }

    [Fact]
    public void APanelTheRecordingLacks_ResolvesToItsNeighbor()
    {
        Assert.Equal(StudioInspectorPanel.Background, StudioInspectorPanels.Resolve(StudioInspectorPanel.Scene, hasCamera: false));
        Assert.Equal(StudioInspectorPanel.Screen, StudioInspectorPanels.Resolve(StudioInspectorPanel.Camera, hasCamera: false));
        foreach (var hasCamera in new[] { true, false })
        {
            var available = StudioInspectorPanels.GetAvailable(hasCamera);
            foreach (var panel in EveryPanel)
            {
                var resolved = StudioInspectorPanels.Resolve(panel, hasCamera);
                Assert.Contains(resolved, available);
                if (available.Contains(panel))
                {
                    Assert.Equal(panel, resolved);
                }
            }
        }
    }

    [Fact]
    public void EveryPanelHasATitleAGlyphAndASummaryOfItsOwn()
    {
        Assert.Equal(EveryPanel.Length, EveryPanel.Select(StudioInspectorPanels.GetTitle).Distinct().Count());
        Assert.Equal(EveryPanel.Length, EveryPanel.Select(StudioInspectorPanels.GetGlyph).Distinct().Count());
        Assert.Equal(EveryPanel.Length, EveryPanel.Select(StudioInspectorPanels.GetSummary).Distinct().Count());
        Assert.DoesNotContain(EveryPanel, panel =>
            StudioInspectorPanels.GetTitle(panel).Length == 0
            || StudioInspectorPanels.GetGlyph(panel).Length == 0
            || StudioInspectorPanels.GetSummary(panel).Length == 0);

        // One glyph of the icon font each, from its private use area.
        Assert.All(EveryPanel, panel =>
        {
            var glyph = StudioInspectorPanels.GetGlyph(panel);
            Assert.Equal(1, glyph.Length);
            Assert.InRange(glyph[0], '\uE700', '\uF8FF');
        });
    }

    [Fact]
    public void AButtonThatAddsWhatAPanelEditsHasThatPanelsGlyph()
    {
        Assert.Equal(StudioInspectorPanels.GetGlyph(StudioInspectorPanel.Zoom), StudioGlyphs.AddZoom);
        Assert.Equal(StudioInspectorPanels.GetGlyph(StudioInspectorPanel.Cut), StudioGlyphs.AddCut);
        Assert.Equal(StudioInspectorPanels.GetGlyph(StudioInspectorPanel.Speed), StudioGlyphs.AddSpeed);
        Assert.Equal(StudioInspectorPanels.GetGlyph(StudioInspectorPanel.Scene), StudioGlyphs.ShowScene);
        Assert.NotEqual(StudioGlyphs.TrimStart, StudioGlyphs.TrimEnd);
        Assert.DoesNotContain(StudioGlyphs.All, glyph => glyph.Length == 0);
    }

    [Theory]
    [InlineData(nameof(StudioGlyphs.SplitScene), '\uE90C')]
    [InlineData(nameof(StudioGlyphs.AddZoom), '\uE8A3')]
    [InlineData(nameof(StudioGlyphs.AddCut), '\uE8C6')]
    [InlineData(nameof(StudioGlyphs.AddSpeed), '\uEC4A')]
    [InlineData(nameof(StudioGlyphs.SuggestZooms), '\uE794')]
    [InlineData(nameof(StudioGlyphs.RemoveSuggestions), '\uEA39')]
    [InlineData(nameof(StudioGlyphs.Delete), '\uE74D')]
    [InlineData(nameof(StudioGlyphs.TrimStart), '\uEA52')]
    [InlineData(nameof(StudioGlyphs.TrimEnd), '\uE8B5')]
    [InlineData(nameof(StudioGlyphs.ResetCrop), '\uE777')]
    [InlineData(nameof(StudioGlyphs.ShowScene), '\uE8B2')]
    [InlineData(nameof(StudioGlyphs.SaveDefaultLook), '\uE790')]
    [InlineData(nameof(StudioGlyphs.SaveRecording), '\uE74E')]
    [InlineData(nameof(StudioGlyphs.OpenProject), '\uE838')]
    [InlineData(nameof(StudioGlyphs.OpenRecentProject), '\uE823')]
    [InlineData(nameof(StudioGlyphs.SaveProject), '\uE74E')]
    [InlineData(nameof(StudioGlyphs.DeleteProject), '\uE74D')]
    public void AButtonsGlyph_IsTheOneTheHandoffNames(string button, char glyph)
    {
        // The table of windows/docs/studio-button-icons.md, so that a glyph is not changed in passing.
        var property = typeof(StudioGlyphs).GetProperty(button);
        Assert.NotNull(property);
        Assert.Equal(glyph.ToString(), property.GetValue(null));
    }

    [Fact]
    public void EveryButtonGlyphIsNamedOnce_AndButtonsThatDoDifferentThingsShowDifferentPictures()
    {
        var named = typeof(StudioGlyphs)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(property => property.PropertyType == typeof(string))
            .ToDictionary(property => property.Name, property => (string)property.GetValue(null)!);

        // Every glyph there is a name for is in the list of all of them, after the rail's nine.
        Assert.Equal(17, named.Count);
        Assert.Equal(EveryPanel.Select(StudioInspectorPanels.GetGlyph), StudioGlyphs.All.Take(EveryPanel.Length));
        Assert.Equal(named.Values.Order(StringComparer.Ordinal), StudioGlyphs.All.Skip(EveryPanel.Length).Order(StringComparer.Ordinal));

        // One glyph of the icon font each, from its private use area.
        Assert.All(StudioGlyphs.All, glyph =>
        {
            Assert.Equal(1, glyph.Length);
            Assert.InRange(glyph[0], '\uE700', '\uF8FF');
        });

        // Two names share a glyph only where the two buttons do one thing: save, and delete.
        Assert.Equal(StudioGlyphs.SaveRecording, StudioGlyphs.SaveProject);
        Assert.Equal(StudioGlyphs.Delete, StudioGlyphs.DeleteProject);
        var apart = named.Where(entry => entry.Key is not (nameof(StudioGlyphs.SaveProject) or nameof(StudioGlyphs.DeleteProject))).ToArray();
        Assert.Equal(apart.Length, apart.Select(entry => entry.Value).Distinct().Count());

        // A button that is not about a panel does not look like the rail's item of one.
        var rail = EveryPanel.Select(StudioInspectorPanels.GetGlyph).ToHashSet();
        string[] ofAPanel = [nameof(StudioGlyphs.AddZoom), nameof(StudioGlyphs.AddCut), nameof(StudioGlyphs.AddSpeed), nameof(StudioGlyphs.ShowScene)];
        Assert.DoesNotContain(named, entry => !ofAPanel.Contains(entry.Key) && rail.Contains(entry.Value));
    }

    [Theory]
    [InlineData(StudioInspectorPanel.Scene, "Scene", "Scene: layout, splits, and transitions")]
    [InlineData(StudioInspectorPanel.Background, "Background", "Background and padding")]
    [InlineData(StudioInspectorPanel.Screen, "Screen", "Screen: corners, shadow, clicks, and crop")]
    [InlineData(StudioInspectorPanel.Camera, "Camera", "Camera: placement, appearance, and crop")]
    [InlineData(StudioInspectorPanel.Zoom, "Zoom", "Zooms")]
    [InlineData(StudioInspectorPanel.Cut, "Cut", "Cuts")]
    [InlineData(StudioInspectorPanel.Speed, "Speed", "Speed changes")]
    [InlineData(StudioInspectorPanel.Audio, "Audio", "Audio: mute")]
    [InlineData(StudioInspectorPanel.Project, "Project", "Project: badge, storage, and default look")]
    public void APanel_IsCalledWhatItsRailItemSays_AndItsHelpSaysWhatItHoldsOnWindows(
        StudioInspectorPanel panel,
        string title,
        string summary)
    {
        // On Windows the sound of a recording is one track, so Audio holds Mute and no volumes,
        // and its help does not promise any. Everything else is as on the Mac.
        Assert.Equal(title, StudioInspectorPanels.GetTitle(panel));
        Assert.Equal(summary, StudioInspectorPanels.GetSummary(panel));
    }
}
