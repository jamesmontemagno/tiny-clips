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
    [InlineData(StudioShortcutKey.Escape, StudioShortcutAction.RequestClose)]
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
    [InlineData(StudioShortcutKey.Escape, false, StudioShortcutAction.None)]
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
    public void Escape_AsksTheWindowToClose()
    {
        var escape = Press(StudioShortcutKey.Escape);

        Assert.Equal(StudioShortcutAction.RequestClose, StudioShortcuts.Resolve(escape));

        // Also where the project is not open: a window that cannot show its project closes on Esc too.
        Assert.Equal(StudioShortcutAction.RequestClose, StudioShortcuts.Resolve(escape with { IsReady = false }));

        // Esc lets go of nothing first: what is selected, and a scene that has the focus, change nothing.
        Assert.Equal(
            StudioShortcutAction.RequestClose,
            StudioShortcuts.Resolve(escape with { HasSelectedZoom = true, HasSelectedCut = true, HasSelectedSpeed = true, IsSceneFocused = true }));
    }

    [Fact]
    public void Escape_WithCtrlShiftOrAlt_IsLeftToTheSystem()
    {
        var escape = Press(StudioShortcutKey.Escape);
        StudioShortcutInput[] modified =
        [
            escape with { IsControlDown = true },
            escape with { IsShiftDown = true },
            escape with { IsAltDown = true },
            escape with { IsControlDown = true, IsShiftDown = true },
            escape with { IsControlDown = true, IsAltDown = true },
            escape with { IsShiftDown = true, IsAltDown = true },
            escape with { IsControlDown = true, IsShiftDown = true, IsAltDown = true },
        ];

        foreach (var press in modified)
        {
            Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(press));

            // Not even to stop an export.
            Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(press with { IsExporting = true }));
        }
    }

    [Fact]
    public void Escape_WhileAnExportRuns_StopsTheExport_AndThatIsAll()
    {
        // A press, wherever the focus is, in a drag or not: the export is stopped, and the
        // window is not asked to close.
        bool[] either = [false, true];
        var presses =
            from text in either
            from list in either
            from open in either
            from dragging in either
            from ready in either
            select Press(StudioShortcutKey.Escape) with
            {
                IsExporting = true,
                IsTextInputFocused = text,
                IsTypeToSearchFocused = list,
                IsDropDownOpen = open,
                IsDragging = dragging,
                IsReady = ready,
            };

        foreach (var press in presses)
        {
            Assert.Equal(StudioShortcutAction.CancelExport, StudioShortcuts.Resolve(press));

            // The same key, still held, stops nothing: see Escape_HeldDown_DoesNothingAtAll.
            Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(press with { IsRepeat = true }));
        }
    }

    [Fact]
    public void Escape_HeldDown_DoesNothingAtAll()
    {
        // The press it belongs to has done what there was to do: the press that stopped an
        // export must not go on to close the window, and the press whose question was answered
        // must not ask it again.
        var held = Press(StudioShortcutKey.Escape) with { IsRepeat = true };

        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(held));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(held with { IsReady = false }));

        // Also while an export runs, because the press may have been someone else's. The Esc
        // that answered "Keep exporting" in the question about a running export, held a little
        // too long, comes to the window once the question is gone: it must not stop the export
        // it was asked to keep. The same for a window in front that closed on Esc.
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(held with { IsExporting = true }));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(held with { IsExporting = true, IsDragging = true }));
    }

    [Fact]
    public void Escape_InTextBeingEdited_OrWhileADropDownListIsOpen_BelongsToThatControl()
    {
        var escape = Press(StudioShortcutKey.Escape);

        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(escape with { IsTextInputFocused = true }));

        // An open list closes on Esc. The focus is then on one of its items, or on the drop-down
        // itself: either way it is also a list that searches as you type.
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(escape with { IsDropDownOpen = true }));
        Assert.Equal(
            StudioShortcutAction.None,
            StudioShortcuts.Resolve(escape with { IsDropDownOpen = true, IsTypeToSearchFocused = true }));
    }

    [Fact]
    public void Escape_OnADropDownThatIsClosed_AsksTheWindowToClose()
    {
        // The focus stays on a drop-down after a choice is made from it. Esc must not be dead
        // there until the focus is moved: only an open list keeps the key.
        var onAClosedDropDown = Press(StudioShortcutKey.Escape) with { IsTypeToSearchFocused = true };

        Assert.Equal(StudioShortcutAction.RequestClose, StudioShortcuts.Resolve(onAClosedDropDown));
        Assert.Equal(StudioShortcutAction.RequestClose, StudioShortcuts.Resolve(onAClosedDropDown with { IsReady = false }));
    }

    [Fact]
    public void Escape_WhileSomethingIsDragged_DoesNotAskTheWindowToClose()
    {
        var escape = Press(StudioShortcutKey.Escape) with { IsDragging = true };

        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(escape));
    }

    [Fact]
    public void Escape_TakesTheFirstRuleThatApplies()
    {
        // Every state an Esc press can arrive in. In this order: a modifier leaves the key to
        // the system; a held key does nothing at all; an export that runs is stopped; text
        // being edited, an open drop-down list and a drag keep the window open; otherwise it is
        // asked to close. A drop-down that has the focus, and whether the project is open,
        // change nothing.
        bool[] either = [false, true];
        var presses =
            from control in either
            from shift in either
            from alt in either
            from exporting in either
            from repeat in either
            from text in either
            from list in either
            from open in either
            from dragging in either
            from ready in either
            select Press(StudioShortcutKey.Escape) with
            {
                IsControlDown = control,
                IsShiftDown = shift,
                IsAltDown = alt,
                IsExporting = exporting,
                IsRepeat = repeat,
                IsTextInputFocused = text,
                IsTypeToSearchFocused = list,
                IsDropDownOpen = open,
                IsDragging = dragging,
                IsReady = ready,
            };

        foreach (var press in presses)
        {
            var expected =
                press.IsControlDown || press.IsShiftDown || press.IsAltDown ? StudioShortcutAction.None
                : press.IsRepeat ? StudioShortcutAction.None
                : press.IsExporting ? StudioShortcutAction.CancelExport
                : press.IsTextInputFocused || press.IsDropDownOpen || press.IsDragging ? StudioShortcutAction.None
                : StudioShortcutAction.RequestClose;
            var actual = StudioShortcuts.Resolve(press);

            Assert.True(expected == actual, $"{press}: expected {expected}, and it was {actual}");
        }
    }

    [Fact]
    public void NoOtherKeyAsksTheWindowToClose()
    {
        bool[] either = [false, true];
        var presses =
            from key in Enum.GetValues<StudioShortcutKey>().Where(key => key != StudioShortcutKey.Escape)
            from control in either
            from shift in either
            from alt in either
            from repeat in either
            from ready in either
            from exporting in either
            select Press(key) with
            {
                IsControlDown = control,
                IsShiftDown = shift,
                IsAltDown = alt,
                IsRepeat = repeat,
                IsReady = ready,
                IsExporting = exporting,
                HasSelectedZoom = true,
                HasSelectedCut = true,
                HasSelectedSpeed = true,
            };

        foreach (var press in presses)
        {
            Assert.NotEqual(StudioShortcutAction.RequestClose, StudioShortcuts.Resolve(press));
        }
    }

    [Fact]
    public void AnOpenDropDownList_ChangesNoOtherKey()
    {
        // The fact is Esc's alone. What a drop-down keeps of the other keys goes by whether it
        // has the focus, as it did.
        bool[] either = [false, true];
        var presses =
            from key in Enum.GetValues<StudioShortcutKey>().Where(key => key != StudioShortcutKey.Escape)
            from control in either
            from shift in either
            from repeat in either
            from list in either
            from exporting in either
            from dragging in either
            select Press(key) with
            {
                IsControlDown = control,
                IsShiftDown = shift,
                IsRepeat = repeat,
                IsTypeToSearchFocused = list,
                IsExporting = exporting,
                IsDragging = dragging,
                HasSelectedZoom = true,
                HasSelectedCut = true,
                HasSelectedSpeed = true,
            };

        foreach (var press in presses)
        {
            Assert.Equal(StudioShortcuts.Resolve(press), StudioShortcuts.Resolve(press with { IsDropDownOpen = true }));
        }
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
