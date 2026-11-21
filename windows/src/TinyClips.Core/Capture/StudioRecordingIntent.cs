using TinyClips.Core.Models;
using TinyClips.Core.Services;

namespace TinyClips.Core.Capture;

/// <summary>What a video recording was asked for with.</summary>
public enum VideoRecordingCommand
{
    /// <summary>Record video in the tray menu. Always an ordinary recording.</summary>
    RecordVideo,

    /// <summary>The global hotkey of Record video. Always an ordinary recording.</summary>
    RecordVideoHotKey,

    /// <summary>Studio recording in the tray menu or from its global hotkey.</summary>
    StudioRecording,

    /// <summary>
    /// Nobody asked: the capture picker came back by itself after a recording. What was asked
    /// for last still holds.
    /// </summary>
    PickerReturned,
}

/// <summary>
/// Whether the video recording being set up is a Studio recording: one that keeps the screen
/// and the camera as separate layers and opens in Tiny Clips Studio when it ends.
/// </summary>
/// <remarks>
/// The command the recording was asked for with decides, and nothing else does: Studio
/// recording makes one, Record video and its hotkey never do. What was asked for is kept
/// through the capture picker, the recording setup panel, and a picker that comes back after
/// the recording, until a recording is asked for again.
/// Not thread-safe: the app uses it on its UI thread.
/// </remarks>
public sealed class StudioRecordingIntent
{
    private readonly ICaptureSettings _settings;
    private bool _askedForStudio;

    public StudioRecordingIntent(ICaptureSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    /// <summary>
    /// A video recording is about to be set up. Call it once the setup really begins: a command
    /// that is ignored because another capture is under way must not change what that one is.
    /// </summary>
    public void Begin(VideoRecordingCommand command)
    {
        _askedForStudio = command switch
        {
            VideoRecordingCommand.StudioRecording => true,
            VideoRecordingCommand.PickerReturned => _askedForStudio,
            _ => false,
        };
    }

    /// <summary>Whether the recording being set up is a Studio recording.</summary>
    public bool IsForStudio => _askedForStudio;

    public CapturePickerIntent GetPickerIntent(CaptureType type) => type switch
    {
        CaptureType.Screenshot => CapturePickerIntent.Screenshot,
        CaptureType.Video => IsForStudio ? CapturePickerIntent.Studio : CapturePickerIntent.Video,
        CaptureType.Gif => CapturePickerIntent.Gif,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    /// <summary>
    /// The options the recording is set up with. Anything other than a Studio recording gets
    /// <see cref="VideoRecordingOptions.Default"/>, which is the ordinary recording path.
    /// </summary>
    /// <param name="appVersion">The version of the app, which a Studio project is stamped with.</param>
    public VideoRecordingOptions CreateOptions(string? appVersion) => IsForStudio
        ? new VideoRecordingOptions
        {
            RecordForStudio = true,
            AppVersion = appVersion,
            Look = _settings.StudioDefaultLook,
        }
        : VideoRecordingOptions.Default;

}
