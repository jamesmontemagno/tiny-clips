using TinyClips.App.Controls.Studio;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.App.Tests;

public sealed class StudioTimelineLayoutTests : StudioViewModelTestBase
{
    [Fact]
    public void MinimumDetailHeightReservesTheTallestClipAndLeavesSingleClipsCompact()
    {
        var id = CreateProject();
        var single = Projects.Load(id);
        var timeline = StudioTimelineEditing.ToTimelineProject(single);
        var lane = timeline.Timeline!;
        var first = lane.Clips[0] with
        {
            Id = "sparse",
            Name = "Sparse",
            SourceStart = 0,
            SourceEnd = 5,
            Zooms = [],
        };
        var second = first with
        {
            Id = "zoomed",
            Name = "Zoomed",
            SourceStart = 5,
            SourceEnd = 10,
            Zooms = [new StudioZoom { Start = 6, End = 7 }],
        };
        var project = timeline with { Timeline = lane with { Clips = [first, second] } };
        Projects.Save(project);

        var laneHeights = new Dictionary<StudioTimelineLane, double>
        {
            [StudioTimelineLane.Scene] = 24,
            [StudioTimelineLane.Zoom] = 26,
            [StudioTimelineLane.Cut] = 24,
            [StudioTimelineLane.Speed] = 24,
            [StudioTimelineLane.Annotate] = 24,
        };
        double MinimumHeight(StudioProject value) =>
            StudioTimelineLayout.MinimumDetailHeight(value, timelineLane => laneHeights[timelineLane], trimBarHeight: 36, spacing: 4);

        Assert.Equal(0, MinimumHeight(single));
        Assert.Equal(66, MinimumHeight(project));
    }
}
