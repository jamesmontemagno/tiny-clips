using TinyClips.Core.Models;
using TinyClips.Core.Services;

namespace TinyClips.App.Tests;

public sealed class HotKeyEditOperationTests
{
    [Theory]
    [InlineData(HotKeyAction.Screenshot)]
    [InlineData(HotKeyAction.StopRecording)]
    public void EditClearAndReset_PersistThroughTheSharedService(HotKeyAction action)
    {
        var hotKeys = CreateHotKeys();
        var operation = new HotKeyEditOperation(hotKeys, () => GlobalHotKeyRegistrationResult.Success, hotKeys.SetBinding);
        var candidate = new HotKeyDefinition(HotKeyModifiers.Control | HotKeyModifiers.Alt, 0x41);

        Assert.True(operation.TryApply(action, candidate, out var error));
        Assert.Empty(error);
        Assert.Equal(candidate, hotKeys.GetBinding(action));

        Assert.True(operation.TryApply(action, new(HotKeyModifiers.None, 0), out error));
        Assert.True(hotKeys.GetBinding(action).IsUnbound);
        Assert.True(operation.TryApply(action, hotKeys.DefaultFor(action), out error));
        Assert.Equal(hotKeys.DefaultFor(action), hotKeys.GetBinding(action));
    }

    [Fact]
    public void HiddenActionAndStopConflicts_AreRejectedWithoutWritingOrRegistering()
    {
        var hotKeys = CreateHotKeys();
        var hiddenBinding = new HotKeyDefinition(HotKeyModifiers.Control | HotKeyModifiers.Alt, 0x41);
        hotKeys.SetBinding(HotKeyAction.RecognizeText, hiddenBinding);
        hotKeys.SetBinding(HotKeyAction.StopRecording, new(HotKeyModifiers.Control | HotKeyModifiers.Shift, 0x53));
        var calls = 0;
        var operation = new HotKeyEditOperation(hotKeys, () =>
        {
            calls++;
            return GlobalHotKeyRegistrationResult.Success;
        }, (action, binding) => throw new InvalidOperationException("Invalid input must not be saved."));

        Assert.False(operation.TryApply(HotKeyAction.Screenshot, hiddenBinding, out var error));
        Assert.Contains("Recognize text", error);
        Assert.False(operation.TryApply(HotKeyAction.Screenshot, hotKeys.GetStopBinding(), out error));
        Assert.Contains("Stop recording", error);
        Assert.False(operation.TryApply(HotKeyAction.Screenshot, new(HotKeyModifiers.None, 0x41), out error));
        Assert.Contains("modifier", error);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(false, HotKeyAction.Screenshot)]
    [InlineData(true, HotKeyAction.Screenshot)]
    [InlineData(false, HotKeyAction.StopRecording)]
    [InlineData(true, HotKeyAction.StopRecording)]
    public void RegistrationFailure_RestoresSavedBindingAndReportsRollbackFailure(bool rollbackFails, HotKeyAction action)
    {
        var hotKeys = CreateHotKeys();
        var previous = hotKeys.GetBinding(action);
        var candidate = new HotKeyDefinition(HotKeyModifiers.Control | HotKeyModifiers.Alt, 0x41);
        var calls = 0;
        var seen = new List<HotKeyDefinition>();
        var failure = GlobalHotKeyRegistrationResult.Failed(new(
            action, HotKeyEditOperation.ActionName(action), 1409, "Shortcut unavailable."));
        var operation = new HotKeyEditOperation(hotKeys, () =>
        {
            seen.Add(hotKeys.GetBinding(action));
            calls++;
            return calls == 1 || rollbackFails ? failure : GlobalHotKeyRegistrationResult.Success;
        }, hotKeys.SetBinding);

        Assert.False(operation.TryApply(action, candidate, out var error));

        Assert.Equal([candidate, previous], seen);
        Assert.Equal(previous, hotKeys.GetBinding(action));
        Assert.Contains("could not register", error);
        Assert.Equal(rollbackFails, error.Contains("could not reactivate", StringComparison.Ordinal));
    }

    private static HotKeyService CreateHotKeys() => new(new CaptureSettings(new MemorySettings()));

    private sealed class MemorySettings : ISettingsService
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);
        public AppTheme Theme { get; set; }
        public string SaveDirectory { get; set; } = string.Empty;
        public T Get<T>(string key, T defaultValue) =>
            _values.TryGetValue(key, out var value) && value is T typed ? typed : defaultValue;
        public void Set<T>(string key, T value) => _values[key] = value;
    }
}
