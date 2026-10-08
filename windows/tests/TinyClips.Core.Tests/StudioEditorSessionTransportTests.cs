using TinyClips.Core.Studio.Editing;

namespace TinyClips.Core.Tests;

/// <summary>Play, pause, step and scrub, and how the playhead follows the preview.</summary>
public sealed class StudioEditorSessionTransportTests : StudioEditorSessionTestBase
{
    /// <summary>Opens a ten second recording trimmed to 2 through 8, with the playhead at 5.</summary>
    private async Task<StudioEditorSession> OpenTrimmedAsync()
    {
        var session = await OpenAsync(CreateProject());
        session.SetTrimStart(2);
        session.SetTrimEnd(8);
        session.Scrub(5);
        Preview.Calls.Clear();
        Preview.Seeks.Clear();
        Changes.Clear();
        return session;
    }

    [Fact]
    public async Task Play_InsideTheTrim_PlaysFromThePlayhead()
    {
        var session = await OpenTrimmedAsync();

        session.TogglePlayback();

        Assert.True(session.IsPlaying);
        Assert.Equal(5, session.Playhead, Precision);
        Assert.Equal(new[] { "Play" }, Preview.Calls);
        Assert.Equal(new[] { StudioEditorChanges.Playback }, Changes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(7.99)]
    [InlineData(9.5)]
    public async Task Play_OutsideTheTrimOrAtItsEnd_StartsAtTheTrimStart(double playhead)
    {
        var session = await OpenTrimmedAsync();
        session.Scrub(playhead);
        Preview.Calls.Clear();
        Preview.Seeks.Clear();

        session.TogglePlayback();

        Assert.True(session.IsPlaying);
        Assert.Equal(2, session.Playhead, Precision);
        Assert.Equal(new[] { "Seek", "Play" }, Preview.Calls);
        Assert.Equal(2, Assert.Single(Preview.Seeks), Precision);
    }

    [Fact]
    public async Task Play_WhilePlaying_Pauses()
    {
        var session = await OpenTrimmedAsync();
        session.TogglePlayback();

        session.TogglePlayback();

        Assert.False(session.IsPlaying);
        Assert.Equal(new[] { "Play", "Pause" }, Preview.Calls);
    }

    [Fact]
    public async Task Playhead_FollowsThePreviewOnlyWhilePlaying()
    {
        var session = await OpenTrimmedAsync();

        Preview.RaisePosition(3);
        Pump();
        Assert.Equal(5, session.Playhead, Precision);

        session.TogglePlayback();
        Preview.RaisePosition(5.5);
        Assert.Equal(5, session.Playhead, Precision);
        Pump();
        Assert.Equal(5.5, session.Playhead, Precision);

        session.Pause();
        Preview.RaisePosition(6);
        Pump();
        Assert.Equal(5.5, session.Playhead, Precision);
    }

    [Fact]
    public async Task ManyPositionsBetweenTwoTurnsOfTheSessionThread_ArePostedOnce()
    {
        var session = await OpenTrimmedAsync();
        session.TogglePlayback();

        Preview.RaisePosition(5.1);
        Preview.RaisePosition(5.2);
        Preview.RaisePosition(5.3);

        Assert.Equal(1, PostedCount);
        Pump();
        Assert.Equal(5.3, session.Playhead, Precision);

        Preview.RaisePosition(5.4);
        Pump();
        Assert.Equal(5.4, session.Playhead, Precision);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(8.02)]
    public async Task Playback_StopsAtTheTrimEnd(double position)
    {
        var session = await OpenTrimmedAsync();
        session.TogglePlayback();
        Preview.Calls.Clear();

        Preview.RaisePosition(position);
        Pump();

        Assert.False(session.IsPlaying);
        Assert.Equal(8, session.Playhead, Precision);
        Assert.Equal(new[] { "Pause", "Seek" }, Preview.Calls);
        Assert.Equal(8, Assert.Single(Preview.Seeks), Precision);
    }

    [Fact]
    public async Task PreviewStoppingByItself_IsReflected_AndItsPositionIsTaken()
    {
        var session = await OpenTrimmedAsync();
        session.TogglePlayback();
        Preview.Calls.Clear();

        Preview.Position = 6.2;
        Preview.RaiseIsPlaying(false);
        Assert.True(session.IsPlaying);
        Pump();

        Assert.False(session.IsPlaying);
        Assert.Equal(6.2, session.Playhead, Precision);
        Assert.Empty(Preview.Calls);
    }

    [Fact]
    public async Task PreviewReachingTheEndOfTheRecording_LeavesThePlayheadAtTheTrimEnd_SoPlayStartsOver()
    {
        var session = await OpenAsync(CreateProject());
        session.Scrub(9);
        session.TogglePlayback();
        Preview.Calls.Clear();
        Preview.Seeks.Clear();

        // The last frame of a ten second recording at 30 frames per second starts here.
        Preview.Position = 10 - Frame;
        Preview.RaisePosition(10 - Frame);
        Preview.RaiseIsPlaying(false);
        Pump();

        Assert.False(session.IsPlaying);
        Assert.Equal(10, session.Playhead, Precision);
        Assert.Equal("0:10.0 / 0:10.0", session.TimeText);

        session.TogglePlayback();

        Assert.True(session.IsPlaying);
        Assert.Equal(0, session.Playhead, Precision);
        Assert.Equal(new[] { "Seek", "Seek", "Play" }, Preview.Calls);
        Assert.Equal(new[] { 10.0, 0.0 }, Preview.Seeks);
    }

    [Fact]
    public async Task StopNotice_FromBeforeTheLatestPlay_IsIgnored()
    {
        var session = await OpenTrimmedAsync();
        session.TogglePlayback();
        session.TogglePlayback();

        // The preview reports the pause late, after Play was pressed again, and has not started yet.
        Preview.RaiseIsPlaying(false);
        session.TogglePlayback();
        Preview.IsPlaying = false;
        Pump();

        Assert.True(session.IsPlaying);
    }

    [Fact]
    public async Task Pause_TakesTheFrameThePreviewStoppedOn()
    {
        var session = await OpenTrimmedAsync();
        session.TogglePlayback();
        Preview.RaisePosition(5.5);
        Pump();
        Changes.Clear();

        // The preview has played two more frames that the session has not heard about yet.
        Preview.Position = 5.5 + 2 * Frame;
        session.Pause();

        Assert.False(session.IsPlaying);
        Assert.Equal(5.5 + 2 * Frame, session.Playhead, Precision);
        Assert.Equal(new[] { StudioEditorChanges.Playback }, Changes);
    }

    [Fact]
    public async Task Pause_WhenNotPlaying_LeavesThePlayheadAlone()
    {
        var session = await OpenTrimmedAsync();
        Preview.Position = 1;

        session.Pause();

        Assert.Equal(5, session.Playhead, Precision);
        Assert.Empty(Changes);
    }

    [Fact]
    public async Task Step_WhilePlaying_StartsFromTheFrameThePreviewStoppedOn()
    {
        var session = await OpenTrimmedAsync();
        session.TogglePlayback();
        Preview.RaisePosition(5.5);
        Pump();

        // Three more frames went by before the step was handled.
        Preview.Position = 5.5 + 3 * Frame;
        session.StepFrames(1);

        Assert.False(session.IsPlaying);
        Assert.Equal(5.5 + 4 * Frame, session.Playhead, Precision);
        Assert.Equal(5.5 + 4 * Frame, Preview.Seeks[^1], Precision);

        // The notice for a frame that was played before the pause arrives afterwards.
        Preview.RaisePosition(5.5 + 3 * Frame);
        Pump();
        Assert.Equal(5.5 + 4 * Frame, session.Playhead, Precision);
    }

    [Fact]
    public async Task Step_PausesAndMovesByWholeFrames()
    {
        var session = await OpenTrimmedAsync();
        session.TogglePlayback();
        Preview.Calls.Clear();

        session.StepFrames(1);

        Assert.False(session.IsPlaying);
        Assert.Equal(5 + Frame, session.Playhead, Precision);
        Assert.Equal(new[] { "Pause", "Seek" }, Preview.Calls);
        Assert.Equal(5 + Frame, Preview.Seeks[^1], Precision);

        session.StepFrames(-3);
        Assert.Equal(5 - 2 * Frame, session.Playhead, Precision);
        Assert.Equal(5 - 2 * Frame, Preview.Seeks[^1], Precision);
    }

    [Fact]
    public async Task StepAndScrub_StayInsideTheRecording()
    {
        var session = await OpenTrimmedAsync();

        session.Scrub(-3);
        Assert.Equal(0, session.Playhead, Precision);
        session.StepFrames(-1);
        Assert.Equal(0, session.Playhead, Precision);

        session.Scrub(99);
        Assert.Equal(10, session.Playhead, Precision);
        session.StepFrames(1);
        Assert.Equal(10, session.Playhead, Precision);

        session.Scrub(double.NaN);
        Assert.Equal(0, session.Playhead, Precision);
        Assert.All(Preview.Seeks, seek => Assert.InRange(seek, 0, 10));
    }

    [Fact]
    public async Task Scrub_PausesAndSeeks()
    {
        var session = await OpenTrimmedAsync();
        session.TogglePlayback();
        Preview.Calls.Clear();
        Changes.Clear();

        session.Scrub(3.5);

        Assert.False(session.IsPlaying);
        Assert.Equal(3.5, session.Playhead, Precision);
        Assert.Equal(new[] { "Pause", "Seek" }, Preview.Calls);
        Assert.Equal(new[] { StudioEditorChanges.Playback }, Changes);
    }

    [Fact]
    public async Task Transport_IsRefusedWhileExporting()
    {
        var session = await OpenTrimmedAsync();
        var export = session.ExportAsync(() => ExportPath, default);
        Preview.Calls.Clear();

        session.TogglePlayback();
        session.StepFrames(1);
        session.Scrub(3);

        Assert.False(session.IsPlaying);
        Assert.Equal(5, session.Playhead, Precision);
        Assert.Empty(Preview.Calls);

        session.CancelExport();
        await FinishAsync(export);
    }

    [Fact]
    public async Task Transport_IsRefusedWhenTheProjectIsUnavailable()
    {
        var session = await OpenAsync(CreateProject(writeScreenFile: false));

        session.TogglePlayback();
        session.StepFrames(1);
        session.Scrub(3);

        Assert.False(session.IsPlaying);
        Assert.Equal(0, session.Playhead, Precision);
    }
}
