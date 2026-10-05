using TinyClips.Core.Studio.Rendering;

namespace TinyClips.Core.Tests;

/// <summary>
/// The clean-up of temporary files an export that was killed leaves behind: it removes only what
/// the exporter itself names, only when it is old enough, and it never throws.
/// </summary>
public sealed class StudioExportTemporaryFileTests : IDisposable
{
    private const string Id = "0123456789abcdef0123456789abcdef";
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "TinyClipsStudioTests-" + Guid.NewGuid().ToString("N"));

    public StudioExportTemporaryFileTests()
    {
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        Directory.Delete(_folder, recursive: true);
    }

    [Theory]
    [InlineData(".clip." + Id + ".tcexport")]
    [InlineData(".poster." + Id + ".tcexport")]
    [InlineData(".My video 2026-10-03 at 17.56.14." + Id + ".tcexport")]
    [InlineData(".." + Id + ".tcexport")]
    public void IsTemporaryName_AcceptsWhatTheExporterWrites(string name)
    {
        Assert.True(StudioRenderingMath.IsTemporaryName(name));
    }

    [Fact]
    public void IsTemporaryName_AcceptsEveryNameTemporaryOutputPathMakes()
    {
        foreach (var output in new[] { @"C:\Videos\clip.mp4", @"C:\Projects\abc\poster.jpg", "clip.mp4", @"C:\Videos\no extension", @"C:\Videos\.mp4", @"C:\Videos\a.b.c.mp4" })
        {
            var name = Path.GetFileName(StudioRenderingMath.TemporaryOutputPath(output));
            Assert.True(StudioRenderingMath.IsTemporaryName(name), name);
        }
    }

    [Fact]
    public void StagedOutputPath_IsNextToTheOutput_UnderANameTheCleanUpRemoves()
    {
        foreach (var output in new[] { @"C:\Videos\TinyClips 2026-10-03 at 17.56.14.mp4", @"C:\Videos\a.b.c.mp4", @"C:\Videos\no extension" })
        {
            var staged = StudioRenderingMath.StagedOutputPath(output);

            Assert.Equal(Path.GetDirectoryName(output), Path.GetDirectoryName(staged));
            Assert.True(StudioRenderingMath.IsTemporaryName(Path.GetFileName(staged)), staged);
            Assert.NotEqual(staged, StudioRenderingMath.StagedOutputPath(output));

            // The exporter writes it under a temporary name of its own first, made from this one.
            var written = StudioRenderingMath.TemporaryOutputPath(staged);
            Assert.Equal(Path.GetDirectoryName(output), Path.GetDirectoryName(written));
            Assert.True(StudioRenderingMath.IsTemporaryName(Path.GetFileName(written)), written);
        }
    }

    [Fact]
    public void StagedOutputPath_IsNoLongerForAVideoWithALongName()
    {
        var name = Path.GetFileName(StudioRenderingMath.StagedOutputPath(@"C:\Videos\a.mp4"));
        var nameForALongOne = Path.GetFileName(StudioRenderingMath.StagedOutputPath(@"C:\Videos\" + new string('x', 120) + ".mp4"));

        Assert.Equal(56, name.Length);
        Assert.Equal(name.Length, nameForALongOne.Length);
    }

    [Theory]
    [InlineData("clip.mp4")]
    [InlineData("clip.tcexport")]
    [InlineData(".clip.tcexport")]
    [InlineData("clip." + Id + ".tcexport")]
    [InlineData(".clip." + Id + ".mp4")]
    [InlineData(".clip." + Id + ".tcexport.mp4")]
    [InlineData(".clip." + Id + ".TCEXPORT")]
    [InlineData(".clip.0123456789ABCDEF0123456789ABCDEF.tcexport")]
    [InlineData(".clip.01234567-89ab-cdef-0123-456789abcdef.tcexport")]
    [InlineData(".clip.0123456789abcdef0123456789abcde.tcexport")]
    [InlineData(".clip.0123456789abcdef0123456789abcdeg.tcexport")]
    [InlineData(".clip" + Id + ".tcexport")]
    [InlineData("." + Id + ".tcexport")]
    [InlineData(".tcexport")]
    [InlineData("")]
    public void IsTemporaryName_RejectsEverythingElse(string name)
    {
        Assert.False(StudioRenderingMath.IsTemporaryName(name));
    }

    [Fact]
    public void DeleteStaleTemporaryFiles_RemovesOnlyOldFilesWithTheExportersName()
    {
        var stale = Touch(".clip." + Id + ".tcexport", age: TimeSpan.FromDays(3));
        var stalePoster = Touch(".poster.ffffffffffffffffffffffffffffffff.tcexport", age: TimeSpan.FromDays(2));
        var recent = Touch(".clip.11111111111111111111111111111111.tcexport", age: TimeSpan.FromHours(1));
        var video = Touch("clip.mp4", age: TimeSpan.FromDays(30));
        var otherExtension = Touch(".clip." + Id + ".mp4", age: TimeSpan.FromDays(30));
        var noId = Touch(".clip.tcexport", age: TimeSpan.FromDays(30));
        var noDot = Touch("clip." + Id + ".tcexport", age: TimeSpan.FromDays(30));
        var upperCase = Touch(".clip.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA.tcexport", age: TimeSpan.FromDays(30));
        Directory.CreateDirectory(Path.Combine(_folder, "inside"));
        var nested = Touch(Path.Combine("inside", ".clip." + Id + ".tcexport"), age: TimeSpan.FromDays(30));
        var folderWithTheName = Path.Combine(_folder, ".folder." + Id + ".tcexport");
        Directory.CreateDirectory(folderWithTheName);

        Assert.Equal(2, StudioRenderingMath.DeleteStaleTemporaryFiles(_folder, Day));

        Assert.False(File.Exists(stale));
        Assert.False(File.Exists(stalePoster));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(video));
        Assert.True(File.Exists(otherExtension));
        Assert.True(File.Exists(noId));
        Assert.True(File.Exists(noDot));
        Assert.True(File.Exists(upperCase));
        Assert.True(File.Exists(nested));
        Assert.True(Directory.Exists(folderWithTheName));

        // Nothing is left for a second pass, and an age of zero takes the recent one too.
        Assert.Equal(0, StudioRenderingMath.DeleteStaleTemporaryFiles(_folder, Day));
        Assert.Equal(1, StudioRenderingMath.DeleteStaleTemporaryFiles(_folder, TimeSpan.Zero));
        Assert.False(File.Exists(recent));
    }

    [Fact]
    public void DeleteStaleTemporaryFiles_GoesByTheLaterOfWrittenAndCreated()
    {
        // Written long ago and put here just now, as a copy is: not old yet.
        var copied = Touch(".clip." + Id + ".tcexport", age: TimeSpan.FromDays(3));
        File.SetCreationTimeUtc(copied, DateTime.UtcNow);

        // Created long ago and written to an hour ago, as a long export still running would be.
        var written = Touch(".clip.22222222222222222222222222222222.tcexport", age: TimeSpan.FromDays(3));
        File.SetLastWriteTimeUtc(written, DateTime.UtcNow - TimeSpan.FromHours(1));

        Assert.Equal(0, StudioRenderingMath.DeleteStaleTemporaryFiles(_folder, Day));
        Assert.True(File.Exists(copied));
        Assert.True(File.Exists(written));
    }

    [Theory]
    [InlineData(FileAccess.ReadWrite, FileShare.Read)]
    [InlineData(FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)]
    [InlineData(FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)]
    public void DeleteStaleTemporaryFiles_LeavesAFileThatIsOpen(FileAccess access, FileShare share)
    {
        // An export that is still writing has its file open. Media Foundation opens it so that
        // others may delete it, which is the second case: the clean-up must not take that offer.
        var open = Touch(".clip." + Id + ".tcexport", age: TimeSpan.FromDays(3));
        var free = Touch(".clip.44444444444444444444444444444444.tcexport", age: TimeSpan.FromDays(3));
        using (new FileStream(open, FileMode.Open, access, share))
        {
            Assert.Equal(1, StudioRenderingMath.DeleteStaleTemporaryFiles(_folder, TimeSpan.Zero));
            Assert.True(File.Exists(open));
            Assert.False(File.Exists(free));
        }

        // Still there once it is closed: it was not marked for deletion behind the holder's back.
        Assert.True(File.Exists(open));
        Assert.Equal(1, StudioRenderingMath.DeleteStaleTemporaryFiles(_folder, TimeSpan.Zero));
        Assert.False(File.Exists(open));
    }

    [Fact]
    public void DeleteStaleTemporaryFiles_LeavesAReadOnlyFile()
    {
        var readOnly = Touch(".clip.33333333333333333333333333333333.tcexport", age: TimeSpan.FromDays(3));
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        try
        {
            Assert.Equal(0, StudioRenderingMath.DeleteStaleTemporaryFiles(_folder, Day));
            Assert.True(File.Exists(readOnly));
        }
        finally
        {
            File.SetAttributes(readOnly, FileAttributes.Normal);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"C:\this\folder\is\not\there")]
    [InlineData("not a path: <>|?*")]
    public void DeleteStaleTemporaryFiles_NeverThrows(string? folder)
    {
        Assert.Equal(0, StudioRenderingMath.DeleteStaleTemporaryFiles(folder!, Day));
    }

    [Fact]
    public void DeleteStaleTemporaryFiles_TreatsAnImpossibleAgeAsAnAge()
    {
        var stale = Touch(".clip." + Id + ".tcexport", age: TimeSpan.FromDays(3));

        // Longer than there have been files: nothing is that old, and nothing overflows.
        Assert.Equal(0, StudioRenderingMath.DeleteStaleTemporaryFiles(_folder, TimeSpan.MaxValue));
        Assert.True(File.Exists(stale));

        // A negative age is no age at all.
        Assert.Equal(1, StudioRenderingMath.DeleteStaleTemporaryFiles(_folder, TimeSpan.FromDays(-5)));
        Assert.False(File.Exists(stale));
    }

    /// <summary>A file in the test folder that was created and last written <paramref name="age"/> ago.</summary>
    private string Touch(string name, TimeSpan age)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, "x");
        var then = DateTime.UtcNow - age;
        File.SetCreationTimeUtc(path, then);
        File.SetLastWriteTimeUtc(path, then);
        return path;
    }
}
