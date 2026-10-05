using TinyClips.Core.Studio.Editing;

namespace TinyClips.Core.Tests;

/// <summary>The Studio editor's keys. The window cannot be driven with a real keyboard in tests, so the rules are here.</summary>
public sealed class StudioEditorSessionShortcutTests
{
    [Theory]
    [InlineData(StudioShortcutKey.Space, StudioShortcutAction.TogglePlayback)]
    [InlineData(StudioShortcutKey.Left, StudioShortcutAction.PreviousFrame)]
    [InlineData(StudioShortcutKey.Right, StudioShortcutAction.NextFrame)]
    [InlineData(StudioShortcutKey.I, StudioShortcutAction.SetTrimStartAtPlayhead)]
    [InlineData(StudioShortcutKey.O, StudioShortcutAction.SetTrimEndAtPlayhead)]
    [InlineData(StudioShortcutKey.Digit1, StudioShortcutAction.ShowScreenLayout)]
    [InlineData(StudioShortcutKey.Digit2, StudioShortcutAction.ShowBubbleLayout)]
    [InlineData(StudioShortcutKey.Digit3, StudioShortcutAction.ShowSideBySideLayout)]
    [InlineData(StudioShortcutKey.Digit4, StudioShortcutAction.ShowCameraLayout)]
    [InlineData(StudioShortcutKey.Z, StudioShortcutAction.AddZoom)]
    [InlineData(StudioShortcutKey.Delete, StudioShortcutAction.None)]
    [InlineData(StudioShortcutKey.Y, StudioShortcutAction.None)]
    [InlineData(StudioShortcutKey.E, StudioShortcutAction.None)]
    [InlineData(StudioShortcutKey.Escape, StudioShortcutAction.None)]
    [InlineData(StudioShortcutKey.Other, StudioShortcutAction.None)]
    public void PlainKeys(StudioShortcutKey key, StudioShortcutAction expected)
    {
        Assert.Equal(expected, StudioShortcuts.Resolve(Press(key)));
    }

    [Theory]
    [InlineData(StudioShortcutKey.Z, false, StudioShortcutAction.Undo)]
    [InlineData(StudioShortcutKey.Z, true, StudioShortcutAction.Redo)]
    [InlineData(StudioShortcutKey.Y, false, StudioShortcutAction.Redo)]
    [InlineData(StudioShortcutKey.Y, true, StudioShortcutAction.None)]
    [InlineData(StudioShortcutKey.E, false, StudioShortcutAction.Export)]
    [InlineData(StudioShortcutKey.E, true, StudioShortcutAction.None)]
    [InlineData(StudioShortcutKey.Space, false, StudioShortcutAction.None)]
    [InlineData(StudioShortcutKey.Left, false, StudioShortcutAction.None)]
    [InlineData(StudioShortcutKey.Digit1, false, StudioShortcutAction.None)]
    [InlineData(StudioShortcutKey.I, false, StudioShortcutAction.None)]
    [InlineData(StudioShortcutKey.Delete, false, StudioShortcutAction.None)]
    public void ControlKeys(StudioShortcutKey key, bool shift, StudioShortcutAction expected)
    {
        Assert.Equal(expected, StudioShortcuts.Resolve(Press(key) with { IsControlDown = true, IsShiftDown = shift }));
    }

    [Theory]
    [InlineData(StudioShortcutKey.Space)]
    [InlineData(StudioShortcutKey.Left)]
    [InlineData(StudioShortcutKey.I)]
    [InlineData(StudioShortcutKey.Z)]
    [InlineData(StudioShortcutKey.Digit2)]
    public void ShiftOrAlt_TurnsAPlainKeyIntoNothing(StudioShortcutKey key)
    {
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(Press(key) with { IsShiftDown = true }));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(Press(key) with { IsAltDown = true }));
        Assert.Equal(
            StudioShortcutAction.None,
            StudioShortcuts.Resolve(Press(StudioShortcutKey.Z) with { IsControlDown = true, IsAltDown = true }));
    }

    [Theory]
    [InlineData(StudioShortcutKey.Left, false, StudioShortcutAction.PreviousFrame)]
    [InlineData(StudioShortcutKey.Right, false, StudioShortcutAction.NextFrame)]
    [InlineData(StudioShortcutKey.Space, false, StudioShortcutAction.None)]
    [InlineData(StudioShortcutKey.I, false, StudioShortcutAction.None)]
    [InlineData(StudioShortcutKey.O, false, StudioShortcutAction.None)]
    [InlineData(StudioShortcutKey.Digit3, false, StudioShortcutAction.None)]
    [InlineData(StudioShortcutKey.Z, false, StudioShortcutAction.None)]
    [InlineData(StudioShortcutKey.Z, true, StudioShortcutAction.Undo)]
    [InlineData(StudioShortcutKey.Y, true, StudioShortcutAction.Redo)]
    [InlineData(StudioShortcutKey.E, true, StudioShortcutAction.None)]
    public void HoldingAKey_RepeatsOnlySteppingUndoAndRedo(StudioShortcutKey key, bool control, StudioShortcutAction expected)
    {
        Assert.Equal(expected, StudioShortcuts.Resolve(Press(key) with { IsControlDown = control, IsRepeat = true }));
    }

    [Theory]
    [InlineData(StudioShortcutKey.Space, false)]
    [InlineData(StudioShortcutKey.Left, false)]
    [InlineData(StudioShortcutKey.I, false)]
    [InlineData(StudioShortcutKey.O, false)]
    [InlineData(StudioShortcutKey.Digit1, false)]
    [InlineData(StudioShortcutKey.Z, false)]
    [InlineData(StudioShortcutKey.Z, true)]
    [InlineData(StudioShortcutKey.Y, true)]
    [InlineData(StudioShortcutKey.E, true)]
    public void ATextBox_KeepsEveryKey(StudioShortcutKey key, bool control)
    {
        Assert.Equal(
            StudioShortcutAction.None,
            StudioShortcuts.Resolve(Press(key) with { IsControlDown = control, IsTextInputFocused = true }));
    }

    [Theory]
    [InlineData(StudioShortcutKey.Space, false)]
    [InlineData(StudioShortcutKey.Right, false)]
    [InlineData(StudioShortcutKey.O, false)]
    [InlineData(StudioShortcutKey.Digit4, false)]
    [InlineData(StudioShortcutKey.Z, false)]
    [InlineData(StudioShortcutKey.Z, true)]
    [InlineData(StudioShortcutKey.E, true)]
    public void NothingActs_UntilTheProjectIsOpen_OrWhileItIsExporting(StudioShortcutKey key, bool control)
    {
        var press = Press(key) with { IsControlDown = control };

        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(press with { IsReady = false }));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(press with { IsExporting = true }));
    }

    [Theory]
    [InlineData(StudioShortcutKey.Space)]
    [InlineData(StudioShortcutKey.Left)]
    [InlineData(StudioShortcutKey.Right)]
    [InlineData(StudioShortcutKey.I)]
    [InlineData(StudioShortcutKey.O)]
    [InlineData(StudioShortcutKey.Digit1)]
    [InlineData(StudioShortcutKey.Digit4)]
    [InlineData(StudioShortcutKey.Z)]
    public void AListThatSearchesAsYouType_KeepsThePlainKeys(StudioShortcutKey key)
    {
        Assert.Equal(
            StudioShortcutAction.None,
            StudioShortcuts.Resolve(Press(key) with { IsTypeToSearchFocused = true }));
    }

    [Theory]
    [InlineData(StudioShortcutKey.Z, false, StudioShortcutAction.Undo)]
    [InlineData(StudioShortcutKey.Z, true, StudioShortcutAction.Redo)]
    [InlineData(StudioShortcutKey.Y, false, StudioShortcutAction.Redo)]
    [InlineData(StudioShortcutKey.E, false, StudioShortcutAction.Export)]
    public void AListThatSearchesAsYouType_LeavesTheControlShortcuts(StudioShortcutKey key, bool shift, StudioShortcutAction expected)
    {
        var press = Press(key) with { IsControlDown = true, IsShiftDown = shift, IsTypeToSearchFocused = true };

        Assert.Equal(expected, StudioShortcuts.Resolve(press));
    }

    [Fact]
    public void Delete_RemovesTheSelectedZoom_AndWithoutOneIsLeftAlone()
    {
        var delete = Press(StudioShortcutKey.Delete);
        var selected = delete with { HasSelectedZoom = true };

        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(delete));
        Assert.Equal(StudioShortcutAction.RemoveSelectedZoom, StudioShortcuts.Resolve(selected));

        // Once per press, and only as a plain key.
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(selected with { IsRepeat = true }));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(selected with { IsControlDown = true }));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(selected with { IsShiftDown = true }));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(selected with { IsAltDown = true }));

        // Text being edited and a list that searches as you type keep it.
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(selected with { IsTextInputFocused = true }));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(selected with { IsTypeToSearchFocused = true }));

        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(selected with { IsReady = false }));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(selected with { IsExporting = true }));
    }

    [Fact]
    public void ASelectedZoom_ChangesNoOtherKey()
    {
        foreach (var key in Enum.GetValues<StudioShortcutKey>().Where(key => key != StudioShortcutKey.Delete))
        {
            foreach (var control in new[] { false, true })
            {
                var press = Press(key) with { IsControlDown = control };

                Assert.Equal(StudioShortcuts.Resolve(press), StudioShortcuts.Resolve(press with { HasSelectedZoom = true }));
            }
        }
    }

    [Fact]
    public void Escape_StopsARunningExport_AndOtherwiseDoesNothing()
    {
        var escape = Press(StudioShortcutKey.Escape);

        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(escape));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(escape with { IsReady = false }));
        Assert.Equal(StudioShortcutAction.CancelExport, StudioShortcuts.Resolve(escape with { IsExporting = true }));
        Assert.Equal(
            StudioShortcutAction.CancelExport,
            StudioShortcuts.Resolve(escape with { IsExporting = true, IsTextInputFocused = true, IsRepeat = true }));
        Assert.Equal(
            StudioShortcutAction.CancelExport,
            StudioShortcuts.Resolve(escape with { IsExporting = true, IsTypeToSearchFocused = true }));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(escape with { IsExporting = true, IsControlDown = true }));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(escape with { IsExporting = true, IsShiftDown = true }));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(escape with { IsExporting = true, IsAltDown = true }));
    }

    [Theory]
    [InlineData(StudioShortcutKey.Space, StudioShortcutAction.TogglePlayback)]
    [InlineData(StudioShortcutKey.Left, StudioShortcutAction.PreviousFrame)]
    [InlineData(StudioShortcutKey.Right, StudioShortcutAction.NextFrame)]
    public void WhileSomethingIsDragged_TheKeysThatMoveThePlayheadStillAct(StudioShortcutKey key, StudioShortcutAction expected)
    {
        Assert.Equal(expected, StudioShortcuts.Resolve(Press(key) with { IsDragging = true }));
    }

    [Theory]
    [InlineData(StudioShortcutKey.I, false, false)]
    [InlineData(StudioShortcutKey.O, false, false)]
    [InlineData(StudioShortcutKey.R, false, false)]
    [InlineData(StudioShortcutKey.S, false, false)]
    [InlineData(StudioShortcutKey.X, false, false)]
    [InlineData(StudioShortcutKey.Z, false, false)]
    [InlineData(StudioShortcutKey.Digit1, false, false)]
    [InlineData(StudioShortcutKey.Digit2, false, false)]
    [InlineData(StudioShortcutKey.Digit3, false, false)]
    [InlineData(StudioShortcutKey.Digit4, false, false)]
    [InlineData(StudioShortcutKey.Z, true, false)]
    [InlineData(StudioShortcutKey.Z, true, true)]
    [InlineData(StudioShortcutKey.Y, true, false)]
    [InlineData(StudioShortcutKey.E, true, false)]
    public void WhileSomethingIsDragged_AKeyThatChangesTheProjectDoesNothing(StudioShortcutKey key, bool control, bool shift)
    {
        var press = Press(key) with { IsControlDown = control, IsShiftDown = shift };

        // The key has a meaning, and has none for as long as the drag lasts.
        Assert.NotEqual(StudioShortcutAction.None, StudioShortcuts.Resolve(press));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(press with { IsDragging = true }));
    }

    [Fact]
    public void WhileSomethingIsDragged_DeleteTakesNothingAway()
    {
        var delete = Press(StudioShortcutKey.Delete);
        StudioShortcutInput[] presses =
        [
            delete with { HasSelectedZoom = true },
            delete with { HasSelectedCut = true },
            delete with { HasSelectedSpeed = true },
            delete with { IsSceneFocused = true },
        ];

        foreach (var press in presses)
        {
            // Among them the block the pointer holds: with it gone, the rest of the drag would
            // move the one next to it.
            Assert.NotEqual(StudioShortcutAction.None, StudioShortcuts.Resolve(press));
            Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(press with { IsDragging = true }));
        }
    }

    [Fact]
    public void WhileSomethingIsDragged_NoKeyDoesMoreThanMoveThePlayhead()
    {
        // Every key, with every modifier, held or not, and with everything selected: a drag gives
        // none of them a new meaning, and leaves only three of them the one they have.
        StudioShortcutAction[] allowed =
        [
            StudioShortcutAction.None,
            StudioShortcutAction.TogglePlayback,
            StudioShortcutAction.PreviousFrame,
            StudioShortcutAction.NextFrame,
        ];
        bool[] either = [false, true];
        var presses =
            from key in Enum.GetValues<StudioShortcutKey>()
            from control in either
            from shift in either
            from repeat in either
            from scene in either
            from list in either
            select Press(key) with
            {
                IsControlDown = control,
                IsShiftDown = shift,
                IsRepeat = repeat,
                IsSceneFocused = scene,
                IsTypeToSearchFocused = list,
                HasSelectedZoom = true,
                HasSelectedCut = true,
                HasSelectedSpeed = true,
            };

        foreach (var press in presses)
        {
            var free = StudioShortcuts.Resolve(press);
            var held = StudioShortcuts.Resolve(press with { IsDragging = true });

            Assert.Contains(held, allowed);
            Assert.True(held == free || held == StudioShortcutAction.None, $"{press.Key}: {free} became {held}");
        }
    }

    [Fact]
    public void WhileSomethingIsDragged_EscapeStillStopsAnExport()
    {
        // An export cannot start in the middle of a drag. Should the editor ever think one is
        // still on, the way to stop an export must not go with it.
        var escape = Press(StudioShortcutKey.Escape) with { IsExporting = true, IsDragging = true };

        Assert.Equal(StudioShortcutAction.CancelExport, StudioShortcuts.Resolve(escape));
    }

    /// <summary>A first press with no modifier, in an open project that is not exporting.</summary>
    private static StudioShortcutInput Press(StudioShortcutKey key) => new(
        key,
        IsControlDown: false,
        IsShiftDown: false,
        IsAltDown: false,
        IsRepeat: false,
        IsTextInputFocused: false,
        IsReady: true,
        IsExporting: false);
}
