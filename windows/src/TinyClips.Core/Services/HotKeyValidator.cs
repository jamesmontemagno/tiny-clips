using TinyClips.Core.Models;

namespace TinyClips.Core.Services;

public static class HotKeyValidator
{
    private static readonly HashSet<uint> ModifierVirtualKeys =
    [
        0x10, 0x11, 0x12,
        0x5B, 0x5C,
        0xA0, 0xA1,
        0xA2, 0xA3,
        0xA4, 0xA5,
    ];

    public static HotKeyValidationResult Validate(
        HotKeyAction targetAction,
        HotKeyDefinition candidate,
        IEnumerable<KeyValuePair<HotKeyAction, HotKeyDefinition>> bindings,
        HotKeyDefinition stopRecordingBinding)
    {
        if (candidate.Modifiers == HotKeyModifiers.None)
        {
            return new HotKeyValidationResult(HotKeyValidationError.ModifierRequired);
        }

        if (candidate.VirtualKey == 0)
        {
            return new HotKeyValidationResult(HotKeyValidationError.KeyRequired);
        }

        if (ModifierVirtualKeys.Contains(candidate.VirtualKey))
        {
            return new HotKeyValidationResult(HotKeyValidationError.ModifierKeyNotAllowed);
        }

        foreach (var binding in bindings)
        {
            if (binding.Key != targetAction && binding.Value == candidate)
            {
                return new HotKeyValidationResult(
                    HotKeyValidationError.DuplicateBinding,
                    binding.Key);
            }
        }

        if (candidate == stopRecordingBinding)
        {
            return new HotKeyValidationResult(HotKeyValidationError.StopRecordingConflict);
        }

        return HotKeyValidationResult.Valid;
    }
}
