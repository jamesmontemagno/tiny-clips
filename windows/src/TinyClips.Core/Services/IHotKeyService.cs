using TinyClips.Core.Models;

namespace TinyClips.Core.Services;

public interface IHotKeyService
{
    HotKeyDefinition GetBinding(HotKeyAction action);
    IReadOnlyList<KeyValuePair<HotKeyAction, HotKeyDefinition>> GetActiveBindings(bool isRecording);
    HotKeyDefinition GetStopBinding();
    string StopRecordingDisplayString { get; }
    void SetBinding(HotKeyAction action, HotKeyDefinition binding);
    HotKeyDefinition DefaultFor(HotKeyAction action);
    HotKeyValidationResult ValidateBinding(HotKeyAction action, HotKeyDefinition binding);
}
