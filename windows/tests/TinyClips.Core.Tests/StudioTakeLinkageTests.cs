using System.Text.Json;
using TinyClips.Core.Studio;

namespace TinyClips.Core.Tests;

public sealed class StudioTakeLinkageTests
{
    private const string Take = "11111111-1111-4111-8111-111111111111";
    private const string Older = "22222222-2222-4222-8222-222222222222";
    private const string Newer = "33333333-3333-4333-8333-333333333333";
    private const string Unrelated = "44444444-4444-4444-8444-444444444444";
    private const string Tied = "55555555-5555-4555-8555-555555555555";
    private static readonly DateTimeOffset Day = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);

    private static (StudioProjectSummary, StudioProject) Entry(string id, string name, DateTimeOffset used, params string?[] takes) => (
        new StudioProjectSummary(id, name, used, DateTimeOffset.UnixEpoch, IsDraft: true, IsFlat: false, KeepSources: false, SizeBytes: 0),
        new StudioProject
        {
            Id = id,
            MediaSources = takes.Select((take, index) =>
            {
                var source = new StudioMediaSource { Id = $"source-{index}" };
                if (take is not null)
                    source.ExtensionData[StudioTakeLinkage.TakeProjectIdKey] = JsonSerializer.SerializeToElement(take);
                return source;
            }).ToArray(),
        });

    [Fact]
    public void LinksTakeToMostRecentlyUsedDestinationAndCountsOnlyExistingTakes()
    {
        var linked = StudioTakeLinkage.Link(
        [
            Entry(Take, "Take", Day, Take),
            Entry(Older, "Older", Day, Take),
            Entry(Newer, "  Newer  ", Day.AddHours(1), Take, Take, Unrelated.Replace('4', '9'), "not-an-id", null),
            Entry(Unrelated, "Unrelated", Day.AddHours(2)),
        ]).ToDictionary(summary => summary.Id);

        Assert.Equal(new StudioTakeReference(Newer, "  Newer  "), linked[Take].TakeOf);
        Assert.Equal(0, linked[Take].TakeCount);
        Assert.Null(linked[Older].TakeOf);
        Assert.Equal(1, linked[Older].TakeCount);
        Assert.Equal(1, linked[Newer].TakeCount);
        Assert.Null(linked[Unrelated].TakeOf);
        Assert.Equal(0, linked[Unrelated].TakeCount);

        Assert.Equal("Take for \u201CNewer\u201D", StudioTakeLinkage.LibraryKind(linked[Take]));
        Assert.Equal("Recorded for \u201CNewer\u201D, which keeps its own copy.", StudioTakeLinkage.DraftNote(linked[Take]));
        Assert.Equal("\u201CNewer\u201D keeps its own copy of this take and is not changed.", StudioTakeLinkage.DeleteNote(linked[Take]));
        Assert.Equal("Its recorded take stays in the Library as a separate draft.", StudioTakeLinkage.DeleteNote(linked[Newer]));
        Assert.Equal("Draft", StudioTakeLinkage.LibraryKind(linked[Unrelated]));
        Assert.Null(StudioTakeLinkage.DraftNote(linked[Unrelated]));
        Assert.Null(StudioTakeLinkage.DeleteNote(linked[Unrelated]));
    }

    [Fact]
    public void TiesGoToTheSmallerIdAndAMissingDestinationLeavesNoLink()
    {
        var tied = StudioTakeLinkage.Link([Entry(Take, "Take", Day), Entry(Tied, "Tied", Day, Take), Entry(Older, "Older", Day, Take)]);
        Assert.Equal(Older, tied.Single(summary => summary.Id == Take).TakeOf!.ProjectId);

        var alone = StudioTakeLinkage.Link([Entry(Take, "Take", Day)]);
        Assert.Null(Assert.Single(alone).TakeOf);
    }

    [Fact]
    public void ManyTakesAreCountedAndUnnamedProjectsUseTheLibraryName()
    {
        var linked = StudioTakeLinkage.Link(
            [Entry(Take, "Take", Day), Entry(Tied, "", Day), Entry(Older, " ", Day, Take, Tied)])
            .ToDictionary(summary => summary.Id);

        Assert.Equal("Its 2 recorded takes stay in the Library as separate drafts.", StudioTakeLinkage.DeleteNote(linked[Older]));
        Assert.Equal("Take for \u201CUntitled Recording\u201D", StudioTakeLinkage.LibraryKind(linked[Take]));
        Assert.Equal("Take also saved as draft \u201CUntitled Recording\u201D in the Library.", StudioTakeLinkage.SavedAsDraftNotice(" "));
        Assert.Equal("Take also saved as draft \u201CTake 2\u201D in the Library.", StudioTakeLinkage.SavedAsDraftNotice("Take 2"));
    }
}
