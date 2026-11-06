using System.Numerics;
using System.Text.Json.Nodes;
using TinyClips.Core.Automation;
using TinyClips.Core.Editing;
using Windows.UI;

namespace TinyClips.Core.Tests;

public sealed class ShapeEffectsTests
{
    private const string Bounds = """{ "x": 10, "y": 10, "width": 100, "height": 50 }""";

    private static ScreenshotAnnotation ParseOne(string fields) =>
        AutomationAnnotationParser.Parse(JsonNode.Parse($$"""[{ "bounds": {{Bounds}}, {{fields}} }]"""), 400, 200)[0];

    [Fact]
    public void NewAnnotationsHaveNoEffects()
    {
        var effects = new ScreenshotAnnotation().ShapeEffects;

        Assert.False(effects.IsGradientFill);
        Assert.False(effects.DrawsShadow);
    }

    [Fact]
    public void ShadowGeometryFollowsTheSharedContract()
    {
        var effects = ShapeEffects.Default with { HasShadow = true, ShadowBlur = 20 };

        Assert.True(effects.DrawsShadow);
        Assert.Equal(10, effects.ShadowStandardDeviation);
        Assert.Equal(7, effects.ShadowOffsetY);
        Assert.False((effects with { ShadowBlur = 0 }).DrawsShadow);
    }

    [Theory]
    [InlineData(0, 0, 10)]
    [InlineData(90, 10, 0)]
    [InlineData(180, 0, -10)]
    [InlineData(270, -10, 0)]
    public void LocalShadowOffsetStillFallsDownOnceTurnedByTheShape(double degrees, float x, float y)
    {
        var local = ShapeEffects.LocalShadowOffset(10, degrees);
        Assert.Equal(x, local.X, 3);
        Assert.Equal(y, local.Y, 3);

        var turned = Vector2.Transform(local, Matrix3x2.CreateRotation((float)(degrees * Math.PI / 180)));
        Assert.Equal(0, turned.X, 3);
        Assert.Equal(10, turned.Y, 3);
    }

    [Fact]
    public void SnapshotCarriesEffects()
    {
        var effects = ShapeEffects.Default with { IsGradientFill = true, HasShadow = true };
        var annotation = new ScreenshotAnnotation { Tool = EditTool.Rectangle, ShapeEffects = effects };

        Assert.Equal(effects, annotation.CaptureSnapshot().ShapeEffects);
    }

    [Fact]
    public void ParserReadsGradientAndShadow()
    {
        var effects = ParseOne("""
            "type": "rectangle", "fillColor": "#FF0000", "fillEndColor": "#0000FF",
            "fillDirection": 90, "shadowBlur": 20, "shadowColor": "#80112233"
            """).ShapeEffects;

        Assert.True(effects.IsGradientFill);
        Assert.Equal(Color.FromArgb(255, 0, 0, 255), effects.GradientEndColor);
        Assert.Equal(90, effects.GradientDirectionDegrees);
        Assert.True(effects.DrawsShadow);
        Assert.Equal(20, effects.ShadowBlur);
        Assert.Equal(Color.FromArgb(128, 17, 34, 51), effects.ShadowColor);
    }

    [Fact]
    public void ParserAppliesDefaultsAndTreatsZeroBlurAsNoShadow()
    {
        var defaults = ParseOne("""
            "type": "ellipse", "fillColor": "#FF0000", "fillEndColor": "#0000FF", "shadowBlur": 12
            """).ShapeEffects;
        Assert.Equal(45, defaults.GradientDirectionDegrees);
        Assert.Equal(Color.FromArgb(89, 0, 0, 0), defaults.ShadowColor);

        var none = ParseOne("""
            "type": "rectangle", "shadowBlur": 0
            """).ShapeEffects;
        Assert.False(none.DrawsShadow);

        var plain = ParseOne("""
            "type": "rectangle"
            """).ShapeEffects;
        Assert.Equal(ShapeEffects.Default, plain);
    }

    [Theory]
    [InlineData("""{"type":"arrow","shadowBlur":4}""")]
    [InlineData("""{"type":"text","text":"x","fillEndColor":"#0000FF"}""")]
    [InlineData("""{"type":"rectangle","fillEndColor":"#0000FF"}""")]
    [InlineData("""{"type":"rectangle","fillColor":"#00FF0000","fillEndColor":"#0000FF"}""")]
    [InlineData("""{"type":"rectangle","fillColor":"#FF0000","fillDirection":45}""")]
    [InlineData("""{"type":"rectangle","fillColor":"#FF0000","fillEndColor":"#0000FF","fillDirection":361}""")]
    [InlineData("""{"type":"rectangle","fillColor":"#FF0000","fillEndColor":"nope"}""")]
    [InlineData("""{"type":"rectangle","shadowBlur":-1}""")]
    [InlineData("""{"type":"rectangle","shadowBlur":501}""")]
    [InlineData("""{"type":"rectangle","shadowColor":"#000000"}""")]
    [InlineData("""{"type":"rectangle","shadowBlur":4,"shadowColor":"black"}""")]
    public void ParserRejectsInvalidFields(string annotation)
    {
        var node = JsonNode.Parse(annotation)!.AsObject();
        node["bounds"] = JsonNode.Parse(Bounds);

        var error = Assert.Throws<AutomationException>(() =>
            AutomationAnnotationParser.Parse(new JsonArray(node), 400, 200));
        Assert.Equal(AutomationErrorCodes.InvalidParams, error.Code);
    }
}
