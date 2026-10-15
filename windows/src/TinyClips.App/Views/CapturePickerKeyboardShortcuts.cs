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
}

internal static class CapturePickerKeyboardShortcuts
{
    internal static CapturePickerMode? ModeForKey(VirtualKey key, bool isScrollingAvailable) => key switch
    {
        VirtualKey.R => CapturePickerMode.Region,
        VirtualKey.S => CapturePickerMode.Screen,
        VirtualKey.W => CapturePickerMode.Window,
        VirtualKey.P when isScrollingAvailable => CapturePickerMode.Scrolling,
        _ => null,
    };
}
