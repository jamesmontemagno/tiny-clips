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
            if (item.IsArea)
            {
                NearRect(label, "rect", entry["rect"], item.Rect);
                if (item.Kind == StudioAnnotationKinds.Redact)
                {
                    var style = item.RedactStyle switch
                    {
                        StudioRedactStyle.Pixelate => "pixelate",
                        StudioRedactStyle.Solid => "solid",
                        _ => "blur",
                    };
                    Assert.True(entry["redactStyle"]!.GetValue<string>() == style, $"{label} redactStyle");
                    Near(label, "redactAmount", entry["redactAmount"], item.RedactAmount);
                }
                else
                {
                    Near(label, "stroke", entry["stroke"], item.Stroke);
                    Near(label, "radius", entry["radius"], item.Radius);
                    Assert.True(entry["shapeFill"]!.GetValue<string>() == item.ShapeFill, $"{label} shapeFill");
                    Near(label, "shapeFillOpacity", entry["shapeFillOpacity"], item.ShapeFillOpacity);
                }

                continue;
            }

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
            if (item.IsEmoji)
            {
                NearRect(label, "textRect", entry["textRect"], StudioAnnotationGeometry.LayoutEmoji(item, textWidth, textHeight, width, height));
                Assert.True(entry["shadow"]!.GetValue<bool>() == item.HasShadow, $"{label} shadow");
                continue;
            }

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

    [Fact]
    public void Areas_RoundTripTheirProperties_AndReadUnknownRedactionsAsTheDefault()
    {
        var project = StudioProjectJson.ReadProject(MinimalProject.Replace("OVERLAYS", """
            { "annotations": [
              { "id": "r", "kind": "rectangle", "start": 0, "end": 2, "width": 0.3, "height": 0.25, "cornerRadius": 0.04, "shapeFill": "#00ff00", "shapeFillOpacity": 0.6 },
              { "id": "d", "kind": "redact", "start": 0, "end": 2, "redactStyle": "pixelate", "redactLevel": "heavy" },
              { "id": "u", "kind": "redact", "start": 0, "end": 2, "redactStyle": "swirl", "redactLevel": "max" },
              { "id": "e", "kind": "ellipse", "start": 0, "end": 2 }
            ] }
            """));

        var again = StudioProjectJson.ReadProject(StudioProjectJson.WriteProject(project)).Overlays.Annotations;
        Assert.Equal((0.3, 0.25, 0.04, "#00ff00", 0.6), (again[0].Width, again[0].Height, again[0].CornerRadius, again[0].ShapeFill, again[0].ShapeFillOpacity));
        Assert.Equal((StudioRedactStyle.Pixelate, StudioRedactLevel.Heavy), (again[1].RedactStyle, again[1].RedactLevel));
        Assert.Equal((StudioRedactStyle.Blur, StudioRedactLevel.Medium), (again[2].RedactStyle, again[2].RedactLevel));
        Assert.Equal((0.2, 0.15, 0.0, "", 0.35), (again[3].Width, again[3].Height, again[3].CornerRadius, again[3].ShapeFill, again[3].ShapeFillOpacity));
    }

    [Fact]
    public void AnAgentPatch_OfAreas_IsReadWrittenAndReadAgain()
    {
        // studio.project.edit puts the patch over the project's JSON, reads it as a project, and saves it.
        var node = JsonNode.Parse(StudioProjectJson.WriteProject(StudioProjectJson.ReadProject(MinimalProject.Replace("OVERLAYS", "{}"))))!.AsObject();
        node["overlays"] = JsonNode.Parse("""
            { "annotations": [
              { "id": "box", "kind": "rectangle", "attach": "screen", "start": 1, "end": 4, "x": 0.4, "y": 0.6, "width": 0.25, "height": 0.1,
                "style": "blue", "cornerRadius": 0.03, "shapeFill": "#FFCC00", "shapeFillOpacity": 0.5, "future": { "kept": true } },
              { "id": "hide", "kind": "redact", "start": 0, "end": 2, "x": 0.5, "y": 0.5, "width": 0.3, "height": 0.2, "redactStyle": "solid", "redactLevel": "light" }
            ] }
            """);

        var saved = StudioProjectJson.WriteProject(StudioProjectJson.ReadProject(node.ToJsonString()));
        var annotations = StudioProjectJson.ReadProject(saved).Overlays.Annotations;

        Assert.Equal((StudioAnnotationKinds.Rectangle, StudioAnnotationAttach.Screen, 0.25, 0.1, 0.03, "#FFCC00", 0.5),
            (annotations[0].Kind, annotations[0].Attach, annotations[0].Width, annotations[0].Height, annotations[0].CornerRadius, annotations[0].ShapeFill, annotations[0].ShapeFillOpacity));
        Assert.Equal((StudioAnnotationKinds.Redact, 0.3, 0.2, StudioRedactStyle.Solid, StudioRedactLevel.Light),
            (annotations[1].Kind, annotations[1].Width, annotations[1].Height, annotations[1].RedactStyle, annotations[1].RedactLevel));
        Assert.True(JsonNode.Parse(saved)!["overlays"]!["annotations"]![0]!["future"]!["kept"]!.GetValue<bool>());
    }

    [Fact]
    public void AnAgentPatch_OfAnEmoji_IsReadWrittenAndReadAgain()
    {
        var node = JsonNode.Parse(StudioProjectJson.WriteProject(StudioProjectJson.ReadProject(MinimalProject.Replace("OVERLAYS", "{}"))))!.AsObject();
        node["overlays"] = JsonNode.Parse("""
            { "annotations": [
              { "id": "wow", "kind": "emoji", "attach": "screen", "start": 1, "end": 4, "x": 0.4, "y": 0.6, "text": "🚀", "size": "large", "scale": 1.5, "shadow": true, "future": { "kept": true } },
              { "id": "meh", "kind": "emoji", "start": 0, "end": 2, "text": "👍", "shadow": "yes" },
              { "id": "none", "kind": "emoji", "start": 0, "end": 2, "text": "👍" }
            ] }
            """);

        var saved = StudioProjectJson.WriteProject(StudioProjectJson.ReadProject(node.ToJsonString()));
        var annotations = StudioProjectJson.ReadProject(saved).Overlays.Annotations;

        Assert.Equal((StudioAnnotationKinds.Emoji, StudioAnnotationAttach.Screen, "🚀", StudioAnnotationSize.Large, 1.5, true),
            (annotations[0].Kind, annotations[0].Attach, annotations[0].Text, annotations[0].Size, annotations[0].Scale, annotations[0].Shadow));
        Assert.False(annotations[1].Shadow);
        Assert.False(annotations[2].Shadow);
        Assert.True(JsonNode.Parse(saved)!["overlays"]!["annotations"]![0]!["future"]!["kept"]!.GetValue<bool>());
        Assert.True(JsonNode.Parse(saved)!["overlays"]!["annotations"]![0]!["shadow"]!.GetValue<bool>());
    }

    [Fact]
    public void AnArea_MovesByItsMiddle_AndResizesByACornerWhileTheOppositeOneStays()
    {
        var area = new StudioAnnotation { Kind = StudioAnnotationKinds.Rectangle, Attach = StudioAnnotationAttach.Frame, End = 5, X = 0.25, Y = 0.25, Width = 0.2, Height = 0.2 };
        var frame = default(StudioResolvedFrame);

        var moved = StudioAnnotationGeometry.GetAreaMoved(area, StudioAnnotationPart.Box, 100, 50, frame, 1000, 500)!.Value;
        Assert.Equal((0.35, 0.35, 0.2, 0.2), (moved.X, moved.Y, moved.Width!.Value, moved.Height!.Value), new TupleComparer());

        // The bottom right corner goes 100 pixels right; the top left corner (150, 75) stays.
        var resized = StudioAnnotationGeometry.GetAreaMoved(area, StudioAnnotationPart.BottomRight, 100, 0, frame, 1000, 500)!.Value;
        Assert.Equal((0.3, 0.25, 0.3, 0.2), (resized.X, resized.Y, resized.Width!.Value, resized.Height!.Value), new TupleComparer());

        // Dragged past the opposite corner, it keeps a size that can be taken hold of again.
        var smallest = StudioAnnotationGeometry.GetAreaMoved(area, StudioAnnotationPart.TopLeft, 900, 900, frame, 1000, 500)!.Value;
        Assert.True(smallest.Width > 0 && smallest.Height > 0);
        Assert.Equal(0.35, smallest.X + (smallest.Width!.Value / 2), 6);
    }

    private sealed class TupleComparer : IEqualityComparer<(double, double, double, double)>
    {
        public bool Equals((double, double, double, double) x, (double, double, double, double) y) =>
            Math.Abs(x.Item1 - y.Item1) < Tolerance && Math.Abs(x.Item2 - y.Item2) < Tolerance
            && Math.Abs(x.Item3 - y.Item3) < Tolerance && Math.Abs(x.Item4 - y.Item4) < Tolerance;

        public int GetHashCode((double, double, double, double) obj) => 0;
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
