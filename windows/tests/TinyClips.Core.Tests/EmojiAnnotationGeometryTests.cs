using TinyClips.Core.Editing;

namespace TinyClips.Core.Tests;

public sealed class EmojiAnnotationGeometryTests
{
    [Fact]
    public void DefaultSidePixels_IsTenPercentOfWidthWithinBounds()
    {
        Assert.Equal(200, EmojiAnnotationMath.DefaultSidePixels(2000, 1000));
        Assert.Equal(EmojiAnnotationMath.MinimumSidePixels, EmojiAnnotationMath.ClampSidePixels(2, 2000, 1000));
        Assert.Equal(1000, EmojiAnnotationMath.ClampSidePixels(5000, 2000, 1000));
    }

    [Fact]
    public void SquareBounds_CentersOnPoint()
    {
        var bounds = EmojiAnnotationMath.SquareBounds(new PointD(500, 250), 100);
        Assert.Equal(new RectD(450, 200, 100, 100), bounds);
        Assert.Equal(new PointD(500, 250), bounds.Center);
    }

    [Fact]
    public void Corners_RotateClockwise()
    {
        var bounds = new RectD(400, 400, 200, 200);
        var corners = RotatableAnnotationGeometry.Corners(bounds, 90);

        // Top-left (400, 400) ends up where top-right was after a clockwise quarter turn.
        Assert.Equal(600, corners[0].X, 6);
        Assert.Equal(400, corners[0].Y, 6);
        // Bottom-right (600, 600) ends up at bottom-left.
        Assert.Equal(400, corners[3].X, 6);
        Assert.Equal(600, corners[3].Y, 6);
    }

    [Fact]
    public void RotationHandle_StartsAboveTopEdgeAndFollowsRotation()
    {
        var bounds = new RectD(400, 400, 200, 200);
        var offset = RotatableAnnotationGeometry.RotationHandleOffset(200);

        var upright = RotatableAnnotationGeometry.RotationHandle(bounds, 0, offset);
        Assert.Equal(500, upright.X, 6);
        Assert.Equal(400 - offset, upright.Y, 6);

        var turned = RotatableAnnotationGeometry.RotationHandle(bounds, 90, offset);
        Assert.Equal(600 + offset, turned.X, 6);
        Assert.Equal(500, turned.Y, 6);
    }

    [Fact]
    public void AngleDegrees_IsZeroUpAndClockwisePositive()
    {
        var center = new PointD(500, 500);
        Assert.Equal(0, RotatableAnnotationGeometry.AngleDegrees(center, new PointD(500, 100)), 6);
        Assert.Equal(90, RotatableAnnotationGeometry.AngleDegrees(center, new PointD(900, 500)), 6);
        Assert.Equal(180, Math.Abs(RotatableAnnotationGeometry.AngleDegrees(center, new PointD(500, 900))), 6);
        Assert.Equal(-90, RotatableAnnotationGeometry.AngleDegrees(center, new PointD(100, 500)), 6);
    }

    [Fact]
    public void Contains_FollowsRotationAndPadding()
    {
        var bounds = new RectD(450, 200, 100, 100);
        var point = new PointD(565, 250);

        Assert.False(RotatableAnnotationGeometry.Contains(point, bounds, 0));
        Assert.True(RotatableAnnotationGeometry.Contains(point, bounds, 45));
        Assert.True(RotatableAnnotationGeometry.Contains(point, bounds, 0, padding: 20));
    }

    [Theory]
    [InlineData(540, 180)]
    [InlineData(-540, 180)]
    [InlineData(-190, 170)]
    [InlineData(45, 45)]
    public void NormalizeDegrees_WrapsIntoHalfOpenRange(double input, double expected)
    {
        Assert.Equal(expected, RotatableAnnotationGeometry.NormalizeDegrees(input), 6);
    }

    [Fact]
    public void Snap_RoundsOnlyWhenRequested()
    {
        Assert.Equal(15, RotatableAnnotationGeometry.Snap(17, 15, snap: true));
        Assert.Equal(17, RotatableAnnotationGeometry.Snap(17, 15, snap: false));
    }

    [Theory]
    [InlineData("😀", "😀")]
    [InlineData("abc🚀", "🚀")]
    [InlineData("❤️", "❤️")]
    [InlineData("👍🏽", "👍🏽")]
    [InlineData("1️⃣", "1️⃣")]
    public void ExtractEmoji_ReturnsTrailingEmoji(string input, string expected)
    {
        Assert.Equal(expected, EmojiAnnotationMath.ExtractEmoji(input));
    }

    [Theory]
    [InlineData("7")]
    [InlineData("hello")]
    [InlineData("")]
    [InlineData(null)]
    public void ExtractEmoji_RejectsPlainText(string? input)
    {
        Assert.Null(EmojiAnnotationMath.ExtractEmoji(input));
    }

    [Fact]
    public void PushRecent_MovesToFrontDedupesAndCaps()
    {
        var recent = new List<string> { "🔥", "⭐", "✅" };

        var updated = EmojiAnnotationMath.PushRecent(recent, "⭐");
        Assert.Equal(["⭐", "🔥", "✅"], updated);

        var many = Enumerable.Range(0, 20).Select(i => $"e{i}").ToList();
        Assert.Equal(EmojiAnnotationMath.MaximumRecent, EmojiAnnotationMath.PushRecent(many, "new").Count);
    }
}
