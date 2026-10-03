namespace TinyClips.Core.Capture;

public sealed record VideoRecordingOptions
{
    public static VideoRecordingOptions Default { get; } = new();

    public bool RecordForStudio { get; init; }

    public string? AppVersion { get; init; }
}
