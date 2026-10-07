using TinyClips.Core.Capture;
using TinyClips.Core.Models;
using TinyClips.Core.Services;
using TinyClips.Core.Studio;

namespace TinyClips.Core.Tests;

/// <summary>
/// Which video recording is a Studio recording. The command it was asked for with decides,
/// and only while Studio is switched on: Studio recording makes one, Record video and its
/// hotkey never do.
/// </summary>
public sealed class StudioRecordingIntentTests
{
    private const string Version = "1.9.0";

    // ---- Asked for with Studio recording

    [Fact]
    public void StudioRecording_WhileStudioIsOn_IsAStudioRecording_WithTheLookNewProjectsStartWith()
    {
        var intent = Create(out var settings, studioOn: true);
        settings.StudioDefaultLook = new StudioLook(new StudioCanvas { Padding = 0.2 }, new StudioScreenStyle(), new StudioCameraStyle());

        intent.Begin(VideoRecordingCommand.StudioRecording);

        Assert.True(intent.IsForStudio);
        var options = intent.CreateOptions(Version);
        Assert.True(options.RecordForStudio);
        Assert.Equal(Version, options.AppVersion);
        Assert.Equal(0.2, Assert.IsType<StudioLook>(options.Look).Canvas.Padding);

        // Studio is still on where the recording starts: it starts as it was set up.
        Assert.Same(options, intent.OptionsAtStart(options));
    }

    [Fact]
    public void StudioRecording_WithStudioSwitchedOffBeforeItIsSetUp_IsAnOrdinaryRecording()
    {
        var intent = Create(out var settings, studioOn: true);
        intent.Begin(VideoRecordingCommand.StudioRecording);

        // The capture picker is open meanwhile, and Settings can be used.
        settings.StudioPreviewEnabled = false;

        Assert.False(intent.IsForStudio);
        Assert.Same(VideoRecordingOptions.Default, intent.CreateOptions(Version));
    }

    [Fact]
    public void StudioRecording_WithStudioSwitchedOffBetweenSetupAndStart_StartsAsAnOrdinaryRecording()
    {
        var intent = Create(out var settings, studioOn: true);
        intent.Begin(VideoRecordingCommand.StudioRecording);
        var setUp = intent.CreateOptions(Version);
        Assert.True(setUp.RecordForStudio);

        // The countdown runs meanwhile.
        settings.StudioPreviewEnabled = false;

        var started = intent.OptionsAtStart(setUp);
        Assert.Same(VideoRecordingOptions.Default, started);
        Assert.False(started.RecordForStudio);
    }

    [Fact]
    public void AStudioRecordingThatIsRestarted_AfterStudioWasSwitchedOff_IsRestartedAsAnOrdinaryOne()
    {
        var intent = Create(out var settings, studioOn: true);
        intent.Begin(VideoRecordingCommand.StudioRecording);
        var recording = intent.OptionsAtStart(intent.CreateOptions(Version));
        Assert.True(recording.RecordForStudio);

        // Restarted with Studio still on: the same kind of recording again.
        Assert.Same(recording, intent.OptionsAtStart(recording));

        settings.StudioPreviewEnabled = false;

        Assert.Same(VideoRecordingOptions.Default, intent.OptionsAtStart(recording));
    }

    [Fact]
    public void StudioRecording_AskedForWhileStudioIsOff_IsOrdinary_AndStaysSoWhenStudioIsSwitchedOn()
    {
        var intent = Create(out var settings, studioOn: false);

        intent.Begin(VideoRecordingCommand.StudioRecording);

        Assert.False(intent.IsForStudio);
        Assert.Same(VideoRecordingOptions.Default, intent.CreateOptions(Version));

        // What counts is whether Studio was on when the recording was asked for.
        settings.StudioPreviewEnabled = true;

        Assert.False(intent.IsForStudio);
        Assert.Same(VideoRecordingOptions.Default, intent.CreateOptions(Version));
    }

    [Fact]
    public void StudioSwitchedOffAndOnAgainWhileTheRecordingIsSetUp_LeavesItAStudioRecording()
    {
        var intent = Create(out var settings, studioOn: true);
        intent.Begin(VideoRecordingCommand.StudioRecording);

        settings.StudioPreviewEnabled = false;
        Assert.False(intent.IsForStudio);

        settings.StudioPreviewEnabled = true;
        Assert.True(intent.IsForStudio);
        Assert.True(intent.CreateOptions(Version).RecordForStudio);
    }

    // ---- Asked for with Record video, from the tray menu or with its hotkey

    [Theory]
    [InlineData(VideoRecordingCommand.RecordVideo)]
    [InlineData(VideoRecordingCommand.RecordVideoHotKey)]
    public void RecordVideo_AndItsHotKey_AreAlwaysAnOrdinaryRecording(VideoRecordingCommand command)
    {
        var intent = Create(out _, studioOn: true);

        intent.Begin(command);

        Assert.False(intent.IsForStudio);
        var options = intent.CreateOptions(Version);
        Assert.Same(VideoRecordingOptions.Default, options);
        Assert.False(options.RecordForStudio);
        Assert.Same(options, intent.OptionsAtStart(options));
    }

    [Theory]
    [InlineData(VideoRecordingCommand.RecordVideo)]
    [InlineData(VideoRecordingCommand.RecordVideoHotKey)]
    public void RecordVideo_AndItsHotKey_AfterAStudioRecording_AreAnOrdinaryRecordingAgain(VideoRecordingCommand command)
    {
        var intent = Create(out _, studioOn: true);
        intent.Begin(VideoRecordingCommand.StudioRecording);
        Assert.True(intent.IsForStudio);

        intent.Begin(command);

        Assert.False(intent.IsForStudio);
        Assert.Same(VideoRecordingOptions.Default, intent.CreateOptions(Version));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnOrdinaryRecording_StartsAsOne_WhateverTheSwitchSays(bool studioOn)
    {
        var intent = Create(out _, studioOn);

        Assert.Same(VideoRecordingOptions.Default, intent.OptionsAtStart(VideoRecordingOptions.Default));
    }

    // ---- How long what was asked for is kept

    [Fact]
    public void BeforeAnyRecordingIsAskedFor_NothingIsAStudioRecording()
    {
        var intent = Create(out _, studioOn: true);

        Assert.False(intent.IsForStudio);
        Assert.Same(VideoRecordingOptions.Default, intent.CreateOptions(Version));

        // A picker that comes back has nothing to keep either.
        intent.Begin(VideoRecordingCommand.PickerReturned);

        Assert.False(intent.IsForStudio);
    }

    [Fact]
    public void WhatWasAskedFor_IsKeptThroughThePickerTheSetupAndAPickerThatComesBack_UntilARecordingIsAskedForAgain()
    {
        var intent = Create(out _, studioOn: true);

        // Studio recording: the picker, the recording setup panel, the countdown, the start.
        intent.Begin(VideoRecordingCommand.StudioRecording);
        Assert.True(intent.IsForStudio);
        var first = intent.OptionsAtStart(intent.CreateOptions(Version));
        Assert.True(first.RecordForStudio);
        Assert.True(intent.IsForStudio);

        // The recording ends and the picker comes back by itself, twice: Studio recordings still.
        intent.Begin(VideoRecordingCommand.PickerReturned);
        Assert.True(intent.IsForStudio);
        Assert.True(intent.CreateOptions(Version).RecordForStudio);
        intent.Begin(VideoRecordingCommand.PickerReturned);
        Assert.True(intent.IsForStudio);

        // Record video is asked for: ordinary from here on, also when its picker comes back.
        intent.Begin(VideoRecordingCommand.RecordVideoHotKey);
        Assert.False(intent.IsForStudio);
        intent.Begin(VideoRecordingCommand.PickerReturned);
        Assert.False(intent.IsForStudio);
        Assert.Same(VideoRecordingOptions.Default, intent.CreateOptions(Version));

        // And Studio recording once more.
        intent.Begin(VideoRecordingCommand.StudioRecording);
        Assert.True(intent.IsForStudio);
    }

    [Fact]
    public void APickerThatComesBack_AfterStudioWasSwitchedOff_IsForAnOrdinaryRecording()
    {
        var intent = Create(out var settings, studioOn: true);
        intent.Begin(VideoRecordingCommand.StudioRecording);

        settings.StudioPreviewEnabled = false;
        intent.Begin(VideoRecordingCommand.PickerReturned);

        Assert.False(intent.IsForStudio);
        Assert.Same(VideoRecordingOptions.Default, intent.CreateOptions(Version));
    }

    [Fact]
    public void WhatIsAskedFor_StoresNothing()
    {
        var intent = Create(out _, out var service, studioOn: true);
        var writes = service.Writes;

        intent.Begin(VideoRecordingCommand.StudioRecording);
        _ = intent.OptionsAtStart(intent.CreateOptions(Version));
        intent.Begin(VideoRecordingCommand.RecordVideo);

        Assert.Equal(writes, service.Writes);
    }

    private static StudioRecordingIntent Create(out CaptureSettings settings, bool studioOn) =>
        Create(out settings, out _, studioOn);

    private static StudioRecordingIntent Create(out CaptureSettings settings, out TestSettingsService service, bool studioOn)
    {
        service = new TestSettingsService();
        settings = new CaptureSettings(service) { StudioPreviewEnabled = studioOn };
        return new StudioRecordingIntent(settings);
    }

    private sealed class TestSettingsService : ISettingsService
    {
        private readonly Dictionary<string, object> _values = new(StringComparer.OrdinalIgnoreCase);

        public int Writes { get; private set; }

        public AppTheme Theme { get; set; }

        public string SaveDirectory { get; set; } = string.Empty;

        public T Get<T>(string key, T defaultValue) =>
            _values.TryGetValue(key, out var value) && value is T typedValue ? typedValue : defaultValue;

        public void Set<T>(string key, T value)
        {
            _values[key] = value is null ? string.Empty : value;
            Writes++;
        }
    }
}
