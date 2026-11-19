using TinyClips.Core.Services;
using TinyClips.Core.Studio;

namespace TinyClips.Core.Capture;

public sealed record VideoRecordingOptions
{
    public static VideoRecordingOptions Default { get; } = new();

    public bool RecordForStudio { get; init; }

    /// <summary>Recording-local device choices; null uses the saved preference.</summary>
    public bool? RecordMicrophone { get; init; }

    public bool? WebcamEnabled { get; init; }

    public string? AppVersion { get; init; }

    /// <summary>The look a Studio project created from this recording starts with; null uses the built-in default.</summary>
    public StudioLook? Look { get; init; }

    public VideoRecordingOptions ResolveDevices(ICaptureSettings settings) => this with
    {
        RecordMicrophone = RecordMicrophone ?? settings.RecordMicrophone,
        WebcamEnabled = WebcamEnabled ?? settings.WebcamEnabled,
    };

    public bool MatchesCaptureOptions(VideoRecordingOptions other) =>
        RecordForStudio == other.RecordForStudio &&
        RecordMicrophone == other.RecordMicrophone &&
        WebcamEnabled == other.WebcamEnabled;
}
