using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TinyClips.Core.Studio;

namespace TinyClips.Core.Automation;

public static class AutomationStudioTimeline
{
    public static string Revision(StudioProject project) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(StudioProjectJson.WriteProject(project))));

    public static JsonObject Describe(StudioProject project, StudioProjectPaths paths)
    {
        var lane = StudioTimelineEditing.ToTimelineProject(project);
        var sources = lane.MediaSources!;
        var map = new StudioTimelineMap(lane.Timeline!, sources);
        var clips = new JsonArray();
        foreach (var (clip, range) in lane.Timeline!.Clips.Zip(map.Clips))
        {
            var item = new JsonObject
            {
                ["clipId"] = clip.Id, ["sourceId"] = clip.SourceId, ["name"] = clip.Name,
                ["sourceStart"] = clip.SourceStart, ["sourceEnd"] = clip.SourceEnd,
                ["projectStart"] = range.ProjectStart, ["projectEnd"] = range.ProjectEnd,
                ["transitionIn"] = TransitionName(clip.TransitionIn),
            };
            if (range.Entry is { } join)
            {
                item["transitionStart"] = join.Start;
                item["transitionEnd"] = join.End;
            }
            clips.Add(item);
        }

        var media = new JsonArray();
        foreach (var source in sources)
        {
            var screen = source.Sources.Screen;
            var native = source.Kind == StudioMediaSourceKind.NativeRecording;
            var item = new JsonObject
            {
                ["sourceId"] = source.Id, ["kind"] = native ? "nativeRecording" : "flatVideo",
                ["width"] = screen.Width, ["height"] = screen.Height, ["duration"] = screen.Duration,
                ["hasCamera"] = native && source.Sources.Camera is not null,
                ["hasEvents"] = native && (project.SchemaVersion == 1
                    ? File.Exists(paths.EventsPath) : source.Sources.Events is not null),
            };
            if (screen.ExtensionData.TryGetValue("audioTracks", out var tracks) && tracks.ValueKind == JsonValueKind.Array)
            {
                item["audioTracks"] = JsonNode.Parse(tracks.GetRawText());
            }
            if (StudioTakeLinkage.TakeProjectId(source) is { } take)
            {
                item["takeProjectId"] = take;
            }
            media.Add(item);
        }
        return new JsonObject
        {
            ["projectId"] = project.Id, ["modifiedAt"] = ModifiedAt(project), ["revision"] = Revision(project),
            ["clips"] = clips, ["sources"] = media, ["duration"] = map.Duration,
        };
    }

    public static void CheckRevision(StudioProject project, JsonObject parameters)
    {
        if (parameters["expectedModifiedAt"]?.GetValue<string>() != ModifiedAt(project)
            || parameters["expectedRevision"]?.GetValue<string>() != Revision(project))
        {
            throw new AutomationException(AutomationErrorCodes.Conflict,
                "The Studio project changed. Read the timeline again before editing.");
        }
    }

    private static string ModifiedAt(StudioProject project) =>
        project.ModifiedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    public static double? InsertionTime(StudioProject project, JsonObject parameters)
    {
        if (!parameters.ContainsKey("projectTime")) return null;
        var lane = StudioTimelineEditing.ToTimelineProject(project);
        var time = Number(parameters, "projectTime");
        if (time < 0 || time > new StudioTimelineMap(lane.Timeline!, lane.MediaSources!).Duration)
        {
            throw Invalid("projectTime must be inside the edited project.");
        }
        return time;
    }

    public static StudioProject Edit(string command, StudioProject project, JsonObject parameters)
    {
        var baseProject = StudioTimelineEditing.ToTimelineProject(project);
        var lane = baseProject.Timeline!;
        var id = parameters["clipId"]?.GetValue<string>();
        var index = Array.FindIndex(lane.Clips, c => c.Id == id);
        if (index < 0) throw new AutomationException(AutomationErrorCodes.NotFound, "No clip with that ID in this timeline.");
        var clip = lane.Clips[index];
        StudioTimeline? edited;
        switch (command)
        {
            case "studio.timeline.insert":
                var copy = clip with { Id = Guid.NewGuid().ToString("D"), TransitionIn = StudioClipTransition.Cut };
                edited = Insert(lane, copy, baseProject.MediaSources!, InsertionTime(project, parameters));
                break;
            case "studio.timeline.move":
                var position = Number(parameters, "index");
                if (position < 0 || position >= lane.Clips.Length || position != Math.Truncate(position))
                    throw Invalid("index must be the clip's zero-based final position.");
                if (index == (int)position) return project;
                edited = StudioTimelineEditing.Move(lane, clip.Id, (int)position);
                break;
            case "studio.timeline.trim":
                var start = Number(parameters, "sourceStart");
                var end = Number(parameters, "sourceEnd");
                if (start == clip.SourceStart && end == clip.SourceEnd) return project;
                edited = StudioTimelineEditing.Trim(lane, clip.Id, start, end);
                break;
            case "studio.timeline.split":
                edited = StudioTimelineEditing.Split(lane, clip.Id, Number(parameters, "sourceTime"));
                break;
            case "studio.timeline.transition":
                var transition = parameters["kind"]?.GetValue<string>() switch
                {
                    "cut" => StudioClipTransition.Cut,
                    "fadeThroughBlack" => StudioClipTransition.FadeThroughBlack,
                    "fadeThroughWhite" => StudioClipTransition.FadeThroughWhite,
                    "crossDissolve" => StudioClipTransition.CrossDissolve,
                    _ => throw Invalid("Choose a supported join effect."),
                };
                if (index == 0) throw Invalid("The first clip has no incoming transition.");
                if (clip.TransitionIn == transition) return project;
                edited = StudioTimelineEditing.SetTransition(lane, clip.Id, transition);
                break;
            case "studio.timeline.remove":
                edited = StudioTimelineEditing.Remove(lane, clip.Id);
                break;
            default: throw new AutomationException(AutomationErrorCodes.NotSupported, $"No timeline edit named {command}.");
        }
        if (edited is null) throw Invalid("The edit must leave playable clips inside their source ranges.");
        _ = new StudioTimelineMap(edited, baseProject.MediaSources!);
        return baseProject with { Timeline = edited };
    }

    public static StudioTimeline Insert(StudioTimeline lane, StudioTimelineClip clip, StudioMediaSource[] sources, double? time) =>
        time is { } at ? StudioTimelineEditing.InsertAtProjectTime(lane, clip, at, sources) : StudioTimelineEditing.Append(lane, clip);

    public static string ResolveImportPath(string path, IEnumerable<string> folders, IEnumerable<string> recentPaths)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\?\", StringComparison.Ordinal)
            || path.StartsWith(@"\\.\", StringComparison.Ordinal)
            || !StudioProjectFolder.IsFileNameInFolder(Path.GetFileName(path))
            || Path.GetExtension(path).ToLowerInvariant() is not (".mp4" or ".mov"))
            throw Invalid("Choose an absolute MP4 or MOV path from a capture folder or the Library.");

        var full = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(full)!;
        var allowedFolder = folders.Where(folder => !string.IsNullOrWhiteSpace(folder))
            .FirstOrDefault(folder => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), parent, StringComparison.OrdinalIgnoreCase)
                || string.Equals(StudioExportTarget.ResolvePath(folder), parent, StringComparison.OrdinalIgnoreCase));
        if (allowedFolder is null && !recentPaths.Any(recent =>
                string.Equals(Path.GetFullPath(recent), full, StringComparison.OrdinalIgnoreCase)))
            throw NotFound();
        if (!File.Exists(full) || (File.GetAttributes(full) & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw NotFound();
        var actual = StudioExportTarget.ResolvePath(full);
        if (allowedFolder is not null && !string.Equals(
            StudioExportTarget.ResolvePath(allowedFolder), Path.GetDirectoryName(actual), StringComparison.OrdinalIgnoreCase))
            throw NotFound();
        return actual;
    }

    private static string TransitionName(StudioClipTransition transition) => transition switch
    {
        StudioClipTransition.FadeThroughBlack => "fadeThroughBlack",
        StudioClipTransition.FadeThroughWhite => "fadeThroughWhite",
        StudioClipTransition.CrossDissolve => "crossDissolve",
        _ => "cut",
    };

    private static double Number(JsonObject parameters, string key)
    {
        if (parameters[key] is JsonValue value && value.GetValueKind() == JsonValueKind.Number)
        {
            var number = value.Deserialize<double>();
            if (double.IsFinite(number)) return number;
        }
        throw Invalid($"{key} must be a finite number.");
    }

    private static AutomationException NotFound() => new(AutomationErrorCodes.NotFound,
        "Choose a regular video directly in a configured capture folder or the Clips Library.");
    private static AutomationException Invalid(string message) => new(AutomationErrorCodes.InvalidParams, message);
}
