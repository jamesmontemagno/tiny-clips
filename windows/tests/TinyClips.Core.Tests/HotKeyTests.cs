using TinyClips.Core.Models;
using TinyClips.Core.Services;

namespace TinyClips.Core.Tests;

public sealed class HotKeyTests
{
    [Fact]
    public void DefaultFor_ReturnsExpectedWindowsChordDefaults()
    {
        var service = CreateService();

        Assert.Equal(new HotKeyDefinition(HotKeyModifiers.Control | HotKeyModifiers.Shift, 0x35), service.DefaultFor(HotKeyAction.Screenshot));
        Assert.True(service.DefaultFor(HotKeyAction.RecordVideo).IsUnbound);
        Assert.True(service.DefaultFor(HotKeyAction.StudioRecording).IsUnbound);
        Assert.True(service.DefaultFor(HotKeyAction.RecordGif).IsUnbound);
        Assert.Equal(new HotKeyDefinition(HotKeyModifiers.None, 0), service.DefaultFor(HotKeyAction.RecognizeText));
        Assert.Equal(new HotKeyDefinition(HotKeyModifiers.None, 0), service.DefaultFor(HotKeyAction.ScreenshotRegion));
        Assert.Equal(new HotKeyDefinition(HotKeyModifiers.None, 0), service.DefaultFor(HotKeyAction.ScreenshotWindow));
        Assert.Equal(new HotKeyDefinition(HotKeyModifiers.None, 0), service.DefaultFor(HotKeyAction.ScreenshotScreen));
        Assert.True(service.DefaultFor(HotKeyAction.StopRecording).IsUnbound);
    }

    [Fact]
    public void DefaultFor_BindsOnlyScreenshot()
    {
        var service = CreateService();

        var bound = Enum.GetValues<HotKeyAction>()
            .Where(action => !service.DefaultFor(action).IsUnbound)
            .ToArray();

        Assert.Equal(
            new[] { HotKeyAction.Screenshot },
            bound);
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

        Assert.Equal(new HotKeyDefinition(HotKeyModifiers.Control | HotKeyModifiers.Shift, 0x35), service.GetBinding(HotKeyAction.Screenshot));
        Assert.True(service.GetBinding(HotKeyAction.RecordVideo).IsUnbound);
        Assert.True(service.GetBinding(HotKeyAction.StudioRecording).IsUnbound);
        Assert.True(service.GetBinding(HotKeyAction.RecordGif).IsUnbound);
        Assert.True(service.GetBinding(HotKeyAction.RecognizeText).IsUnbound);
        Assert.True(service.GetBinding(HotKeyAction.StopRecording).IsUnbound);
        Assert.Empty(service.StopRecordingDisplayString);
    }

    [Fact]
    public void SetBinding_And_GetBinding_RoundTripCustomChord()
    {
        var service = CreateService();

        service.SetBinding(HotKeyAction.RecognizeText, new HotKeyDefinition(HotKeyModifiers.Alt | HotKeyModifiers.Control, 0x42));

        Assert.Equal(new HotKeyDefinition(HotKeyModifiers.Alt | HotKeyModifiers.Control, 0x42), service.GetBinding(HotKeyAction.RecognizeText));
    }

    [Fact]
    public void SetBinding_Unbound_ClearsDefaultChordUntilReset()
    {
        var service = CreateService();
        var unbound = new HotKeyDefinition(HotKeyModifiers.None, 0);

        service.SetBinding(HotKeyAction.Screenshot, unbound);

        Assert.Equal(unbound, service.GetBinding(HotKeyAction.Screenshot));
        Assert.True(service.GetBinding(HotKeyAction.Screenshot).IsUnbound);

        service.SetBinding(
            HotKeyAction.Screenshot,
            service.DefaultFor(HotKeyAction.Screenshot));

        Assert.Equal(
            new HotKeyDefinition(HotKeyModifiers.Control | HotKeyModifiers.Shift, 0x35),
            service.GetBinding(HotKeyAction.Screenshot));
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
            HotKeyAction.Screenshot,
            new HotKeyDefinition(modifiers, virtualKey));

        Assert.Equal(expectedError, result.Error);
    }

    [Fact]
    public void ValidateBinding_RejectsAnotherCaptureBinding()
    {
        var service = CreateService();
        var videoBinding = new HotKeyDefinition(HotKeyModifiers.Control | HotKeyModifiers.Shift, 0x36);
        service.SetBinding(HotKeyAction.RecordVideo, videoBinding);

        var result = service.ValidateBinding(HotKeyAction.Screenshot, videoBinding);

        Assert.Equal(HotKeyValidationError.DuplicateBinding, result.Error);
        Assert.Equal(HotKeyAction.RecordVideo, result.ConflictingAction);
    }

    [Fact]
    public void ValidateBinding_RejectsAssignedStopRecordingBindingButNotTheOldFixedChord()
    {
        var service = CreateService();

        var chord = new HotKeyDefinition(HotKeyModifiers.Control | HotKeyModifiers.Shift, 0x53);
        Assert.True(service.ValidateBinding(HotKeyAction.RecordGif, chord).IsValid);
        service.SetBinding(HotKeyAction.StopRecording, chord);
        var result = service.ValidateBinding(HotKeyAction.RecordGif, chord);

        Assert.Equal(HotKeyValidationError.DuplicateBinding, result.Error);
        Assert.Equal(HotKeyAction.StopRecording, result.ConflictingAction);
        Assert.True(service.ValidateBinding(HotKeyAction.StopRecording, chord).IsValid);
        Assert.Equal(
            HotKeyAction.Screenshot,
            service.ValidateBinding(HotKeyAction.StopRecording, service.GetBinding(HotKeyAction.Screenshot)).ConflictingAction);
    }

    [Fact]
    public void GetStopBinding_MatchesStopRecordingAction()
    {
        var service = CreateService();

        Assert.Equal(service.GetBinding(HotKeyAction.StopRecording), service.GetStopBinding());
    }

    [Fact]
    public void StopRecordingBinding_CanBeEditedClearedAndReset()
    {
        var service = CreateService();

        var binding = new HotKeyDefinition(HotKeyModifiers.Alt, 0x41);
        service.SetBinding(HotKeyAction.StopRecording, binding);
        Assert.Equal(binding, service.GetStopBinding());
        Assert.Equal("Alt+A", service.StopRecordingDisplayString);
        Assert.True(service.ValidateBinding(HotKeyAction.StopRecording, default).IsValid);
        service.SetBinding(HotKeyAction.StopRecording, default);
        Assert.True(service.GetStopBinding().IsUnbound);
        service.SetBinding(HotKeyAction.StopRecording, binding);
        service.SetBinding(HotKeyAction.StopRecording, service.DefaultFor(HotKeyAction.StopRecording));
        Assert.True(service.GetStopBinding().IsUnbound);
    }

    [Fact]
    public void ActiveBindings_IncludeStopOnlyDuringRecordingAndRespectEditsAndClearing()
    {
        var service = CreateService();
        Assert.Equal([HotKeyAction.Screenshot], service.GetActiveBindings(false).Select(pair => pair.Key));
        Assert.Equal([HotKeyAction.Screenshot], service.GetActiveBindings(true).Select(pair => pair.Key));

        var stop = new HotKeyDefinition(HotKeyModifiers.Control | HotKeyModifiers.Shift, 0x53);
        var video = new HotKeyDefinition(HotKeyModifiers.Alt, 0x56);
        service.SetBinding(HotKeyAction.RecordVideo, video);
        service.SetBinding(HotKeyAction.StopRecording, stop);
        Assert.DoesNotContain(service.GetActiveBindings(false), pair => pair.Key == HotKeyAction.StopRecording);
        Assert.Contains(new KeyValuePair<HotKeyAction, HotKeyDefinition>(HotKeyAction.StopRecording, stop), service.GetActiveBindings(true));
        Assert.Contains(new KeyValuePair<HotKeyAction, HotKeyDefinition>(HotKeyAction.RecordVideo, video), service.GetActiveBindings(false));
        Assert.DoesNotContain(service.GetActiveBindings(false), pair => pair.Key == HotKeyAction.StopRecording);

        var replacement = new HotKeyDefinition(HotKeyModifiers.Alt, 0x53);
        service.SetBinding(HotKeyAction.StopRecording, replacement);
        Assert.Contains(new KeyValuePair<HotKeyAction, HotKeyDefinition>(HotKeyAction.StopRecording, replacement), service.GetActiveBindings(true));
        service.SetBinding(HotKeyAction.StopRecording, default);
        Assert.DoesNotContain(service.GetActiveBindings(true), pair => pair.Key == HotKeyAction.StopRecording);
    }

    [Fact]
    public void ValidateBinding_AllowsCurrentBindingAndUnusedChord()
    {
        var service = CreateService();

        Assert.True(service.ValidateBinding(
            HotKeyAction.Screenshot,
            service.GetBinding(HotKeyAction.Screenshot)).IsValid);
        Assert.True(service.ValidateBinding(
            HotKeyAction.Screenshot,
            new HotKeyDefinition(HotKeyModifiers.Alt, 0x41)).IsValid);

        Assert.Equal(
            HotKeyValidationError.DuplicateBinding,
            service.ValidateBinding(
                HotKeyAction.RecognizeText,
                service.GetBinding(HotKeyAction.Screenshot)).Error);
    }

    [Fact]
    public void GetBinding_OnFreshSettings_LeavesDirectScreenshotActionsUnbound()
    {
        var service = CreateService();

        Assert.Equal(new HotKeyDefinition(HotKeyModifiers.None, 0), service.DefaultFor(HotKeyAction.ScreenshotRegion));
        Assert.Equal(new HotKeyDefinition(HotKeyModifiers.None, 0), service.DefaultFor(HotKeyAction.ScreenshotWindow));
        Assert.Equal(new HotKeyDefinition(HotKeyModifiers.None, 0), service.DefaultFor(HotKeyAction.ScreenshotScreen));
        Assert.Equal(new HotKeyDefinition(HotKeyModifiers.None, 0), service.GetBinding(HotKeyAction.ScreenshotRegion));
        Assert.Equal(new HotKeyDefinition(HotKeyModifiers.None, 0), service.GetBinding(HotKeyAction.ScreenshotWindow));
        Assert.Equal(new HotKeyDefinition(HotKeyModifiers.None, 0), service.GetBinding(HotKeyAction.ScreenshotScreen));
        Assert.True(service.GetBinding(HotKeyAction.ScreenshotRegion).IsUnbound);
        Assert.True(service.GetBinding(HotKeyAction.ScreenshotScreen).IsUnbound);
        Assert.Equal("Not set", service.GetBinding(HotKeyAction.ScreenshotRegion).DisplayString);
        Assert.False(service.GetBinding(HotKeyAction.Screenshot).IsUnbound);
    }

    [Fact]
    public void SetBinding_And_GetBinding_RoundTripScreenshotRegionChord()
    {
        var service = CreateService();

        service.SetBinding(HotKeyAction.ScreenshotRegion, new HotKeyDefinition(HotKeyModifiers.Control | HotKeyModifiers.Shift, 0x34));

        Assert.Equal(new HotKeyDefinition(HotKeyModifiers.Control | HotKeyModifiers.Shift, 0x34), service.GetBinding(HotKeyAction.ScreenshotRegion));
        Assert.Equal(new HotKeyDefinition(HotKeyModifiers.Control | HotKeyModifiers.Shift, 0x35), service.GetBinding(HotKeyAction.Screenshot));
        Assert.Equal(new HotKeyDefinition(HotKeyModifiers.None, 0), service.GetBinding(HotKeyAction.ScreenshotWindow));
    }

    [Fact]
    public void SetBinding_And_GetBinding_RoundTripScreenshotScreenChord()
    {
        var service = CreateService();
        var binding = new HotKeyDefinition(HotKeyModifiers.Control | HotKeyModifiers.Shift, 0x33);

        service.SetBinding(HotKeyAction.ScreenshotScreen, binding);

        Assert.Equal(binding, service.GetBinding(HotKeyAction.ScreenshotScreen));
    }

    [Fact]
    public void ValidateBinding_DetectsConflictWithScreenshotScreen()
    {
        var service = CreateService();
        var binding = new HotKeyDefinition(HotKeyModifiers.Control | HotKeyModifiers.Shift, 0x33);
        service.SetBinding(HotKeyAction.ScreenshotRegion, binding);

        var result = service.ValidateBinding(HotKeyAction.ScreenshotScreen, binding);

        Assert.Equal(HotKeyValidationError.DuplicateBinding, result.Error);
        Assert.Equal(HotKeyAction.ScreenshotRegion, result.ConflictingAction);
    }

    [Fact]
    public void ValidateBinding_DetectsConflictWithNewScreenshotActions()
    {
        var service = CreateService();
        var chord = new HotKeyDefinition(HotKeyModifiers.Control | HotKeyModifiers.Shift, 0x34);
        service.SetBinding(HotKeyAction.ScreenshotRegion, chord);

        var result = service.ValidateBinding(HotKeyAction.ScreenshotWindow, chord);

        Assert.Equal(HotKeyValidationError.DuplicateBinding, result.Error);
        Assert.Equal(HotKeyAction.ScreenshotRegion, result.ConflictingAction);
    }

    [Fact]
    public void ValidateBinding_UnboundActionsDoNotConflictWithEachOther()
    {
        var service = CreateService();

        // Direct screenshot actions are unbound on fresh settings; a valid unused chord must not
        // be flagged as a duplicate of another unbound action.
        var result = service.ValidateBinding(
            HotKeyAction.ScreenshotRegion,
            new HotKeyDefinition(HotKeyModifiers.Alt, 0x41));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void StudioBinding_PersistsIndependentlyAndCanBeClearedAndReset()
    {
        var store = new TestSettingsService();
        var settings = new CaptureSettings(store);
        var service = new HotKeyService(settings);
        var video = new HotKeyDefinition(HotKeyModifiers.Alt, 0x56);
        var studio = new HotKeyDefinition(HotKeyModifiers.Control | HotKeyModifiers.Shift, 0x38);
        service.SetBinding(HotKeyAction.RecordVideo, video);
        service.SetBinding(HotKeyAction.StudioRecording, studio);

        var reopened = new HotKeyService(new CaptureSettings(store));
        Assert.Equal(studio, reopened.GetBinding(HotKeyAction.StudioRecording));
        Assert.Equal(video, reopened.GetBinding(HotKeyAction.RecordVideo));
        Assert.Contains(new KeyValuePair<HotKeyAction, HotKeyDefinition>(HotKeyAction.StudioRecording, studio), reopened.GetActiveBindings(false));
        Assert.Equal(HotKeyAction.StudioRecording, reopened.ValidateBinding(HotKeyAction.RecordGif, studio).ConflictingAction);
        Assert.Equal(HotKeyAction.RecordVideo, reopened.ValidateBinding(HotKeyAction.StudioRecording, video).ConflictingAction);
        Assert.True(reopened.ValidateBinding(HotKeyAction.StudioRecording, studio).IsValid);

        reopened.SetBinding(HotKeyAction.StudioRecording, default);
        Assert.DoesNotContain(reopened.GetActiveBindings(false), pair => pair.Key == HotKeyAction.StudioRecording);
        Assert.True(reopened.ValidateBinding(HotKeyAction.RecordGif, studio).IsValid);
        reopened.SetBinding(HotKeyAction.StudioRecording, studio);
        reopened.SetBinding(HotKeyAction.StudioRecording, reopened.DefaultFor(HotKeyAction.StudioRecording));
        Assert.True(reopened.GetBinding(HotKeyAction.StudioRecording).IsUnbound);
        reopened.SetBinding(HotKeyAction.StudioRecording, studio);
        settings.ResetToDefaults();
        Assert.True(reopened.GetBinding(HotKeyAction.StudioRecording).IsUnbound);
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
