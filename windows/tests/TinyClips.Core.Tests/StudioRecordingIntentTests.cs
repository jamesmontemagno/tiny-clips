using TinyClips.Core.Capture;
using TinyClips.Core.Models;
using TinyClips.Core.Services;
using TinyClips.Core.Studio;

namespace TinyClips.Core.Tests;

public sealed class StudioRecordingIntentTests
{
    private const string Version = "1.9.0";

    [Theory]
    [InlineData(VideoRecordingCommand.StudioRecording, true)]
    [InlineData(VideoRecordingCommand.RecordVideo, false)]
    [InlineData(VideoRecordingCommand.RecordVideoHotKey, false)]
    public void TheRequestedCommandDeterminesWhetherRecordingUsesStudio(VideoRecordingCommand command, bool forStudio)
    {
        var intent = Create();

        intent.Begin(command);

        Assert.Equal(forStudio, intent.IsForStudio);
        Assert.Equal(forStudio, intent.CreateOptions(Version).RecordForStudio);
    }

    [Theory]
    [InlineData(VideoRecordingCommand.StudioRecording, CapturePickerIntent.Studio, "Studio", true)]
    [InlineData(VideoRecordingCommand.RecordVideo, CapturePickerIntent.Video, "Video", false)]
    [InlineData(VideoRecordingCommand.RecordVideoHotKey, CapturePickerIntent.Video, "Video", false)]
    public void PickerPresentation_AgreesWithRecordingRouting(
        VideoRecordingCommand command, CapturePickerIntent expected, string label, bool recordsForStudio)
    {
        var intent = Create();
        intent.Begin(command);

        var picker = intent.GetPickerIntent(CaptureType.Video);

        Assert.Equal(expected, picker);
        Assert.Equal(label, picker.GetLabel());
        Assert.Equal(CaptureType.Video, picker.GetCaptureType());
        Assert.Equal(recordsForStudio, intent.CreateOptions(Version).RecordForStudio);

        intent.Begin(VideoRecordingCommand.PickerReturned);
        Assert.Equal(picker, intent.GetPickerIntent(CaptureType.Video));
        Assert.Equal(recordsForStudio, intent.CreateOptions(Version).RecordForStudio);
    }

    [Theory]
    [InlineData(CaptureType.Screenshot, CapturePickerIntent.Screenshot, "Screenshot")]
    [InlineData(CaptureType.Gif, CapturePickerIntent.Gif, "GIF")]
    public void OtherCaptureModes_DoNotInheritTheStudioPicker(
        CaptureType type, CapturePickerIntent expected, string label)
    {
        var intent = Create();
        intent.Begin(VideoRecordingCommand.StudioRecording);

        var picker = intent.GetPickerIntent(type);

        Assert.Equal(expected, picker);
        Assert.Equal(label, picker.GetLabel());
        Assert.Equal(type, picker.GetCaptureType());
        Assert.Equal(CapturePickerIntent.Studio, intent.GetPickerIntent(CaptureType.Video));
    }

    [Fact]
    public void ANewVideoCommandReplacesThePreviousIntent()
    {
        var intent = Create();
        intent.Begin(VideoRecordingCommand.StudioRecording);
        Assert.True(intent.IsForStudio);

        intent.Begin(VideoRecordingCommand.RecordVideo);

        Assert.False(intent.IsForStudio);
        Assert.Same(VideoRecordingOptions.Default, intent.CreateOptions(Version));
    }

    [Fact]
    public void StudioRecording_UsesTheSavedLookAndAppVersion()
    {
        var settings = new CaptureSettings(new TestSettingsService())
        {
            StudioDefaultLook = new StudioLook(new StudioCanvas { Padding = 0.2 }, new StudioScreenStyle(), new StudioCameraStyle()),
        };
        var intent = new StudioRecordingIntent(settings);

        intent.Begin(VideoRecordingCommand.StudioRecording);
        var options = intent.CreateOptions(Version);

        Assert.True(options.RecordForStudio);
        Assert.Equal(Version, options.AppVersion);
        Assert.Equal(0.2, Assert.IsType<StudioLook>(options.Look).Canvas.Padding);
    }

    [Fact]
    public void RecordVideoAndItsHotKeyAlwaysUseOrdinaryRecordingOptions()
    {
        var intent = Create();

        intent.Begin(VideoRecordingCommand.StudioRecording);
        intent.Begin(VideoRecordingCommand.RecordVideoHotKey);

        Assert.False(intent.IsForStudio);
        Assert.Same(VideoRecordingOptions.Default, intent.CreateOptions(Version));
    }

    [Fact]
    public void PickerReturnedRetainsThePreviousCommandUntilAnotherRecordingIsRequested()
    {
        var intent = Create();

        intent.Begin(VideoRecordingCommand.StudioRecording);
        intent.Begin(VideoRecordingCommand.PickerReturned);
        Assert.True(intent.IsForStudio);
        Assert.True(intent.CreateOptions(Version).RecordForStudio);

        intent.Begin(VideoRecordingCommand.RecordVideo);
        intent.Begin(VideoRecordingCommand.PickerReturned);
        Assert.False(intent.IsForStudio);
        Assert.Same(VideoRecordingOptions.Default, intent.CreateOptions(Version));
    }

    private static StudioRecordingIntent Create() => new(new CaptureSettings(new TestSettingsService()));

    private sealed class TestSettingsService : ISettingsService
    {
        private readonly Dictionary<string, object> _values = new(StringComparer.OrdinalIgnoreCase);

        public AppTheme Theme { get; set; }

        public string SaveDirectory { get; set; } = string.Empty;

        public T Get<T>(string key, T defaultValue) =>
            _values.TryGetValue(key, out var value) && value is T typedValue ? typedValue : defaultValue;

        public void Set<T>(string key, T value) => _values[key] = value is null ? string.Empty : value;
    }
}
