using Windows.System;

namespace TinyClips.App;

/// <summary>How the user chose to scope a capture.</summary>
public enum CapturePickerMode
{
    Region,
    Screen,
    Window,
    RecognizeText,
    /// <summary>Screenshot only: select a region, scroll, and stitch the frames into one tall image.</summary>
    Scrolling,
    /// <summary>
    /// Screenshot only: select a region of another app's scrollable pane; Tiny Clips scrolls it in
    /// the background and stitches the frames into one tall image.
    /// </summary>
    AutoScroll,
}

internal static class CapturePickerKeyboardShortcuts
{
    internal static CapturePickerMode? ModeForKey(VirtualKey key, bool isScrollingAvailable) => key switch
    {
        VirtualKey.R => CapturePickerMode.Region,
        VirtualKey.S => CapturePickerMode.Screen,
        VirtualKey.W => CapturePickerMode.Window,
        VirtualKey.P when isScrollingAvailable => CapturePickerMode.Scrolling,
        VirtualKey.A when isScrollingAvailable => CapturePickerMode.AutoScroll,
        _ => null,
    };
}
