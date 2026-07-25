using TinyClips.Core.Models;
using TinyClips.Core.Services;

namespace TinyClips.Core.Tests;

public sealed class HotKeyTests
{
    [Fact]
    public void DefaultFor_ReturnsExpectedWindowsChordDefaults()
    {
        var service = CreateService();

        Assert.Equal(new HotKeyDefinition(HotKeyModifiers.Control | HotKeyModifiers.Shift, 0x35), service.DefaultFor(CaptureType.Screenshot));
        Assert.Equal(new HotKeyDefinition(HotKeyModifiers.Control | HotKeyModifiers.Shift, 0x36), service.DefaultFor(CaptureType.Video));
        Assert.Equal(new HotKeyDefinition(HotKeyModifiers.Control | HotKeyModifiers.Shift, 0x37), service.DefaultFor(CaptureType.Gif));
    }

    [Theory]
    [InlineData(HotKeyModifiers.Control | HotKeyModifiers.Shift, 0x35, "Ctrl+Shift+5")]
    [InlineData(HotKeyModifiers.Control | HotKeyModifiers.Shift, 0x36, "Ctrl+Shift+6")]
    [InlineData(HotKeyModifiers.Win, 0x41, "Win+A")]
    [InlineData(HotKeyModifiers.Alt, 0x70, "Alt+F1")]
    [InlineData(HotKeyModifiers.None, 0x20, "Space")]
    public void DisplayString_FormatsExpectedTokens(HotKeyModifiers modifiers, uint virtualKey, string expected)
    {
        var definition = new HotKeyDefinition(modifiers, virtualKey);

        Assert.Equal(expected, definition.DisplayString);
    }

    [Fact]
    public void GetBinding_OnFreshSettings_ReturnsDefaultChords()
    {
        var service = CreateService();

        Assert.Equal(new HotKeyDefinition(HotKeyModifiers.Control | HotKeyModifiers.Shift, 0x35), service.GetBinding(CaptureType.Screenshot));
        Assert.Equal(new HotKeyDefinition(HotKeyModifiers.Control | HotKeyModifiers.Shift, 0x36), service.GetBinding(CaptureType.Video));
        Assert.Equal(new HotKeyDefinition(HotKeyModifiers.Control | HotKeyModifiers.Shift, 0x37), service.GetBinding(CaptureType.Gif));
    }

    [Fact]
    public void SetBinding_And_GetBinding_RoundTripCustomChord()
    {
        var service = CreateService();

        service.SetBinding(CaptureType.Screenshot, new HotKeyDefinition(HotKeyModifiers.Alt | HotKeyModifiers.Control, 0x42));

        Assert.Equal(new HotKeyDefinition(HotKeyModifiers.Alt | HotKeyModifiers.Control, 0x42), service.GetBinding(CaptureType.Screenshot));
    }

    [Theory]
    [InlineData(HotKeyModifiers.None, 0x41, HotKeyValidationError.ModifierRequired)]
    [InlineData(HotKeyModifiers.Control, 0, HotKeyValidationError.KeyRequired)]
    [InlineData(HotKeyModifiers.Control, 0x11, HotKeyValidationError.ModifierKeyNotAllowed)]
    public void ValidateBinding_RejectsIncompleteChords(
        HotKeyModifiers modifiers,
        uint virtualKey,
        HotKeyValidationError expectedError)
    {
        var service = CreateService();

        var result = service.ValidateBinding(
            CaptureType.Screenshot,
            new HotKeyDefinition(modifiers, virtualKey));

        Assert.Equal(expectedError, result.Error);
    }

    [Fact]
    public void ValidateBinding_RejectsAnotherCaptureBinding()
    {
        var service = CreateService();
        var videoBinding = service.GetBinding(CaptureType.Video);

        var result = service.ValidateBinding(CaptureType.Screenshot, videoBinding);

        Assert.Equal(HotKeyValidationError.DuplicateBinding, result.Error);
        Assert.Equal(CaptureType.Video, result.ConflictingCaptureType);
    }

    [Fact]
    public void ValidateBinding_RejectsFixedStopRecordingBinding()
    {
        var service = CreateService();

        var result = service.ValidateBinding(CaptureType.Gif, service.GetStopBinding());

        Assert.Equal(HotKeyValidationError.StopRecordingConflict, result.Error);
    }

    [Fact]
    public void ValidateBinding_AllowsCurrentBindingAndUnusedChord()
    {
        var service = CreateService();

        Assert.True(service.ValidateBinding(
            CaptureType.Screenshot,
            service.GetBinding(CaptureType.Screenshot)).IsValid);
        Assert.True(service.ValidateBinding(
            CaptureType.Screenshot,
            new HotKeyDefinition(HotKeyModifiers.Alt, 0x41)).IsValid);
    }

    private static IHotKeyService CreateService()
    {
        var settings = new CaptureSettings(new TestSettingsService());
        return new HotKeyService(settings);
    }

    private sealed class TestSettingsService : ISettingsService
    {
        private readonly Dictionary<string, object> _values = new(StringComparer.OrdinalIgnoreCase);

        public AppTheme Theme { get; set; }

        public string SaveDirectory { get; set; } = string.Empty;

        public T Get<T>(string key, T defaultValue)
        {
            if (_values.TryGetValue(key, out var value))
            {
                if (value is T typedValue)
                {
                    return typedValue;
                }

                if (value is string stringValue && typeof(T).IsEnum)
                {
                    return (T)Enum.Parse(typeof(T), stringValue, true);
                }
            }

            return defaultValue;
        }

        public void Set<T>(string key, T value)
        {
            _values[key] = value is null ? string.Empty : value;
        }
    }
}
