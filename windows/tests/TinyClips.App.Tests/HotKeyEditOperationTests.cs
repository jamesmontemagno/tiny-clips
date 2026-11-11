using TinyClips.Core.Models;
using TinyClips.Core.Services;

namespace TinyClips.App.Tests;

public sealed class HotKeyEditOperationTests
{
    [Fact]
    public void EditClearAndReset_PersistThroughTheSharedService()
    {
        var hotKeys = CreateHotKeys();
        var operation = new HotKeyEditOperation(hotKeys, () => GlobalHotKeyRegistrationResult.Success, hotKeys.SetBinding);
        var candidate = new HotKeyDefinition(HotKeyModifiers.Control | HotKeyModifiers.Alt, 0x41);

        Assert.True(operation.TryApply(HotKeyAction.Screenshot, candidate, out var error));
        Assert.Empty(error);
        Assert.Equal(candidate, hotKeys.GetBinding(HotKeyAction.Screenshot));

        Assert.True(operation.TryApply(HotKeyAction.Screenshot, new(HotKeyModifiers.None, 0), out error));
        Assert.True(hotKeys.GetBinding(HotKeyAction.Screenshot).IsUnbound);
        Assert.True(operation.TryApply(HotKeyAction.Screenshot, hotKeys.DefaultFor(HotKeyAction.Screenshot), out error));
        Assert.Equal(hotKeys.DefaultFor(HotKeyAction.Screenshot), hotKeys.GetBinding(HotKeyAction.Screenshot));
    }

    [Fact]
    public void HiddenActionConflictAndReservedStop_AreRejectedWithoutWritingOrRegistering()
    {
        var hotKeys = CreateHotKeys();
        var hiddenBinding = new HotKeyDefinition(HotKeyModifiers.Control | HotKeyModifiers.Alt, 0x41);
        hotKeys.SetBinding(HotKeyAction.RecognizeText, hiddenBinding);
        var calls = 0;
        var operation = new HotKeyEditOperation(hotKeys, () =>
        {
            calls++;
            return GlobalHotKeyRegistrationResult.Success;
        }, (action, binding) => throw new InvalidOperationException("Invalid input must not be saved."));

        Assert.False(operation.TryApply(HotKeyAction.Screenshot, hiddenBinding, out var error));
        Assert.Contains("Recognize text", error);
        Assert.False(operation.TryApply(HotKeyAction.Screenshot, hotKeys.GetStopBinding(), out error));
        Assert.Contains("reserved", error);
        Assert.False(operation.TryApply(HotKeyAction.Screenshot, new(HotKeyModifiers.None, 0x41), out error));
        Assert.Contains("modifier", error);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RegistrationFailure_RestoresSavedBindingAndReportsRollbackFailure(bool rollbackFails)
    {
        var hotKeys = CreateHotKeys();
        var previous = hotKeys.GetBinding(HotKeyAction.Screenshot);
        var candidate = new HotKeyDefinition(HotKeyModifiers.Control | HotKeyModifiers.Alt, 0x41);
        var calls = 0;
        var seen = new List<HotKeyDefinition>();
        var failure = GlobalHotKeyRegistrationResult.Failed(new(
            HotKeyAction.Screenshot, "Screenshot", 1409, "Shortcut unavailable."));
        var operation = new HotKeyEditOperation(hotKeys, () =>
        {
            seen.Add(hotKeys.GetBinding(HotKeyAction.Screenshot));
            calls++;
            return calls == 1 || rollbackFails ? failure : GlobalHotKeyRegistrationResult.Success;
        }, hotKeys.SetBinding);

        Assert.False(operation.TryApply(HotKeyAction.Screenshot, candidate, out var error));

        Assert.Equal([candidate, previous], seen);
        Assert.Equal(previous, hotKeys.GetBinding(HotKeyAction.Screenshot));
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
