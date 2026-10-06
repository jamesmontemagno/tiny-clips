using TinyClips.Core.Models;
using TinyClips.Core.Services;
using TinyClips.Core.Studio;

namespace TinyClips.Core.Tests;

public sealed class CaptureSettingsStudioTests
{
    private const long BytesPerGigabyte = 1024L * 1024 * 1024;

    [Fact]
    public void Defaults_FollowTheTrimmerToggleAndKeepStudioOff()
    {
        var settings = Create(out var service);

        Assert.Equal(VideoAfterRecording.Trimmer, settings.VideoAfterRecording);
        Assert.False(settings.StudioPreviewEnabled);
        Assert.False(settings.IsStudioRecordingEnabled);
        Assert.Equal(30, settings.StudioSourceRetentionDays);
        Assert.Equal(10, settings.StudioStorageCapGigabytes);
        Assert.Null(settings.StudioDefaultLook);

        // Reading never stores a choice, so the trimmer toggle keeps deciding until one is made.
        Assert.Equal("unset", service.Get("videoAfterRecording", "unset"));
    }

    [Theory]
    [InlineData(true, VideoAfterRecording.Trimmer)]
    [InlineData(false, VideoAfterRecording.Save)]
    public void VideoAfterRecording_IsDerivedFromShowTrimmerUntilAChoiceIsStored(bool showTrimmer, VideoAfterRecording expected)
    {
        var settings = Create(out var service);
        service.Set("showTrimmer", showTrimmer);

        Assert.Equal(expected, settings.VideoAfterRecording);

        settings.ShowTrimmer = !showTrimmer;

        Assert.Equal(
            showTrimmer ? VideoAfterRecording.Save : VideoAfterRecording.Trimmer,
            settings.VideoAfterRecording);
        Assert.Equal("unset", service.Get("videoAfterRecording", "unset"));
    }

    [Theory]
    [InlineData(VideoAfterRecording.Save, "save", false)]
    [InlineData(VideoAfterRecording.Trimmer, "trimmer", true)]
    [InlineData(VideoAfterRecording.Studio, "studio", false)]
    public void VideoAfterRecording_RoundTripsAndWritesShowTrimmer(VideoAfterRecording value, string stored, bool showTrimmer)
    {
        var settings = Create(out var service);
        service.Set("showTrimmer", !showTrimmer);

        settings.VideoAfterRecording = value;

        Assert.Equal(value, settings.VideoAfterRecording);
        Assert.Equal(stored, service.Get("videoAfterRecording", string.Empty));
        Assert.Equal(showTrimmer, settings.ShowTrimmer);
        Assert.Equal(showTrimmer, service.Get("showTrimmer", !showTrimmer));
    }

    [Theory]
    [InlineData("save", VideoAfterRecording.Save)]
    [InlineData("studio", VideoAfterRecording.Studio)]
    [InlineData("STUDIO", VideoAfterRecording.Studio)]
    public void VideoAfterRecording_StoredChoiceWinsOverShowTrimmer(string stored, VideoAfterRecording expected)
    {
        var settings = Create(out var service);
        service.Set("showTrimmer", true);
        service.Set("videoAfterRecording", stored);

        Assert.Equal(expected, settings.VideoAfterRecording);
    }

    [Theory]
    [InlineData("later")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("3")]
    public void VideoAfterRecording_UnknownStoredValueBehavesAsNeverWritten(string stored)
    {
        var settings = Create(out var service);
        service.Set("videoAfterRecording", stored);

        service.Set("showTrimmer", true);
        Assert.Equal(VideoAfterRecording.Trimmer, settings.VideoAfterRecording);

        service.Set("showTrimmer", false);
        Assert.Equal(VideoAfterRecording.Save, settings.VideoAfterRecording);
    }

    [Fact]
    public void VideoAfterRecording_StoredValueOfAnotherTypeBehavesAsNeverWritten()
    {
        var settings = Create(out var service);
        service.Set("videoAfterRecording", 2);
        service.Set("showTrimmer", false);

        Assert.Equal(VideoAfterRecording.Save, settings.VideoAfterRecording);
    }

    [Fact]
    public void ShowTrimmer_KeepsAStoredChoiceInStep()
    {
        var settings = Create(out var service);
        settings.VideoAfterRecording = VideoAfterRecording.Studio;

        // Turning the toggle off is already what a Studio choice means for the trimmer.
        settings.ShowTrimmer = false;
        Assert.Equal(VideoAfterRecording.Studio, settings.VideoAfterRecording);

        settings.ShowTrimmer = true;
        Assert.Equal(VideoAfterRecording.Trimmer, settings.VideoAfterRecording);
        Assert.Equal("trimmer", service.Get("videoAfterRecording", string.Empty));

        settings.ShowTrimmer = false;
        Assert.Equal(VideoAfterRecording.Save, settings.VideoAfterRecording);
        Assert.Equal("save", service.Get("videoAfterRecording", string.Empty));
    }

    [Fact]
    public void StudioPreviewEnabled_RoundTripsThroughItsOwnKey()
    {
        var settings = Create(out var service);

        settings.StudioPreviewEnabled = true;

        Assert.True(settings.StudioPreviewEnabled);
        Assert.True(service.Get("studioPreviewEnabled", false));

        settings.StudioPreviewEnabled = false;

        Assert.False(settings.StudioPreviewEnabled);
    }

    [Theory]
    [InlineData(false, VideoAfterRecording.Save, false)]
    [InlineData(false, VideoAfterRecording.Trimmer, false)]
    [InlineData(false, VideoAfterRecording.Studio, false)]
    [InlineData(true, VideoAfterRecording.Save, false)]
    [InlineData(true, VideoAfterRecording.Trimmer, false)]
    [InlineData(true, VideoAfterRecording.Studio, true)]
    public void IsStudioRecordingEnabled_NeedsThePreviewSwitchAndTheStudioChoice(bool preview, VideoAfterRecording choice, bool expected)
    {
        var settings = Create(out _);
        settings.StudioPreviewEnabled = preview;
        settings.VideoAfterRecording = choice;

        Assert.Equal(expected, settings.IsStudioRecordingEnabled);
    }

    [Fact]
    public void StoredStudioChoice_BehavesLikeSaveWhileThePreviewSwitchIsOff()
    {
        var settings = Create(out _);
        settings.VideoAfterRecording = VideoAfterRecording.Studio;

        Assert.False(settings.StudioPreviewEnabled);
        Assert.False(settings.IsStudioRecordingEnabled);
        Assert.False(settings.ShowTrimmer);

        settings.StudioPreviewEnabled = true;

        Assert.True(settings.IsStudioRecordingEnabled);
        Assert.False(settings.ShowTrimmer);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(45, 45)]
    [InlineData(365, 365)]
    [InlineData(366, 365)]
    public void StudioSourceRetentionDays_ClampsToSupportedRange(int requested, int expected)
    {
        var settings = Create(out var service);

        settings.StudioSourceRetentionDays = requested;

        Assert.Equal(expected, settings.StudioSourceRetentionDays);
        Assert.Equal(expected, service.Get("studioSourceRetentionDays", -1));

        // A value stored by another build or edited by hand is clamped when read.
        service.Set("studioSourceRetentionDays", requested);
        Assert.Equal(expected, settings.StudioSourceRetentionDays);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(25, 25)]
    [InlineData(500, 500)]
    [InlineData(501, 500)]
    public void StudioStorageCapGigabytes_ClampsToSupportedRange(int requested, int expected)
    {
        var settings = Create(out var service);

        settings.StudioStorageCapGigabytes = requested;

        Assert.Equal(expected, settings.StudioStorageCapGigabytes);
        Assert.Equal(expected, service.Get("studioStorageCapGigabytes", -1));

        service.Set("studioStorageCapGigabytes", requested);
        Assert.Equal(expected, settings.StudioStorageCapGigabytes);
    }

    [Fact]
    public void GetStudioCleanupOptions_UsesTheRetentionAndStorageSettings()
    {
        var settings = Create(out _);

        Assert.Equal(new StudioCleanupOptions(), settings.GetStudioCleanupOptions());

        settings.StudioSourceRetentionDays = 7;
        settings.StudioStorageCapGigabytes = 500;

        var options = settings.GetStudioCleanupOptions();

        Assert.Equal(7, options.RetentionDays);
        Assert.Equal(500 * BytesPerGigabyte, options.SizeCapBytes);
    }

    [Fact]
    public void GetStudioCleanupOptions_ZeroSwitchesBothRulesOff()
    {
        var settings = Create(out _);
        settings.StudioSourceRetentionDays = 0;
        settings.StudioStorageCapGigabytes = 0;

        var options = settings.GetStudioCleanupOptions();

        Assert.Equal(0, options.RetentionDays);
        Assert.Equal(0, options.SizeCapBytes);

        // With both rules off an old, large, exported project is kept.
        var now = DateTimeOffset.UtcNow;
        var old = new StudioProjectSummary(
            "11111111-2222-3333-4444-555555555555",
            "Old",
            now.AddDays(-400),
            now.AddDays(-400),
            IsDraft: false,
            IsFlat: false,
            KeepSources: false,
            SizeBytes: 900 * BytesPerGigabyte,
            ExternalVideoExists: true);
        Assert.Empty(StudioCleanupPolicy.Plan([old], now, options).ProjectIdsToDelete);
    }

    [Fact]
    public void StudioDefaultLook_RoundTripsAsProjectJson()
    {
        var settings = Create(out var service);

        settings.StudioDefaultLook = new StudioLook(
            new StudioCanvas
            {
                Aspect = StudioCanvasAspect.Landscape16X9,
                Padding = 0.12,
                Background = new StudioBackground
                {
                    Style = StudioBackgroundStyle.Solid,
                    Preset = null,
                    Primary = "#101820",
                    Secondary = null,
                },
            },
            new StudioScreenStyle { CornerRadius = 0.05, Shadow = 0.25 },
            new StudioCameraStyle
            {
                Shape = StudioCameraShape.Squircle,
                CornerRadius = 0.2,
                Mirror = false,
                BorderWidth = 0.01,
                BorderColor = "#FF8800",
                Shadow = 0.1,
            });

        var look = Assert.IsType<StudioLook>(settings.StudioDefaultLook);
        Assert.Equal(StudioCanvasAspect.Landscape16X9, look.Canvas.Aspect);
        Assert.Equal(0.12, look.Canvas.Padding);
        Assert.Equal(StudioBackgroundStyle.Solid, look.Canvas.Background.Style);
        Assert.Null(look.Canvas.Background.Preset);
        Assert.Equal("#101820", look.Canvas.Background.Primary);
        Assert.Null(look.Canvas.Background.Secondary);
        Assert.Equal(0.05, look.Screen.CornerRadius);
        Assert.Equal(0.25, look.Screen.Shadow);
        Assert.Equal(StudioCameraShape.Squircle, look.Camera.Shape);
        Assert.Equal(0.2, look.Camera.CornerRadius);
        Assert.False(look.Camera.Mirror);
        Assert.Equal(0.01, look.Camera.BorderWidth);
        Assert.Equal("#FF8800", look.Camera.BorderColor);
        Assert.Equal(0.1, look.Camera.Shadow);

        // Stored with the same names and enum strings as project.json.
        var stored = service.Get("studioDefaultLook", string.Empty);
        Assert.Contains("\"aspect\": \"landscape16x9\"", stored);
        Assert.Contains("\"shape\": \"squircle\"", stored);
        Assert.Contains("\"borderColor\": \"#FF8800\"", stored);
    }

    [Fact]
    public void StudioDefaultLook_NeverKeepsCrops()
    {
        var settings = Create(out var service);

        settings.StudioDefaultLook = new StudioLook(
            new StudioCanvas(),
            new StudioScreenStyle { Shadow = 0.9, Crop = new StudioRect(0.1, 0.2, 0.3, 0.4) },
            new StudioCameraStyle { Shadow = 0.8, Crop = new StudioRect(0.5, 0.25, 0.125, 0.0625) });

        var look = Assert.IsType<StudioLook>(settings.StudioDefaultLook);
        Assert.Null(look.Screen.Crop);
        Assert.Null(look.Camera.Crop);
        Assert.Equal(0.9, look.Screen.Shadow);
        Assert.Equal(0.8, look.Camera.Shadow);

        var stored = service.Get("studioDefaultLook", string.Empty);
        Assert.DoesNotContain("0.0625", stored);
        Assert.DoesNotContain("\"width\"", stored);
    }

    [Fact]
    public void StudioDefaultLook_DropsCropsFoundInStoredText()
    {
        var settings = Create(out var service);
        service.Set(
            "studioDefaultLook",
            """
            {
              "screen": { "shadow": 0.7, "crop": { "x": 0.1, "y": 0.1, "width": 0.5, "height": 0.5 } },
              "camera": { "mirror": false, "crop": { "x": 0.2, "y": 0.2, "width": 0.5, "height": 0.5 } }
            }
            """);

        var look = Assert.IsType<StudioLook>(settings.StudioDefaultLook);

        Assert.Equal(0.7, look.Screen.Shadow);
        Assert.Null(look.Screen.Crop);
        Assert.False(look.Camera.Mirror);
        Assert.Null(look.Camera.Crop);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "canvas": null, "screen": null, "camera": null }""")]
    [InlineData("""{ "canvas": { "background": null } }""")]
    [InlineData("""{ "canvas": { "background": { "primary": null } }, "camera": { "borderColor": null } }""")]
    public void StudioDefaultLook_MissingPartsReadAsDefaults(string stored)
    {
        var settings = Create(out var service);
        service.Set("studioDefaultLook", stored);

        var look = Assert.IsType<StudioLook>(settings.StudioDefaultLook);

        Assert.Equal(StudioCanvasAspect.Auto, look.Canvas.Aspect);
        Assert.Equal(0.06, look.Canvas.Padding);
        Assert.Equal(StudioBackgroundStyle.Gradient, look.Canvas.Background.Style);
        Assert.Equal("#2687E8", look.Canvas.Background.Primary);
        Assert.Equal(0.02, look.Screen.CornerRadius);
        Assert.Equal(0.5, look.Screen.Shadow);
        Assert.Equal(StudioCameraShape.Circle, look.Camera.Shape);
        Assert.True(look.Camera.Mirror);
        Assert.Equal("#FFFFFF", look.Camera.BorderColor);
    }

    [Fact]
    public void StudioDefaultLook_MissingMembersTakeTheirProjectDefaults()
    {
        var settings = Create(out var service);
        service.Set(
            "studioDefaultLook",
            """{ "canvas": { "padding": 0.2, "futureThing": 3 }, "camera": { "shape": "rectangle" } }""");

        var look = Assert.IsType<StudioLook>(settings.StudioDefaultLook);

        Assert.Equal(0.2, look.Canvas.Padding);
        Assert.Equal(StudioBackgroundStyle.Gradient, look.Canvas.Background.Style);
        Assert.Equal("#2EE0BF", look.Canvas.Background.Secondary);
        Assert.Equal(0.5, look.Screen.Shadow);
        Assert.Equal(StudioCameraShape.Rectangle, look.Camera.Shape);
        Assert.True(look.Camera.Mirror);
        Assert.Equal(0.12, look.Camera.CornerRadius);
        Assert.Equal(0.35, look.Camera.Shadow);

        // Members this build does not know survive being saved again, as they do in project.json.
        settings.StudioDefaultLook = look;
        Assert.Contains("\"futureThing\": 3", service.Get("studioDefaultLook", string.Empty));
    }

    [Fact]
    public void StudioDefaultLook_NullClearsTheStoredLook()
    {
        var settings = Create(out var service);
        settings.StudioDefaultLook = new StudioLook(new StudioCanvas(), new StudioScreenStyle(), new StudioCameraStyle());
        Assert.NotNull(settings.StudioDefaultLook);

        settings.StudioDefaultLook = null;

        Assert.Null(settings.StudioDefaultLook);
        Assert.Equal(string.Empty, service.Get("studioDefaultLook", "unset"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("[1, 2]")]
    [InlineData("""{ "canvas": 5 }""")]
    [InlineData("""{ "canvas": { "padding": "wide" } }""")]
    [InlineData("""{ "camera": { "mirror": "yes" } }""")]
    public void StudioDefaultLook_UnreadableStoredTextReadsAsNull(string stored)
    {
        var settings = Create(out var service);
        service.Set("studioDefaultLook", stored);

        Assert.Null(settings.StudioDefaultLook);
    }

    [Fact]
    public void ResetToDefaults_RestoresStudioDefaults()
    {
        var settings = Create(out var service);
        settings.VideoAfterRecording = VideoAfterRecording.Studio;
        settings.StudioPreviewEnabled = true;
        settings.StudioSourceRetentionDays = 90;
        settings.StudioStorageCapGigabytes = 250;
        settings.StudioDefaultLook = new StudioLook(new StudioCanvas { Padding = 0.2 }, new StudioScreenStyle(), new StudioCameraStyle());
        Assert.True(settings.IsStudioRecordingEnabled);

        settings.ResetToDefaults();

        Assert.Equal(VideoAfterRecording.Trimmer, settings.VideoAfterRecording);
        Assert.True(settings.ShowTrimmer);
        Assert.False(settings.StudioPreviewEnabled);
        Assert.False(settings.IsStudioRecordingEnabled);
        Assert.Equal(30, settings.StudioSourceRetentionDays);
        Assert.Equal(10, settings.StudioStorageCapGigabytes);
        Assert.Null(settings.StudioDefaultLook);
        Assert.False(service.Get("studioPreviewEnabled", true));
        Assert.Equal(30, service.Get("studioSourceRetentionDays", -1));
        Assert.Equal(10, service.Get("studioStorageCapGigabytes", -1));
        Assert.Equal(string.Empty, service.Get("studioDefaultLook", "unset"));
    }

    private static CaptureSettings Create(out TestSettingsService service)
    {
        service = new TestSettingsService();
        return new CaptureSettings(service);
    }

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
