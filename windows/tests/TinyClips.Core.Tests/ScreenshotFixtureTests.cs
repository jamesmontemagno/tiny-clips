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
            ? Directory.EnumerateFiles(directory, "*.json").Order().ToArray()
            : [];
        Assert.True(files.Length > 0, $"No screenshot arrow fixtures were copied to {directory}.");

        foreach (var file in files)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var index = 0;
            foreach (var testCase in document.RootElement.GetProperty("cases").EnumerateArray())
            {
                var label = $"{Path.GetFileName(file)} case {index++}";
                var actual = ArrowGeometry.Build(
                    ReadPoint(testCase.GetProperty("start")),
                    ReadPoint(testCase.GetProperty("end")),
                    testCase.GetProperty("width").GetDouble(),
                    ParseArrowStyle(testCase.GetProperty("style").GetString()),
                    testCase.GetProperty("scale").GetDouble());
                var expected = testCase.GetProperty("expected");

                Assert.True(expected.GetProperty("curved").GetBoolean() == actual.Curved, $"{label} curved");
                AssertPoint(expected.GetProperty("shaftStart"), actual.ShaftStart, $"{label} shaftStart");
                AssertPoint(expected.GetProperty("shaftControl"), actual.ShaftControl, $"{label} shaftControl");
                AssertPoint(expected.GetProperty("shaftEnd"), actual.ShaftEnd, $"{label} shaftEnd");
                AssertPoint(expected.GetProperty("tip"), actual.Tip, $"{label} tip");
                AssertPoint(expected.GetProperty("head1"), actual.Head1, $"{label} head1");
                AssertPoint(expected.GetProperty("head2"), actual.Head2, $"{label} head2");
            }
        }
    }

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

    private static ArrowStyle ParseArrowStyle(string? value) => value switch
    {
        "straight" => ArrowStyle.Straight,
        "curvedLeft" => ArrowStyle.Curved1,
        "curvedRight" => ArrowStyle.Curved2,
        _ => throw new InvalidOperationException($"Unknown arrow style {value}."),
    };

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
