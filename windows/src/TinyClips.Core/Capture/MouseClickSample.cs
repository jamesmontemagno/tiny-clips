namespace TinyClips.Core.Capture;

/// <summary>
/// A single recorded mouse-button-down event captured during a recording: the time
/// (seconds, relative to recording start) and the screen location in virtual-desktop
/// physical pixels.
/// </summary>
public readonly record struct MouseClickSample(
    double TimeSeconds,
    int ScreenX,
    int ScreenY,
    MouseClickButton Button = MouseClickButton.Left,
    TimeSpan SourceTimestamp = default);

public enum MouseClickButton
{
    Left,
    Right,
    Middle,
    Other,
}
