using TinyClips.Core.Studio;

namespace TinyClips.Core.Capture;

public sealed record VideoRecordingOptions
{
    public static VideoRecordingOptions Default { get; } = new();

    public bool RecordForStudio { get; init; }

    public string? AppVersion { get; init; }

    /// <summary>The look a Studio project created from this recording starts with; null uses the built-in default.</summary>
    public StudioLook? Look { get; init; }
}
