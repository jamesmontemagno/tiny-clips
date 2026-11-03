using System.Text.Json;
using TinyClips.Core.Editing;

namespace TinyClips.Core.Tests;

public sealed class ScreenshotFixtureTests
{
    private const double Tolerance = 1e-6;

    [Fact]
    public void SharedArrowFixturesMatchWindowsImplementation()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "ScreenshotFixtures", "arrow");
        var files = Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.json").Where(f => Path.GetFileName(f) != "handle.json").Order().ToArray()
            : [];
        Assert.True(files.Length > 0, $"No screenshot arrow fixtures were copied to {directory}.");

        foreach (var file in files)
        {
            foreach (var (label, testCase) in Cases("arrow", Path.GetFileName(file)))
            {
                var actual = ArrowGeometry.Build(
                    ReadPoint(testCase.GetProperty("start")),
                    ReadPoint(testCase.GetProperty("end")),
                    testCase.GetProperty("width").GetDouble(),
                    new ArrowShapeOptions(
                        testCase.GetProperty("bend").GetDouble(),
                        testCase.GetProperty("startWidthScale").GetDouble(),
                        testCase.GetProperty("endWidthScale").GetDouble(),
                        testCase.GetProperty("headScale").GetDouble()),
                    ParseCap(testCase.GetProperty("startCap").GetString()),
                    ParseJoin(testCase.GetProperty("join").GetString()),
                    testCase.GetProperty("scale").GetDouble());
                var expected = testCase.GetProperty("expected");

                AssertPoint(expected.GetProperty("control"), actual.Control, $"{label} control");
                AssertPoint(expected.GetProperty("handle"), actual.Handle, $"{label} handle");
                var points = expected.GetProperty("points").EnumerateArray().ToArray();
                Assert.True(points.Length == actual.Points.Count, $"{label}: expected {points.Length} points but was {actual.Points.Count}.");
                for (var i = 0; i < points.Length; i++)
                {
                    AssertPoint(points[i], actual.Points[i], $"{label} point {i}");
                }
            }

            if (Path.GetFileName(file) is var name && name != "options.json")
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                var preset = document.RootElement.GetProperty("preset");
                var options = new ArrowShapeOptions(
                    preset.GetProperty("bend").GetDouble(),
                    preset.GetProperty("startWidthScale").GetDouble(),
                    preset.GetProperty("endWidthScale").GetDouble(),
                    preset.GetProperty("headScale").GetDouble());
                Assert.Equal(Path.GetFileNameWithoutExtension(name), options.Preset.ToString().ToLowerInvariant());
            }
        }
    }

    [Fact]
    public void SharedArrowHandleFixturesMatchWindowsImplementation()
    {
        foreach (var (label, testCase) in Cases("arrow", "handle.json"))
        {
            var actual = ArrowGeometry.Bend(
                ReadPoint(testCase.GetProperty("start")),
                ReadPoint(testCase.GetProperty("end")),
                ReadPoint(testCase.GetProperty("handle")));
            Assert.True(Math.Abs(testCase.GetProperty("expected").GetDouble() - actual) <= Tolerance, $"{label}: bend was {actual}.");
        }
    }

    [Fact]
    public void SharedLineFixturesMatchWindowsImplementation()
    {
        foreach (var (label, testCase) in Cases("line", "line.json"))
        {
            var actual = LineGeometry.Outline(
                ReadPoint(testCase.GetProperty("start")),
                ReadPoint(testCase.GetProperty("end")),
                testCase.GetProperty("width").GetDouble(),
                ParseCap(testCase.GetProperty("startCap").GetString()),
                ParseCap(testCase.GetProperty("endCap").GetString()),
                testCase.GetProperty("scale").GetDouble());
            var points = testCase.GetProperty("expected").EnumerateArray().ToArray();
            Assert.True(points.Length == actual.Count, $"{label}: expected {points.Length} points but was {actual.Count}.");
            for (var i = 0; i < points.Length; i++)
            {
                AssertPoint(points[i], actual[i], $"{label} point {i}");
            }
        }
    }

    [Fact]
    public void SharedPenFixturesMatchWindowsImplementation()
    {
        foreach (var (label, testCase) in Cases("pen", "pen.json"))
        {
            var actual = PenGeometry.Outline(
                testCase.GetProperty("points").EnumerateArray().Select(ReadPoint).ToArray(),
                testCase.GetProperty("width").GetDouble());
            var points = testCase.GetProperty("expected").EnumerateArray().ToArray();
            Assert.True(points.Length == actual.Count, $"{label}: expected {points.Length} points but was {actual.Count}.");
            for (var i = 0; i < points.Length; i++)
            {
                AssertPoint(points[i], actual[i], $"{label} point {i}");
            }
        }
    }

    private static StrokeCap ParseCap(string? value) => value switch
    {
        "butt" => StrokeCap.Butt,
        "round" => StrokeCap.Round,
        "square" => StrokeCap.Square,
        _ => throw new InvalidOperationException($"Unknown cap {value}."),
    };

    [Fact]
    public void SharedRectangleAlignmentFixturesMatchWindowsImplementation()
    {
        foreach (var (label, testCase) in Cases("alignment", "rectangle.json"))
        {
            var actual = StrokeAlignmentGeometry.RectangleRing(
                ReadRect(testCase.GetProperty("bounds")),
                testCase.GetProperty("width").GetDouble(),
                ParseAlignment(testCase.GetProperty("alignment").GetString()),
                ParseJoin(testCase.GetProperty("join").GetString()));
            var expected = testCase.GetProperty("expected");

            AssertRect(expected.GetProperty("outer"), actual.Outer, $"{label} outer");
            Assert.True(ParseJoin(expected.GetProperty("outerJoin").GetString()) == actual.OuterJoin, $"{label} outerJoin");
            Assert.True(Math.Abs(expected.GetProperty("cornerSize").GetDouble() - actual.CornerSize) <= Tolerance, $"{label} cornerSize");
            AssertRect(expected.GetProperty("inner"), actual.Inner, $"{label} inner");
        }
    }

    [Fact]
    public void SharedEllipseAlignmentFixturesMatchWindowsImplementation()
    {
        foreach (var (label, testCase) in Cases("alignment", "ellipse.json"))
        {
            var actual = StrokeAlignmentGeometry.EllipseCenterline(
                ReadRect(testCase.GetProperty("bounds")),
                testCase.GetProperty("width").GetDouble(),
                ParseAlignment(testCase.GetProperty("alignment").GetString()));
            AssertRect(testCase.GetProperty("expected"), actual, label);
        }
    }

    private static IEnumerable<(string Label, JsonElement Case)> Cases(string folder, string fileName)
    {
        var file = Path.Combine(AppContext.BaseDirectory, "ScreenshotFixtures", folder, fileName);
        Assert.True(File.Exists(file), $"Missing screenshot fixture {file}.");
        using var document = JsonDocument.Parse(File.ReadAllText(file));
        var index = 0;
        foreach (var testCase in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            yield return ($"{fileName} case {index++}", testCase.Clone());
        }
    }

    private static StrokeAlignment ParseAlignment(string? value) => value switch
    {
        "center" => StrokeAlignment.Center,
        "inside" => StrokeAlignment.Inside,
        "outside" => StrokeAlignment.Outside,
        _ => throw new InvalidOperationException($"Unknown alignment {value}."),
    };

    private static StrokeJoin ParseJoin(string? value) => value switch
    {
        "miter" => StrokeJoin.Miter,
        "round" => StrokeJoin.Round,
        "bevel" => StrokeJoin.Bevel,
        _ => throw new InvalidOperationException($"Unknown join {value}."),
    };

    private static RectD ReadRect(JsonElement element) => new(
        element.GetProperty("x").GetDouble(),
        element.GetProperty("y").GetDouble(),
        element.GetProperty("width").GetDouble(),
        element.GetProperty("height").GetDouble());

    private static void AssertRect(JsonElement expected, RectD actual, string label)
    {
        var rect = ReadRect(expected);
        Assert.True(
            Math.Abs(rect.X - actual.X) <= Tolerance
                && Math.Abs(rect.Y - actual.Y) <= Tolerance
                && Math.Abs(rect.Width - actual.Width) <= Tolerance
                && Math.Abs(rect.Height - actual.Height) <= Tolerance,
            $"{label}: expected {rect} but was {actual}.");
    }

    private static PointD ReadPoint(JsonElement element) =>
        new(element.GetProperty("x").GetDouble(), element.GetProperty("y").GetDouble());

    private static void AssertPoint(JsonElement expected, PointD actual, string label)
    {
        var point = ReadPoint(expected);
        Assert.True(
            Math.Abs(point.X - actual.X) <= Tolerance && Math.Abs(point.Y - actual.Y) <= Tolerance,
            $"{label}: expected ({point.X}, {point.Y}) but was ({actual.X}, {actual.Y}).");
    }
}
