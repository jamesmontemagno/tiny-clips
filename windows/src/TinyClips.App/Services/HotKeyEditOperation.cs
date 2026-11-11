using TinyClips.Core.Models;
using TinyClips.Core.Services;

namespace TinyClips.App;

internal sealed class HotKeyEditOperation(
    IHotKeyService hotKeys,
    Func<GlobalHotKeyRegistrationResult> register,
    Action<HotKeyAction, HotKeyDefinition> setBinding)
{
    public bool TryApply(HotKeyAction action, HotKeyDefinition candidate, out string error)
    {
        var validation = hotKeys.ValidateBinding(action, candidate);
        if (!candidate.IsUnbound && !validation.IsValid)
        {
            error = ValidationMessage(validation);
            return false;
        }

        var previous = hotKeys.GetBinding(action);
        setBinding(action, candidate);
        var failures = register().BlockingFailuresFor(action);
        if (failures.Count == 0)
        {
            error = string.Empty;
            return true;
        }

        setBinding(action, previous);
        var rollbackFailures = register().BlockingFailuresFor(action);
        error = GlobalHotKeyFailureFormatter.FormatApply(failures);
        if (rollbackFailures.Count > 0)
        {
            error += GlobalHotKeyFailureFormatter.FormatRollback(rollbackFailures);
        }

        return false;
    }

    public static string ValidationMessage(HotKeyValidationResult result) => result.Error switch
    {
        HotKeyValidationError.ModifierRequired => "Include at least one modifier: Ctrl, Alt, Shift, or Win.",
        HotKeyValidationError.KeyRequired or HotKeyValidationError.ModifierKeyNotAllowed =>
            "Include one non-modifier key with the modifier.",
        HotKeyValidationError.DuplicateBinding =>
            $"That shortcut is already assigned to {ActionName(result.ConflictingAction!.Value)}.",
        HotKeyValidationError.StopRecordingConflict =>
            "That shortcut is reserved for Stop recording (Ctrl+Shift+S). Choose a different combination.",
        _ => string.Empty,
    };

    public static string ActionName(HotKeyAction action) => action switch
    {
        HotKeyAction.Screenshot => "Screenshot",
        HotKeyAction.RecordVideo => "Record video",
        HotKeyAction.RecordGif => "Record GIF",
        HotKeyAction.RecognizeText => "Recognize text",
        HotKeyAction.ScreenshotRegion => "Screenshot region",
        HotKeyAction.ScreenshotWindow => "Screenshot window",
        HotKeyAction.ScreenshotScreen => "Screenshot screen",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };
}
