using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.Core.Tests;

/// <summary>What the primary clip strip shows of a project, and the words it uses.</summary>
public sealed class StudioClipStripTests : StudioEditorSessionTestBase
{
    private async Task<StudioEditorSession> OpenSplitAsync()
    {
        var session = await OpenAsync(CreateProject());
        session.Scrub(4);
        Assert.True(session.SplitClipAtPlayhead());
        return session;
    }

    [Fact]
    public async Task ASingleRecordingIsOneClipThatSpansIt()
    {
        var session = await OpenAsync(CreateProject());

        var items = StudioClipStrip.Build(session.Manifest!, session.TimelineMap, session.SelectedClipId, "Recording", 9.5);

        var item = Assert.Single(items);
        Assert.Equal(session.Manifest!.Id, item.Id);
        Assert.True(item.IsSelected);
        Assert.Equal(0, item.ProjectStart);
        Assert.Equal(9.5, item.ProjectEnd);
        Assert.Equal("Recording", item.Name);
        Assert.Equal(StudioClipTransition.Cut, item.TransitionIn);
    }

    [Fact]
    public async Task ClipsAreInOrderInProjectTimeWithTheSelectedOneMarked()
    {
        var session = await OpenSplitAsync();

        var items = StudioClipStrip.Build(session.Manifest!, session.TimelineMap, session.SelectedClipId, "Recording", session.ProjectDuration);

        Assert.Equal(2, items.Count);
        Assert.Equal(session.TimelineMap!.Clips.Select(clip => clip.ClipId), items.Select(item => item.Id));
        Assert.Equal([false, true], items.Select(item => item.IsSelected));
        Assert.Equal(0, items[0].ProjectStart, Precision);
        Assert.Equal(items[0].ProjectEnd, items[1].ProjectStart, Precision);
        Assert.Equal(session.ProjectDuration, items[1].ProjectEnd, Precision);
        Assert.All(items, item => Assert.Equal(2, item.Count));
        Assert.Equal([0, 1], items.Select(item => item.Index));
    }

    [Fact]
    public async Task AJoinBelongsToTheClipThatComesInAndTheFirstClipHasNone()
    {
        var session = await OpenSplitAsync();
        var second = session.TimelineMap!.Clips[1].ClipId;
        Assert.True(session.SetClipTransition(second, StudioClipTransition.FadeThroughBlack));

        var items = StudioClipStrip.Build(session.Manifest!, session.TimelineMap, session.SelectedClipId, "Recording", session.ProjectDuration);

        Assert.Equal(StudioClipTransition.Cut, items[0].TransitionIn);
        Assert.Equal(StudioClipTransition.FadeThroughBlack, items[1].TransitionIn);
    }

    [Fact]
    public void AClipWithoutANameIsCalledByItsPosition()
    {
        Assert.Equal("Clip 3", StudioClipStrip.GetClipName(null, 2));
        Assert.Equal("Clip 1", StudioClipStrip.GetClipName("   ", 0));
        Assert.Equal("Intro", StudioClipStrip.GetClipName("  Intro ", 4));
    }

    [Fact]
    public void ScreenReadersGetThePositionTheNameAndTheLength()
    {
        var item = new StudioClipStripItem("c", 1, 3, "Demo", 4, 8.5, StudioClipTransition.Cut, false);

        Assert.Equal(4.5, item.Duration);
        Assert.StartsWith("Clip 2 of 3: Demo, ", item.AccessibleName, StringComparison.Ordinal);
        Assert.Equal("Transition from Intro to Demo", StudioClipStrip.GetJoinAccessibleName("Intro", "Demo"));
    }

    [Theory]
    [InlineData(StudioClipTransition.Cut, "Cut")]
    [InlineData(StudioClipTransition.FadeThroughBlack, "Fade Through Black")]
    [InlineData(StudioClipTransition.FadeThroughWhite, "Fade Through White")]
    [InlineData(StudioClipTransition.CrossDissolve, "Cross-Dissolve")]
    public void EveryJoinHasAName(StudioClipTransition transition, string name)
    {
        Assert.Equal(name, StudioClipStrip.GetTransitionName(transition));
        Assert.False(string.IsNullOrEmpty(StudioClipStrip.GetTransitionGlyph(transition)));
    }

    [Fact]
    public void MessagesSayWhatHappened()
    {
        Assert.Equal("Clip added to the timeline.", StudioClipStrip.GetClipAddedMessage(1));
        Assert.Equal("3 clips added to the timeline.", StudioClipStrip.GetClipAddedMessage(3));
        Assert.Equal("Demo moved to position 2 of 4.", StudioClipStrip.GetClipMovedMessage("Demo", 2, 4));
        Assert.Equal("Demo removed from the timeline.", StudioClipStrip.GetClipRemovedMessage("Demo"));
        Assert.Equal("Demo split at the playhead.", StudioClipStrip.GetClipSplitMessage("Demo"));
        Assert.Equal("Cross-Dissolve before Demo.", StudioClipStrip.GetTransitionSetMessage("Demo", StudioClipTransition.CrossDissolve));
    }
}
