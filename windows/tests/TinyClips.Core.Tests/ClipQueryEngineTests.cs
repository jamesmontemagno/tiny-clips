using TinyClips.Core.Models;
using TinyClips.Core.Models.ClipsLibrary;
using TinyClips.Core.Services.ClipsLibrary;

namespace TinyClips.Core.Tests;

public sealed class ClipQueryEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Apply_DefaultQuery_ReturnsAllNewestFirst()
    {
        var clips = Sample();

        var result = ClipQueryEngine.Apply(clips, ClipQuery.Default, Now);

        Assert.Equal(clips.Count, result.Count);
        Assert.Equal("today.mp4", result[0].Entry.FileName);
        Assert.Equal("old.png", result[^1].Entry.FileName);
    }

    [Theory]
    [InlineData(ClipTypeFilter.Screenshots, CaptureType.Screenshot)]
    [InlineData(ClipTypeFilter.Videos, CaptureType.Video)]
    [InlineData(ClipTypeFilter.Gifs, CaptureType.Gif)]
    public void Apply_TypeFilter_RestrictsToType(ClipTypeFilter filter, CaptureType expected)
    {
        var result = ClipQueryEngine.Apply(Sample(), new ClipQuery(TypeFilter: filter), Now);

        Assert.NotEmpty(result);
        Assert.All(result, clip => Assert.Equal(expected, clip.Type));
    }

    [Fact]
    public void Apply_FavoritesFilter_ReturnsOnlyFavorites()
    {
        var result = ClipQueryEngine.Apply(Sample(), new ClipQuery(TypeFilter: ClipTypeFilter.Favorites), Now);

        Assert.Single(result);
        Assert.True(result[0].Metadata.IsFavorite);
    }

    [Fact]
    public void Apply_DateFilterToday_ExcludesOlderClips()
    {
        var result = ClipQueryEngine.Apply(Sample(), new ClipQuery(DateFilter: ClipDateFilter.Today), Now);

        Assert.Contains(result, clip => clip.Entry.FileName == "today.mp4");
        Assert.DoesNotContain(result, clip => clip.Entry.FileName == "old.png");
    }

    [Fact]
    public void Apply_Last7Days_IncludesSixDaysAgoButNotTenDaysAgo()
    {
        var result = ClipQueryEngine.Apply(Sample(), new ClipQuery(DateFilter: ClipDateFilter.Last7Days), Now);

        Assert.Contains(result, clip => clip.Entry.FileName == "week.gif");
        Assert.DoesNotContain(result, clip => clip.Entry.FileName == "month.png");
    }

    [Fact]
    public void Apply_Search_MatchesDisplayNameTagsAndNotes()
    {
        var clips = Sample();

        Assert.Single(ClipQueryEngine.Apply(clips, new ClipQuery(SearchText: "Launch"), Now));
        Assert.Single(ClipQueryEngine.Apply(clips, new ClipQuery(SearchText: "demo"), Now));
        Assert.Single(ClipQueryEngine.Apply(clips, new ClipQuery(SearchText: "reviewed"), Now));
        Assert.Empty(ClipQueryEngine.Apply(clips, new ClipQuery(SearchText: "zzz"), Now));
    }

    [Fact]
    public void Apply_Search_RequiresAllTerms()
    {
        var result = ClipQueryEngine.Apply(Sample(), new ClipQuery(SearchText: "launch demo"), Now);

        Assert.Single(result);
        Assert.Equal("today.mp4", result[0].Entry.FileName);
    }

    [Fact]
    public void Apply_TagAndCollection_Filter()
    {
        var clips = Sample();

        var byTag = ClipQueryEngine.Apply(clips, new ClipQuery(Tag: "DEMO"), Now);
        var byCollection = ClipQueryEngine.Apply(clips, new ClipQuery(Collection: "Work"), Now);

        Assert.Single(byTag);
        Assert.Equal(2, byCollection.Count);
    }

    [Fact]
    public void Apply_SmartCollections()
    {
        var clips = Sample();

        Assert.Single(ClipQueryEngine.Apply(clips, new ClipQuery(SmartCollection: SmartCollection.LargeFiles), Now));
        Assert.Single(ClipQueryEngine.Apply(clips, new ClipQuery(SmartCollection: SmartCollection.Favorites), Now));
        Assert.Single(ClipQueryEngine.Apply(clips, new ClipQuery(SmartCollection: SmartCollection.Recent), Now));
        Assert.Equal(2, ClipQueryEngine.Apply(clips, new ClipQuery(SmartCollection: SmartCollection.Screenshots), Now).Count);
    }

    [Fact]
    public void Apply_SortOptions()
    {
        var clips = Sample();

        Assert.Equal("old.png", ClipQueryEngine.Apply(clips, new ClipQuery(Sort: ClipSortOption.OldestFirst), Now)[0].Entry.FileName);
        Assert.Equal("month.png", ClipQueryEngine.Apply(clips, new ClipQuery(Sort: ClipSortOption.Largest), Now)[0].Entry.FileName);
        Assert.Equal("Launch demo", ClipQueryEngine.Apply(clips, new ClipQuery(Sort: ClipSortOption.Name), Now)[0].DisplayName);
        Assert.True(ClipQueryEngine.Apply(clips, new ClipQuery(Sort: ClipSortOption.FavoritesFirst), Now)[0].Metadata.IsFavorite);
    }

    [Fact]
    public void CollectTagsAndCollections_AreDistinctCaseInsensitiveAndSorted()
    {
        var clips = Sample();

        Assert.Equal(["demo", "Release"], ClipQueryEngine.CollectTags(clips));
        Assert.Equal(["Personal", "Work"], ClipQueryEngine.CollectCollections(clips));
    }

    [Fact]
    public void CountSmartCollections_CountsEveryCollection()
    {
        var counts = ClipQueryEngine.CountSmartCollections(Sample(), Now);

        Assert.Equal(4, counts[SmartCollection.AllClips]);
        Assert.Equal(1, counts[SmartCollection.Videos]);
        Assert.Equal(1, counts[SmartCollection.Gifs]);
    }

    [Fact]
    public void ClipQuery_HasActiveFilters_IgnoresSort()
    {
        Assert.False(new ClipQuery(Sort: ClipSortOption.Name).HasActiveFilters);
        Assert.True(new ClipQuery(Tag: "x").HasActiveFilters);
        Assert.Equal(ClipSortOption.Name, new ClipQuery(Tag: "x", Sort: ClipSortOption.Name).ClearFilters().Sort);
    }

    private static List<LibraryClip> Sample()
    {
        LibraryClip Make(string name, CaptureType type, DateTimeOffset at, long size, ClipMetadata? meta = null)
        {
            var path = Path.Combine(@"C:\Clips", name);
            return new LibraryClip(new ClipEntry(path, type, name, at, size), meta ?? ClipMetadata.Empty(path));
        }

        return
        [
            Make("today.mp4", CaptureType.Video, Now.AddHours(-1), 5_000_000,
                new ClipMetadata(@"C:\Clips\today.mp4", DisplayName: "Launch demo", Tags: ["demo"], Collection: "Work")),
            Make("week.gif", CaptureType.Gif, Now.AddDays(-6), 1_000,
                new ClipMetadata(@"C:\Clips\week.gif", IsFavorite: true, Tags: ["Release"], Notes: "Reviewed by team", Collection: "work")),
            Make("month.png", CaptureType.Screenshot, Now.AddDays(-10), 60L * 1024 * 1024,
                new ClipMetadata(@"C:\Clips\month.png", Collection: "Personal")),
            Make("old.png", CaptureType.Screenshot, Now.AddDays(-90), 10),
        ];
    }
}
