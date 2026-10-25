using TinyClips.App;
using Windows.System;

namespace TinyClips.App.Tests;

public sealed class CapturePickerKeyboardShortcutsTests
{
    [Theory]
    [InlineData(VirtualKey.R, CapturePickerMode.Region)]
    [InlineData(VirtualKey.S, CapturePickerMode.Screen)]
    [InlineData(VirtualKey.W, CapturePickerMode.Window)]
    public void ModeForKey_MapsCaptureKeys(VirtualKey key, CapturePickerMode expected)
    {
        Assert.Equal(expected, CapturePickerKeyboardShortcuts.ModeForKey(key, isScrollingAvailable: false));
    }

    [Fact]
    public void ModeForKey_MapsScrollingKeyOnlyWhenAvailable()
    {
        Assert.Null(CapturePickerKeyboardShortcuts.ModeForKey(VirtualKey.P, isScrollingAvailable: false));
        Assert.Equal(
            CapturePickerMode.Scrolling,
            CapturePickerKeyboardShortcuts.ModeForKey(VirtualKey.P, isScrollingAvailable: true));
    }

    [Fact]
    public void ModeForKey_MapsAutoScrollKeyOnlyWhenAvailable()
    {
        Assert.Null(CapturePickerKeyboardShortcuts.ModeForKey(VirtualKey.A, isScrollingAvailable: false));
        Assert.Equal(
            CapturePickerMode.AutoScroll,
            CapturePickerKeyboardShortcuts.ModeForKey(VirtualKey.A, isScrollingAvailable: true));
    }

    [Fact]
    public void ModeForKey_DoesNotMapTextRecognitionShortcut()
    {
        Assert.Null(CapturePickerKeyboardShortcuts.ModeForKey(VirtualKey.T, isScrollingAvailable: true));
    }
}
