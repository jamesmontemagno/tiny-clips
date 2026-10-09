using TinyClips.Core.Models;

namespace TinyClips.App.Tests;

public sealed class GlobalHotKeyRegistrationResultTests
{
    [Fact]
    public void BlockingFailuresFor_IgnoresPreexistingConflictForAnotherAction()
    {
        var result = new GlobalHotKeyRegistrationResult(
        [
            Failure(HotKeyAction.RecordVideo, "Record video"),
            Failure(HotKeyAction.RecordGif, "Record GIF"),
        ]);

        Assert.Empty(result.BlockingFailuresFor(HotKeyAction.ScreenshotRegion));
    }

    [Fact]
    public void BlockingFailuresFor_ReturnsConflictForEditedAction()
    {
        var failure = Failure(HotKeyAction.ScreenshotRegion, "Screenshot region");
        var result = new GlobalHotKeyRegistrationResult([failure]);

        Assert.Equal([failure], result.BlockingFailuresFor(HotKeyAction.ScreenshotRegion));
    }

    [Fact]
    public void BlockingFailuresFor_ReturnsGlobalServiceFailure()
    {
        var failure = Failure(null, "TinyClips hotkey service");
        var result = new GlobalHotKeyRegistrationResult([failure]);

        Assert.Equal([failure], result.BlockingFailuresFor(HotKeyAction.ScreenshotRegion));
    }

    private static GlobalHotKeyRegistrationFailure Failure(HotKeyAction? action, string name)
        => new(action, name, 1409, "Windows rejected this shortcut.");
}
