using System.Text.Json.Nodes;
using TinyClips.Core.Automation;
using TinyClips.Core.Studio;

namespace TinyClips.Core.Tests;

/// <summary>The Captions panel's edits to the cues: the same as on the Mac.</summary>
public sealed class StudioCaptionEditingTests
{
    private const double Tolerance = 1e-6;

    [Fact]
    public void MovingAnEnd_KeepsACaptionAShortestPartLong_AndInTheRecording()
    {
        StudioCaptionWord[] words = [Word(1, 1.4, "Hi"), Word(1.5, 2, "there")];
        StudioCaptionCue[] cues = [Cue("a", 1, 2, "Hi there", words), Cue("b", 3, 4, "Next")];

        var earlier = StudioCaptionEditing.Retime(cues, 0, StudioCaptionEdge.Start, -5, 60)!;
        Assert.Equal(0, earlier.Cues[0].Start);
        Assert.Equal(words, earlier.Cues[0].Words);

        var late = StudioCaptionEditing.Retime(cues, 0, StudioCaptionEdge.Start, 10, 60)!;
        Assert.Equal(1.9, late.Cues[0].Start, Tolerance);
        Assert.Empty(late.Cues[0].Words);

        Assert.Equal(60, StudioCaptionEditing.Retime(cues, 1, StudioCaptionEdge.End, 100, 60)!.Cues[1].End);
        Assert.Equal(3.1, StudioCaptionEditing.Retime(cues, 1, StudioCaptionEdge.End, 0, 60)!.Cues[1].End, Tolerance);

        var moved = StudioCaptionEditing.Retime(cues, 1, StudioCaptionEdge.Start, 0.5, 60)!;
        Assert.Equal(["b", "a"], moved.Cues.Select(static cue => cue.Id));
        Assert.Equal(0, moved.Index);

        Assert.Null(StudioCaptionEditing.Retime(cues, 0, StudioCaptionEdge.Start, 1, 60));
        Assert.Null(StudioCaptionEditing.Retime(cues, 0, StudioCaptionEdge.End, double.NaN, 60));
        Assert.Null(StudioCaptionEditing.Retime(cues, 5, StudioCaptionEdge.End, 3, 60));
    }

    [Fact]
    public void SplittingACaption_GoesByItsWords_AndKeepsItsText()
    {
        var cue = Cue("a", 1, 3, "Hello there,\nworld", [Word(1, 1.4, "Hello"), Word(1.5, 1.9, "there,"), Word(2.5, 3, "world")]);

        var result = StudioCaptionEditing.Split([cue], 0, 2.2)!;
        Assert.Equal(["Hello there,", "world"], result.Cues.Select(static part => part.Text));
        Assert.Equal([1, 2.2], result.Cues.Select(static part => part.Start));
        Assert.Equal([2.2, 3], result.Cues.Select(static part => part.End));
        Assert.Equal([2, 1], result.Cues.Select(static part => part.Words.Length));
        Assert.Equal("a", result.Cues[0].Id);
        Assert.NotEqual("a", result.Cues[1].Id);
        Assert.Equal(result.Cues[1].Id.ToLowerInvariant(), result.Cues[1].Id);
        Assert.Equal(1, result.Index);

        var early = StudioCaptionEditing.Split([cue], 0, 1.1)!;
        Assert.Equal(["Hello", "there,\nworld"], early.Cues.Select(static part => part.Text));
        Assert.Empty(early.Cues[0].Words);

        var unspaced = Cue("b", 0, 2, "こんにちは世界", [Word(0, 1, "こんにちは"), Word(1, 2, "世界")]);
        Assert.Equal(["こんにちは", "世界"], StudioCaptionEditing.Split([unspaced], 0, 1)!.Cues.Select(static part => part.Text));
    }

    [Fact]
    public void SplittingACaptionWithoutWords_GoesByHowFarThroughItIs()
    {
        var cue = Cue("a", 0, 4, "aaaa bbbb\ncccc  dddd");

        var result = StudioCaptionEditing.Split([cue], 0, 1)!;
        Assert.Equal(["aaaa", "bbbb cccc dddd"], result.Cues.Select(static part => part.Text));
        Assert.All(result.Cues, static part => Assert.Empty(part.Words));

        Assert.True(StudioCaptionEditing.CanSplit(cue, 2));
        Assert.False(StudioCaptionEditing.CanSplit(cue, 0.05));
        Assert.False(StudioCaptionEditing.CanSplit(cue, 3.95));
        Assert.False(StudioCaptionEditing.CanSplit(Cue("b", 0, 4, "Hello"), 2));
        Assert.Null(StudioCaptionEditing.Split([cue], 0, 5));
    }

    [Fact]
    public void MergingACaption_WithTheNextOneInTime()
    {
        var first = Cue("a", 1, 2, "Hello\nthere", [Word(1, 1.4, "Hello"), Word(1.5, 2, "there")]);
        var next = Cue("b", 3, 4, " world ", [Word(3, 4, "world")]);

        var result = StudioCaptionEditing.MergeWithNext([next, first], 1)!;
        var merged = Assert.Single(result.Cues);
        Assert.Equal(0, result.Index);
        Assert.Equal("a", merged.Id);
        Assert.Equal("Hello there world", merged.Text);
        Assert.Equal(1, merged.Start);
        Assert.Equal(4, merged.End);
        Assert.Equal(["Hello", "there", "world"], merged.Words.Select(static word => word.Text));

        Assert.Empty(StudioCaptionEditing.MergeWithNext([first, next with { Words = [] }], 0)!.Cues[0].Words);
        Assert.False(StudioCaptionEditing.CanMergeWithNext([first, next], 1));
        Assert.Null(StudioCaptionEditing.MergeWithNext([first, next], 1));
        Assert.Null(StudioCaptionEditing.MergeWithNext([first], 0));
    }

    [Fact]
    public void TheCaptionAtATime_EndsWhereTheNextStarts()
    {
        StudioCaptionCue[] cues = [Cue("a", 0, 1, "A"), Cue("b", 1, 2, "B"), Cue("c", 3, 4, "C")];

        Assert.Equal(0, StudioCaptionEditing.GetPositionAt(cues, 0.5));
        Assert.Equal(1, StudioCaptionEditing.GetPositionAt(cues, 1));
        Assert.Null(StudioCaptionEditing.GetPositionAt(cues, 2.5));
        Assert.Equal(2, StudioCaptionEditing.GetPositionAt(cues, 3.99));
        Assert.Null(StudioCaptionEditing.GetPositionAt(cues, 4));
        Assert.Null(StudioCaptionEditing.GetPositionAt(cues, -1));
        Assert.Null(StudioCaptionEditing.GetPositionAt(cues, double.NaN));
        Assert.Null(StudioCaptionEditing.GetPositionAt([Cue("a", 0, 10, "A"), Cue("b", 2, 3, "B")], 5));
    }

    [Fact]
    public void NextProblem_GoesRoundAfterTheLast()
    {
        int[] problems = [1, 3];
        int? Next(int? current) => StudioCaptionEditing.GetNextProblem(5, current, problems.Contains);

        Assert.Equal(1, Next(null));
        Assert.Equal(3, Next(1));
        Assert.Equal(3, Next(2));
        Assert.Equal(1, Next(3));
        Assert.Equal(1, Next(4));
        Assert.Equal(3, StudioCaptionEditing.GetNextProblem(5, 3, row => row == 3));
        Assert.Null(StudioCaptionEditing.GetNextProblem(5, null, _ => false));
        Assert.Null(StudioCaptionEditing.GetNextProblem(0, null, _ => true));
    }

    [Theory]
    [InlineData(null, StudioCaptionTiming.Recording)]
    [InlineData("\"recording\"", StudioCaptionTiming.Recording)]
    [InlineData("\"editedVideo\"", StudioCaptionTiming.EditedVideo)]
    public void AnImportsTiming_IsTheRecordingUnlessItSaysOtherwise(string? json, StudioCaptionTiming expected) =>
        Assert.Equal(expected, AutomationCaptionTiming.Parse(json is null ? null : JsonNode.Parse(json)));

    [Theory]
    [InlineData("\"edited\"")]
    [InlineData("\"EditedVideo\"")]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("{}")]
    public void AnImportsTiming_ThatIsNeither_IsRefused(string json)
    {
        var error = Assert.Throws<AutomationException>(() => AutomationCaptionTiming.Parse(JsonNode.Parse(json)));
        Assert.Equal(AutomationErrorCodes.InvalidParams, error.Code);
        Assert.Equal("timing must be recording or editedVideo.", error.Message);
    }

    [Fact]
    public void Searching_FindsEveryPlace_WhateverTheCaseAccentsOrWidth()
    {
        Assert.Equal(
            [new StudioTextRange(0, 4), new StudioTextRange(14, 4), new StudioTextRange(19, 4)],
            StudioCaptionSearch.GetRanges("Caf\u00E9 au lait, CAFE \uFF43\uFF41\uFF46\uFF45", StudioCaptionSearch.Normalize(" cafe ")));
        Assert.Equal([new StudioTextRange(0, 2), new StudioTextRange(2, 2)], StudioCaptionSearch.GetRanges("aaaa", "aa"));
        Assert.Empty(StudioCaptionSearch.GetRanges("Hello", string.Empty));
        Assert.Empty(StudioCaptionSearch.GetRanges("Hello", "bye"));
    }

    private static StudioCaptionCue Cue(string id, double start, double end, string text, StudioCaptionWord[]? words = null) =>
        new() { Id = id, Start = start, End = end, Text = text, Words = words ?? [] };

    private static StudioCaptionWord Word(double start, double end, string text) => new() { Start = start, End = end, Text = text };
}
