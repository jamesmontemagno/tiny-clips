using TinyClips.Core.Models;
using TinyClips.Core.Services;
using TinyClips.Core.Studio;

namespace TinyClips.Core.Tests;

public sealed class CaptureSettingsStudioTests
{
    private const long BytesPerGigabyte = 1024L * 1024 * 1024;

    [Fact]
    public void Defaults_KeepStudioOff()
    {
        var settings = Create(out _);

        Assert.False(settings.StudioPreviewEnabled);
        Assert.True(settings.ShowTrimmer);
        Assert.Equal(30, settings.StudioSourceRetentionDays);
        Assert.Equal(10, settings.StudioStorageCapGigabytes);
        Assert.Null(settings.StudioDefaultLook);
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
    [InlineData(true)]
    [InlineData(false)]
    public void SwitchingStudioOnOrOff_LeavesTheTrimmerSwitchAsItIs(bool showTrimmer)
    {
        var settings = Create(out var service);
        settings.ShowTrimmer = showTrimmer;

        settings.StudioPreviewEnabled = true;
        Assert.Equal(showTrimmer, settings.ShowTrimmer);

        // The switch is what decides while Studio is on as well, and it is used.
        settings.ShowTrimmer = !showTrimmer;
        Assert.Equal(!showTrimmer, settings.ShowTrimmer);

        settings.StudioPreviewEnabled = false;
        Assert.Equal(!showTrimmer, settings.ShowTrimmer);
        Assert.Equal(!showTrimmer, service.Get("showTrimmer", showTrimmer));
    }

    /// <summary>
    /// Until 7 October there was an After recording choice, stored as "videoAfterRecording",
    /// with Open in Studio as one of three. A value that is still stored is left where it is
    /// and read by nothing: the trimmer switch says what it says, whatever was chosen there.
    /// </summary>
    [Theory]
    [InlineData("studio", true)]
    [InlineData("studio", false)]
    [InlineData("save", true)]
    [InlineData("trimmer", false)]
    public void AnAfterRecordingChoiceStoredByAnEarlierBuild_IsReadByNothing_AndIsLeftWhereItIs(string stored, bool showTrimmer)
    {
        var settings = Create(out var service);
        service.Set("videoAfterRecording", stored);
        service.Set("showTrimmer", showTrimmer);

        Assert.Equal(showTrimmer, settings.ShowTrimmer);

        settings.StudioPreviewEnabled = true;
        Assert.Equal(showTrimmer, settings.ShowTrimmer);

        settings.StudioPreviewEnabled = false;
        Assert.Equal(showTrimmer, settings.ShowTrimmer);

        settings.ShowTrimmer = !showTrimmer;
        Assert.Equal(!showTrimmer, settings.ShowTrimmer);
        Assert.Equal(stored, service.Get("videoAfterRecording", string.Empty));

        settings.ResetToDefaults();
        Assert.True(settings.ShowTrimmer);
        Assert.Equal(stored, service.Get("videoAfterRecording", string.Empty));
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
        settings.ShowTrimmer = false;
        settings.StudioPreviewEnabled = true;
        settings.StudioSourceRetentionDays = 90;
        settings.StudioStorageCapGigabytes = 250;
        settings.StudioDefaultLook = new StudioLook(new StudioCanvas { Padding = 0.2 }, new StudioScreenStyle(), new StudioCameraStyle());

        settings.ResetToDefaults();

        Assert.True(settings.ShowTrimmer);
        Assert.False(settings.StudioPreviewEnabled);
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
