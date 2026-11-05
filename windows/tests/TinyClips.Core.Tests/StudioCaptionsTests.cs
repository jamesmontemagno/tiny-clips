using System.Text.Json;
using TinyClips.Core.Studio;

namespace TinyClips.Core.Tests;

public sealed class StudioCaptionsTests
{
    private const double Tolerance = 1e-6;

    private const string ProjectHead =
        """
        "schemaVersion": 1,
        "id": "3f0013cf-ba10-4453-af91-792b7882dae6",
        "sources": { "screen": { "width": 1920, "height": 1080, "duration": 60 } }
        """;

    public static TheoryData<string> CaptionFixtures => FixtureNames("captions");

    public static TheoryData<string> CaptionFileFixtures => FixtureNames("captionfiles");

    [Theory]
    [MemberData(nameof(CaptionFixtures))]
    public void ResolvesAndWritesSharedFixtures(string name)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath("captions", name)));
        var root = document.RootElement;
        var project = StudioProjectJson.ReadProject(
            $$"""
            {
              {{ProjectHead}},
              "edits": {{root.GetProperty("edits").GetRawText()}},
              "captions": {{root.GetProperty("captions").GetRawText()}}
            }
            """);
        var map = new StudioTimeMap(root.GetProperty("sourceDuration").GetDouble(), project.Edits);

        var actual = StudioCaptionResolver.Resolve(project.Captions, map);

        var expected = root.GetProperty("expected").EnumerateArray().ToArray();
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].GetProperty("start").GetDouble(), actual[i].Start, Tolerance);
            Assert.Equal(expected[i].GetProperty("end").GetDouble(), actual[i].End, Tolerance);
            Assert.Equal(expected[i].GetProperty("text").GetString(), actual[i].Text);
        }

        foreach (var query in root.GetProperty("at").EnumerateArray())
        {
            var index = query.GetProperty("index");
            Assert.Equal(
                index.ValueKind == JsonValueKind.Null ? -1 : index.GetInt32(),
                StudioCaptionResolver.IndexAt(actual, query.GetProperty("output").GetDouble()));
        }

        Assert.Equal(root.GetProperty("srt").GetString(), StudioCaptionFiles.WriteSrt(actual));
        Assert.Equal(root.GetProperty("vtt").GetString(), StudioCaptionFiles.WriteVtt(actual));
    }

    [Theory]
    [MemberData(nameof(CaptionFileFixtures))]
    public void ReadsSharedFixtures(string name)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath("captionfiles", name)));
        var root = document.RootElement;

        var actual = StudioCaptionFiles.Read(root.GetProperty("text").GetString()!);

        Assert.Equal(root.GetProperty("skipped").GetInt32(), actual.SkippedBlocks);
        var expected = root.GetProperty("expected").EnumerateArray().ToArray();
        Assert.Equal(expected.Length, actual.Cues.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].GetProperty("start").GetDouble(), actual.Cues[i].Start, Tolerance);
            Assert.Equal(expected[i].GetProperty("end").GetDouble(), actual.Cues[i].End, Tolerance);
            Assert.Equal(expected[i].GetProperty("text").GetString(), actual.Cues[i].Text);
            Assert.True(Guid.TryParse(actual.Cues[i].Id, out _));
            Assert.Empty(actual.Cues[i].Words);
        }
    }

    [Theory]
    [MemberData(nameof(CaptionFixtures))]
    public void WhatIsWrittenReadsBackTheSame(string name)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath("captions", name)));
        var expected = document.RootElement.GetProperty("expected").EnumerateArray().ToArray();

        foreach (var file in new[] { "srt", "vtt" })
        {
            var cues = StudioCaptionFiles.Read(document.RootElement.GetProperty(file).GetString()!);
            Assert.Equal(0, cues.SkippedBlocks);
            Assert.Equal(expected.Length, cues.Cues.Length);
            for (var i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i].GetProperty("text").GetString(), cues.Cues[i].Text);
                Assert.Equal(expected[i].GetProperty("start").GetDouble(), cues.Cues[i].Start, 0.0006);
                Assert.Equal(expected[i].GetProperty("end").GetDouble(), cues.Cues[i].End, 0.0006);
            }
        }
    }

    [Fact]
    public void AProjectWithoutCaptionsReadsTheDefaults()
    {
        var project = StudioProjectJson.ReadProject($$"""{ {{ProjectHead}} }""");

        Assert.Null(project.Captions.Language);
        Assert.Empty(project.Captions.Cues);
        Assert.False(project.Overlays.Captions.Enabled);
        Assert.Equal(StudioCaptionSize.Medium, project.Overlays.Captions.Size);
        Assert.Equal(StudioCaptionPosition.Bottom, project.Overlays.Captions.Position);
        Assert.Equal(StudioCaptionAppearance.ClassicOutline, project.Overlays.Captions.Appearance);
    }

    [Fact]
    public void NullCaptionsAndNullStyleReadTheDefaults()
    {
        var project = StudioProjectJson.ReadProject(
            $$"""{ {{ProjectHead}}, "captions": null, "overlays": { "captions": null } }""");

        Assert.Empty(project.Captions.Cues);
        Assert.Equal(StudioCaptionSize.Medium, project.Overlays.Captions.Size);

        project = StudioProjectJson.ReadProject(
            $$"""{ {{ProjectHead}}, "overlays": { "captions": { "enabled": null, "size": null, "position": "side", "appearance": "neon" } } }""");

        Assert.False(project.Overlays.Captions.Enabled);
        Assert.Equal(StudioCaptionSize.Medium, project.Overlays.Captions.Size);
        Assert.Equal(StudioCaptionPosition.Bottom, project.Overlays.Captions.Position);
        Assert.Equal(StudioCaptionAppearance.ClassicOutline, project.Overlays.Captions.Appearance);
    }

    [Fact]
    public void AStyleThatNamesOnlySomeOfItsPropertiesReadsTheDefaultsForTheRest()
    {
        var project = StudioProjectJson.ReadProject(
            $$"""{ {{ProjectHead}}, "overlays": { "captions": { "enabled": true } } }""");

        Assert.True(project.Overlays.Captions.Enabled);
        Assert.Equal(StudioCaptionSize.Medium, project.Overlays.Captions.Size);
        Assert.Equal(StudioCaptionPosition.Bottom, project.Overlays.Captions.Position);
        Assert.Equal(StudioCaptionAppearance.ClassicOutline, project.Overlays.Captions.Appearance);
    }

    [Fact]
    public void CaptionsAndUnknownPropertiesSurviveASave()
    {
        var project = StudioProjectJson.ReadProject(
            $$"""
            {
              {{ProjectHead}},
              "overlays": { "captions": { "enabled": true, "size": "large", "position": "top", "appearance": "boldImpact", "font": "x" } },
              "captions": {
                "language": "en-US",
                "future": 1,
                "cues": [ { "id": "a", "start": 1, "end": 2, "text": "Hi", "speaker": "Sam",
                  "words": [ { "start": 1, "end": 2, "text": "Hi", "confidence": 0.9 } ] } ]
              }
            }
            """);

        using var written = JsonDocument.Parse(StudioProjectJson.WriteProject(project));
        var root = written.RootElement;

        var style = root.GetProperty("overlays").GetProperty("captions");
        Assert.True(style.GetProperty("enabled").GetBoolean());
        Assert.Equal("large", style.GetProperty("size").GetString());
        Assert.Equal("top", style.GetProperty("position").GetString());
        Assert.Equal("boldImpact", style.GetProperty("appearance").GetString());
        Assert.Equal("x", style.GetProperty("font").GetString());

        var captions = root.GetProperty("captions");
        Assert.Equal("en-US", captions.GetProperty("language").GetString());
        Assert.Equal(1, captions.GetProperty("future").GetInt32());
        var cue = captions.GetProperty("cues")[0];
        Assert.Equal("a", cue.GetProperty("id").GetString());
        Assert.Equal("Sam", cue.GetProperty("speaker").GetString());
        Assert.Equal(0.9, cue.GetProperty("words")[0].GetProperty("confidence").GetDouble());
    }

    [Fact]
    public void ALanguageThatIsNotKnownIsLeftOut()
    {
        var written = StudioProjectJson.WriteProject(StudioProjectJson.ReadProject($$"""{ {{ProjectHead}} }"""));

        using var document = JsonDocument.Parse(written);
        Assert.False(document.RootElement.GetProperty("captions").TryGetProperty("language", out _));
    }

    [Fact]
    public void AChangeToTheCaptionsIsAChangeToTheEditableState()
    {
        var project = StudioProjectJson.ReadProject($$"""{ {{ProjectHead}} }""");
        var changed = project with { Captions = new StudioCaptions { Cues = [new StudioCaptionCue { Start = 1, End = 2, Text = "Hi" }] } };

        var before = StudioEditableState.From(project);
        var after = StudioEditableState.From(changed);

        Assert.False(before.ContentEquals(after));
        Assert.Equal("Hi", after.ApplyTo(project).Captions.Cues[0].Text);
        Assert.True(StudioEditableState.From(StudioProjectJson.ReadProject(StudioProjectJson.WriteProject(changed))).ContentEquals(after));
    }

    [Fact]
    public void IndexAtFindsTheCaptionShowingInAListOfAnyLength()
    {
        var map = new StudioTimeMap(100);
        var captions = new StudioCaptions
        {
            Cues = Enumerable.Range(0, 200)
                .Reverse()
                .Select(i => new StudioCaptionCue { Start = i * 0.5, End = (i * 0.5) + 0.25, Text = $"Cue {i}" })
                .ToArray(),
        };

        var resolved = StudioCaptionResolver.Resolve(captions, map);

        Assert.Equal(200, resolved.Length);
        Assert.Equal("Cue 0", resolved[0].Text);
        Assert.Equal(120, StudioCaptionResolver.IndexAt(resolved, 60.1));
        Assert.Equal(-1, StudioCaptionResolver.IndexAt(resolved, 60.3));
        Assert.Equal(-1, StudioCaptionResolver.IndexAt([], 1));
    }

    private static TheoryData<string> FixtureNames(string folder)
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "StudioFixtures", folder), "*.json").Order())
        {
            data.Add(Path.GetFileName(file));
        }

        return data;
    }

    private static string FixturePath(string folder, string name) =>
        Path.Combine(AppContext.BaseDirectory, "StudioFixtures", folder, name);
}
