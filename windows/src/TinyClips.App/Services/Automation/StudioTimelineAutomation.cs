using System.IO;
using System.Text.Json.Nodes;
using TinyClips.App.ViewModels.Studio;
using TinyClips.Core.Automation;
using TinyClips.Core.Studio;

namespace TinyClips.App;

internal sealed class StudioTimelineAutomation(
    IStudioProjectStore store, Func<string, StudioViewModel?> editorFor,
    Func<string, string> resolveImportPath, Action<TimelineRequest> checkAccess)
{
    private StudioProject CheckedTimelineProject(TimelineRequest request)
    {
        checkAccess(request);
        var project = ReadProject(request.ProjectId, store, editorFor(request.ProjectId));
        AutomationStudioTimeline.CheckRevision(project, request.Parameters);
        return project;
    }

    internal void ValidateRecordingStart(TimelineRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var project = CheckedTimelineProject(request);
        _ = AutomationStudioTimeline.InsertionTime(project, request.Parameters);
        if (editorFor(request.ProjectId) is { IsReadyForTimelineChange: false })
            throw new AutomationException(AutomationErrorCodes.Busy, "The Studio editor is busy.");
    }

    internal async Task<JsonNode> ExecuteAsync(TimelineRequest request, string? recordingId, CancellationToken cancellationToken)
    {
        var id = request.ProjectId;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Command == "studio.timeline.get")
            {
                checkAccess(request);
                var project = ReadProject(id, store, editorFor(id));
                return AutomationStudioTimeline.Describe(project, store.GetPaths(project));
            }

            var current = CheckedTimelineProject(request);
            using var projectUse = store.AcquireProjectUse(id);
            var parameters = request.Parameters;
            var time = AutomationStudioTimeline.InsertionTime(current, parameters);
            var addingMedia = request.Command is "studio.timeline.import" or "studio.timeline.record";
            StudioProject Edit(StudioProject project)
            {
                if (request.Command != "studio.project.edit")
                    return AutomationStudioTimeline.Edit(request.Command, project, parameters);
                var node = JsonNode.Parse(StudioProjectJson.WriteProject(project))!.AsObject();
                MergeEditableFields(parameters["patch"]!.AsObject(), node);
                return StudioProjectJson.ReadProject(node.ToJsonString());
            }
            if (!addingMedia && ReferenceEquals(current, Edit(current)))
                return TimelineEditResult(current);

            var editor = editorFor(id);
            using var lease = editor?.BeginTimelineChange();
            if (editor is not null && lease is null)
                throw new AutomationException(AutomationErrorCodes.Busy, "The Studio editor is busy.");
            if (lease is not null)
            {
                // Flushing the exact edit the client saw can advance modifiedAt, but must not
                // silently accept different edits or new bookkeeping along with that timestamp.
                if (AutomationStudioTimeline.Revision(lease.BaseProject with { ModifiedAt = current.ModifiedAt })
                    != AutomationStudioTimeline.Revision(current))
                    throw TimelineConflict("The Studio project changed while flushing the editor.");
                current = lease.BaseProject;
            }
            var revision = AutomationStudioTimeline.Revision(current);

            var imports = new List<StudioTimelineImport>();
            Action? revalidateSource = null;
            if (recordingId is not null)
            {
                var recording = ReadProject(recordingId, store, editorFor(recordingId));
                if (recording.SchemaVersion != 1)
                    throw TimelineConflict("The finished recording is no longer a single take.");
                imports.Add(new StudioTimelineImport(store.GetPaths(recording).ScreenPath, recording));
                var takeRevision = AutomationStudioTimeline.Revision(recording);
                revalidateSource = () =>
                {
                    if (AutomationStudioTimeline.Revision(ReadProject(recordingId, store, editorFor(recordingId))) != takeRevision)
                        throw TimelineConflict("The finished take changed before insertion.");
                };
            }
            else if (addingMedia)
            {
                var path = request.RequiredString("path");
                var resolved = resolveImportPath(path);
                var identity = StudioMediaFileIdentity.Capture(resolved);
                string? LinkedProjectId() => parameters["mode"]?.GetValue<string>() == "file" ? null
                    : store.FindProjectIdByExportPath(path) ?? store.FindProjectIdByExportPath(resolved);
                var linkedId = LinkedProjectId();
                var linked = linkedId is null ? null : ReadProject(linkedId, store, editorFor(linkedId));
                if (linked?.SchemaVersion >= 2)
                    throw new AutomationException(AutomationErrorCodes.NotSupported,
                        "A linked multi-clip project cannot be merged. Use mode=file to import its rendered video.");
                var linkedRevision = linked is null ? null : AutomationStudioTimeline.Revision(linked);
                imports.Add(new StudioTimelineImport(resolved, linked, identity));
                revalidateSource = () =>
                {
                    if (!StringComparer.OrdinalIgnoreCase.Equals(resolved, resolveImportPath(path))
                        || identity != StudioMediaFileIdentity.Capture(resolved) || LinkedProjectId() != linkedId
                        || (linkedId is not null && AutomationStudioTimeline.Revision(ReadProject(linkedId, store, editorFor(linkedId))) != linkedRevision))
                        throw TimelineConflict("The Library source changed while importing.");
                };
            }

            using var prepared = await ((IStudioTimelineMediaStore)store).StageTimelineAsync(current, imports, cancellationToken);
            var proposal = addingMedia ? prepared.ProposedProject : Edit(prepared.BaseProject);
            if (addingMedia && time is not null)
            {
                proposal = proposal with { Timeline = AutomationStudioTimeline.Insert(
                    prepared.BaseProject.Timeline!, prepared.ImportedClips.Single(), proposal.MediaSources!, time) };
            }

            StudioProject Commit(StudioProject project)
            {
                cancellationToken.ThrowIfCancellationRequested();
                checkAccess(request);
                if (AutomationStudioTimeline.Revision(ReadProject(id, store, editor)) != revision)
                    throw TimelineConflict("The Studio project changed before the edit could be saved.");
                revalidateSource?.Invoke();
                return prepared.Commit(project, cancellationToken);
            }

            if (!ReferenceEquals(editor, editorFor(id)))
                throw TimelineConflict("The Studio editor opened or closed while preparing the edit.");
            if (editor is null) return TimelineEditResult(Commit(proposal));
            var result = editor.CommitTimelineChange(lease!, proposal, Commit, prepared.ImportedClips.FirstOrDefault()?.Id);
            if (!result.Succeeded || result.Manifest is null)
                throw TimelineConflict(result.Message ?? "The Studio editor changed while preparing the edit.");
            return TimelineEditResult(result.Manifest);
        }
        catch (Exception ex) when (ex is StudioProjectStaleException or StudioMediaFileChangedException)
        {
            throw TimelineConflict(ex.Message);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException
            or StudioProjectFolderException { Problem: StudioProjectFolderProblem.MissingFile })
        {
            throw new AutomationException(AutomationErrorCodes.NotFound, ex.Message);
        }
        catch (Exception ex) when (ex is StudioProjectInvalidException or StudioProjectFolderException or ArgumentException)
        {
            throw new AutomationException(AutomationErrorCodes.InvalidParams, ex.Message);
        }
    }

    private static AutomationException TimelineConflict(string message) => new(AutomationErrorCodes.Conflict, message);

    private static JsonObject TimelineEditResult(StudioProject project) =>
        new() { ["project"] = JsonNode.Parse(StudioProjectJson.WriteProject(project)), ["revision"] = AutomationStudioTimeline.Revision(project) };

    internal static StudioProject ReadProject(string id, IStudioProjectStore store, StudioViewModel? editor)
    {
        if (editor is not null)
            return editor.Manifest ?? throw new AutomationException(AutomationErrorCodes.Busy, "The Studio project is not ready for agent control.");
        if (!store.Exists(id)) throw new AutomationException(AutomationErrorCodes.NotFound, $"No Studio project with ID {id}.");
        try { return store.Load(id); }
        catch (DirectoryNotFoundException) { throw new AutomationException(AutomationErrorCodes.NotFound, $"No Studio project with ID {id}."); }
    }

    internal static void MergeEditableFields(JsonObject patch, JsonObject target)
    {
        foreach (var (key, value) in patch)
        {
            if (value is JsonObject patchObject && target[key] is JsonObject targetObject)
                MergeEditableFields(patchObject, targetObject);
            else
                target[key] = value?.DeepClone();
        }
    }
}

// A take can outlive the start request. Keep the originating identity, not its token or the identity of the stopper.
internal sealed record TimelineRequest(string Command, string ClientId, string ClientName, JsonObject Parameters)
{
    public string ProjectId => RequiredString("projectId");
    public string RequiredString(string key) => Parameters[key]?.GetValue<string>() is { Length: > 0 } value ? value
        : throw new AutomationException(AutomationErrorCodes.InvalidParams, $"{key} is required.");
    public static TimelineRequest From(AutomationCommandContext context) =>
        new(context.Command.Name, context.Client.Id, context.Client.Name, context.Params);

    public void RequireAccess(bool enabled, AutomationClientStore clients)
    {
        var client = clients.List().FirstOrDefault(candidate => candidate.Id == ClientId);
        var decision = new AutomationPolicy(AutomationContract.Default).Decide(enabled, client?.Scopes, Command);
        if (decision.Kind != AutomationDecisionKind.Allow)
            throw new AutomationException(decision.ErrorCode ?? AutomationErrorCodes.ApprovalDenied,
                "The originating client no longer has permission for this timeline operation.");
    }
}
