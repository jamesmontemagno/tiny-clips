namespace TinyClips.Core.Studio;

public readonly record struct StudioTimelinePosition(string ClipId, string SourceId, double SourceTime);

public sealed class StudioTimelineMap
{
    private readonly StudioTimelineMapClip[] _clips;

    public StudioTimelineMap(StudioTimeline timeline, IReadOnlyList<StudioMediaSource> sources)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(sources);

        var sourceById = sources.ToDictionary(source => source.Id, StringComparer.Ordinal);
        var clips = new List<StudioTimelineMapClip>();
        var projectStart = 0d;
        foreach (var clip in timeline.Clips)
        {
            if (!sourceById.TryGetValue(clip.SourceId, out var source)
                || !double.IsFinite(clip.SourceStart)
                || !double.IsFinite(clip.SourceEnd)
                || clip.SourceStart < 0
                || clip.SourceEnd <= clip.SourceStart
                || clip.SourceEnd > source.Sources.Screen.Duration)
            {
                throw new StudioProjectInvalidException("Timeline clip has an invalid source range or unknown source.");
            }

            var editor = new StudioEditorModel(new StudioProject
            {
                Sources = source.Sources,
                Edits = clip.Edits with
                {
                    TrimStart = Math.Max(clip.SourceStart, clip.Edits.TrimStart),
                    TrimEnd = Math.Min(clip.SourceEnd, clip.Edits.TrimEnd ?? clip.SourceEnd),
                },
            });
            if (!(editor.OutputDuration > 0))
            {
                throw new StudioProjectInvalidException("Timeline clip has no playable duration.");
            }

            var projectEnd = projectStart + editor.OutputDuration;
            clips.Add(new StudioTimelineMapClip(clip.Id, clip.SourceId, projectStart, projectEnd, editor));
            projectStart = projectEnd;
        }

        _clips = clips.ToArray();
        Duration = projectStart;
    }

    public double Duration { get; }

    public StudioTimelinePosition? SourcePositionAt(double projectTime)
    {
        if (_clips.Length == 0 || !double.IsFinite(projectTime))
        {
            return null;
        }

        var clamped = Math.Clamp(projectTime, 0, Duration);
        var clip = _clips.FirstOrDefault(entry => clamped < entry.ProjectEnd) ?? _clips[^1];
        var sourceTime = clip.Editor.GetSourceTime(Math.Clamp(clamped - clip.ProjectStart, 0, clip.ProjectEnd - clip.ProjectStart));
        return new StudioTimelinePosition(clip.ClipId, clip.SourceId, sourceTime);
    }

    public double? ProjectTimeFor(string clipId, double sourceTime)
    {
        if (!double.IsFinite(sourceTime))
        {
            return null;
        }

        var clip = _clips.FirstOrDefault(entry => string.Equals(entry.ClipId, clipId, StringComparison.Ordinal));
        return clip is null ? null : clip.ProjectStart + clip.Editor.GetOutputTime(sourceTime);
    }

    private sealed record StudioTimelineMapClip(
        string ClipId,
        string SourceId,
        double ProjectStart,
        double ProjectEnd,
        StudioEditorModel Editor);
}

public static class StudioTimelineEditing
{
    public static StudioProject ToTimelineProject(StudioProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.SchemaVersion >= 2)
        {
            return project;
        }

        var sourceId = $"source-{project.Id}";
        var clip = new StudioTimelineClip
        {
            Id = $"clip-{project.Id}",
            SourceId = sourceId,
            Name = project.Name,
            SourceStart = 0,
            SourceEnd = project.Sources.Screen.Duration,
            Edits = project.Edits,
            Screen = project.Screen,
            Camera = project.Camera,
            Scenes = project.Scenes,
            Zooms = project.Zooms,
            Audio = project.Audio,
            Overlays = project.Overlays,
            Captions = project.Captions,
        };
        return project with
        {
            SchemaVersion = 2,
            MediaSources = [new StudioMediaSource
            {
                Id = sourceId,
                Kind = project.Sources.Screen.External ? StudioMediaSourceKind.FlatVideo : StudioMediaSourceKind.NativeRecording,
                Sources = project.Sources,
            }],
            Timeline = new StudioTimeline { Clips = [clip] },
        };
    }

    public static StudioTimeline Append(StudioTimeline timeline, StudioTimelineClip clip) =>
        timeline with { Clips = [.. timeline.Clips, clip] };

    public static StudioTimeline Insert(StudioTimeline timeline, StudioTimelineClip clip, int index)
    {
        var clips = timeline.Clips.ToList();
        clips.Insert(Math.Clamp(index, 0, clips.Count), clip);
        return timeline with { Clips = clips.ToArray() };
    }

    public static StudioTimeline? Move(StudioTimeline timeline, string clipId, int index)
    {
        var clips = timeline.Clips.ToList();
        var oldIndex = clips.FindIndex(clip => string.Equals(clip.Id, clipId, StringComparison.Ordinal));
        if (oldIndex < 0)
        {
            return null;
        }

        var clip = clips[oldIndex];
        clips.RemoveAt(oldIndex);
        clips.Insert(Math.Clamp(index, 0, clips.Count), clip);
        return timeline with { Clips = clips.ToArray() };
    }

    public static StudioTimeline? Remove(StudioTimeline timeline, string clipId)
    {
        if (!timeline.Clips.Any(clip => string.Equals(clip.Id, clipId, StringComparison.Ordinal)))
        {
            return null;
        }

        return timeline with
        {
            Clips = timeline.Clips.Where(clip => !string.Equals(clip.Id, clipId, StringComparison.Ordinal)).ToArray(),
        };
    }

    public static StudioTimeline? Trim(StudioTimeline timeline, string clipId, double sourceStart, double sourceEnd)
    {
        var index = Array.FindIndex(timeline.Clips, clip => string.Equals(clip.Id, clipId, StringComparison.Ordinal));
        if (index < 0 || !double.IsFinite(sourceStart) || !double.IsFinite(sourceEnd))
        {
            return null;
        }

        var clip = timeline.Clips[index];
        if (sourceStart < clip.SourceStart || sourceEnd > clip.SourceEnd || sourceEnd <= sourceStart)
        {
            return null;
        }

        var clips = (StudioTimelineClip[])timeline.Clips.Clone();
        clips[index] = clip with
        {
            SourceStart = sourceStart,
            SourceEnd = sourceEnd,
            Edits = ClipEdits(clip.Edits, sourceStart, sourceEnd),
        };
        return timeline with { Clips = clips };
    }

    public static StudioTimeline? Split(StudioTimeline timeline, string clipId, double sourceTime)
    {
        var index = Array.FindIndex(timeline.Clips, clip => string.Equals(clip.Id, clipId, StringComparison.Ordinal));
        if (index < 0)
        {
            return null;
        }

        var clip = timeline.Clips[index];
        if (!double.IsFinite(sourceTime) || sourceTime <= clip.SourceStart || sourceTime >= clip.SourceEnd)
        {
            return null;
        }

        var left = clip with
        {
            SourceEnd = sourceTime,
            Edits = ClipEdits(clip.Edits, clip.SourceStart, sourceTime),
        };
        var right = clip with
        {
            Id = Guid.NewGuid().ToString("D"),
            SourceStart = sourceTime,
            Edits = ClipEdits(clip.Edits, sourceTime, clip.SourceEnd),
        };
        var clips = timeline.Clips.ToList();
        clips.RemoveAt(index);
        clips.InsertRange(index, [left, right]);
        return timeline with { Clips = clips.ToArray() };
    }

    private static StudioEdits ClipEdits(StudioEdits edits, double start, double end) =>
        edits with
        {
            TrimStart = Math.Max(start, edits.TrimStart),
            TrimEnd = Math.Min(end, edits.TrimEnd ?? end),
            Cuts = edits.Cuts
                .Select(range => range with { Start = Math.Max(start, range.Start), End = Math.Min(end, range.End) })
                .Where(range => range.End > range.Start)
                .ToArray(),
            Speed = edits.Speed
                .Select(range => range with { Start = Math.Max(start, range.Start), End = Math.Min(end, range.End) })
                .Where(range => range.End > range.Start)
                .ToArray(),
        };
}
