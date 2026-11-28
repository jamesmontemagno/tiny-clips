using System.Text.Json.Nodes;
using TinyClips.App;
using TinyClips.Core.Automation;
using TinyClips.Core.Studio;

namespace TinyClips.App.Tests;

public sealed class StudioTimelineAutomationTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("tinyclips-agent-timeline-");
    private readonly StudioProjectStore _store;
    private readonly AutomationClientStore _clients;
    private readonly string _library;
    private readonly string _video;
    private readonly string _id;
    private readonly AutomationClient _client;
    private readonly string _token;

    public StudioTimelineAutomationTests()
    {
        _library = _root.CreateSubdirectory("Library").FullName;
        _store = new StudioProjectStore(Path.Combine(_root.FullName, "Projects"));
        _clients = new AutomationClientStore(Path.Combine(_root.FullName, "clients.json"));
        (_client, _token) = _clients.Add("Timeline tests", ["read", "edit", "record"]);
        _video = Path.Combine(_library, "portrait.mp4");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Timeline", "flat.mp4"), _video);
        _id = _store.GetOrCreateFlatProject(_video, new StudioRecordingSourceInfo(32, 64, 1, 60), "test").Id;
    }

    public void Dispose() => _root.Delete(recursive: true);

    [Fact]
    public async Task PairedClientImportsAndEditsWithFreshTokensWithoutChangingTheOriginal()
    {
        var timeline = Service();
        var handler = new HostAdapter(timeline);
        var host = new AutomationHost(AutomationContract.Default, _clients, null, handler, handler, () => true);
        Task<AutomationResponse> Call(string command, JsonObject parameters) => host.HandleAsync(new AutomationRequest
        {
            Command = command, Token = _token, Params = parameters,
        }, TestContext.Current.CancellationToken);

        var read = (await Call("studio.timeline.get", new JsonObject { ["projectId"] = _id })).Result!;
        Assert.Equal(1, _store.Load(_id).SchemaVersion);
        var args = Tokens(read);
        args["path"] = _video;
        var imported = await Call("studio.timeline.import", args);
        Assert.True(imported.Ok, imported.Error?.Message);
        Assert.Equal(2, imported.Result!["project"]!["timeline"]!["clips"]!.AsArray().Count);
        Assert.Equal("conflict", (await Call("studio.timeline.import", args)).Error?.Code);

        File.Delete(_video);
        var lane = (await Call("studio.timeline.get", new JsonObject { ["projectId"] = _id })).Result!;
        var join = Tokens(lane);
        join["clipId"] = (string)lane["clips"]![1]!["clipId"]!;
        join["kind"] = "crossDissolve";
        var edited = await Call("studio.timeline.transition", join);
        Assert.True(edited.Ok, edited.Error?.Message);
        var saved = _store.Load(_id);
        Assert.Equal(StudioClipTransition.CrossDissolve, saved.Timeline!.Clips[1].TransitionIn);
        Assert.All(saved.MediaSources!, source =>
            Assert.True(File.Exists(Path.Combine(_store.GetPaths(_id).ProjectDirectory, source.Sources.Screen.File))));
    }

    [Fact]
    public async Task LinkedTimelineRequiresExplicitFileModeToImportItsRenderedVideo()
    {
        var timeline = Service();
        var project = _store.Load(_id);
        var take = CreateTake(project);
        var token = TestContext.Current.CancellationToken;
        using (var staged = await _store.StageTimelineAsync(_store.Load(take), [new(_video)], token))
            staged.Commit(staged.ProposedProject, token);
        var exported = Path.Combine(_library, "linked-export.mp4");
        File.Copy(_video, exported);
        _store.RecordExport(take, exported);

        var args = Tokens(AutomationStudioTimeline.Describe(project, _store.GetPaths(project)));
        args["path"] = exported;
        var request = new TimelineRequest("studio.timeline.import", _client.Id, _client.Name, args);
        var error = await Assert.ThrowsAsync<AutomationException>(() => timeline.ExecuteAsync(request, null, token));
        Assert.Equal("not_supported", error.Code);
        Assert.Equal(1, _store.Load(_id).SchemaVersion);

        args["mode"] = "file";
        await timeline.ExecuteAsync(request, null, token);
        var saved = _store.Load(_id);
        var imported = Assert.Single(saved.Timeline!.Clips, clip => clip.Name == "linked-export");
        var source = Assert.Single(saved.MediaSources!, media => media.Id == imported.SourceId);
        Assert.Equal(StudioMediaSourceKind.FlatVideo, source.Kind);
        Assert.False(source.Sources.Screen.External);
    }

    [Fact]
    public async Task LosingPermissionAtCommitRollsBackStagedMedia()
    {
        var count = 0;
        var timeline = Service(() =>
        {
            if (++count == 2) _clients.UpdateScopes(_client.Id, ["read"]);
        });
        var project = _store.Load(_id);
        var args = Tokens(AutomationStudioTimeline.Describe(project, _store.GetPaths(project)));
        args["path"] = _video;
        var path = _store.GetPaths(project).ProjectDirectory;
        var before = Directory.GetFileSystemEntries(path).Order().ToArray();
        var error = await Assert.ThrowsAsync<AutomationException>(() =>
            timeline.ExecuteAsync(new("studio.timeline.import", _client.Id, _client.Name, args), null, TestContext.Current.CancellationToken));
        Assert.Equal("missing_scope", error.Code);
        Assert.Equal(before, Directory.GetFileSystemEntries(path).Order());
        Assert.Equal(AutomationStudioTimeline.Revision(project), AutomationStudioTimeline.Revision(_store.Load(_id)));
        Assert.True(File.Exists(_video));
    }

    [Fact]
    public async Task CompletedTakeRequiresOriginalRevisionAndBothLiveGrants()
    {
        var timeline = Service();
        var project = _store.Load(_id);
        var args = Tokens(AutomationStudioTimeline.Describe(project, _store.GetPaths(project)));
        var request = new TimelineRequest("studio.timeline.record", _client.Id, _client.Name, args);
        timeline.ValidateRecordingStart(request, TestContext.Current.CancellationToken);
        var draft = CreateTake(project);
        _clients.UpdateScopes(_client.Id, ["record"]);
        var denied = await Assert.ThrowsAsync<AutomationException>(() => timeline.ExecuteAsync(request, draft, CancellationToken.None));
        Assert.Equal("missing_scope", denied.Code);
        _clients.UpdateScopes(_client.Id, ["record", "edit"]);
        _store.Save(project with { Name = "User's latest edit" });
        var stale = await Assert.ThrowsAsync<AutomationException>(() => timeline.ExecuteAsync(request, draft, CancellationToken.None));
        Assert.Equal("conflict", stale.Code);
        Assert.True(_store.Exists(draft));
        Assert.Equal(1, _store.Load(_id).SchemaVersion);
        Assert.Equal("User's latest edit", _store.Load(_id).Name);
    }

    [Fact]
    public async Task FinishedTakeInsertsAtProjectTimeAndSurvivesDeletionOfTheSeparateDraft()
    {
        var timeline = Service();
        var project = _store.Load(_id);
        var args = Tokens(AutomationStudioTimeline.Describe(project, _store.GetPaths(project)));
        args["projectTime"] = 0.5;
        var request = new TimelineRequest("studio.timeline.record", _client.Id, _client.Name, args);
        timeline.ValidateRecordingStart(request, TestContext.Current.CancellationToken);
        var draft = CreateTake(project);
        await timeline.ExecuteAsync(request, draft, TestContext.Current.CancellationToken);
        _store.Delete(draft);
        File.Delete(_video);
        var saved = _store.Load(_id);
        var clips = saved.Timeline!.Clips;
        Assert.Equal(3, clips.Length);
        Assert.Equal(clips[0].SourceId, clips[2].SourceId);
        Assert.Equal(0.5, clips[0].SourceEnd);
        Assert.Equal(0.5, clips[2].SourceStart);
        Assert.Equal("Completed take", clips[1].Name);
        var take = Assert.Single(saved.MediaSources!, source => source.Id == clips[1].SourceId);
        Assert.Equal(StudioMediaSourceKind.NativeRecording, take.Kind);
        Assert.True(File.Exists(Path.Combine(_store.GetPaths(saved).ProjectDirectory, take.Sources.Screen.File)));
    }

    private string CreateTake(StudioProject project)
    {
        var draft = _store.BeginRecording();
        File.Copy(_video, draft.ScreenPath);
        _store.Save(project with
        {
            Id = draft.ProjectId, Name = "Completed take",
            Sources = new StudioSources { Screen = project.Sources.Screen with { External = false, File = "screen.mp4" } },
        });
        return draft.ProjectId;
    }

    private StudioTimelineAutomation Service(Action? beforeCheck = null) => new(_store, _ => null,
        path => AutomationStudioTimeline.ResolveImportPath(path, [_library], []),
        request => { beforeCheck?.Invoke(); request.RequireAccess(true, _clients); });

    private JsonObject Tokens(JsonNode lane) => new()
    {
        ["projectId"] = _id, ["expectedModifiedAt"] = (string)lane["modifiedAt"]!, ["expectedRevision"] = (string)lane["revision"]!,
    };

    private sealed class HostAdapter(StudioTimelineAutomation timeline) : IAutomationCommandHandler, IAutomationApprovalPrompt
    {
        public string AppVersion => "test";
        public string BuildFlavor => "direct";
        public IReadOnlyCollection<string> SupportedCommands => ["studio.timeline.get", "studio.timeline.import", "studio.timeline.transition"];
        public async Task<JsonNode?> ExecuteAsync(AutomationCommandContext context, CancellationToken cancellationToken) =>
            await timeline.ExecuteAsync(TimelineRequest.From(context), null, cancellationToken);
        public Task<IReadOnlyList<string>?> RequestPairingApprovalAsync(AutomationPairingRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The test client is already paired.");
        public Task<bool> RequestCommandApprovalAsync(AutomationCommandContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Timeline edits are not sensitive actions.");
    }

    public sealed class StudioTimelineOpenEditorAutomationTests : StudioViewModelTestBase
    {
        [Fact]
        public async Task EditorLeaseAndExternalEditReturnDifferentActionableErrors()
        {
            var id = CreateProject(camera: false);
            var editor = await OpenAsync(id);
            try
            {
                var service = new StudioTimelineAutomation(Projects, _ => editor, path => path, _ => { });
                var token = TestContext.Current.CancellationToken;
                var args = new JsonObject { ["projectId"] = id };
                var lane = await service.ExecuteAsync(new("studio.timeline.get", "test", "test", args), null, token);
                args["expectedModifiedAt"] = lane["modifiedAt"]!.DeepClone();
                args["expectedRevision"] = lane["revision"]!.DeepClone();
                args["clipId"] = lane["clips"]![0]!["clipId"]!.DeepClone();
                args["sourceTime"] = 2;
                var split = new TimelineRequest("studio.timeline.split", "test", "test", args);
                using (var held = editor.BeginTimelineChange().Lease)
                {
                    Assert.NotNull(held);
                    var busy = await Assert.ThrowsAsync<AutomationException>(() => service.ExecuteAsync(split, null, token));
                    Assert.Equal("busy", busy.Code);
                }

                var saved = Projects.Load(id);
                Projects.Save(saved with { Audio = saved.Audio with { Muted = true } });
                var stale = await Assert.ThrowsAsync<AutomationException>(() => service.ExecuteAsync(split, null, token));
                Assert.Equal("conflict", stale.Code);
                Assert.Null(Projects.Load(id).Timeline);
                Assert.True(Projects.Load(id).Audio.Muted);
            }
            finally { await CloseAsync(editor); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task LinkedImportUsesCurrentCaptionAndRefusesAChangeDuringStaging(bool changeDuringStaging)
        {
            var destination = CreateProject(camera: false);
            var source = CreateProject(p => p with
            {
                Captions = new() { Cues = [new() { Id = "cue", Start = 0, End = 0.25, Text = "Saved" }] },
            }, camera: false);
            Directory.CreateDirectory(VideosFolder);
            var export = Path.Combine(VideosFolder, "linked-export.mp4");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Timeline", "flat.mp4"), export);
            File.Copy(export, Projects.GetPaths(source).ScreenPath, overwrite: true);
            Projects.RecordExport(source, export);
            var editor = await OpenAsync(source);
            try
            {
                editor.SelectCaptionRow(0);
                editor.SetPendingCaptionText("Typed before import");
                var checks = 0;
                var service = new StudioTimelineAutomation(Projects, id => id == source ? editor : null, path => path, _ =>
                {
                    if (++checks == 2 && changeDuringStaging)
                        editor.SetPendingCaptionText("Changed while importing");
                });
                var project = Projects.Load(destination);
                var lane = AutomationStudioTimeline.Describe(project, Projects.GetPaths(project));
                var args = new JsonObject
                {
                    ["projectId"] = destination, ["path"] = export,
                    ["expectedModifiedAt"] = lane["modifiedAt"]!.DeepClone(), ["expectedRevision"] = lane["revision"]!.DeepClone(),
                };
                var import = service.ExecuteAsync(new("studio.timeline.import", "test", "test", args), null, TestContext.Current.CancellationToken);
                if (changeDuringStaging)
                {
                    Assert.Equal("conflict", (await Assert.ThrowsAsync<AutomationException>(() => import)).Code);
                    Assert.Null(Projects.Load(destination).Timeline);
                }
                else
                {
                    await import;
                    var added = Projects.Load(destination).Timeline!.Clips[1];
                    Assert.Equal("Typed before import", Assert.Single(added.Captions!.Cues).Text);
                }
                Assert.False(editor.CanUndo);
                Assert.Equal("Saved", Assert.Single(editor.Project!.Captions.Cues).Text);
            }
            finally { await CloseAsync(editor); }
        }

        [Fact]
        public async Task ImportIntoOpenEditorUsesOneUndoStepAndRedoRestoresOwnedMedia()
        {
            var id = CreateProject(camera: false);
            var view = await OpenAsync(id);
            try
            {
                var service = new StudioTimelineAutomation(Projects, _ => view, path => path, _ => { });
                var token = TestContext.Current.CancellationToken;
                var args = new JsonObject { ["projectId"] = id };
                var lane = await service.ExecuteAsync(new("studio.timeline.get", "test", "test", args), null, token);
                args["expectedModifiedAt"] = lane["modifiedAt"]!.DeepClone();
                args["expectedRevision"] = lane["revision"]!.DeepClone();
                args["path"] = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Timeline", "flat.mp4");
                await service.ExecuteAsync(new("studio.timeline.import", "test", "test", args), null, token);
                Pump();
                Assert.Equal(2, view.Manifest!.Timeline!.Clips.Length);
                Assert.Equal(2, Projects.Load(id).Timeline!.Clips.Length);
                view.Undo();
                Pump();
                Assert.False(view.CanUndo);
                Assert.Null(view.Manifest!.Timeline);
                view.Redo();
                Pump();
                Assert.Equal(2, view.Manifest!.Timeline!.Clips.Length);
            }
            finally { await CloseAsync(view); }
            Assert.Equal(2, Projects.Load(id).Timeline!.Clips.Length);
        }
    }
}
