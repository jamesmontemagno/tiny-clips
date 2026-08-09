using TinyClips.Core.Services;

namespace TinyClips.Core.Tests;

public sealed class TinyClipsTemporaryFilesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TinyClipsTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void GetSummary_ReturnsEmptyForMissingDirectory()
    {
        var summary = TinyClipsTemporaryFiles.GetSummary(_directory);

        Assert.Equal(0, summary.FileCount);
        Assert.Equal(0, summary.TotalSize);
    }

    [Fact]
    public void GetSummary_AggregatesFileCountAndSize()
    {
        WriteFile("one.tmp", 3);
        WriteFile("two.tmp", 5);

        var summary = TinyClipsTemporaryFiles.GetSummary(_directory);

        Assert.Equal(2, summary.FileCount);
        Assert.Equal(8, summary.TotalSize);
    }

    [Fact]
    public void Purge_RemovesInactiveFilesAndPreservesActiveFiles()
    {
        var inactivePath = WriteFile("inactive.tmp", 3);
        var activePath = WriteFile("active.tmp", 5);

        var result = TinyClipsTemporaryFiles.Purge(_directory, [activePath]);

        Assert.Equal(1, result.RemovedFileCount);
        Assert.Equal(1, result.SkippedFileCount);
        Assert.False(File.Exists(inactivePath));
        Assert.True(File.Exists(activePath));
        Assert.Equal(new TemporaryFilesSummary(1, 5), TinyClipsTemporaryFiles.GetSummary(_directory));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string WriteFile(string fileName, int length)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, fileName);
        File.WriteAllBytes(path, new byte[length]);
        return path;
    }
}
