using TinyClips.Core.Studio.Rendering;

namespace TinyClips.Core.Tests;

/// <summary>
/// The parts of finding people in a camera frame that need no model and no graphics device:
/// what a model's shapes have to be, and how a picture goes in and a mask comes out.
/// </summary>
public sealed class StudioPersonFinderTests
{
    [Theory]
    [InlineData(new long[] { 1, 256, 256, 3 }, 256, 256, false)]
    [InlineData(new long[] { 1, 144, 256, 3 }, 256, 144, false)]
    [InlineData(new long[] { 1, 3, 144, 256 }, 256, 144, true)]
    [InlineData(new long[] { -1, 256, 256, 3 }, 256, 256, false)]
    [InlineData(new long[] { 0, 3, 64, 32 }, 32, 64, true)]
    [InlineData(new long[] { 1, 1, 1024, 3 }, 1024, 1, false)]
    public void APictureShape_GivesItsSizeAndWhereTheColoursAre(long[] shape, int width, int height, bool channelsFirst)
    {
        Assert.Equal((width, height, channelsFirst), StudioModelPersonFinder.ReadPictureShape(shape));
    }

    [Theory]
    [InlineData(new long[] { 2, 256, 256, 3 })]
    [InlineData(new long[] { 1, 256, 256, 4 })]
    [InlineData(new long[] { 1, 256, 256, 1 })]
    [InlineData(new long[] { 1, 256, 256 })]
    [InlineData(new long[] { 1, 256, 256, 3, 1 })]
    [InlineData(new long[] { 1, 0, 256, 3 })]
    [InlineData(new long[] { 1, -1, 256, 3 })]
    [InlineData(new long[] { 1, 256, -1, 3 })]
    [InlineData(new long[] { 1, 1025, 256, 3 })]
    [InlineData(new long[] { 1, 3, 256, 1025 })]
    [InlineData(new long[] { })]
    public void AShapeThatIsNoPictureOfAFixedSize_IsRefused(long[] shape)
    {
        Assert.Null(StudioModelPersonFinder.ReadPictureShape(shape));
    }

    [Theory]
    [InlineData(new long[] { 1, 256, 256, 1 }, true)]
    [InlineData(new long[] { 1, 1, 256, 256 }, true)]
    [InlineData(new long[] { 1, 256, 256 }, true)]
    [InlineData(new long[] { -1, 256, 256, 1 }, true)]
    [InlineData(new long[] { 256, 256 }, true)]
    [InlineData(new long[] { 1, 256, 256, 2 }, false)]
    [InlineData(new long[] { 1, 128, 128, 1 }, false)]
    [InlineData(new long[] { 1, -1, 256, 1 }, false)]
    [InlineData(new long[] { 1, 256, 0, 1 }, false)]
    [InlineData(new long[] { 65536 }, false)]
    [InlineData(new long[] { 1, 1, 1, 256, 256 }, false)]
    [InlineData(new long[] { 2, 256, 256, 1 }, false)]
    [InlineData(new long[] { 1, 3037000500, 3037000500, 1 }, false)]
    public void AnOutput_HasToHoldOneValueForEachPixel(long[] shape, bool expected)
    {
        Assert.Equal(expected, StudioModelPersonFinder.HoldsOneValueAPixel(shape, 256, 256));
    }

    [Fact]
    public void AnOutput_IsMeasuredAgainstThePictureItsModelLooksAt()
    {
        Assert.True(StudioModelPersonFinder.HoldsOneValueAPixel([1, 144, 256, 1], 256, 144));
        Assert.False(StudioModelPersonFinder.HoldsOneValueAPixel([1, 144, 256, 1], 256, 256));
    }

    [Fact]
    public void ThePicture_GoesInRedFirst_From0To1_ColoursLast()
    {
        // Two pixels, blue first as the renderer has them. The fourth byte means nothing.
        byte[] bgra = [0, 51, 255, 7, 102, 153, 204, 200];
        var input = new float[6];

        StudioModelPersonFinder.FillInput(bgra, input, width: 2, height: 1, channelsFirst: false);

        Assert.Equal([1f, 0.2f, 0f, 0.8f, 0.6f, 0.4f], input, Near);
    }

    [Fact]
    public void ThePicture_GoesInRedFirst_From0To1_ColoursFirst()
    {
        byte[] bgra = [0, 51, 255, 7, 102, 153, 204, 200];
        var input = new float[6];

        StudioModelPersonFinder.FillInput(bgra, input, width: 1, height: 2, channelsFirst: true);

        // All the red, then all the green, then all the blue.
        Assert.Equal([1f, 0.8f, 0.2f, 0.6f, 0f, 0.4f], input, Near);
    }

    [Fact]
    public void TheMask_IsHowSureTheModelIs_AsBytes()
    {
        float[] values = [-1f, 0f, 0.001f, 0.25f, 0.5f, 0.999f, 1f, 2f, float.NaN, float.PositiveInfinity, float.NegativeInfinity];
        var mask = new byte[values.Length];
        Array.Fill(mask, (byte)77);

        StudioModelPersonFinder.ToMask(values, mask);

        Assert.Equal(new byte[] { 0, 0, 0, 64, 128, 255, 255, 255, 0, 255, 0 }, mask);
    }

    [Fact]
    public void AModelThatIsNotThere_OrIsNoModel_GivesNoFinder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "TinyClipsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Assert.Null(StudioModelPersonFinder.TryCreate(Path.Combine(folder, "missing.onnx")));

            var notAModel = Path.Combine(folder, "text.onnx");
            File.WriteAllText(notAModel, "This is not a model.");
            Assert.Null(StudioModelPersonFinder.TryCreate(notAModel));

            var empty = Path.Combine(folder, "empty.onnx");
            File.WriteAllBytes(empty, []);
            Assert.Null(StudioModelPersonFinder.TryCreate(empty));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void TheAppLooksForItsModelNextToItself()
    {
        Assert.Equal(
            Path.Combine(AppContext.BaseDirectory, "Assets", "Studio", "selfie_segmentation.onnx"),
            StudioPersonFinders.ModelPath);

        // The tests ship no model, so nothing can find people here, and asking is not an error.
        Assert.False(StudioPersonFinders.IsAvailable);
        Assert.Null(StudioPersonFinders.CreateDefault());
    }

    [Theory]
    [InlineData(1280, 720, 25.6)]
    [InlineData(720, 1280, 25.6)]
    [InlineData(1920, 1080, 38.4)]
    [InlineData(640, 640, 12.8)]
    [InlineData(16000, 9000, 250)]
    public void TheBlur_IsTwoPercentOfTheLongerSide_UpToWhatDirect2DTakes(int width, int height, double expected)
    {
        Assert.Equal(expected, StudioSceneRenderer.PeopleBlurDeviation(width, height), 9);
    }

    private static bool Near(float a, float b) => Math.Abs(a - b) < 1e-6f;
}
