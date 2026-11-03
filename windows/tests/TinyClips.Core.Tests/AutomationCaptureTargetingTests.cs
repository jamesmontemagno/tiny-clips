using System.Text.Json.Nodes;
using TinyClips.Core.Automation;
using TinyClips.Core.Capture;
using TinyClips.Core.Editing;

namespace TinyClips.Core.Tests;

public sealed class AutomationCaptureTargetingTests
{
    private static JsonObject Params(string json) => JsonNode.Parse(json)!.AsObject();

    private static void AssertInvalid(Action action)
    {
        var ex = Assert.Throws<AutomationException>(action);
        Assert.Equal(AutomationErrorCodes.InvalidParams, ex.Code);
    }

    // -- Registry ----------------------------------------------------------------------------

    [Fact]
    public void Registry_IdsAreStableOpaqueAndTyped()
    {
        var registry = new AutomationCaptureTargetRegistry();
        var display = registry.GetDisplayId(0x1234);
        var window = registry.GetWindowId(0x1234);

        Assert.StartsWith(AutomationCaptureTargetRegistry.DisplayPrefix, display);
        Assert.StartsWith(AutomationCaptureTargetRegistry.WindowPrefix, window);
        Assert.DoesNotContain("1234", display, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(display, registry.GetDisplayId(0x1234));
        Assert.NotEqual(display, registry.GetDisplayId(0x5678));

        Assert.True(registry.TryResolveDisplay(display, out var handle));
        Assert.Equal((nint)0x1234, handle);
        Assert.True(registry.TryResolveWindow(window, out handle));
        Assert.Equal((nint)0x1234, handle);
    }

    [Fact]
    public void Registry_RejectsUnknownAndCrossKindIds()
    {
        var registry = new AutomationCaptureTargetRegistry();
        var display = registry.GetDisplayId(7);
        var window = registry.GetWindowId(8);

        Assert.False(registry.TryResolveWindow(display, out _));
        Assert.False(registry.TryResolveDisplay(window, out _));
        Assert.False(registry.TryResolveDisplay("display-000000000000", out _));
        Assert.False(registry.TryResolveDisplay("", out _));
        Assert.False(new AutomationCaptureTargetRegistry().TryResolveDisplay(display, out _));
    }

    // -- Contract ----------------------------------------------------------------------------

    [Fact]
    public void Contract_DefinesTargetingCommandsWithNestedSchemas()
    {
        var contract = AutomationContract.Default;
        Assert.True(contract.TryGetCommand("capture.targets", out var targets));
        Assert.Equal("capture", targets.Scope);
        Assert.True(contract.TryGetCommand("editor.annotate", out var annotate));
        Assert.Equal("edit", annotate.Scope);
        Assert.True(annotate.Parameters["path"].Required);
        Assert.Equal("array", annotate.Parameters["annotations"].Type);
        Assert.True(contract.TryGetCommand("capture.screenshot", out var screenshot));
        Assert.Equal("object", screenshot.Parameters["region"].Type);
        Assert.False(screenshot.Parameters["region"].Required);

        Assert.True(AutomationHost.TryValidateParams(screenshot, Params("""{"mode":"region","displayId":"d","region":{"x":0,"y":0,"width":1,"height":1}}"""), out _, out _));
        Assert.False(AutomationHost.TryValidateParams(screenshot, Params("""{"region":[0,0,1,1]}"""), out _, out _));
        Assert.False(AutomationHost.TryValidateParams(annotate, Params("""{"path":"a.png","annotations":{}}"""), out _, out _));
    }

    // -- Capture request ---------------------------------------------------------------------

    [Fact]
    public void Request_ParsesEachMode()
    {
        var screen = AutomationCaptureRequest.Parse(Params("""{"mode":"screen"}"""));
        Assert.Equal(AutomationCaptureMode.Screen, screen.Mode);
        Assert.Null(screen.DisplayId);

        Assert.Equal(AutomationCaptureMode.Screen, AutomationCaptureRequest.Parse(new JsonObject()).Mode);

        var region = AutomationCaptureRequest.Parse(Params(
            """{"mode":"region","displayId":"display-a","region":{"x":0,"y":10,"width":20,"height":30}}"""));
        Assert.Equal(AutomationCaptureMode.Region, region.Mode);
        Assert.Equal("display-a", region.DisplayId);
        Assert.Equal(new PixelRect(0, 10, 20, 30), region.Region);

        var window = AutomationCaptureRequest.Parse(Params("""{"mode":"window","windowId":"window-a"}"""));
        Assert.Equal("window-a", window.WindowId);
    }

    [Theory]
    [InlineData("""{"mode":"window"}""")]
    [InlineData("""{"mode":"window","windowId":"w","region":{"x":0,"y":0,"width":1,"height":1}}""")]
    [InlineData("""{"mode":"window","windowId":"w","displayId":"d"}""")]
    [InlineData("""{"mode":"region","region":{"x":0,"y":0,"width":1,"height":1}}""")]
    [InlineData("""{"mode":"region","displayId":"d"}""")]
    [InlineData("""{"mode":"region","displayId":"d","windowId":"w","region":{"x":0,"y":0,"width":1,"height":1}}""")]
    [InlineData("""{"mode":"region","displayId":"d","region":{"x":-1,"y":0,"width":1,"height":1}}""")]
    [InlineData("""{"mode":"region","displayId":"d","region":{"x":0,"y":0,"width":0,"height":1}}""")]
    [InlineData("""{"mode":"region","displayId":"d","region":{"x":0.5,"y":0,"width":1,"height":1}}""")]
    [InlineData("""{"mode":"region","displayId":"d","region":{"x":0,"y":0,"width":1}}""")]
    [InlineData("""{"mode":"region","displayId":"d","region":{"x":0,"y":0,"width":1,"height":1,"z":1}}""")]
    [InlineData("""{"mode":"region","displayId":"d","region":[0,0,1,1]}""")]
    [InlineData("""{"mode":"region","displayId":"d","region":{"x":"0","y":0,"width":1,"height":1}}""")]
    [InlineData("""{"mode":"screen","windowId":"w"}""")]
    [InlineData("""{"mode":"screen","region":{"x":0,"y":0,"width":1,"height":1}}""")]
    [InlineData("""{"mode":"scrolling"}""")]
    [InlineData("""{"mode":"screen","displayId":""}""")]
    public void Request_RejectsMissingOrMalformedTargets(string json) =>
        AssertInvalid(() => AutomationCaptureRequest.Parse(Params(json)));

    [Fact]
    public void Region_MustFitDisplay()
    {
        AutomationCaptureRequest.EnsureRegionFits(new PixelRect(0, 0, 1920, 1080), 1920, 1080);
        AutomationCaptureRequest.EnsureRegionFits(new PixelRect(1919, 1079, 1, 1), 1920, 1080);
        AssertInvalid(() => AutomationCaptureRequest.EnsureRegionFits(new PixelRect(1, 0, 1920, 1080), 1920, 1080));
        AssertInvalid(() => AutomationCaptureRequest.EnsureRegionFits(new PixelRect(0, 1000, 10, 81), 1920, 1080));
        AssertInvalid(() => AutomationCaptureRequest.EnsureRegionFits(new PixelRect(int.MaxValue, 0, 1, 1), 1920, 1080));
    }

    // -- Annotations -------------------------------------------------------------------------

    private static IReadOnlyList<ScreenshotAnnotation> Parse(string json, int width = 200, int height = 100) =>
        AutomationAnnotationParser.Parse(JsonNode.Parse(json), width, height);

    [Fact]
    public void Annotations_MapEveryToolType()
    {
        var parsed = Parse("""
            [
              {"type":"rectangle","bounds":{"x":1,"y":2,"width":30,"height":20},"color":"#00FF00","fillColor":"#80000000","lineWidth":3},
              {"type":"ellipse","bounds":{"x":0,"y":0,"width":10,"height":10}},
              {"type":"arrow","bounds":{"x":10,"y":10,"width":40,"height":20}},
              {"type":"line","bounds":{"x":0,"y":0,"width":0,"height":0},"points":[{"x":5,"y":5},{"x":50,"y":60}]},
              {"type":"pen","bounds":{"x":0,"y":0,"width":20,"height":20},"points":[{"x":0,"y":0},{"x":10,"y":10},{"x":20,"y":5}]},
              {"type":"text","bounds":{"x":5,"y":5,"width":0,"height":0},"text":"Hello","textColor":"#FFFFFF","fillColor":"#FF000000","fontSize":40},
              {"type":"number","bounds":{"x":0,"y":0,"width":40,"height":20}},
              {"type":"number","bounds":{"x":50,"y":0,"width":20,"height":20},"text":"7"},
              {"type":"number","bounds":{"x":80,"y":0,"width":20,"height":20}},
              {"type":"emoji","bounds":{"x":100,"y":10,"width":32,"height":32},"emoji":"🎉"},
              {"type":"redact","bounds":{"x":0,"y":50,"width":100,"height":20},"redactionLevel":"heavy","redactionStyle":"pixelate"}
            ]
            """);

        Assert.Equal(11, parsed.Count);
        Assert.Equal(
            new[] { EditTool.Rectangle, EditTool.Ellipse, EditTool.Arrow, EditTool.Line, EditTool.Pen, EditTool.Text,
                EditTool.Counter, EditTool.Counter, EditTool.Counter, EditTool.Emoji, EditTool.Redact },
            parsed.Select(a => a.Tool));

        var rectangle = parsed[0];
        Assert.Equal(Windows.UI.Color.FromArgb(255, 0, 255, 0), rectangle.Color);
        Assert.Equal(Windows.UI.Color.FromArgb(0x80, 0, 0, 0), rectangle.FillColor);
        Assert.Equal(3, rectangle.Thickness);
        Assert.Equal(AutomationAnnotationParser.DefaultColor, parsed[1].Color);
        Assert.Equal(AutomationAnnotationParser.DefaultLineWidth, parsed[1].Thickness);

        Assert.Equal(new[] { new System.Numerics.Vector2(10, 10), new System.Numerics.Vector2(50, 30) }, parsed[2].Points);
        Assert.Equal(new[] { new System.Numerics.Vector2(5, 5), new System.Numerics.Vector2(50, 60) }, parsed[3].Points);
        Assert.Equal(3, parsed[4].Points.Count);

        var text = parsed[5];
        Assert.Equal("Hello", text.Text);
        Assert.Equal(40, text.FontSize);
        Assert.Equal(Windows.UI.Color.FromArgb(255, 255, 255, 255), text.Color);
        Assert.Equal(TextBoxPreset.Custom, text.TextBoxStyle.Preset);

        Assert.Equal(1, parsed[6].Number);
        Assert.Equal(new Windows.Foundation.Rect(10, 0, 20, 20), parsed[6].Bounds);
        Assert.Equal(7, parsed[7].Number);
        Assert.Equal(8, parsed[8].Number);
        Assert.Equal("🎉", parsed[9].Text);
        Assert.Equal(RedactionLevel.Heavy, parsed[10].Redaction);
        Assert.Equal(RedactionStyle.Pixelate, parsed[10].RedactStyle);
    }

    [Theory]
    [InlineData("""{"annotations":[]}""")]
    [InlineData("""[]""")]
    [InlineData("""[1]""")]
    [InlineData("""[{"bounds":{"x":0,"y":0,"width":1,"height":1}}]""")]
    [InlineData("""[{"type":"star","bounds":{"x":0,"y":0,"width":1,"height":1}}]""")]
    [InlineData("""[{"type":"rectangle"}]""")]
    [InlineData("""[{"type":"rectangle","bounds":{"x":0,"y":0,"width":1}}]""")]
    [InlineData("""[{"type":"rectangle","bounds":{"x":0,"y":0,"width":1,"height":1,"depth":1}}]""")]
    [InlineData("""[{"type":"rectangle","bounds":{"x":0,"y":0,"width":-1,"height":1}}]""")]
    [InlineData("""[{"type":"rectangle","bounds":{"x":0,"y":0,"width":0,"height":1}}]""")]
    [InlineData("""[{"type":"rectangle","bounds":{"x":"0","y":0,"width":1,"height":1}}]""")]
    [InlineData("""[{"type":"rectangle","bounds":{"x":0,"y":0,"width":1,"height":1},"rotation":45}]""")]
    [InlineData("""[{"type":"rectangle","bounds":{"x":0,"y":0,"width":1,"height":1},"color":"red"}]""")]
    [InlineData("""[{"type":"rectangle","bounds":{"x":0,"y":0,"width":1,"height":1},"color":"#12345"}]""")]
    [InlineData("""[{"type":"rectangle","bounds":{"x":0,"y":0,"width":1,"height":1},"fillColor":"#GGGGGG"}]""")]
    [InlineData("""[{"type":"rectangle","bounds":{"x":0,"y":0,"width":1,"height":1},"lineWidth":-1}]""")]
    [InlineData("""[{"type":"text","bounds":{"x":0,"y":0,"width":1,"height":1},"text":"a","fontSize":0}]""")]
    [InlineData("""[{"type":"text","bounds":{"x":0,"y":0,"width":1,"height":1}}]""")]
    [InlineData("""[{"type":"text","bounds":{"x":0,"y":0,"width":1,"height":1},"text":"   "}]""")]
    [InlineData("""[{"type":"emoji","bounds":{"x":0,"y":0,"width":10,"height":10}}]""")]
    [InlineData("""[{"type":"number","bounds":{"x":0,"y":0,"width":10,"height":10},"text":"one"}]""")]
    [InlineData("""[{"type":"number","bounds":{"x":0,"y":0,"width":10,"height":10},"text":"-1"}]""")]
    [InlineData("""[{"type":"pen","bounds":{"x":0,"y":0,"width":1,"height":1},"points":[{"x":0,"y":0}]}]""")]
    [InlineData("""[{"type":"pen","bounds":{"x":0,"y":0,"width":1,"height":1}}]""")]
    [InlineData("""[{"type":"line","bounds":{"x":5,"y":5,"width":0,"height":0}}]""")]
    [InlineData("""[{"type":"arrow","bounds":{"x":0,"y":0,"width":1,"height":1},"points":[{"x":1,"y":1}]}]""")]
    [InlineData("""[{"type":"line","bounds":{"x":0,"y":0,"width":1,"height":1},"points":[{"x":1}, {"x":2,"y":2}]}]""")]
    [InlineData("""[{"type":"line","bounds":{"x":0,"y":0,"width":1,"height":1},"points":[{"x":1,"y":1,"z":0},{"x":2,"y":2}]}]""")]
    [InlineData("""[{"type":"redact","bounds":{"x":0,"y":0,"width":1,"height":1},"redactionLevel":"extreme"}]""")]
    [InlineData("""[{"type":"redact","bounds":{"x":0,"y":0,"width":1,"height":1},"redactionStyle":"smudge"}]""")]
    public void Annotations_RejectMalformedInput(string json) =>
        AssertInvalid(() => AutomationAnnotationParser.ValidateStructure(JsonNode.Parse(json)));

    [Theory]
    [InlineData("""[{"type":"rectangle","bounds":{"x":-1,"y":0,"width":10,"height":10}}]""")]
    [InlineData("""[{"type":"rectangle","bounds":{"x":0,"y":0,"width":201,"height":10}}]""")]
    [InlineData("""[{"type":"rectangle","bounds":{"x":150,"y":95,"width":10,"height":10}}]""")]
    [InlineData("""[{"type":"pen","bounds":{"x":0,"y":0,"width":10,"height":10},"points":[{"x":0,"y":0},{"x":250,"y":5}]}]""")]
    [InlineData("""[{"type":"arrow","bounds":{"x":0,"y":0,"width":0,"height":0},"points":[{"x":0,"y":-1},{"x":5,"y":5}]}]""")]
    public void Annotations_RejectOutOfBoundsAgainstImage(string json)
    {
        AutomationAnnotationParser.ValidateStructure(JsonNode.Parse(json));
        AssertInvalid(() => Parse(json));
    }

    [Fact]
    public void Annotations_RejectTooMany()
    {
        var array = new JsonArray();
        for (var i = 0; i <= AutomationAnnotationParser.MaximumAnnotations; i++)
        {
            array.Add(JsonNode.Parse("""{"type":"rectangle","bounds":{"x":0,"y":0,"width":1,"height":1}}"""));
        }

        AssertInvalid(() => AutomationAnnotationParser.ValidateStructure(array));
    }

    [Fact]
    public void Annotations_AllowFullImageBounds()
    {
        var parsed = Parse("""[{"type":"redact","bounds":{"x":0,"y":0,"width":200,"height":100}}]""");
        Assert.Single(parsed);
    }

    [Theory]
    [InlineData("#FF8000", 255, 255, 128, 0)]
    [InlineData("#80FF8000", 128, 255, 128, 0)]
    [InlineData("#00000000", 0, 0, 0, 0)]
    public void Colors_Parse(string value, byte a, byte r, byte g, byte b)
    {
        Assert.True(AutomationAnnotationParser.TryParseColor(value, out var color));
        Assert.Equal(Windows.UI.Color.FromArgb(a, r, g, b), color);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("FF8000")]
    [InlineData("#FF80")]
    [InlineData("#FF8000F")]
    [InlineData("#+F8000")]
    public void Colors_RejectInvalid(string? value) =>
        Assert.False(AutomationAnnotationParser.TryParseColor(value, out _));
}
