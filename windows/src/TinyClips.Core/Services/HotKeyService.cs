using TinyClips.Core.Models;

namespace TinyClips.Core.Services;

public sealed class HotKeyService : IHotKeyService
{
    private readonly ICaptureSettings _settings;

    public HotKeyService(ICaptureSettings settings)
    {
        _settings = settings;
    }

    public HotKeyDefinition GetBinding(HotKeyAction action)
    {
        var modifiers = GetStoredModifiers(action);
        var virtualKey = GetStoredVirtualKey(action);
        return new HotKeyDefinition((HotKeyModifiers)modifiers, virtualKey);
    }

    public IReadOnlyList<KeyValuePair<HotKeyAction, HotKeyDefinition>> GetActiveBindings(bool isRecording)
        => Enum.GetValues<HotKeyAction>()
            .Where(action => action != HotKeyAction.StopRecording || isRecording)
            .Select(action => new KeyValuePair<HotKeyAction, HotKeyDefinition>(action, GetBinding(action)))
            .Where(pair => !pair.Value.IsUnbound)
            .ToArray();

    public void SetBinding(HotKeyAction action, HotKeyDefinition binding)
    {
        switch (action)
        {
            case HotKeyAction.Screenshot:
                _settings.ScreenshotHotKeyModifiers = (int)binding.Modifiers;
                _settings.ScreenshotHotKeyCode = (int)binding.VirtualKey;
                break;
            case HotKeyAction.RecordVideo:
                _settings.VideoHotKeyModifiers = (int)binding.Modifiers;
                _settings.VideoHotKeyCode = (int)binding.VirtualKey;
                break;
            case HotKeyAction.RecordGif:
                _settings.GifHotKeyModifiers = (int)binding.Modifiers;
                _settings.GifHotKeyCode = (int)binding.VirtualKey;
                break;
            case HotKeyAction.RecognizeText:
                _settings.OcrHotKeyModifiers = (int)binding.Modifiers;
                _settings.OcrHotKeyCode = (int)binding.VirtualKey;
                break;
            case HotKeyAction.ScreenshotRegion:
                _settings.ScreenshotRegionHotKeyModifiers = (int)binding.Modifiers;
                _settings.ScreenshotRegionHotKeyCode = (int)binding.VirtualKey;
                break;
            case HotKeyAction.ScreenshotWindow:
                _settings.ScreenshotWindowHotKeyModifiers = (int)binding.Modifiers;
                _settings.ScreenshotWindowHotKeyCode = (int)binding.VirtualKey;
                break;
            case HotKeyAction.ScreenshotScreen:
                _settings.ScreenshotScreenHotKeyModifiers = (int)binding.Modifiers;
                _settings.ScreenshotScreenHotKeyCode = (int)binding.VirtualKey;
                break;
            case HotKeyAction.StopRecording:
                _settings.StopRecordingHotKeyModifiers = (int)binding.Modifiers;
                _settings.StopRecordingHotKeyCode = (int)binding.VirtualKey;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, null);
        }
    }

    public HotKeyDefinition GetStopBinding() => GetBinding(HotKeyAction.StopRecording);

    public string StopRecordingDisplayString => GetStopBinding().IsUnbound ? string.Empty : GetStopBinding().DisplayString;

    public HotKeyDefinition DefaultFor(HotKeyAction action) => action switch
    {
        HotKeyAction.Screenshot => new HotKeyDefinition(HotKeyModifiers.Control | HotKeyModifiers.Shift, 0x35),
        HotKeyAction.RecordVideo => new HotKeyDefinition(HotKeyModifiers.None, 0),
        HotKeyAction.RecordGif => new HotKeyDefinition(HotKeyModifiers.None, 0),
        HotKeyAction.RecognizeText => new HotKeyDefinition(HotKeyModifiers.None, 0),
        HotKeyAction.ScreenshotRegion => new HotKeyDefinition(HotKeyModifiers.None, 0),
        HotKeyAction.ScreenshotWindow => new HotKeyDefinition(HotKeyModifiers.None, 0),
        HotKeyAction.ScreenshotScreen => new HotKeyDefinition(HotKeyModifiers.None, 0),
        HotKeyAction.StopRecording => new HotKeyDefinition(HotKeyModifiers.None, 0),
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };

    public HotKeyValidationResult ValidateBinding(HotKeyAction action, HotKeyDefinition binding)
    {
        // Skip the unbound sentinel (no modifiers, no key) so two unbound actions are not
        // reported as conflicting with each other.
        var bindings = Enum.GetValues<HotKeyAction>()
            .Select(a => new KeyValuePair<HotKeyAction, HotKeyDefinition>(a, GetBinding(a)))
            .Where(pair => !pair.Value.IsUnbound)
            .ToArray();

        return HotKeyValidator.Validate(action, binding, bindings);
    }

    private int GetStoredModifiers(HotKeyAction action) => action switch
    {
        HotKeyAction.Screenshot => _settings.ScreenshotHotKeyModifiers,
        HotKeyAction.RecordVideo => _settings.VideoHotKeyModifiers,
        HotKeyAction.RecordGif => _settings.GifHotKeyModifiers,
        HotKeyAction.RecognizeText => _settings.OcrHotKeyModifiers,
        HotKeyAction.ScreenshotRegion => _settings.ScreenshotRegionHotKeyModifiers,
        HotKeyAction.ScreenshotWindow => _settings.ScreenshotWindowHotKeyModifiers,
        HotKeyAction.ScreenshotScreen => _settings.ScreenshotScreenHotKeyModifiers,
        HotKeyAction.StopRecording => _settings.StopRecordingHotKeyModifiers,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };

    private uint GetStoredVirtualKey(HotKeyAction action) => action switch
    {
        HotKeyAction.Screenshot => (uint)_settings.ScreenshotHotKeyCode,
        HotKeyAction.RecordVideo => (uint)_settings.VideoHotKeyCode,
        HotKeyAction.RecordGif => (uint)_settings.GifHotKeyCode,
        HotKeyAction.RecognizeText => (uint)_settings.OcrHotKeyCode,
        HotKeyAction.ScreenshotRegion => (uint)_settings.ScreenshotRegionHotKeyCode,
        HotKeyAction.ScreenshotWindow => (uint)_settings.ScreenshotWindowHotKeyCode,
        HotKeyAction.ScreenshotScreen => (uint)_settings.ScreenshotScreenHotKeyCode,
        HotKeyAction.StopRecording => (uint)_settings.StopRecordingHotKeyCode,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };
}
