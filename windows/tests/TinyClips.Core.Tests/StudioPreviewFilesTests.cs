using TinyClips.Core.Studio.Preview;

namespace TinyClips.Core.Tests;

/// <summary>
/// Which files a closing preview waits for, and opens with no sharing to find out whether the
/// players have let go: its own recordings in the project folder, and never the user's own video
/// that a project points at.
/// </summary>
public sealed class StudioPreviewFilesTests
{
    private const string Folder = @"C:\Users\someone\AppData\Local\TinyClips\Studio\3f2c";

    [Theory]
    [InlineData(@"C:\Users\someone\AppData\Local\TinyClips\Studio\3f2c\screen.mp4")]
    [InlineData(@"C:\Users\someone\AppData\Local\TinyClips\Studio\3f2c\camera.mp4")]
    [InlineData(@"C:\Users\someone\AppData\Local\TinyClips\Studio\3f2c\takes\2\screen.mp4")]
    [InlineData(@"c:\users\SOMEONE\appdata\local\tinyclips\studio\3F2C\SCREEN.MP4")]
    [InlineData(@"C:/Users/someone/AppData/Local/TinyClips/Studio/3f2c/screen.mp4")]
    [InlineData(@"C:\Users\someone\AppData\Local\TinyClips\Studio\other\..\3f2c\screen.mp4")]
    [InlineData(@"C:\Users\someone\AppData\Local\TinyClips\Studio\3f2c\.\screen.mp4")]
    public void AFileInTheProjectFolder_IsTheEnginesToWaitFor(string path)
    {
        Assert.True(StudioPreviewFiles.IsInFolder(Folder, path));
        Assert.True(StudioPreviewFiles.IsInFolder(Folder + @"\", path));
    }

    [Theory]
    [InlineData(@"D:\Videos\holiday.mp4")]
    [InlineData(@"C:\Users\someone\Videos\holiday.mp4")]
    [InlineData(@"C:\Users\someone\AppData\Local\TinyClips\Studio\screen.mp4")]
    [InlineData(@"C:\Users\someone\AppData\Local\TinyClips\Studio\3f2c0\screen.mp4")]
    [InlineData(@"C:\Users\someone\AppData\Local\TinyClips\Studio\3f2c-copy\screen.mp4")]
    [InlineData(@"C:\Users\someone\AppData\Local\TinyClips\Studio\3f2c\..\9a1b\screen.mp4")]
    [InlineData(@"\\server\share\Studio\3f2c\screen.mp4")]
    [InlineData(@"C:\Users\someone\AppData\Local\TinyClips\Studio\3f2c")]
    public void AFileAnywhereElse_IsLeftAlone(string path)
    {
        Assert.False(StudioPreviewFiles.IsInFolder(Folder, path));
        Assert.False(StudioPreviewFiles.IsInFolder(Folder + @"\", path));
    }

    [Theory]
    [InlineData(null, @"C:\a\screen.mp4")]
    [InlineData("", @"C:\a\screen.mp4")]
    [InlineData("   ", @"C:\a\screen.mp4")]
    [InlineData(@"C:\a", null)]
    [InlineData(@"C:\a", "")]
    [InlineData(@"C:\a", "  ")]
    public void WithoutAFolderOrAPath_NothingIsWaitedFor(string? folder, string? path)
    {
        Assert.False(StudioPreviewFiles.IsInFolder(folder, path));
    }

    [Fact]
    public void AFolderThatIsADriveRoot_HoldsEveryFileOnThatDrive_AndNoOther()
    {
        Assert.True(StudioPreviewFiles.IsInFolder(@"C:\", @"C:\screen.mp4"));
        Assert.True(StudioPreviewFiles.IsInFolder(@"C:\", @"C:\Studio\screen.mp4"));
        Assert.False(StudioPreviewFiles.IsInFolder(@"C:\", @"D:\screen.mp4"));
    }

    [Fact]
    public void APathThatCannotBeRead_IsLeftAlone()
    {
        Assert.False(StudioPreviewFiles.IsInFolder(Folder, "C:\\Studio\\\0\\screen.mp4"));
        Assert.False(StudioPreviewFiles.IsInFolder("C:\\Stu\0dio", @"C:\Studio\screen.mp4"));
    }
}
