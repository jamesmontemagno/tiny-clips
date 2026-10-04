using System.Text.Json;
using TinyClips.Core.Studio;

namespace TinyClips.Core.Tests;

public sealed class StudioFixtureTests
{
    private const double Tolerance = 1e-6;

    [Fact]
    public void SharedLayoutAndTimeMapFixturesMatchWindowsImplementation()
    {
        var fixtureRoot = Path.Combine(AppContext.BaseDirectory, "StudioFixtures");
        var layoutFiles = Directory.Exists(Path.Combine(fixtureRoot, "layout"))
            ? Directory.EnumerateFiles(Path.Combine(fixtureRoot, "layout"), "*.json").Order().ToArray()
            : [];
        var timeMapFiles = Directory.Exists(Path.Combine(fixtureRoot, "timemap"))
            ? Directory.EnumerateFiles(Path.Combine(fixtureRoot, "timemap"), "*.json").Order().ToArray()
            : [];
        var canvasFiles = Directory.Exists(Path.Combine(fixtureRoot, "canvas"))
            ? Directory.EnumerateFiles(Path.Combine(fixtureRoot, "canvas"), "*.json").Order().ToArray()
            : [];
        var autoZoomFiles = Directory.Exists(Path.Combine(fixtureRoot, "autozoom"))
            ? Directory.EnumerateFiles(Path.Combine(fixtureRoot, "autozoom"), "*.json").Order().ToArray()
            : [];

        Assert.True(layoutFiles.Length > 0, $"No Studio layout fixtures were copied to {Path.Combine(fixtureRoot, "layout")}.");
        Assert.True(timeMapFiles.Length > 0, $"No Studio timemap fixtures were copied to {Path.Combine(fixtureRoot, "timemap")}.");
        Assert.True(canvasFiles.Length > 0, $"No Studio canvas fixtures were copied to {Path.Combine(fixtureRoot, "canvas")}.");
        Assert.True(autoZoomFiles.Length > 0, $"No Studio autozoom fixtures were copied to {Path.Combine(fixtureRoot, "autozoom")}.");

        foreach (var file in layoutFiles)
        {
            VerifyLayoutFixture(file);
        }

        foreach (var file in timeMapFiles)
        {
            VerifyTimeMapFixture(file);
        }

        foreach (var file in canvasFiles)
        {
            VerifyCanvasFixture(file);
        }

        foreach (var file in autoZoomFiles)
        {
            VerifyAutoZoomFixture(file);
        }
    }

    private static void VerifyLayoutFixture(string file)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(file));
        var root = document.RootElement;
        var project = StudioProjectJson.ReadProject(root.GetProperty("project").GetRawText());
        var events = root.TryGetProperty("events", out var eventsElement)
            ? StudioProjectJson.ReadEvents(eventsElement.GetRawText())
            : null;

        var natural = StudioCanvasMath.NaturalSize(project);
        CompareNumber(file, -1, "naturalCanvas.width", root.GetProperty("naturalCanvas").GetProperty("width").GetDouble(), natural.Width);
        CompareNumber(file, -1, "naturalCanvas.height", root.GetProperty("naturalCanvas").GetProperty("height").GetDouble(), natural.Height);

        var cases = root.GetProperty("cases").EnumerateArray().ToArray();
        for (var i = 0; i < cases.Length; i++)
        {
            var testCase = cases[i];
            var canvas = testCase.GetProperty("canvas");
            var frame = StudioLayoutResolver.Resolve(
                project,
                events,
                testCase.GetProperty("time").GetDouble(),
                canvas.GetProperty("width").GetDouble(),
                canvas.GetProperty("height").GetDouble());
            CompareFrame(file, i, testCase.GetProperty("expected"), frame);
        }
    }

    private static void VerifyTimeMapFixture(string file)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(file));
        var root = document.RootElement;
        var edits = root.TryGetProperty("edits", out var editsElement)
            ? JsonSerializer.Deserialize<StudioEdits>(editsElement.GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new StudioEdits()
            : new StudioEdits();
        var map = new StudioTimeMap(root.GetProperty("sourceDuration").GetDouble(), edits);
        var expected = root.GetProperty("expected");

        CompareNumber(file, -1, "expected.outputDuration", expected.GetProperty("outputDuration").GetDouble(), map.OutputDuration);

        var segments = expected.GetProperty("segments").EnumerateArray().ToArray();
        Assert.Equal(segments.Length, map.Segments.Count);
        for (var i = 0; i < segments.Length; i++)
        {
            CompareNumber(file, i, "segments.start", segments[i].GetProperty("start").GetDouble(), map.Segments[i].Start);
            CompareNumber(file, i, "segments.end", segments[i].GetProperty("end").GetDouble(), map.Segments[i].End);
        }

        var pieces = expected.GetProperty("pieces").EnumerateArray().ToArray();
        Assert.True(pieces.Length == map.Pieces.Count, $"{Path.GetFileName(file)}: {map.Pieces.Count} pieces, expected {pieces.Length}");
        for (var i = 0; i < pieces.Length; i++)
        {
            CompareNumber(file, i, "pieces.start", pieces[i].GetProperty("start").GetDouble(), map.Pieces[i].Start);
            CompareNumber(file, i, "pieces.end", pieces[i].GetProperty("end").GetDouble(), map.Pieces[i].End);
            CompareNumber(file, i, "pieces.rate", pieces[i].GetProperty("rate").GetDouble(), map.Pieces[i].Rate);
        }

        foreach (var sample in expected.GetProperty("sourceToOutput").EnumerateArray().Select((value, index) => (value, index)))
        {
            CompareNumber(file, sample.index, "sourceToOutput.output", sample.value.GetProperty("output").GetDouble(), map.SourceToOutput(sample.value.GetProperty("source").GetDouble()));
        }

        foreach (var sample in expected.GetProperty("outputToSource").EnumerateArray().Select((value, index) => (value, index)))
        {
            CompareNumber(file, sample.index, "outputToSource.source", sample.value.GetProperty("source").GetDouble(), map.OutputToSource(sample.value.GetProperty("output").GetDouble()));
        }
    }

    private static void VerifyCanvasFixture(string file)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(file));
        var cases = document.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        for (var i = 0; i < cases.Length; i++)
        {
            var testCase = cases[i];
            var natural = testCase.GetProperty("natural");
            var actual = StudioCanvasMath.ExportSize(
                new StudioSize(natural.GetProperty("width").GetDouble(), natural.GetProperty("height").GetDouble()),
                testCase.GetProperty("limit").GetDouble());
            var expected = testCase.GetProperty("expected");

            CompareNumber(file, i, "expected.width", expected.GetProperty("width").GetDouble(), actual.Width);
            CompareNumber(file, i, "expected.height", expected.GetProperty("height").GetDouble(), actual.Height);
        }
    }

    private static void VerifyAutoZoomFixture(string file)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(file));
        var root = document.RootElement;
        var project = StudioProjectJson.ReadProject(root.GetProperty("project").GetRawText());
        var events = StudioProjectJson.ReadEvents(root.GetProperty("events").GetRawText());
        var actual = StudioZoomSuggestions.Suggest(project, events);
        var expected = root.GetProperty("expected").GetProperty("zooms").EnumerateArray().ToArray();

        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            CompareZoom(file, i, expected[i], actual[i]);
        }

        // Two zooms are chained when one ends on the very number the next starts on (section 6.8),
        // so where the fixture has that, close is not enough.
        for (var i = 0; i + 1 < expected.Length; i++)
        {
            if (expected[i].GetProperty("end").GetDouble() == expected[i + 1].GetProperty("start").GetDouble())
            {
                Assert.True(actual[i].End == actual[i + 1].Start, $"{file} zooms {i} and {i + 1} are chained in the fixture: {actual[i].End} and {actual[i + 1].Start} must be the same number.");
            }
        }
    }

    private static void CompareFrame(string file, int caseIndex, JsonElement expected, StudioResolvedFrame actual)
    {
        CompareNumber(file, caseIndex, "sceneIndex", expected.GetProperty("sceneIndex").GetInt32(), actual.SceneIndex);
        CompareString(file, caseIndex, "layout", expected.GetProperty("layout").GetString(), LayoutString(actual.Layout));
        CompareScreen(file, caseIndex, expected.GetProperty("screen"), actual.Screen);
        CompareCamera(file, caseIndex, expected.GetProperty("camera"), actual.Camera);
    }

    private static void CompareScreen(string file, int caseIndex, JsonElement expected, StudioResolvedScreen? actual)
    {
        if (expected.ValueKind == JsonValueKind.Null)
        {
            Assert.True(actual is null, $"{file} case {caseIndex} field screen expected null.");
            return;
        }

        Assert.True(actual is not null, $"{file} case {caseIndex} field screen expected non-null.");
        var screen = actual.Value;
        CompareRect(file, caseIndex, "screen.rect", expected.GetProperty("rect"), screen.Rect);
        CompareRect(file, caseIndex, "screen.source", expected.GetProperty("source"), screen.Source);
        CompareNumber(file, caseIndex, "screen.cornerRadius", expected.GetProperty("cornerRadius").GetDouble(), screen.CornerRadius);
        CompareShadow(file, caseIndex, "screen.shadow", expected.GetProperty("shadow"), screen.Shadow);
        CompareNumber(file, caseIndex, "screen.opacity", expected.GetProperty("opacity").GetDouble(), screen.Opacity);
    }

    private static void CompareCamera(string file, int caseIndex, JsonElement expected, StudioResolvedCamera? actual)
    {
        if (expected.ValueKind == JsonValueKind.Null)
        {
            Assert.True(actual is null, $"{file} case {caseIndex} field camera expected null.");
            return;
        }

        Assert.True(actual is not null, $"{file} case {caseIndex} field camera expected non-null.");
        var camera = actual.Value;
        CompareRect(file, caseIndex, "camera.rect", expected.GetProperty("rect"), camera.Rect);
        CompareRect(file, caseIndex, "camera.source", expected.GetProperty("source"), camera.Source);
        CompareString(file, caseIndex, "camera.shape", expected.GetProperty("shape").GetString(), ShapeString(camera.Shape));
        CompareNumber(file, caseIndex, "camera.cornerRadius", expected.GetProperty("cornerRadius").GetDouble(), camera.CornerRadius);
        CompareBool(file, caseIndex, "camera.mirror", expected.GetProperty("mirror").GetBoolean(), camera.Mirror);
        CompareNumber(file, caseIndex, "camera.borderWidth", expected.GetProperty("borderWidth").GetDouble(), camera.BorderWidth);
        CompareShadow(file, caseIndex, "camera.shadow", expected.GetProperty("shadow"), camera.Shadow);
        CompareNumber(file, caseIndex, "camera.sourceTime", expected.GetProperty("sourceTime").GetDouble(), camera.SourceTime);
        CompareBool(file, caseIndex, "camera.visible", expected.GetProperty("visible").GetBoolean(), camera.Visible);
        CompareNumber(file, caseIndex, "camera.opacity", expected.GetProperty("opacity").GetDouble(), camera.Opacity);
    }

    private static void CompareRect(string file, int caseIndex, string field, JsonElement expected, StudioFrameRect actual)
    {
        CompareNumber(file, caseIndex, $"{field}.x", expected.GetProperty("x").GetDouble(), actual.X);
        CompareNumber(file, caseIndex, $"{field}.y", expected.GetProperty("y").GetDouble(), actual.Y);
        CompareNumber(file, caseIndex, $"{field}.width", expected.GetProperty("width").GetDouble(), actual.Width);
        CompareNumber(file, caseIndex, $"{field}.height", expected.GetProperty("height").GetDouble(), actual.Height);
    }

    private static void CompareShadow(string file, int caseIndex, string field, JsonElement expected, StudioResolvedShadow actual)
    {
        CompareNumber(file, caseIndex, $"{field}.blur", expected.GetProperty("blur").GetDouble(), actual.Blur);
        CompareNumber(file, caseIndex, $"{field}.offsetY", expected.GetProperty("offsetY").GetDouble(), actual.OffsetY);
        CompareNumber(file, caseIndex, $"{field}.opacity", expected.GetProperty("opacity").GetDouble(), actual.Opacity);
    }

    private static void CompareZoom(string file, int caseIndex, JsonElement expected, StudioZoom actual)
    {
        CompareNumber(file, caseIndex, "zoom.start", expected.GetProperty("start").GetDouble(), actual.Start);
        CompareNumber(file, caseIndex, "zoom.end", expected.GetProperty("end").GetDouble(), actual.End);
        CompareNumber(file, caseIndex, "zoom.scale", expected.GetProperty("scale").GetDouble(), actual.Scale);
        CompareString(file, caseIndex, "zoom.focus.mode", expected.GetProperty("focus").GetProperty("mode").GetString(), FocusModeString(actual.Focus.Mode));
        CompareNumber(file, caseIndex, "zoom.focus.x", expected.GetProperty("focus").GetProperty("x").GetDouble(), actual.Focus.X);
        CompareNumber(file, caseIndex, "zoom.focus.y", expected.GetProperty("focus").GetProperty("y").GetDouble(), actual.Focus.Y);
        CompareNumber(file, caseIndex, "zoom.easeIn", expected.GetProperty("easeIn").GetDouble(), actual.EaseIn);
        CompareNumber(file, caseIndex, "zoom.easeOut", expected.GetProperty("easeOut").GetDouble(), actual.EaseOut);
        CompareString(file, caseIndex, "zoom.origin", expected.GetProperty("origin").GetString(), OriginString(actual.Origin));
    }

    private static void CompareNumber(string file, int caseIndex, string field, double expected, double actual) =>
        Assert.True(Math.Abs(expected - actual) <= Tolerance, $"{file} case {caseIndex} field {field}: expected {expected}, actual {actual}.");

    private static void CompareString(string file, int caseIndex, string field, string? expected, string actual) =>
        Assert.True(string.Equals(expected, actual, StringComparison.Ordinal), $"{file} case {caseIndex} field {field}: expected {expected}, actual {actual}.");

    private static void CompareBool(string file, int caseIndex, string field, bool expected, bool actual) =>
        Assert.True(expected == actual, $"{file} case {caseIndex} field {field}: expected {expected}, actual {actual}.");

    private static string LayoutString(StudioLayout layout) =>
        layout switch
        {
            StudioLayout.Screen => "screen",
            StudioLayout.SideBySide => "sideBySide",
            StudioLayout.Camera => "camera",
            _ => "bubble",
        };

    private static string ShapeString(StudioCameraShape shape) =>
        shape switch
        {
            StudioCameraShape.RoundedRectangle => "roundedRectangle",
            StudioCameraShape.Squircle => "squircle",
            StudioCameraShape.Rectangle => "rectangle",
            _ => "circle",
        };

    private static string FocusModeString(StudioZoomFocusMode mode) =>
        mode == StudioZoomFocusMode.Cursor ? "cursor" : "point";

    private static string OriginString(StudioZoomOrigin origin) =>
        origin == StudioZoomOrigin.Auto ? "auto" : "manual";
}
