using TinyClips.Core.Models;

namespace TinyClips.Core.Services;

public sealed class HotKeyService : IHotKeyService
{
    private static readonly HotKeyDefinition StopRecordingBinding =
        new(HotKeyModifiers.Control | HotKeyModifiers.Shift, 0x53);

    private readonly ICaptureSettings _settings;

    public HotKeyService(ICaptureSettings settings)
    {
        _settings = settings;
    }

    public HotKeyDefinition GetBinding(CaptureType type)
    {
        var modifiers = GetStoredModifiers(type);
        var virtualKey = GetStoredVirtualKey(type);

        if (modifiers == 0 && virtualKey == 0)
        {
            return DefaultFor(type);
        }

        return new HotKeyDefinition((HotKeyModifiers)modifiers, virtualKey);
    }

    public void SetBinding(CaptureType type, HotKeyDefinition binding)
    {
        switch (type)
        {
            case CaptureType.Screenshot:
                _settings.ScreenshotHotKeyModifiers = (int)binding.Modifiers;
                _settings.ScreenshotHotKeyCode = (int)binding.VirtualKey;
                break;
            case CaptureType.Video:
                _settings.VideoHotKeyModifiers = (int)binding.Modifiers;
                _settings.VideoHotKeyCode = (int)binding.VirtualKey;
                break;
            case CaptureType.Gif:
                _settings.GifHotKeyModifiers = (int)binding.Modifiers;
                _settings.GifHotKeyCode = (int)binding.VirtualKey;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, null);
        }
    }

    public HotKeyDefinition GetStopBinding() => StopRecordingBinding;

    public string StopRecordingDisplayString => GetStopBinding().DisplayString;

    public HotKeyDefinition DefaultFor(CaptureType type) => type switch
    {
        CaptureType.Screenshot => new HotKeyDefinition(HotKeyModifiers.Control | HotKeyModifiers.Shift, 0x35),
        CaptureType.Video => new HotKeyDefinition(HotKeyModifiers.Control | HotKeyModifiers.Shift, 0x36),
        CaptureType.Gif => new HotKeyDefinition(HotKeyModifiers.Control | HotKeyModifiers.Shift, 0x37),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    public HotKeyValidationResult ValidateBinding(CaptureType type, HotKeyDefinition binding)
    {
        var captureBindings = new[]
        {
            new KeyValuePair<CaptureType, HotKeyDefinition>(
                CaptureType.Screenshot,
                GetBinding(CaptureType.Screenshot)),
            new KeyValuePair<CaptureType, HotKeyDefinition>(
                CaptureType.Video,
                GetBinding(CaptureType.Video)),
            new KeyValuePair<CaptureType, HotKeyDefinition>(
                CaptureType.Gif,
                GetBinding(CaptureType.Gif)),
        };

        return HotKeyValidator.Validate(type, binding, captureBindings, GetStopBinding());
    }

    private int GetStoredModifiers(CaptureType type) => type switch
    {
        CaptureType.Screenshot => _settings.ScreenshotHotKeyModifiers,
        CaptureType.Video => _settings.VideoHotKeyModifiers,
        CaptureType.Gif => _settings.GifHotKeyModifiers,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    private uint GetStoredVirtualKey(CaptureType type) => type switch
    {
        CaptureType.Screenshot => (uint)_settings.ScreenshotHotKeyCode,
        CaptureType.Video => (uint)_settings.VideoHotKeyCode,
        CaptureType.Gif => (uint)_settings.GifHotKeyCode,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };
}
