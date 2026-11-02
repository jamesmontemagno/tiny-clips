using System.Numerics;
using TinyClips.Core.Studio.Rendering;

namespace TinyClips.Core.Tests;

public sealed class StudioVideoOrientationTests
{
    [Theory]
    [InlineData(0, 0, 0, 200, 100)]
    [InlineData(90, 100, 0, 0, 200)]
    [InlineData(180, 200, 100, 0, 0)]
    [InlineData(270, 0, 200, 100, 0)]
    public void MapsStoredCornersToUprightPicture(int rotation, float left, float top, float right, float bottom)
    {
        var transform = StudioVideoOrientation.Transform(200, 100, rotation);
        Assert.Equal(new Vector2(left, top), Vector2.Transform(Vector2.Zero, transform));
        Assert.Equal(new Vector2(right, bottom), Vector2.Transform(new Vector2(200, 100), transform));
    }
}
