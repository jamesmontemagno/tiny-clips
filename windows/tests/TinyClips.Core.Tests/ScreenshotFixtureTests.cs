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
