using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.App.Controls.Studio;

internal static class StudioTimelineLayout
{
    internal static double MinimumDetailHeight(
        StudioProject project,
        Func<StudioTimelineLane, double> laneHeight,
        double trimBarHeight,
        double spacing)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(laneHeight);

        if (project.SchemaVersion < 2 || project.Timeline is not { Clips.Length: > 1 } timeline)
        {
            return 0;
        }

        var maximum = trimBarHeight;
        foreach (var clip in timeline.Clips)
        {
            var local = StudioTimelineProjection.ProjectForClip(project, clip.Id);
            var height = trimBarHeight;
            foreach (var lane in StudioTimelineLanes.GetShown(local))
            {
                height += spacing + laneHeight(lane);
            }

            maximum = Math.Max(maximum, height);
        }

        return maximum;
    }
}
