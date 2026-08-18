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
        HotKeyAction.StopRecording => new HotKeyDefinition(HotKeyModifiers.Control | HotKeyModifiers.Shift, 0x53),
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };

    public HotKeyValidationResult ValidateBinding(HotKeyAction action, HotKeyDefinition binding)
    {
        var bindings = new[]
        {
            new KeyValuePair<HotKeyAction, HotKeyDefinition>(
                HotKeyAction.Screenshot,
                GetBinding(HotKeyAction.Screenshot)),
            new KeyValuePair<HotKeyAction, HotKeyDefinition>(
                HotKeyAction.RecordVideo,
                GetBinding(HotKeyAction.RecordVideo)),
            new KeyValuePair<HotKeyAction, HotKeyDefinition>(
                HotKeyAction.RecordGif,
                GetBinding(HotKeyAction.RecordGif)),
            new KeyValuePair<HotKeyAction, HotKeyDefinition>(
                HotKeyAction.RecognizeText,
                GetBinding(HotKeyAction.RecognizeText)),
        };

        return HotKeyValidator.Validate(action, binding, bindings, GetStopBinding());
    }

    private int GetStoredModifiers(HotKeyAction action) => action switch
    {
        HotKeyAction.Screenshot => _settings.ScreenshotHotKeyModifiers,
        HotKeyAction.RecordVideo => _settings.VideoHotKeyModifiers,
        HotKeyAction.RecordGif => _settings.GifHotKeyModifiers,
        HotKeyAction.RecognizeText => _settings.OcrHotKeyModifiers,
        HotKeyAction.StopRecording => 0,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };

    private uint GetStoredVirtualKey(HotKeyAction action) => action switch
    {
        HotKeyAction.Screenshot => (uint)_settings.ScreenshotHotKeyCode,
        HotKeyAction.RecordVideo => (uint)_settings.VideoHotKeyCode,
        HotKeyAction.RecordGif => (uint)_settings.GifHotKeyCode,
        HotKeyAction.RecognizeText => (uint)_settings.OcrHotKeyCode,
        HotKeyAction.StopRecording => 0,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };
}
