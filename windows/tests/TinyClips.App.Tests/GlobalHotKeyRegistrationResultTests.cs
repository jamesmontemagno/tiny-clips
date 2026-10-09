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

    [Fact]
    public void FormatApply_GlobalServiceFailure_UsesDiagnosticMessage()
    {
        var failure = new GlobalHotKeyRegistrationFailure(
            null,
            "TinyClips hotkey service",
            0,
            "The UI dispatcher is not available.");

        var message = GlobalHotKeyFailureFormatter.FormatApply([failure]);

        Assert.Equal("The UI dispatcher is not available.", message);
        Assert.DoesNotContain("Another app", message);
    }

    [Fact]
    public void FormatRollback_GlobalServiceFailure_UsesDiagnosticMessage()
    {
        var failure = new GlobalHotKeyRegistrationFailure(
            null,
            "TinyClips hotkey service",
            0,
            "Timed out while starting the Windows hotkey service.");

        var message = GlobalHotKeyFailureFormatter.FormatRollback([failure]);

        Assert.Contains("Timed out while starting the Windows hotkey service.", message);
        Assert.DoesNotContain("Close the competing app", message);
    }

    [Fact]
    public void FormatApply_BindingFailure_UsesConflictGuidance()
    {
        var failure = Failure(HotKeyAction.ScreenshotRegion, "Screenshot region (Ctrl+Shift+1)");

        var message = GlobalHotKeyFailureFormatter.FormatApply([failure]);

        Assert.Contains("Another app may already use this shortcut.", message);
        Assert.Contains("Choose a different combination.", message);
    }

    private static GlobalHotKeyRegistrationFailure Failure(HotKeyAction? action, string name)
        => new(action, name, 1409, "Windows rejected this shortcut.");
}
