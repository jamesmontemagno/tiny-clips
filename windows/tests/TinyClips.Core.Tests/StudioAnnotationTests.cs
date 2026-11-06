using System.Text.Json;
using System.Text.Json.Nodes;
using TinyClips.Core.Studio;

namespace TinyClips.Core.Tests;

public sealed class StudioAnnotationTests
{
    private const double Tolerance = 1e-6;

    private const string MinimalProject = """
        { "id": "p", "sources": { "screen": { "width": 1920, "height": 1080, "duration": 10 } }, "overlays": OVERLAYS }
        """;

    [Fact]
    public void SharedFixturesMatchWindowsImplementation()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "StudioFixtures", "annotations");
        var files = Directory.EnumerateFiles(folder, "*.json").Order().ToArray();
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var root = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            var project = StudioProjectJson.ReadProject(root["project"]!.ToJsonString());
            var cases = root["cases"]!.AsArray();
            for (var caseIndex = 0; caseIndex < cases.Count; caseIndex++)
            {
                VerifyCase(name, caseIndex, project, cases[caseIndex]!.AsObject());
            }
        }
    }

    private static void VerifyCase(string name, int caseIndex, StudioProject project, JsonObject testCase)
    {
        var time = testCase["time"]!.GetValue<double>();
        var width = testCase["canvas"]!["width"]!.GetValue<double>();
        var height = testCase["canvas"]!["height"]!.GetValue<double>();
        var textWidth = testCase["textSize"]!["width"]!.GetValue<double>();
        var textHeight = testCase["textSize"]!["height"]!.GetValue<double>();
        var frame = StudioLayoutResolver.Resolve(project, time, width, height);

        var actual = new List<StudioResolvedAnnotation>();
        for (var index = 0; index < project.Overlays.Annotations.Length; index++)
        {
            if (StudioAnnotationGeometry.TryResolve(project.Overlays.Annotations[index], index, frame, time, width, height, out var resolved))
            {
                actual.Add(resolved);
            }
        }

        var expected = testCase["expected"]!.AsArray();
        var where = $"{name} case {caseIndex}";
        Assert.True(expected.Count == actual.Count, $"{where}: {actual.Count} drawn, expected {expected.Count}");
        for (var i = 0; i < expected.Count; i++)
        {
            var entry = expected[i]!.AsObject();
            var item = actual[i];
            var label = $"{where} item {i}";
            Assert.True(entry["index"]!.GetValue<int>() == item.Index, $"{label} index");
            Assert.True(entry["kind"]!.GetValue<string>() == item.Kind, $"{label} kind");
            Assert.True(entry["fill"]!.GetValue<string>() == item.Fill, $"{label} fill");
            Assert.True(entry["textColor"]!.GetValue<string>() == item.TextColor, $"{label} textColor");
            Near(label, "opacity", entry["opacity"], item.Opacity);
            NearPoint(label, "anchor", entry["anchor"], item.Anchor);
            if (item.IsArrow)
            {
                Near(label, "stroke", entry["stroke"], item.Stroke);
                Near(label, "halfWidth", entry["halfWidth"], item.HalfWidth);
                Assert.True(entry["hasShaft"]!.GetValue<bool>() == item.HasShaft, $"{label} hasShaft");
                NearPoint(label, "tail", entry["tail"], item.Tail);
                NearPoint(label, "headBase", entry["headBase"], item.HeadBase);
                continue;
            }

            Near(label, "font", entry["font"], item.Font);
            Near(label, "maxTextWidth", entry["maxTextWidth"], item.MaxTextWidth);
            Near(label, "radius", entry["radius"], item.Radius);
            var placed = StudioAnnotationGeometry.LayoutBox(item, textWidth, textHeight, width, height);
            NearRect(label, "box", entry["box"], placed.Box);
            NearRect(label, "textRect", entry["textRect"], placed.TextRect);
            var pointer = entry["pointer"];
            Assert.True((pointer is not null) == placed.HasPointer, $"{label} pointer");
            if (pointer is not null)
            {
                NearPoint(label, "pointer a", pointer[0], placed.PointerA);
                NearPoint(label, "pointer b", pointer[1], placed.PointerB);
            }
        }
    }

    [Fact]
    public void RoundTrip_KeepsEveryPropertyAndAKindFromALaterVersion()
    {
        var project = StudioProjectJson.ReadProject(MinimalProject.Replace("OVERLAYS", """
            { "annotations": [
              { "id": "a", "kind": "callout", "attach": "frame", "start": 1.5, "end": 3.25, "x": 0.1, "y": 0.2, "offsetX": 0.3, "offsetY": -0.4, "text": "Line one\nLine two", "style": "dark", "size": "large", "later": { "keep": true } },
              { "id": "b", "kind": "highlight", "start": 1, "end": 2, "strength": 3 }
            ] }
            """));

        var written = StudioProjectJson.WriteProject(project);
        var again = StudioProjectJson.ReadProject(written);
        var first = again.Overlays.Annotations[0];
        Assert.Equal(("a", StudioAnnotationKinds.Callout, StudioAnnotationAttach.Frame), (first.Id, first.Kind, first.Attach));
        Assert.Equal((1.5, 3.25, 0.1, 0.2, 0.3, -0.4), (first.Start, first.End, first.X, first.Y, first.OffsetX, first.OffsetY));
        Assert.Equal(("Line one\nLine two", StudioAnnotationStyle.Dark, StudioAnnotationSize.Large), (first.Text, first.Style, first.Size));

        var node = JsonNode.Parse(written)!["overlays"]!["annotations"]!;
        Assert.True(node[0]!["later"]!["keep"]!.GetValue<bool>());
        Assert.Equal("highlight", node[1]!["kind"]!.GetValue<string>());
        Assert.Equal(3, node[1]!["strength"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("{ }")]
    [InlineData("""{ "annotations": null }""")]
    [InlineData("""{ "annotations": [] }""")]
    [InlineData("""{ "annotations": [ null ] }""")]
    public void AProjectWithoutAnnotations_ReadsAnEmptyList(string overlays)
    {
        var project = StudioProjectJson.ReadProject(MinimalProject.Replace("OVERLAYS", overlays));

        Assert.Empty(project.Overlays.Annotations);
    }

    [Fact]
    public void MissingAndNullProperties_TakeTheDefaults()
    {
        var project = StudioProjectJson.ReadProject(MinimalProject.Replace("OVERLAYS", """
            { "annotations": [ { }, { "id": null, "kind": null, "attach": null, "x": null, "offsetX": null, "offsetY": null, "text": null, "style": null, "size": null } ] }
            """));

        foreach (var annotation in project.Overlays.Annotations)
        {
            Assert.Equal((string.Empty, StudioAnnotationKinds.Arrow, StudioAnnotationAttach.Screen), (annotation.Id, annotation.Kind, annotation.Attach));
            Assert.Equal((0, 0, 0.5, 0.5, -0.12, 0.12), (annotation.Start, annotation.End, annotation.X, annotation.Y, annotation.OffsetX, annotation.OffsetY));
            Assert.Equal((string.Empty, StudioAnnotationStyle.Red, StudioAnnotationSize.Medium), (annotation.Text, annotation.Style, annotation.Size));
        }
    }

    [Fact]
    public void EditableState_SeesAChangedAnnotationAndEqualContentInAnotherArray()
    {
        StudioProject With(string text) => new()
        {
            Id = "p",
            Overlays = new StudioOverlays { Annotations = [new StudioAnnotation { Kind = StudioAnnotationKinds.Text, Text = text, End = 1 }] },
        };

        var state = StudioEditableState.From(With("One"));

        Assert.True(state.ContentEquals(StudioEditableState.From(With("One"))));
        Assert.False(state.ContentEquals(StudioEditableState.From(With("Two"))));
    }

    [Fact]
    public void ChangesBetween_IsAboutTheFramesAfterTheFirst()
    {
        StudioAnnotation[] annotations = [new StudioAnnotation { Start = 2, End = 4 }];

        Assert.False(StudioAnnotationGeometry.ChangesBetween(annotations, 0, 1.9));
        Assert.True(StudioAnnotationGeometry.ChangesBetween(annotations, 1.9, 2));
        Assert.False(StudioAnnotationGeometry.ChangesBetween(annotations, 2, 3));
        Assert.True(StudioAnnotationGeometry.ChangesBetween(annotations, 3, 4));
        Assert.False(StudioAnnotationGeometry.ChangesBetween(annotations, 4, 5));
        Assert.True(StudioAnnotationGeometry.ChangesBetween(annotations, 1, 5));
        Assert.False(StudioAnnotationGeometry.ChangesBetween(null, 0, 5));
    }

    private static void Near(string label, string field, JsonNode? expected, double actual) =>
        Assert.True(Math.Abs(expected!.GetValue<double>() - actual) <= Tolerance, $"{label} {field}: {actual}, expected {expected}");

    private static void NearPoint(string label, string field, JsonNode? expected, StudioPoint actual)
    {
        Near(label, field + ".x", expected!["x"], actual.X);
        Near(label, field + ".y", expected["y"], actual.Y);
    }

    private static void NearRect(string label, string field, JsonNode? expected, StudioFrameRect actual)
    {
        Near(label, field + ".x", expected!["x"], actual.X);
        Near(label, field + ".y", expected["y"], actual.Y);
        Near(label, field + ".width", expected["width"], actual.Width);
        Near(label, field + ".height", expected["height"], actual.Height);
    }
}
