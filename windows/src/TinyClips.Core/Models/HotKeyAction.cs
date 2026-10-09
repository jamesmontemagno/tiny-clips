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
    /// Stops the active recording. Not user-configurable; its binding is fixed
    /// and is not persisted in settings.
    /// </summary>
    StopRecording,
}
