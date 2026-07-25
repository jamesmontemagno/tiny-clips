namespace TinyClips.Core.Models;

public enum HotKeyValidationError
{
    None,
    ModifierRequired,
    KeyRequired,
    ModifierKeyNotAllowed,
    DuplicateBinding,
    StopRecordingConflict,
}

public readonly record struct HotKeyValidationResult(
    HotKeyValidationError Error,
    CaptureType? ConflictingCaptureType = null)
{
    public bool IsValid => Error == HotKeyValidationError.None;

    public static HotKeyValidationResult Valid => new(HotKeyValidationError.None);
}
