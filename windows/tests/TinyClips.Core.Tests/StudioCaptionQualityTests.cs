using System.Text.Json;
using TinyClips.Core.Studio;

namespace TinyClips.Core.Tests;

public sealed class StudioCaptionQualityTests
{
    private const double Tolerance = 1e-6;

    public static TheoryData<string> QualityFixtures => FixtureNames("captionquality");

    public static TheoryData<string> ImportFixtures => FixtureNames("captionimport");

    [Theory]
    [MemberData(nameof(QualityFixtures))]
    public void ChecksAndFixesMatchSharedFixtures(string name)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath("captionquality", name)));
        var root = document.RootElement;
        var duration = root.GetProperty("sourceDuration").GetDouble();
        var cues = ReadCues(root.GetProperty("cues"));

        var findings = StudioCaptionQuality.Check(cues, duration);

        var expected = root.GetProperty("findings").EnumerateArray().ToArray();
        Assert.Equal(expected.Length, findings.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].GetProperty("cue").GetInt32(), findings[i].Cue);
            Assert.Equal(expected[i].GetProperty("kind").GetString(), StudioCaptionQuality.GetKindName(findings[i].Kind));
            Assert.Equal(expected[i].GetProperty("canFix").GetBoolean(), findings[i].CanFix);
        }

        Assert.Equal(root.GetProperty("summary").GetString(), StudioCaptionQuality.GetSummary(findings));

        var result = StudioCaptionQuality.Fix(cues, duration);

        Assert.Equal(root.GetProperty("applied").GetInt32(), result.Applied);
        var fixedCues = root.GetProperty("fixed").EnumerateArray().ToArray();
        Assert.Equal(fixedCues.Length, result.Cues.Length);
        for (var i = 0; i < fixedCues.Length; i++)
        {
            Assert.Equal(fixedCues[i].GetProperty("start").GetDouble(), result.Cues[i].Start, Tolerance);
            Assert.Equal(fixedCues[i].GetProperty("end").GetDouble(), result.Cues[i].End, Tolerance);
            Assert.Equal(fixedCues[i].GetProperty("text").GetString(), result.Cues[i].Text);
            Assert.Equal(fixedCues[i].GetProperty("wordCount").GetInt32(), result.Cues[i].Words.Length);
        }

        Assert.Equal(
            root.GetProperty("after").EnumerateArray().Select(static kind => kind.GetString()),
            StudioCaptionQuality.Check(result.Cues, duration).Select(static finding => StudioCaptionQuality.GetKindName(finding.Kind)));
    }

    [Theory]
    [MemberData(nameof(ImportFixtures))]
    public void AFileTimedToTheEditedVideoMatchesSharedFixtures(string name)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath("captionimport", name)));
        var root = document.RootElement;
        var map = new StudioTimeMap(root.GetProperty("sourceDuration").GetDouble(), ReadEdits(root.GetProperty("edits")));
        var text = StudioCaptionFiles.WriteSrt(root.GetProperty("cues").EnumerateArray()
            .Select(static cue => new StudioResolvedCaption(cue.GetProperty("start").GetDouble(), cue.GetProperty("end").GetDouble(), cue.GetProperty("text").GetString()!))
            .ToArray());

        var contents = StudioCaptionFiles.Read(text, StudioCaptionTiming.EditedVideo, map);

        Assert.Equal(root.GetProperty("dropped").GetInt32(), contents.SkippedBlocks);
        var expected = root.GetProperty("expected").EnumerateArray().ToArray();
        Assert.Equal(expected.Length, contents.Cues.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].GetProperty("start").GetDouble(), contents.Cues[i].Start, Tolerance);
            Assert.Equal(expected[i].GetProperty("end").GetDouble(), contents.Cues[i].End, Tolerance);
            Assert.Equal(expected[i].GetProperty("text").GetString(), contents.Cues[i].Text);
        }
    }

    [Theory]
    [MemberData(nameof(CaptionFixtures))]
    public void WhatIsWrittenReadsBackTimedToTheEditedVideo_AsTheSameCaptions(string name)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath("captions", name)));
        var root = document.RootElement;
        var map = new StudioTimeMap(root.GetProperty("sourceDuration").GetDouble(), ReadEdits(root.GetProperty("edits")));
        var expected = root.GetProperty("expected").EnumerateArray().ToArray();

        foreach (var file in new[] { "srt", "vtt" })
        {
            var contents = StudioCaptionFiles.Read(root.GetProperty(file).GetString()!, StudioCaptionTiming.EditedVideo, map);
            var again = StudioCaptionResolver.Resolve(new StudioCaptions { Cues = contents.Cues }, map);

            Assert.Equal(expected.Length, again.Length);
            for (var i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i].GetProperty("text").GetString(), again[i].Text);
                Assert.Equal(expected[i].GetProperty("start").GetDouble(), again[i].Start, 0.0011);
                Assert.Equal(expected[i].GetProperty("end").GetDouble(), again[i].End, 0.0011);
            }
        }
    }

    public static TheoryData<string> CaptionFixtures => FixtureNames("captions");

    [Fact]
    public void AFixKeepsTheIdOfTheFirstPartAndGivesTheOthersNewOnes()
    {
        var text = string.Join(' ', Enumerable.Repeat("word", 40));
        var result = StudioCaptionQuality.Fix([new StudioCaptionCue { Id = "first", Start = 0, End = 20, Text = text }], 60);

        Assert.True(result.Cues.Length > 1);
        Assert.Equal("first", result.Cues[0].Id);
        Assert.All(result.Cues.Skip(1), cue => Assert.True(Guid.TryParse(cue.Id, out _)));
        Assert.Equal(result.Cues.Length, result.Cues.Select(static cue => cue.Id).Distinct().Count());
    }

    [Fact]
    public void ACharacterIsACodePoint_SoAnEmojiCountsOnce()
    {
        // Forty-two emoji are 84 UTF-16 units, and one line.
        var line = string.Concat(Enumerable.Repeat("\U0001F600", 42));

        var findings = StudioCaptionQuality.Check([new StudioCaptionCue { Start = 0, End = 10, Text = line }], 60);

        Assert.Empty(findings);
    }

    [Fact]
    public void ReplaceFileKeepsNoStagedFile_AndWritesUtf8WithoutAMark()
    {
        var folder = Directory.CreateTempSubdirectory("tinyclips-captions-").FullName;
        try
        {
            var path = Path.Combine(folder, "video.srt");
            File.WriteAllText(path, "old");

            StudioCaptionFiles.ReplaceFile(path, "Café\n");

            Assert.Equal(new byte[] { 0x43, 0x61, 0x66, 0xC3, 0xA9, 0x0A }, File.ReadAllBytes(path));
            Assert.Equal(["video.srt"], Directory.GetFiles(folder).Select(Path.GetFileName));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void WriteNewFileDoesNotReplaceAFile()
    {
        var folder = Directory.CreateTempSubdirectory("tinyclips-captions-").FullName;
        try
        {
            var path = Path.Combine(folder, "video.vtt");
            StudioCaptionFiles.WriteNewFile(path, "WEBVTT\n");

            Assert.Throws<IOException>(() => StudioCaptionFiles.WriteNewFile(path, "other"));
            Assert.Equal("WEBVTT\n", File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ReadFileUnderstandsAMark_AndRefusesWhatIsTooLarge()
    {
        var folder = Directory.CreateTempSubdirectory("tinyclips-captions-").FullName;
        try
        {
            var path = Path.Combine(folder, "unicode.srt");
            File.WriteAllText(path, "1\n00:00:01,000 --> 00:00:02,000\nCafé\n", System.Text.Encoding.Unicode);
            Assert.Equal("Café", StudioCaptionFiles.Read(StudioCaptionFiles.ReadFile(path)).Cues[0].Text);

            var large = Path.Combine(folder, "large.srt");
            using (var stream = File.Create(large))
            {
                stream.SetLength(StudioCaptionFiles.LargestFile + 1);
            }

            Assert.Throws<InvalidDataException>(() => StudioCaptionFiles.ReadFile(large));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void WhatTheEditorSaysAboutCaptions()
    {
        Assert.Equal("No captions yet", StudioCaptionText.GetCountText(0));
        Assert.Equal("1 caption", StudioCaptionText.GetCountText(1));
        Assert.Equal("12 captions", StudioCaptionText.GetCountText(12));
        Assert.Equal("1.0 to 2.5 seconds", StudioCaptionText.GetRangeText(new StudioCaptionCue { Start = 1, End = 2.5 }));
        Assert.Equal("One two", StudioCaptionText.GetRowText(new StudioCaptionCue { Text = " One\ntwo " }));
        Assert.Equal("(Empty)", StudioCaptionText.GetRowText(new StudioCaptionCue { Text = " " }));
        Assert.Equal("Imported 1 caption.", StudioCaptionText.GetImportedMessage(1, 0));
        Assert.Equal("Imported 2 captions. 1 block could not be read.", StudioCaptionText.GetImportedMessage(2, 1));
        Assert.Equal("Imported 2 captions. 3 blocks could not be read.", StudioCaptionText.GetImportedMessage(2, 3));
        Assert.Equal("Nothing to fix.", StudioCaptionText.GetFixedMessage(0));
        Assert.Equal("Made 1 fix.", StudioCaptionText.GetFixedMessage(1));
        Assert.Equal("Made 3 fixes.", StudioCaptionText.GetFixedMessage(3));
        Assert.Equal("Made 2 fixes. 1 problem needs a manual edit.", StudioCaptionText.GetFixedMessage(2, left: 1));
        Assert.Equal("Nothing could be fixed automatically. 3 problems need a manual edit.", StudioCaptionText.GetFixedMessage(0, left: 3));
        Assert.Null(StudioCaptionText.GetManualFixNote(2, 2));
        Assert.Equal("Auto Fix can't fix this one. Edit the caption's text or timing.", StudioCaptionText.GetManualFixNote(0, 1));
        Assert.Equal("Auto Fix can't fix these. Edit the captions' text or timing.", StudioCaptionText.GetManualFixNote(0, 2));
        Assert.Equal("Auto Fix can fix 2 of 3. The other needs a manual edit.", StudioCaptionText.GetManualFixNote(2, 3));
        Assert.Equal("Auto Fix can fix 1 of 3. The others need a manual edit.", StudioCaptionText.GetManualFixNote(1, 3));
    }

    [Theory]
    [InlineData("a.srt", StudioCaptionFormat.Srt)]
    [InlineData("a.SRT", StudioCaptionFormat.Srt)]
    [InlineData("a.vtt", StudioCaptionFormat.Vtt)]
    [InlineData("a.mp4", null)]
    [InlineData("a", null)]
    public void AFormatComesFromTheExtension(string path, StudioCaptionFormat? format) =>
        Assert.Equal(format, StudioCaptionFiles.GetFormat(path));

    private static StudioCaptionCue[] ReadCues(JsonElement cues) => StudioProjectJson.ReadProject(
        $$"""
        {
          "id": "3f0013cf-ba10-4453-af91-792b7882dae6",
          "sources": { "screen": { "width": 1920, "height": 1080, "duration": 60 } },
          "captions": { "cues": {{cues.GetRawText()}} }
        }
        """).Captions.Cues;

    private static StudioEdits ReadEdits(JsonElement edits) => StudioProjectJson.ReadProject(
        $$"""
        {
          "id": "3f0013cf-ba10-4453-af91-792b7882dae6",
          "sources": { "screen": { "width": 1920, "height": 1080, "duration": 60 } },
          "edits": {{edits.GetRawText()}}
        }
        """).Edits;

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
