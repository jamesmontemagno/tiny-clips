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

        if (modifiers == 0 && virtualKey == 0)
        {
            return DefaultFor(action);
        }

        return new HotKeyDefinition((HotKeyModifiers)modifiers, virtualKey);
    }

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
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, null);
        }
    }

    public HotKeyDefinition GetStopBinding() => GetBinding(HotKeyAction.StopRecording);

    public string StopRecordingDisplayString => GetStopBinding().DisplayString;

    public HotKeyDefinition DefaultFor(HotKeyAction action) => action switch
    {
        HotKeyAction.Screenshot => new HotKeyDefinition(HotKeyModifiers.Control | HotKeyModifiers.Shift, 0x35),
        HotKeyAction.RecordVideo => new HotKeyDefinition(HotKeyModifiers.Control | HotKeyModifiers.Shift, 0x36),
        HotKeyAction.RecordGif => new HotKeyDefinition(HotKeyModifiers.Control | HotKeyModifiers.Shift, 0x37),
        HotKeyAction.RecognizeText => new HotKeyDefinition(HotKeyModifiers.Control | HotKeyModifiers.Shift, 0x54),
        HotKeyAction.ScreenshotRegion => new HotKeyDefinition(HotKeyModifiers.None, 0),
        HotKeyAction.ScreenshotWindow => new HotKeyDefinition(HotKeyModifiers.None, 0),
        HotKeyAction.StopRecording => new HotKeyDefinition(HotKeyModifiers.Control | HotKeyModifiers.Shift, 0x53),
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };

    public HotKeyValidationResult ValidateBinding(HotKeyAction action, HotKeyDefinition binding)
    {
        var actions = new[]
        {
            HotKeyAction.Screenshot,
            HotKeyAction.RecordVideo,
            HotKeyAction.RecordGif,
            HotKeyAction.RecognizeText,
            HotKeyAction.ScreenshotRegion,
            HotKeyAction.ScreenshotWindow,
        };

        // Skip the unbound sentinel (no modifiers, no key) so two unbound actions are not
        // reported as conflicting with each other.
        var bindings = actions
            .Select(a => new KeyValuePair<HotKeyAction, HotKeyDefinition>(a, GetBinding(a)))
            .Where(pair => !pair.Value.IsUnbound)
            .ToArray();

        return HotKeyValidator.Validate(action, binding, bindings, GetStopBinding());
    }

    private int GetStoredModifiers(HotKeyAction action) => action switch
    {
        HotKeyAction.Screenshot => _settings.ScreenshotHotKeyModifiers,
        HotKeyAction.RecordVideo => _settings.VideoHotKeyModifiers,
        HotKeyAction.RecordGif => _settings.GifHotKeyModifiers,
        HotKeyAction.RecognizeText => _settings.OcrHotKeyModifiers,
        HotKeyAction.ScreenshotRegion => _settings.ScreenshotRegionHotKeyModifiers,
        HotKeyAction.ScreenshotWindow => _settings.ScreenshotWindowHotKeyModifiers,
        HotKeyAction.StopRecording => 0,
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
        HotKeyAction.StopRecording => 0,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };
}
