namespace TinyClips.Core.Studio;

public readonly record struct StudioTimelinePosition(string ClipId, string SourceId, double SourceTime);

public sealed record StudioTimelineJoin(StudioClipTransition Kind, double Start, double End);

public sealed record StudioTimelineMapClip(
    string ClipId,
    string SourceId,
    double ProjectStart,
    double ProjectEnd,
    StudioTimeMap TimeMap,
    StudioTimelineJoin? Entry);

public sealed class StudioTimelineMap
{
    private readonly StudioTimelineMapClip[] _clips;

    public StudioTimelineMap(StudioTimeline timeline, IReadOnlyList<StudioMediaSource> sources)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(sources);

        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (timeline.Clips.Length == 0
            || sources.Any(source => string.IsNullOrWhiteSpace(source.Id)
                || !ids.Add(source.Id)
                || (source.Kind == StudioMediaSourceKind.FlatVideo
                    && (source.Sources.Camera is not null || source.Sources.Events is not null)))
            || timeline.Clips.Any(clip => string.IsNullOrWhiteSpace(clip.Id) || !ids.Add(clip.Id)))
        {
            throw new StudioProjectInvalidException("A timeline requires at least one clip, distinct IDs, and valid source layers.");
        }

        var sourceById = sources.ToDictionary(source => source.Id, StringComparer.Ordinal);
        var clips = new List<StudioTimelineMapClip>();
        foreach (var clip in timeline.Clips)
        {
            if (!sourceById.TryGetValue(clip.SourceId, out var source)
                || !double.IsFinite(clip.SourceStart)
                || !double.IsFinite(clip.SourceEnd)
                || clip.SourceStart < 0
                || clip.SourceEnd <= clip.SourceStart
                || !double.IsFinite(source.Sources.Screen.Duration)
                || clip.SourceEnd > source.Sources.Screen.Duration)
            {
                throw new StudioProjectInvalidException("Timeline clip has an invalid source range or unknown source.");
            }

            var timeMap = new StudioTimeMap(source.Sources.Screen.Duration, (clip.Edits ?? new StudioEdits()) with
            {
                TrimStart = Math.Max(clip.SourceStart, clip.Edits?.TrimStart ?? 0),
                TrimEnd = Math.Min(clip.SourceEnd, clip.Edits?.TrimEnd ?? clip.SourceEnd),
            });
            if (!(timeMap.OutputDuration > 0) || !double.IsFinite(timeMap.OutputDuration))
            {
                throw new StudioProjectInvalidException("Timeline clip has no playable duration.");
            }

            var previous = clips.LastOrDefault();
            StudioTimelineJoin? entry = null;
            var projectStart = previous?.ProjectEnd ?? 0;
            if (previous is not null && clip.TransitionIn != StudioClipTransition.Cut)
            {
                var limit = Math.Min(previous.TimeMap.OutputDuration, timeMap.OutputDuration) / 2;
                var length = Math.Min(clip.TransitionIn == StudioClipTransition.CrossDissolve ? 0.5 : 0.25, limit);
                entry = new StudioTimelineJoin(
                    clip.TransitionIn,
                    projectStart - length,
                    clip.TransitionIn == StudioClipTransition.CrossDissolve ? projectStart : projectStart + length);
                if (clip.TransitionIn == StudioClipTransition.CrossDissolve)
                {
                    projectStart = entry.Start;
                }
            }

            var projectEnd = projectStart + timeMap.OutputDuration;
            if (!double.IsFinite(projectEnd) || projectEnd <= projectStart)
            {
                throw new StudioProjectInvalidException("Clip duration cannot be represented on the project timeline.");
            }
            clips.Add(new StudioTimelineMapClip(clip.Id, clip.SourceId, projectStart, projectEnd, timeMap, entry));
        }

        _clips = clips.ToArray();
        Duration = _clips[^1].ProjectEnd;
    }

    public double Duration { get; }

    public IReadOnlyList<StudioTimelineMapClip> Clips => _clips;

    public StudioTimelinePosition? SourcePositionAt(double projectTime, string? preferredClipId = null)
    {
        if (_clips.Length == 0 || !double.IsFinite(projectTime))
        {
            return null;
        }

        var clamped = Math.Clamp(projectTime, 0, Duration);
        var clip = _clips.FirstOrDefault(entry => entry.ClipId == preferredClipId
                && entry.ProjectStart <= clamped && clamped < entry.ProjectEnd)
            ?? _clips.Last(entry => entry.ProjectStart <= clamped);
        var sourceTime = clip.TimeMap.OutputToSource(Math.Clamp(clamped - clip.ProjectStart, 0, clip.TimeMap.OutputDuration));
        return new StudioTimelinePosition(clip.ClipId, clip.SourceId, sourceTime);
    }

    public double? ProjectTimeFor(string clipId, double sourceTime)
    {
        if (!double.IsFinite(sourceTime))
        {
            return null;
        }

        var clip = _clips.FirstOrDefault(entry => string.Equals(entry.ClipId, clipId, StringComparison.Ordinal));
        return clip is null ? null : clip.ProjectStart + clip.TimeMap.SourceToOutput(sourceTime);
    }
}

public static class StudioTimelineEditing
{
    public static bool IsFlatRecording(StudioProject project) => project.SchemaVersion == 1
        && (project.Sources.Screen.External
            || (project.Sources.ExtensionData.TryGetValue("mediaKind", out var kind)
                && kind.ValueKind == System.Text.Json.JsonValueKind.String && kind.GetString() == "flatVideo"));

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
        var flat = IsFlatRecording(project);
        return project with
        {
            SchemaVersion = 2,
            MediaSources = [new StudioMediaSource
            {
                Id = sourceId,
                Kind = flat ? StudioMediaSourceKind.FlatVideo : StudioMediaSourceKind.NativeRecording,
                Sources = flat ? project.Sources with { Camera = null, Events = null } : project.Sources,
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

    public static StudioTimeline InsertAtProjectTime(
        StudioTimeline timeline,
        StudioTimelineClip clip,
        double projectTime,
        IReadOnlyList<StudioMediaSource> sources)
    {
        var map = new StudioTimelineMap(timeline, sources);
        if (!double.IsFinite(projectTime) || projectTime < 0 || projectTime > map.Duration)
        {
            throw new StudioProjectInvalidException("Choose an insertion point inside the project.");
        }

        var result = timeline;
        var index = timeline.Clips.Length;
        if (projectTime < map.Duration && map.SourcePositionAt(projectTime) is { } position)
        {
            index = Array.FindIndex(timeline.Clips, item => item.Id == position.ClipId);
            if (projectTime > map.Clips[index].ProjectStart)
            {
                result = Split(result, position.ClipId, position.SourceTime)
                    ?? throw new StudioProjectInvalidException("The clip cannot be split at this position.");
                index++;
            }
        }

        result = Insert(result, clip, index);
        _ = new StudioTimelineMap(result, sources);
        return result;
    }

    public static StudioTimeline? SetTransition(StudioTimeline timeline, string clipId, StudioClipTransition transition)
    {
        var index = Array.FindIndex(timeline.Clips, clip => string.Equals(clip.Id, clipId, StringComparison.Ordinal));
        if (index <= 0 || !Enum.IsDefined(transition))
        {
            return null;
        }

        var clips = (StudioTimelineClip[])timeline.Clips.Clone();
        clips[index] = clips[index] with { TransitionIn = transition };
        return timeline with { Clips = clips };
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
        if (timeline.Clips.Length <= 1 || !timeline.Clips.Any(clip => string.Equals(clip.Id, clipId, StringComparison.Ordinal)))
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
        return HasPlayableTime(clips[index]) ? timeline with { Clips = clips } : null;
    }

    public static StudioTimeline? Split(StudioTimeline timeline, string clipId, double sourceTime)
    {
        var index = Array.FindIndex(timeline.Clips, clip => string.Equals(clip.Id, clipId, StringComparison.Ordinal));
        if (index < 0)
        {
            return null;
        }

        var clip = timeline.Clips[index];
        if (!double.IsFinite(sourceTime) || sourceTime <= clip.SourceStart || sourceTime >= clip.SourceEnd
            || (clip.Edits.Cuts ?? []).Any(cut => sourceTime > cut.Start && sourceTime < cut.End))
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
            TransitionIn = StudioClipTransition.Cut,
            SourceStart = sourceTime,
            Edits = ClipEdits(clip.Edits, sourceTime, clip.SourceEnd),
        };
        if (!HasPlayableTime(left) || !HasPlayableTime(right))
        {
            return null;
        }
        var clips = timeline.Clips.ToList();
        clips.RemoveAt(index);
        clips.InsertRange(index, [left, right]);
        return timeline with { Clips = clips.ToArray() };
    }

    private static bool HasPlayableTime(StudioTimelineClip clip) =>
        new StudioTimeMap(clip.SourceEnd, clip.Edits).OutputDuration > 0;

    private static StudioEdits ClipEdits(StudioEdits edits, double start, double end) =>
        edits with
        {
            TrimStart = Math.Max(start, edits.TrimStart),
            TrimEnd = Math.Min(end, edits.TrimEnd ?? end),
            Cuts = (edits.Cuts ?? [])
                .Select(range => range with { Start = Math.Max(start, range.Start), End = Math.Min(end, range.End) })
                .Where(range => range.End > range.Start)
                .ToArray(),
            Speed = (edits.Speed ?? [])
                .Select(range => range with { Start = Math.Max(start, range.Start), End = Math.Min(end, range.End) })
                .Where(range => range.End > range.Start)
                .ToArray(),
        };
}
