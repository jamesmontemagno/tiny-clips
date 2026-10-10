using TinyClips.Core.Editing;

namespace TinyClips.Core.Tests;

public sealed class EditorEscapeTests
{
    [Theory]
    [InlineData(true, true, ScreenshotEditorEscapeAction.LeaveToTextInput)]
    [InlineData(true, false, ScreenshotEditorEscapeAction.LeaveToTextInput)]
    [InlineData(false, true, ScreenshotEditorEscapeAction.ClearCropSelection)]
    [InlineData(false, false, ScreenshotEditorEscapeAction.Close)]
    public void ResolveEditorAction_TakesOnlyTheFirstStepThatApplies(
        bool textInputHasFocus, bool hasCropSelection, ScreenshotEditorEscapeAction expected)
    {
        Assert.Equal(expected, EditorEscape.ResolveEditorAction(textInputHasFocus, hasCropSelection));
    }

    [Theory]
    [InlineData(true, true, EditorEscapePrompt.DiscardChanges)]
    [InlineData(true, false, EditorEscapePrompt.CloseWithoutChanges)]
    public void ResolvePrompt_AlwaysAsksWhileConfirmationIsOn(
        bool confirmOnEscape, bool hasUnsavedChanges, EditorEscapePrompt expected)
    {
        Assert.Equal(expected, EditorEscape.ResolvePrompt(confirmOnEscape, hasUnsavedChanges));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ResolvePrompt_ClosesWithoutAskingWhenConfirmationIsOff(bool hasUnsavedChanges)
    {
        Assert.Null(EditorEscape.ResolvePrompt(confirmOnEscape: false, hasUnsavedChanges));
    }

    [Fact]
    public void OnlyDiscardingChangesIsDestructive()
    {
        Assert.True(EditorEscape.IsDestructive(EditorEscapePrompt.DiscardChanges));
        Assert.False(EditorEscape.IsDestructive(EditorEscapePrompt.CloseWithoutChanges));
    }

    [Theory]
    [InlineData(EditorEscapeSurface.ScreenshotEditor, "Close the editor?", "Close editor", "screenshot")]
    [InlineData(EditorEscapeSurface.VideoTrimmer, "Close the trimmer?", "Close trimmer", "recording")]
    [InlineData(EditorEscapeSurface.GifTrimmer, "Close the trimmer?", "Close trimmer", "GIF")]
    public void CloseWithoutChangesText_NamesTheSurfaceAndPointsToSettings(
        EditorEscapeSurface surface, string title, string confirm, string media)
    {
        const EditorEscapePrompt prompt = EditorEscapePrompt.CloseWithoutChanges;

        Assert.Equal(title, EditorEscape.Title(prompt, surface));
        Assert.Equal(confirm, EditorEscape.ConfirmButtonText(prompt, surface));
        var message = EditorEscape.Message(prompt, surface);
        Assert.Contains($"This {media}", message);
        Assert.Contains("General settings", message);
    }

    [Theory]
    [InlineData(EditorEscapeSurface.VideoTrimmer, "original recording")]
    [InlineData(EditorEscapeSurface.GifTrimmer, "original GIF")]
    public void TrimmerDiscardChangesText_SaysTheOriginalIsKept(EditorEscapeSurface surface, string kept)
    {
        const EditorEscapePrompt prompt = EditorEscapePrompt.DiscardChanges;

        Assert.Equal("Discard changes?", EditorEscape.Title(prompt, surface));
        Assert.Equal("Discard changes", EditorEscape.ConfirmButtonText(prompt, surface));
        Assert.Contains(kept, EditorEscape.Message(prompt, surface));
    }

    [Fact]
    public void EditorDiscardChangesText_MatchesTheExistingCloseGuard()
    {
        const EditorEscapePrompt prompt = EditorEscapePrompt.DiscardChanges;
        const EditorEscapeSurface surface = EditorEscapeSurface.ScreenshotEditor;

        Assert.Equal("Discard changes?", EditorEscape.Title(prompt, surface));
        Assert.Equal("You have unsaved annotations. Close anyway?", EditorEscape.Message(prompt, surface));
        Assert.Equal("Discard", EditorEscape.ConfirmButtonText(prompt, surface));
    }
}
