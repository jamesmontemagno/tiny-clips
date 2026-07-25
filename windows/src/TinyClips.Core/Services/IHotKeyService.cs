using TinyClips.Core.Models;

namespace TinyClips.Core.Services;

public interface IHotKeyService
{
    HotKeyDefinition GetBinding(CaptureType type);
    HotKeyDefinition GetStopBinding();
    string StopRecordingDisplayString { get; }
    void SetBinding(CaptureType type, HotKeyDefinition binding);
    HotKeyDefinition DefaultFor(CaptureType type);
    HotKeyValidationResult ValidateBinding(CaptureType type, HotKeyDefinition binding);
}
