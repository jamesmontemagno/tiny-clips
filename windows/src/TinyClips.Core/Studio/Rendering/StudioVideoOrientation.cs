using System.Numerics;

namespace TinyClips.Core.Studio.Rendering;

public static class StudioVideoOrientation
{
    public static Matrix3x2 Transform(int width, int height, int clockwiseDegrees) => clockwiseDegrees switch
    {
        0 => Matrix3x2.Identity,
        90 => new Matrix3x2(0, 1, -1, 0, height, 0),
        180 => new Matrix3x2(-1, 0, 0, -1, width, height),
        270 => new Matrix3x2(0, -1, 1, 0, 0, width),
        _ => throw new ArgumentOutOfRangeException(nameof(clockwiseDegrees)),
    };
}
