namespace TinyClips.Core.Models;

public enum HotKeyValidationError
{
    None,
    ModifierRequired,
    KeyRequired,
    ModifierKeyNotAllowed,
    DuplicateBinding,
}

public readonly record struct HotKeyValidationResult(
    HotKeyValidationError Error,
    HotKeyAction? ConflictingAction = null)
{
    public bool IsValid => Error == HotKeyValidationError.None;

    public static HotKeyValidationResult Valid => new(HotKeyValidationError.None);
}
