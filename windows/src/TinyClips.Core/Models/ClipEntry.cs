namespace TinyClips.Core.Models;

public sealed record ClipEntry(
    string Path,
    CaptureType Type,
    string FileName,
    DateTimeOffset CapturedAt,
    long FileSizeBytes);
