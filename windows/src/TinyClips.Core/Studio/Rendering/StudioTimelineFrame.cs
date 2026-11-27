namespace TinyClips.Core.Studio.Rendering;

internal readonly record struct StudioTimelineLayer(StudioTimelineMapClip Clip, double SourceTime, double Opacity);
internal sealed record StudioTimelineFrame(StudioTimelineLayer[] Layers, double ColorOpacity, bool White)
{
    public static StudioTimelineFrame At(StudioTimelineMap map, double projectTime)
    {
        var time = Math.Clamp(projectTime, 0, map.Duration);
        var active = map.Clips.Where(clip => clip.ProjectStart <= time && time < clip.ProjectEnd).ToArray();
        if (active.Length == 0)
        {
            active = [map.Clips[^1]];
        }
        var layers = active.Select(clip => new StudioTimelineLayer(clip,
            clip.TimeMap.OutputToSource(Math.Clamp(time - clip.ProjectStart, 0, clip.TimeMap.OutputDuration)),
            Gain(map, clip, time))).ToArray();
        foreach (var clip in map.Clips)
        {
            if (clip.Entry is { Kind: StudioClipTransition.FadeThroughBlack or StudioClipTransition.FadeThroughWhite } join
                && time >= join.Start && time < join.End)
            {
                var midpoint = clip.ProjectStart;
                var opacity = time < midpoint ? (time - join.Start) / (midpoint - join.Start)
                    : (join.End - time) / (join.End - midpoint);
                return new(layers, opacity, join.Kind == StudioClipTransition.FadeThroughWhite);
            }
        }
        return new(layers, 0, false);
    }

    public static double Gain(StudioTimelineMap map, StudioTimelineMapClip clip, double time)
    {
        if (clip.Entry is { Kind: StudioClipTransition.CrossDissolve } entry && time < entry.End)
        {
            return Math.Clamp((time - entry.Start) / (entry.End - entry.Start), 0, 1);
        }
        for (var i = 1; i < map.Clips.Count; i++)
        {
            if (map.Clips[i - 1].ClipId == clip.ClipId
                && map.Clips[i].Entry is { Kind: StudioClipTransition.CrossDissolve } exit && time >= exit.Start)
                return Math.Clamp((exit.End - time) / (exit.End - exit.Start), 0, 1);
        }
        return 1;
    }
}
