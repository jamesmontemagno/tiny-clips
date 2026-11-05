using System.Text.Json;
using TinyClips.Core.Automation;
using TinyClips.Core.Models;
using TinyClips.Core.Services;
using TinyClips.Core.Studio;

namespace TinyClips.Core.Tests;

public sealed class StudioExportPresetsTests
{
    private static JsonElement Fixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "StudioFixtures", "export", "presets.json");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement;
    }

    private static StudioSize SizeOf(JsonElement size) =>
        new(size.GetProperty("width").GetDouble(), size.GetProperty("height").GetDouble());

    private static StudioExportResolution Resolution(string id)
    {
        Assert.True(StudioExportPresets.TryParseResolution(id, out var resolution), id);
        return resolution;
    }

    private static StudioExportQuality Quality(string id)
    {
        Assert.True(StudioExportPresets.TryParseQuality(id, out var quality), id);
        return quality;
    }

    [Fact]
    public void SharedFixture_Sizes()
    {
        foreach (var testCase in Fixture().GetProperty("sizes").EnumerateArray())
        {
            var size = StudioExportPresets.GetSize(SizeOf(testCase.GetProperty("natural")), Resolution(testCase.GetProperty("resolution").GetString()!));
            Assert.Equal(SizeOf(testCase.GetProperty("expected")), size);
        }
    }

    [Fact]
    public void SharedFixture_OfferedResolutions()
    {
        foreach (var testCase in Fixture().GetProperty("offered").EnumerateArray())
        {
            var offered = StudioExportPresets.GetResolutionOptions(SizeOf(testCase.GetProperty("natural"))).Select(option => option.Id);
            Assert.Equal(testCase.GetProperty("expected").EnumerateArray().Select(id => id.GetString()!), offered);
        }
    }

    [Fact]
    public void SharedFixture_Bitrates()
    {
        foreach (var testCase in Fixture().GetProperty("bitrates").EnumerateArray())
        {
            var codec = testCase.GetProperty("codec").GetString() == "hevc" ? VideoCodec.Hevc : VideoCodec.H264;
            var bitrate = StudioExportPresets.GetVideoBitrate(
                testCase.GetProperty("width").GetDouble(),
                testCase.GetProperty("height").GetDouble(),
                testCase.GetProperty("frameRate").GetDouble(),
                codec,
                Quality(testCase.GetProperty("quality").GetString()!));
            Assert.Equal(testCase.GetProperty("expected").GetUInt32(), bitrate);
        }
    }

    [Fact]
    public void SharedFixture_Estimates()
    {
        foreach (var testCase in Fixture().GetProperty("estimates").EnumerateArray())
        {
            var bytes = StudioExportPresets.EstimateBytes(
                testCase.GetProperty("videoBitrate").GetUInt32(),
                testCase.GetProperty("duration").GetDouble(),
                testCase.GetProperty("hasSound").GetBoolean());
            Assert.Equal(testCase.GetProperty("expected").GetInt64(), bytes);
        }
    }

    [Theory]
    [InlineData(500_000, "Less than 1 MB")]
    [InlineData(71_303_168, "About 68 MB")]
    [InlineData(1_288_490_189, "About 1.2 GB")]
    public void EstimateText_ReadsInWholeMegabytesOrTenthsOfAGigabyte(long bytes, string expected)
    {
        Assert.Equal(expected, StudioExportPresets.GetEstimateText(bytes));
    }

    [Fact]
    public void Options_OfferTheSavedChoiceAndSayWhatItComesTo()
    {
        var options = new StudioExportOptionsModel(new StudioSize(3840, 2160), 30, VideoCodec.H264, 60, hasSound: true, StudioExportChoice.Default);

        Assert.Equal(StudioExportChoice.Default, options.Choice);
        Assert.Equal(new StudioSize(1920, 1080), options.Size);
        Assert.Equal("1920 × 1080 · 30 fps · H.264", options.DetailsText);
        Assert.Equal("About 68 MB", options.EstimateText);

        options.Choice = new StudioExportChoice(StudioExportResolution.P720, StudioExportQuality.Small);

        Assert.Equal("1280 × 720 · 30 fps · H.264", options.DetailsText);
        Assert.Equal(new StudioExportChoice(StudioExportResolution.P720, StudioExportQuality.Small), options.ChoiceToRemember);
    }

    [Fact]
    public void Options_OnASmallCanvas_OfferMaximumAndKeepTheSavedResolutionUnlessItIsChanged()
    {
        var options = new StudioExportOptionsModel(new StudioSize(1280, 720), 30, VideoCodec.H264, 60, hasSound: false, StudioExportChoice.Default);

        Assert.Equal(StudioExportResolution.Maximum, options.Choice.Resolution);
        Assert.Equal(StudioExportResolution.P1080, options.ChoiceToRemember.Resolution);

        options.Choice = options.Choice with { Resolution = StudioExportResolution.P480 };

        Assert.Equal(StudioExportResolution.P480, options.ChoiceToRemember.Resolution);
    }

    [Fact]
    public void Options_OfferEveryQualityFromBestToSmallest()
    {
        var options = new StudioExportOptionsModel(new StudioSize(1920, 1080), 30, VideoCodec.H264, 10, hasSound: false, StudioExportChoice.Default);

        Assert.Equal(["best", "high", "standard", "small"], options.Qualities.Select(option => option.Id));
        Assert.True(options.Qualities.Zip(options.Qualities.Skip(1)).All(pair => pair.First.EstimatedBytes > pair.Second.EstimatedBytes));
    }

    [Fact]
    public void Settings_StartAt1080pAndHigh_AndKeepTheChoice()
    {
        var settings = new CaptureSettings(new MemorySettings());

        Assert.Equal(StudioExportChoice.Default, new StudioExportChoice(settings.StudioExportResolution, settings.StudioExportQuality));
        Assert.Equal(StudioExportResolution.P1080, StudioExportChoice.Default.Resolution);
        Assert.Equal(StudioExportQuality.High, StudioExportChoice.Default.Quality);

        settings.StudioExportResolution = StudioExportResolution.P2160;
        settings.StudioExportQuality = StudioExportQuality.Best;

        Assert.Equal(StudioExportResolution.P2160, settings.StudioExportResolution);
        Assert.Equal(StudioExportQuality.Best, settings.StudioExportQuality);
    }

    [Fact]
    public void Settings_AreAvailableToAgents_AndRefuseAnythingElse()
    {
        var settings = new CaptureSettings(new MemorySettings());
        var catalog = new AutomationSettingsCatalog(settings);

        catalog.Set("studioExportResolution", JsonValue("720p"));
        catalog.Set("studioExportQuality", JsonValue("small"));

        Assert.Equal(StudioExportResolution.P720, settings.StudioExportResolution);
        Assert.Equal(StudioExportQuality.Small, settings.StudioExportQuality);
        Assert.Throws<AutomationException>(() => catalog.Set("studioExportResolution", JsonValue("8k")));
        Assert.Throws<AutomationException>(() => catalog.Set("studioExportQuality", JsonValue("nice")));
        Assert.Equal(StudioExportResolution.P720, settings.StudioExportResolution);
    }

    private static System.Text.Json.Nodes.JsonNode JsonValue(string value) => System.Text.Json.Nodes.JsonValue.Create(value);

    private sealed class MemorySettings : ISettingsService
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);

        public AppTheme Theme { get; set; }

        public string SaveDirectory { get; set; } = string.Empty;

        public T Get<T>(string key, T defaultValue) => _values.TryGetValue(key, out var value) && value is T typed ? typed : defaultValue;

        public void Set<T>(string key, T value) => _values[key] = value;
    }
}
