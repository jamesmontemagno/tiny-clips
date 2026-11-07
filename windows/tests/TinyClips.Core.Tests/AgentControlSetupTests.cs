using TinyClips.App;
using TinyClips.Core.Models;
using TinyClips.Core.Services;

namespace TinyClips.Core.Tests;

public sealed class AgentControlSetupTests
{
    [Fact]
    public void FirstEnableRequestsSetupOnlyOnce()
    {
        var settings = new MemorySettings();

        Assert.False(AgentControlService.UpdateEnabledSetting(settings, false));
        Assert.Null(settings.Get<bool?>(AgentControlService.EnabledSettingKey, null));
        Assert.True(AgentControlService.UpdateEnabledSetting(settings, true));
        Assert.True(settings.Get<bool?>(AgentControlService.EnabledSettingKey, null));
        Assert.False(AgentControlService.UpdateEnabledSetting(settings, true));
        Assert.False(AgentControlService.UpdateEnabledSetting(settings, false));
        Assert.False(AgentControlService.UpdateEnabledSetting(settings, true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExistingPreferenceDoesNotRequestSetup(bool enabled)
    {
        var settings = new MemorySettings();
        settings.Set(AgentControlService.EnabledSettingKey, enabled);

        Assert.False(AgentControlService.UpdateEnabledSetting(settings, true));
        Assert.True(settings.Get<bool?>(AgentControlService.EnabledSettingKey, null));
    }

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
