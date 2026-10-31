using System.Text.Json;
using TinyClips.Core.Models;
using TinyClips.Core.Models.ClipsLibrary;
using TinyClips.Core.Services.ClipsLibrary;

namespace TinyClips.Core.Tests;

public sealed class ClipSidebarAggregateTests
{
    [Fact]
    public void EmptyAndLoadedMetadata_MatchOriginalCallbacks()
    {
        AssertParity([]);
        var loaded = JsonSerializer.Deserialize<ClipMetadata>(
            """{"Path":"a.png","Tags":["Work","work",""," ","\t",null," Work ","β","Β"],"Collection":"Work"}""")!;
        var clips = new[]
        {
            Clip(loaded),
            Clip(new ClipMetadata("b.png", Tags: ["WORK", "alpha"], Collection: "work")),
            Clip(new ClipMetadata("c.png", Tags: ["Alpha", ""], Collection: " ")),
            Clip(new ClipMetadata("d.png", Collection: " Work ")),
        };

        AssertParity(clips);
        var aggregate = ClipSidebarAggregate.Create(clips);
        Assert.Equal(2, aggregate.TagCount("work"));
        Assert.Equal(2, aggregate.CollectionCount("WORK"));
        Assert.Equal(4, aggregate.TagCount(""));
        Assert.Equal(4, aggregate.TagCount(null));
        Assert.Equal("Work", aggregate.Tags.Single(tag => string.Equals(tag, "work", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal("Work", aggregate.Collections.Single(collection => string.Equals(collection, "work", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void DeterministicRandomMetadata_MatchesOriginalCallbacks()
    {
        var random = new Random(73519);
        string[] tags = ["a", "A", "beta", "BETA", "β", "Β", "İ", "i", "", " ", "\t", " a "];
        string?[] collections = [null, "", " ", "Work", "work", "Personal", " personal ", "β", "Β"];
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var clips = Enumerable.Range(0, random.Next(0, 100)).Select(i =>
                Clip(new ClipMetadata($"{i}.png",
                    Tags: Enumerable.Range(0, random.Next(0, 9)).Select(_ => tags[random.Next(tags.Length)]).ToArray(),
                    Collection: collections[random.Next(collections.Length)]))).ToArray();
            AssertParity(clips);
            AssertParity(clips.Reverse().ToArray());
        }
    }

    [Fact]
    public void EachRefreshReflectsMutationsWithoutChangingPriorSnapshot()
    {
        var clips = new List<LibraryClip>
        {
            Clip(new ClipMetadata("a.png", Tags: ["Work", "work"], Collection: "First")),
            Clip(new ClipMetadata("b.png", Tags: ["Work", "Other"], Collection: "first")),
        };
        var before = ClipSidebarAggregate.Create(clips);
        clips[0] = clips[0] with { Metadata = new ClipMetadata("renamed.png", Tags: ["New"], Collection: "Next") };
        AssertParity(clips);
        clips.RemoveAt(1);
        AssertParity(clips);
        var after = ClipSidebarAggregate.Create(clips);
        Assert.DoesNotContain("Work", after.Tags);
        Assert.Equal(0, after.TagCount("Work"));
        Assert.Equal(2, before.TagCount("WORK"));
        Assert.Equal(["First"], before.Collections);
        clips.Clear();
        AssertParity(clips);
        Assert.Equal(1, after.TagCount("New"));
    }

    [Fact]
    public void CreateEnumeratesInputOnce()
    {
        var enumerations = 0;
        IEnumerable<LibraryClip> Input()
        {
            Assert.Equal(1, ++enumerations);
            yield return Clip(new ClipMetadata("a.png", Tags: ["a", "A"]));
            yield return Clip(new ClipMetadata("b.png", Tags: ["A"]));
        }

        Assert.Equal(2, ClipSidebarAggregate.Create(Input()).TagCount("a"));
        Assert.Equal(1, enumerations);
    }

    private static void AssertParity(IReadOnlyList<LibraryClip> clips)
    {
        var actual = ClipSidebarAggregate.Create(clips);
        var tags = ClipQueryEngine.CollectTags(clips);
        var collections = ClipQueryEngine.CollectCollections(clips);
        Assert.Equal(tags, actual.Tags);
        Assert.Equal(collections, actual.Collections);
        foreach (var tag in tags.Concat(new[] { "", " ", "\t", "missing", "WORK" }))
        {
            Assert.Equal(clips.Count(clip => ClipQueryEngine.MatchesTag(clip, tag)), actual.TagCount(tag));
        }
        foreach (var collection in collections.Concat(new[] { "", " ", "missing", "WORK" }))
        {
            Assert.Equal(clips.Count(clip => ClipQueryEngine.MatchesCollection(clip, collection)), actual.CollectionCount(collection));
        }
        Assert.Equal(clips.Count, actual.TagCount(null));
        Assert.Equal(clips.Count, actual.CollectionCount(null));
    }

    private static LibraryClip Clip(ClipMetadata metadata) =>
        new(new ClipEntry(metadata.Path, CaptureType.Screenshot, metadata.Path,
            new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero), 32), metadata);
}
