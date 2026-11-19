using System.Text.Json.Nodes;
using TinyClips.Core.Automation;
using TinyClips.Core.Capture;
using TinyClips.Core.Models;
using TinyClips.Core.Services;

namespace TinyClips.Core.Tests;

public sealed class AutomationRecordingOptionsTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void OmissionUsesSavedDevicesWithoutWritingPreferences(bool microphone, bool camera)
    {
        var (settings, store) = Create(microphone, camera);
        var writes = store.Writes;

        var options = AutomationRecordingOptions.Resolve(new JsonObject(), settings, true, true);

        Assert.Equal(microphone, options.RecordMicrophone);
        Assert.Equal(camera, options.WebcamEnabled);
        Assert.Equal(writes, store.Writes);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void OffDoesNotNeedDevicePermissionOrChangePreferences(bool savedMicrophone, bool savedCamera)
    {
        var (settings, store) = Create(savedMicrophone, savedCamera);
        var writes = store.Writes;

        var options = AutomationRecordingOptions.Resolve(
            JsonNode.Parse("""{"microphone":false,"camera":false}""")!.AsObject(), settings, false, false);

        Assert.False(options.RecordMicrophone);
        Assert.False(options.WebcamEnabled);
        Assert.Equal(savedMicrophone, settings.RecordMicrophone);
        Assert.Equal(savedCamera, settings.WebcamEnabled);
        Assert.Equal(writes, store.Writes);
        var next = AutomationRecordingOptions.Resolve(new JsonObject(), settings, true, true);
        Assert.Equal(savedMicrophone, next.RecordMicrophone);
        Assert.Equal(savedCamera, next.WebcamEnabled);
    }

    [Theory]
    [InlineData("microphone")]
    [InlineData("camera")]
    public void OnNeedsBothUserOptInAndOsPermission(string device)
    {
        foreach (var optedIn in new[] { false, true })
        foreach (var allowed in new[] { false, true })
        {
            var (settings, store) = Create(device == "microphone" && optedIn, device == "camera" && optedIn);
            var parameters = new JsonObject { [device] = true };
            var writes = store.Writes;
            if (optedIn && allowed)
            {
                var options = AutomationRecordingOptions.Resolve(parameters, settings, allowed, allowed);
                Assert.True(device == "microphone" ? options.RecordMicrophone : options.WebcamEnabled);
            }
            else
            {
                var error = Assert.Throws<AutomationException>(() =>
                    AutomationRecordingOptions.Resolve(parameters, settings, allowed, allowed));
                Assert.Equal(AutomationErrorCodes.PermissionRequired, error.Code);
            }
            Assert.Equal(writes, store.Writes);
        }
    }

    [Theory]
    [InlineData("microphone")]
    [InlineData("camera")]
    public void OmissionDoesNotBypassOsPermission(string device)
    {
        var (settings, _) = Create(device == "microphone", device == "camera");
        var error = Assert.Throws<AutomationException>(() =>
            AutomationRecordingOptions.Resolve(new JsonObject(), settings, false, false));
        Assert.Equal(AutomationErrorCodes.PermissionRequired, error.Code);
    }

    [Theory]
    [InlineData("""{"microphone":false}""", false, true)]
    [InlineData("""{"camera":false}""", true, false)]
    public void OneOverridePreservesTheOtherDevice(string json, bool microphone, bool camera)
    {
        var (settings, _) = Create(true, true);
        var options = AutomationRecordingOptions.Resolve(JsonNode.Parse(json)!.AsObject(), settings, true, true);
        Assert.Equal(microphone, options.RecordMicrophone);
        Assert.Equal(camera, options.WebcamEnabled);
    }

    [Theory]
    [InlineData("""{"microphone":"false"}""")]
    [InlineData("""{"camera":1}""")]
    public void MalformedChoicesAreNotSilentlyEnabled(string json)
    {
        var (settings, _) = Create(true, true);
        var error = Assert.Throws<AutomationException>(() =>
            AutomationRecordingOptions.Resolve(JsonNode.Parse(json)!.AsObject(), settings, true, true));
        Assert.Equal(AutomationErrorCodes.InvalidParams, error.Code);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PermissionOrOptInRevocationIsRecheckedBeforeStartAndRestart(bool revokeOsPermission)
    {
        var (settings, _) = Create(true, true);
        var options = AutomationRecordingOptions.Resolve(new JsonObject(), settings, true, true);
        if (!revokeOsPermission)
        {
            settings.RecordMicrophone = false;
        }

        var error = Assert.Throws<AutomationException>(() =>
            AutomationRecordingOptions.EnsureAuthorized(options, settings, !revokeOsPermission, true));
        Assert.Equal(AutomationErrorCodes.PermissionRequired, error.Code);
    }

    [Fact]
    public void PreparedPipelineCannotReuseDifferentDeviceChoices()
    {
        var (settings, _) = Create(true, true);
        var prepared = VideoRecordingOptions.Default.ResolveDevices(settings);
        var screenOnly = AutomationRecordingOptions.Resolve(
            JsonNode.Parse("""{"microphone":false,"camera":false}""")!.AsObject(), settings, false, false);

        Assert.False(prepared.MatchesCaptureOptions(screenOnly));
        Assert.False(prepared.MatchesCaptureOptions(prepared with { RecordMicrophone = false }));
        Assert.False(prepared.MatchesCaptureOptions(prepared with { WebcamEnabled = false }));
        Assert.False(prepared.MatchesCaptureOptions(prepared with { RecordForStudio = true }));
        Assert.True(screenOnly.MatchesCaptureOptions(screenOnly.ResolveDevices(settings)));
    }

    private static (CaptureSettings Settings, MemorySettings Store) Create(bool microphone, bool camera)
    {
        var store = new MemorySettings();
        var settings = new CaptureSettings(store) { RecordMicrophone = microphone, WebcamEnabled = camera };
        return (settings, store);
    }

    private sealed class MemorySettings : ISettingsService
    {
        private readonly Dictionary<string, object?> _values = [];
        public int Writes { get; private set; }
        public AppTheme Theme { get; set; }
        public string SaveDirectory { get; set; } = string.Empty;
        public T Get<T>(string key, T defaultValue) => _values.TryGetValue(key, out var value) ? (T)value! : defaultValue;
        public void Set<T>(string key, T value)
        {
            Writes++;
            _values[key] = value;
        }
    }
}
