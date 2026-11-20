namespace TinyClips.Core.Models;

public enum HotKeyAction
{
    Screenshot,
    RecordVideo,
    RecordGif,
    RecognizeText,
    ScreenshotRegion,
    ScreenshotWindow,
    ScreenshotScreen,

    /// <summary>
    /// Stops the active recording. Its optional global binding is registered only while recording.
    /// </summary>
    StopRecording,
}
