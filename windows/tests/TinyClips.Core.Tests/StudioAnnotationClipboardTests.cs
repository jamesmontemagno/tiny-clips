using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.Core.Tests;

public sealed class StudioAnnotationClipboardTests
{
    internal static byte[] FixtureBytes() => File.ReadAllBytes(
        Path.Combine(AppContext.BaseDirectory, "StudioFixtures", "annotation-clipboard", "transfer.json"));

    internal static StudioAnnotationTransfer Fixture() => StudioAnnotationClipboard.Decode(FixtureBytes());

    [Theory]
    [InlineData("arrow")]
    [InlineData("callout")]
    [InlineData("text")]
    [InlineData("rectangle")]
    [InlineData("ellipse")]
    [InlineData("redact")]
    [InlineData("emoji")]
    public void AllKinds_RoundtripAllFieldsIncludingExtensions(string kind)
    {
        var node = JsonNode.Parse(FixtureBytes())!;
        node["annotation"]!["kind"] = kind;
        node["annotation"]!["text"] = "Editable 👋";
        var bytes = Encoding.UTF8.GetBytes(node.ToJsonString());
        var decoded = StudioAnnotationClipboard.Decode(bytes);
        using var expected = JsonDocument.Parse(bytes);
        using var actual = JsonDocument.Parse(StudioAnnotationClipboard.Encode(decoded));
        Assert.True(JsonElement.DeepEquals(expected.RootElement, actual.RootElement));
        Assert.True(decoded.Annotation.ExtensionData["futureField"].GetProperty("keep").GetBoolean());
    }

    [Fact]
    public void EveryExistingFieldIsRequired_NotADefaultAnnotation()
    {
        var original = JsonNode.Parse(FixtureBytes())!.AsObject();
        foreach (var name in original.Select(property => property.Key))
        {
            var missing = (JsonObject)original.DeepClone();
            missing.Remove(name);
            Assert.Throws<InvalidDataException>(() => StudioAnnotationClipboard.Decode(Encoding.UTF8.GetBytes(missing.ToJsonString())));
        }
        foreach (var name in original["annotation"]!.AsObject().Select(property => property.Key).Where(name => name != "futureField"))
        {
            var missing = (JsonObject)original.DeepClone();
            missing["annotation"]!.AsObject().Remove(name);
            Assert.Throws<InvalidDataException>(() => StudioAnnotationClipboard.Decode(Encoding.UTF8.GetBytes(missing.ToJsonString())));
            missing["annotation"]![name] = null;
            Assert.Throws<InvalidDataException>(() => StudioAnnotationClipboard.Decode(Encoding.UTF8.GetBytes(missing.ToJsonString())));
        }
    }

    [Theory]
    [InlineData("kind", "\"future\"")]
    [InlineData("attach", "\"camera\"")]
    [InlineData("style", "\"future\"")]
    [InlineData("size", "\"future\"")]
    [InlineData("redactStyle", "\"future\"")]
    [InlineData("redactLevel", "\"future\"")]
    [InlineData("text", "5")]
    [InlineData("shadow", "\"true\"")]
    [InlineData("x", "\"1.01\"")]
    [InlineData("y", "\"-0.01\"")]
    [InlineData("offsetX", "\"-1.01\"")]
    [InlineData("offsetY", "\"1.01\"")]
    [InlineData("scale", "\"0.49\"")]
    [InlineData("width", "\"-0.01\"")]
    [InlineData("height", "\"1.01\"")]
    [InlineData("cornerRadius", "\"0.51\"")]
    [InlineData("shapeFillOpacity", "\"-0.01\"")]
    [InlineData("start", "\"-1\"")]
    [InlineData("end", "1")]
    [InlineData("rotation", "\"NaN\"")]
    public void InvalidFieldsAreExplicitErrors(string field, string json)
    {
        var node = JsonNode.Parse(FixtureBytes())!;
        node["annotation"]![field] = JsonNode.Parse(json);
        Assert.Throws<InvalidDataException>(() => StudioAnnotationClipboard.Decode(Encoding.UTF8.GetBytes(node.ToJsonString())));
    }

    [Fact]
    public void MalformedUnsupportedNonfiniteAndOversizedEnvelopesAreErrors()
    {
        var json = Encoding.UTF8.GetString(FixtureBytes());
        foreach (var invalid in new[]
        {
            "[]", "null", "{", "{}", json.Replace("\"version\": 1", "\"version\": 2"),
            json.Replace("\"version\": 1", "\"version\": 1.5"),
            json.Replace("\"version\": 1", "\"version\": \"1\""),
            json.Replace("\"version\": 1", "\"version\": 1, \"version\": 1"),
            json.Replace("\"outputDurationSeconds\": 3", "\"outputDurationSeconds\": 0"),
            json.Replace("\"outputDurationSeconds\": 3", "\"outputDurationSeconds\": 1e999"),
            json.Replace("\"rotation\": 45", "\"rotation\": 1e999"),
        })
        {
            Assert.Throws<InvalidDataException>(() => StudioAnnotationClipboard.Decode(Encoding.UTF8.GetBytes(invalid)));
        }
        Assert.Throws<InvalidDataException>(() => StudioAnnotationClipboard.Decode(new byte[StudioAnnotationClipboard.MaximumPayloadBytes + 1]));
        Assert.Throws<InvalidDataException>(() => StudioAnnotationClipboard.Decode(new byte[] { 0xff, 0xfe }));
        var transfer = Fixture();
        Assert.Throws<InvalidDataException>(() => StudioAnnotationClipboard.Encode(transfer with
        {
            Annotation = transfer.Annotation with { Text = new string('a', StudioAnnotationClipboard.MaximumPayloadBytes) },
        }));
        Assert.Throws<InvalidDataException>(() => StudioAnnotationClipboard.Encode(transfer with { OutputDurationSeconds = double.NaN }));
        Assert.Throws<InvalidDataException>(() => StudioAnnotationClipboard.Encode(transfer with
        {
            Annotation = transfer.Annotation with { Style = (StudioAnnotationStyle)999 },
        }));
    }

    [Fact]
    public void SchemaBoundaryValuesAndAnyFiniteRotationArePreserved()
    {
        var transfer = Fixture();
        var annotation = transfer.Annotation with
        {
            Id = "", X = 0, Y = 1, Width = 0, Height = 0, OffsetX = -1, OffsetY = 1,
            Scale = 0.5, CornerRadius = 0.5, ShapeFillOpacity = 0, Rotation = 1080,
        };
        var decoded = StudioAnnotationClipboard.Decode(StudioAnnotationClipboard.Encode(transfer with { Annotation = annotation }));
        Assert.Equal((0d, 0d, 1080d), (decoded.Annotation.Width, decoded.Annotation.Height, decoded.Annotation.Rotation));
    }

    [Fact]
    public void OutOfRangeGeometryAndNegativeSourceStartRoundtripExactly()
    {
        var node = JsonNode.Parse(FixtureBytes())!;
        foreach (var (field, value) in new Dictionary<string, double>
        {
            ["start"] = -5, ["x"] = -2, ["y"] = 3, ["offsetX"] = -4, ["offsetY"] = 5,
            ["scale"] = 7, ["rotation"] = -1080, ["width"] = -0.2, ["height"] = 2,
            ["cornerRadius"] = 3, ["shapeFillOpacity"] = 4,
        })
        {
            node["annotation"]![field] = value;
        }
        var bytes = Encoding.UTF8.GetBytes(node.ToJsonString());
        var decoded = StudioAnnotationClipboard.Decode(bytes);
        using var expected = JsonDocument.Parse(bytes);
        using var actual = JsonDocument.Parse(StudioAnnotationClipboard.Encode(decoded));
        Assert.True(JsonElement.DeepEquals(expected.RootElement, actual.RootElement));
        Assert.Equal(-5, decoded.Annotation.Start);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("end")]
    [InlineData("x")]
    [InlineData("y")]
    [InlineData("offsetX")]
    [InlineData("offsetY")]
    [InlineData("scale")]
    [InlineData("rotation")]
    [InlineData("width")]
    [InlineData("height")]
    [InlineData("cornerRadius")]
    [InlineData("shapeFillOpacity")]
    public void EveryAnnotationNumberMustStillBeFinite(string field)
    {
        var node = JsonNode.Parse(FixtureBytes())!;
        node["annotation"]![field] = null;
        var json = node.ToJsonString().Replace($"\"{field}\":null", $"\"{field}\":1e999", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => StudioAnnotationClipboard.Decode(Encoding.UTF8.GetBytes(json)));
    }

    [Theory]
    [InlineData(StudioShortcutKey.C, StudioShortcutAction.CopySelectedAnnotation)]
    [InlineData(StudioShortcutKey.V, StudioShortcutAction.PasteAnnotation)]
    public void ShortcutsPreserveNativeTextAndBusyModifierRepeatGuards(StudioShortcutKey key, StudioShortcutAction expected)
    {
        var input = new StudioShortcutInput(key, true, false, false, false, false, true, false)
        {
            CanCopyAnnotation = true, CanPasteAnnotation = true,
        };
        Assert.Equal(expected, StudioShortcuts.Resolve(input));
        foreach (var blocked in new[]
        {
            input with { IsTextInputFocused = true }, input with { IsTypeToSearchFocused = true },
            input with { IsShiftDown = true }, input with { IsAltDown = true },
            input with { IsRepeat = true }, input with { IsReady = false },
            input with { IsExporting = true }, input with { IsSavingProject = true },
            input with { IsDragging = true }, input with { CanCopyAnnotation = false, CanPasteAnnotation = false },
        })
        {
            Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(blocked));
        }
    }
}
