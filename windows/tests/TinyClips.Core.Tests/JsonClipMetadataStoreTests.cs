using TinyClips.Core.Models.ClipsLibrary;
using TinyClips.Core.Services.ClipsLibrary;

namespace TinyClips.Core.Tests;

public sealed class JsonClipMetadataStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TinyClipsTests", Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_directory, "clip-metadata.json");

    [Fact]
    public void Get_UnknownPath_ReturnsEmptyMetadata()
    {
        using var store = Create();

        var metadata = store.Get(@"C:\Clips\missing.png");

        Assert.True(metadata.IsEmpty);
        Assert.Equal(@"C:\Clips\missing.png", metadata.Path);
    }

    [Fact]
    public void Upsert_ThenFlush_RoundTripsThroughDisk()
    {
        using (var store = Create())
        {
            store.Upsert(new ClipMetadata(@"C:\Clips\a.png", DisplayName: "Hero", IsFavorite: true, Tags: ["b", "A", "b"], Notes: "n", Collection: "Work"));
            store.Flush();
        }

        Assert.True(File.Exists(FilePath));

        using var reloaded = Create();
        var metadata = reloaded.Get(@"C:\Clips\a.png");
        Assert.Equal("Hero", metadata.DisplayName);
        Assert.True(metadata.IsFavorite);
        Assert.Equal(["A", "b"], metadata.Tags);
        Assert.Equal("Work", metadata.Collection);
    }

    [Fact]
    public void Upsert_EmptyMetadata_RemovesRecord()
    {
        using var store = Create();
        store.Upsert(new ClipMetadata(@"C:\Clips\a.png", IsFavorite: true));

        store.Upsert(ClipMetadata.Empty(@"C:\Clips\a.png"));

        Assert.Empty(store.GetAll());
    }

    [Fact]
    public void Get_IsCaseAndSeparatorInsensitive()
    {
        using var store = Create();
        store.Upsert(new ClipMetadata(@"C:\Clips\A.png", IsFavorite: true));

        Assert.True(store.Get(@"c:/clips/a.png").IsFavorite);
    }

    [Fact]
    public void RenamePath_MovesRecordToNewKey()
    {
        using var store = Create();
        store.Upsert(new ClipMetadata(@"C:\Clips\a.png", Notes: "keep"));

        store.RenamePath(@"C:\Clips\a.png", @"C:\Clips\b.png");

        Assert.True(store.Get(@"C:\Clips\a.png").IsEmpty);
        Assert.Equal("keep", store.Get(@"C:\Clips\b.png").Notes);
        Assert.Equal(@"C:\Clips\b.png", store.Get(@"C:\Clips\b.png").Path);
    }

    [Fact]
    public void Prune_DropsRecordsForMissingFiles()
    {
        using var store = Create();
        store.Upsert(new ClipMetadata(@"C:\Clips\a.png", IsFavorite: true));
        store.Upsert(new ClipMetadata(@"C:\Clips\b.png", IsFavorite: true));

        var removed = store.Prune([@"C:\Clips\b.png"]);

        Assert.Equal(1, removed);
        Assert.Single(store.GetAll());
    }

    [Fact]
    public void Changed_FiresOnMutationOnly()
    {
        using var store = Create();
        var count = 0;
        store.Changed += (_, _) => count++;

        store.Upsert(new ClipMetadata(@"C:\Clips\a.png", IsFavorite: true));
        store.Remove(@"C:\Clips\nothing.png");
        store.Remove(@"C:\Clips\a.png");

        Assert.Equal(2, count);
    }

    [Fact]
    public void Load_ToleratesCorruptFile()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, "{ not json");

        using var store = Create();

        Assert.Empty(store.GetAll());
    }

    [Fact]
    public void Debounce_WritesAfterDelayWithoutExplicitFlush()
    {
        var time = new ManualTimeProvider();
        using var store = new JsonClipMetadataStore(FilePath, time, TimeSpan.FromMilliseconds(100));

        store.Upsert(new ClipMetadata(@"C:\Clips\a.png", IsFavorite: true));
        Assert.False(File.Exists(FilePath));

        time.Advance(TimeSpan.FromMilliseconds(150));

        Assert.True(File.Exists(FilePath));
    }

    private JsonClipMetadataStore Create() => new(FilePath, debounce: TimeSpan.FromMinutes(10));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch
        {
        }
    }
}
