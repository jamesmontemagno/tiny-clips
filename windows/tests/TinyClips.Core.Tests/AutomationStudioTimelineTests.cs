using System.Text.Json.Nodes;
using TinyClips.Core.Automation;
using TinyClips.Core.Studio;

namespace TinyClips.Core.Tests;

public sealed class AutomationStudioTimelineTests
{
    private static StudioProject Project() => StudioProjectJson.ReadProject(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "StudioFixtures", "timeline", "two-clips-native-flat.json")));

    private static StudioProjectPaths Paths => new("p", ".", "project.json", "screen.mp4", null, "events.json", "poster.jpg");

    [Fact]
    public void DiscoveryReportsEditedJoinAndRejectsSameTimestampWithStaleRevision()
    {
        var original = Project();
        var read = AutomationStudioTimeline.Describe(original, Paths);
        var tokens = new JsonObject
        {
            ["expectedModifiedAt"] = (string)read["modifiedAt"]!, ["expectedRevision"] = (string)read["revision"]!,
        };
        AutomationStudioTimeline.CheckRevision(original, tokens);
        var changed = AutomationStudioTimeline.Edit("studio.timeline.transition", original,
            new JsonObject { ["clipId"] = "flat-clip", ["kind"] = "crossDissolve" });
        var after = AutomationStudioTimeline.Describe(changed, Paths);
        Assert.Equal(10.5, (double)after["duration"]!);
        Assert.Equal(5.5, (double)after["clips"]![1]!["transitionStart"]!);
        Assert.Equal(6.0, (double)after["clips"]![1]!["transitionEnd"]!);
        Assert.True((bool)after["sources"]![0]!["hasEvents"]!);
        Assert.False((bool)after["sources"]![1]!["hasCamera"]!);
        Assert.Equal("conflict", Assert.Throws<AutomationException>(() =>
            AutomationStudioTimeline.CheckRevision(changed, tokens)).Code);
    }

    [Fact]
    public void DiscoveryKeepsTheFlatOriginOfAPortableV1Project()
    {
        var project = StudioProjectJson.ReadProject("""
            { "id": "p", "sources": { "mediaKind": "flatVideo", "screen": { "width": 320, "height": 240, "duration": 3 } } }
            """);
        var read = AutomationStudioTimeline.Describe(project, Paths);
        Assert.Equal("flatVideo", (string)read["sources"]![0]!["kind"]!);
        Assert.False((bool)read["sources"]![0]!["hasEvents"]!);
        Assert.Null(project.MediaSources);
    }

    [Fact]
    public void V1ReadAndNoOpDoNotUpgradeAndCompoundInsertSharesTheSource()
    {
        var legacy = StudioProjectJson.ReadProject("""
            { "id": "p", "sources": { "screen": { "width": 640, "height": 480, "duration": 8 } } }
            """);
        var read = AutomationStudioTimeline.Describe(legacy, Paths);
        var clipId = (string)read["clips"]![0]!["clipId"]!;
        Assert.Same(legacy, AutomationStudioTimeline.Edit("studio.timeline.move", legacy,
            new JsonObject { ["clipId"] = clipId, ["index"] = 0 }));
        Assert.Null(legacy.Timeline);
        var inserted = AutomationStudioTimeline.Edit("studio.timeline.insert", legacy,
            new JsonObject { ["clipId"] = clipId, ["projectTime"] = 3 });
        Assert.Single(inserted.MediaSources!);
        var clips = inserted.Timeline!.Clips;
        Assert.Equal(3, clips.Select(c => c.Id).Distinct().Count());
        Assert.Equal(new double[] { 3, 8, 8 }, clips.Select(c => c.SourceEnd));
        Assert.Equal(new double[] { 0, 0, 3 }, clips.Select(c => c.SourceStart));
        Assert.Equal(clipId, clips[0].Id);
        Assert.Equal((string)read["revision"]!, AutomationStudioTimeline.Revision(legacy));
    }

    [Theory]
    [InlineData("studio.timeline.trim", """{"clipId":"native-clip","sourceStart":4,"sourceEnd":5}""", "invalid_params")]
    [InlineData("studio.timeline.insert", """{"clipId":"flat-clip","projectTime":14}""", "invalid_params")]
    [InlineData("studio.timeline.move", """{"clipId":"flat-clip","index":1.5}""", "invalid_params")]
    [InlineData("studio.timeline.split", """{"clipId":"missing","sourceTime":1}""", "not_found")]
    [InlineData("studio.timeline.transition", """{"clipId":"native-clip","kind":"crossDissolve"}""", "invalid_params")]
    public void InvalidLaneEditsHaveContractErrors(string command, string json, string code)
    {
        Assert.Equal(code, Assert.Throws<AutomationException>(() =>
            AutomationStudioTimeline.Edit(command, Project(), JsonNode.Parse(json)!.AsObject())).Code);
    }

    [Fact]
    public void ImportRequiresARegularCaptureChildOrAnExactRecentPath()
    {
        var root = Directory.CreateTempSubdirectory("tinyclips-timeline-");
        try
        {
            var library = root.CreateSubdirectory("library").FullName;
            var video = Path.Combine(library, "First.mov");
            var other = Path.Combine(root.FullName, "Second.mp4");
            File.WriteAllBytes(video, [0]);
            File.WriteAllBytes(other, [0]);
            Assert.Equal(video, AutomationStudioTimeline.ResolveImportPath(video, [library], []));
            Assert.Equal(other, AutomationStudioTimeline.ResolveImportPath(other, [library], [other]));
            Assert.Equal("not_found", Assert.Throws<AutomationException>(() =>
                AutomationStudioTimeline.ResolveImportPath(other, [library], [])).Code);
            Assert.Equal("invalid_params", Assert.Throws<AutomationException>(() =>
                AutomationStudioTimeline.ResolveImportPath(Path.ChangeExtension(video, ".txt"), [library], [])).Code);
            Assert.Equal("invalid_params", Assert.Throws<AutomationException>(() =>
                AutomationStudioTimeline.ResolveImportPath("First.mov", [library], [])).Code);
        }
        finally { root.Delete(recursive: true); }
    }
}
