using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using Windows.AI.MachineLearning;
using WinRT;

namespace TinyClips.Core.Studio.Rendering;

/// <summary>
/// Finds the people in a camera picture, for a camera whose background is blurred or removed
/// (section 6.7 of the project format).
/// </summary>
/// <remarks>
/// A finder belongs to the renderer that made it. The renderer calls it from one thread at a
/// time, with the same picture size every time, and disposes it.
/// </remarks>
public interface IStudioPersonFinder : IDisposable
{
    /// <summary>The width, in pixels, of the picture the finder looks at. The renderer scales every camera frame to it.</summary>
    int Width { get; }

    /// <summary>The height of that picture.</summary>
    int Height { get; }

    /// <summary>Looks for the people in one camera frame.</summary>
    /// <param name="bgra">
    /// The frame scaled to <see cref="Width"/> by <see cref="Height"/>: four bytes a pixel, blue
    /// first, rows from the top, no padding between rows. The fourth byte means nothing.
    /// </param>
    /// <param name="mask">
    /// Receives <see cref="Width"/> × <see cref="Height"/> bytes, rows from the top: 255 where
    /// there is a person, 0 where there is none, and the values between along an edge.
    /// </param>
    /// <returns>False when the frame could not be looked at. It is then drawn with its background kept.</returns>
    bool TryFind(ReadOnlySpan<byte> bgra, Span<byte> mask);
}

/// <summary>The person finder a renderer uses when it is not given one.</summary>
public static class StudioPersonFinders
{
    /// <summary>The file name of the segmentation model.</summary>
    public const string ModelFileName = "selfie_segmentation.onnx";

    /// <summary>A finder that asks for a picture with a side past this is not one for a camera frame.</summary>
    internal const int MaxSide = 1024;

    /// <summary>
    /// Where the model is looked for: next to the app, under <c>Assets\Studio</c>. Without the
    /// file nothing can find people, and a blurred or removed background is drawn as kept.
    /// </summary>
    public static string ModelPath { get; } = Path.Combine(AppContext.BaseDirectory, "Assets", "Studio", ModelFileName);

    /// <summary>
    /// Whether the model file is there. This does not load it, so it is cheap enough to ask
    /// before showing a control; a file that turns out not to be a usable model still leaves
    /// the background kept.
    /// </summary>
    public static bool IsAvailable => File.Exists(ModelPath);

    /// <summary>A finder that runs the model at <see cref="ModelPath"/>, or null when there is none to run.</summary>
    public static IStudioPersonFinder? CreateDefault() => StudioModelPersonFinder.TryCreate(ModelPath);
}

/// <summary>
/// Finds people with a segmentation model in ONNX form, run on the processor by the machine
/// learning API that is part of Windows (<c>Windows.AI.MachineLearning</c>). Nothing is added to
/// the app but the model file.
/// </summary>
/// <remarks>
/// The model has one input, a picture as 32-bit floats from 0 to 1 in red, green, blue order,
/// shaped [1, height, width, 3] or [1, 3, height, width], and one output with one float from 0
/// to 1 for each pixel of that picture: how sure it is that a person is there. MediaPipe's
/// selfie segmentation model is one such.
/// <para>
/// It runs on the processor and not the graphics card: for a model this small the two take
/// about as long, and the processor needs no second graphics device next to the one that is
/// busy decoding, drawing and encoding.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed unsafe partial class StudioModelPersonFinder : IStudioPersonFinder
{
    private const int MaxSide = StudioPersonFinders.MaxSide;

    private static readonly Guid MemoryBufferByteAccessGuid = new("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D");

    private readonly LearningModel _model;
    private readonly LearningModelSession _session;
    private readonly string _inputName;
    private readonly string _outputName;
    private readonly bool _channelsFirst;
    private readonly long[] _inputShape;
    private readonly float[] _input;
    private bool _disposed;

    private StudioModelPersonFinder(LearningModel model, LearningModelSession session, string inputName, string outputName, int width, int height, bool channelsFirst)
    {
        _model = model;
        _session = session;
        _inputName = inputName;
        _outputName = outputName;
        _channelsFirst = channelsFirst;
        Width = width;
        Height = height;
        _inputShape = channelsFirst ? [1, 3, height, width] : [1, height, width, 3];
        _input = new float[width * height * 3];
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>
    /// Loads the model. Null when the file is not there, is not a model Windows can run, or
    /// does not have the one input and one output described on the class.
    /// </summary>
    public static StudioModelPersonFinder? TryCreate(string modelPath)
    {
        ArgumentNullException.ThrowIfNull(modelPath);
        if (!File.Exists(modelPath))
        {
            return null;
        }

        LearningModel? model = null;
        LearningModelSession? session = null;
        try
        {
            model = LearningModel.LoadFromFilePath(Path.GetFullPath(modelPath));
            if (model.InputFeatures.Count != 1
                || model.OutputFeatures.Count != 1
                || model.InputFeatures[0] is not TensorFeatureDescriptor { TensorKind: TensorKind.Float } input
                || model.OutputFeatures[0] is not TensorFeatureDescriptor { TensorKind: TensorKind.Float } output
                || ReadPictureShape([.. input.Shape]) is not { } picture
                || !HoldsOneValueAPixel([.. output.Shape], picture.Width, picture.Height))
            {
                model.Dispose();
                return null;
            }

            session = new LearningModelSession(model, new LearningModelDevice(LearningModelDeviceKind.Cpu));
            return new StudioModelPersonFinder(model, session, input.Name, output.Name, picture.Width, picture.Height, picture.ChannelsFirst);
        }
        catch (Exception)
        {
            // Whatever is wrong with the file, the answer is the same: there is no finder.
            session?.Dispose();
            model?.Dispose();
            return null;
        }
    }

    public bool TryFind(ReadOnlySpan<byte> bgra, Span<byte> mask)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var pixels = Width * Height;
        if (bgra.Length < pixels * 4 || mask.Length < pixels)
        {
            return false;
        }

        FillInput(bgra, _input, Width, Height, _channelsFirst);

        // The tensors are closed as soon as they have been used: each holds its own copy of the
        // picture outside the garbage collector's sight, and thirty a second would pile up.
        using var input = TensorFloat.CreateFromArray(_inputShape, _input);
        var binding = new LearningModelBinding(_session);
        binding.Bind(_inputName, input);
        var result = _session.Evaluate(binding, string.Empty);
        if (!result.Succeeded || !result.Outputs.TryGetValue(_outputName, out var value) || value is not TensorFloat output)
        {
            return false;
        }

        using (output)
        using (var reference = output.CreateReference())
        {
            var referencePointer = ((IWinRTObject)reference).NativeObject.ThisPtr;
            if (Marshal.QueryInterface(referencePointer, in MemoryBufferByteAccessGuid, out var accessPointer) < 0)
            {
                return false;
            }

            try
            {
                var access = ComInterfaceMarshaller<IMemoryBufferByteAccess>.ConvertToManaged((void*)accessPointer)!;
                if (access.GetBuffer(out var data, out var capacity) < 0 || data == 0 || capacity < (uint)pixels * sizeof(float))
                {
                    return false;
                }

                ToMask(new ReadOnlySpan<float>((void*)data, pixels), mask);
                return true;
            }
            finally
            {
                ComInterfaceMarshaller<IMemoryBufferByteAccess>.Free((void*)accessPointer);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session.Dispose();
        _model.Dispose();
    }

    /// <summary>
    /// The picture size a model's input shape asks for, and whether its three colours come
    /// before the rows (channels first) or after the columns. Null for a shape that is not a
    /// picture of a fixed size. The first number is how many pictures at once, which may be
    /// left open (below 1).
    /// </summary>
    internal static (int Width, int Height, bool ChannelsFirst)? ReadPictureShape(ReadOnlySpan<long> shape)
    {
        if (shape.Length != 4 || shape[0] > 1)
        {
            return null;
        }

        var (height, width, channelsFirst) = shape[1] == 3
            ? (shape[2], shape[3], true)
            : shape[3] == 3 ? (shape[1], shape[2], false) : (0, 0, false);
        return width is >= 1 and <= MaxSide && height is >= 1 and <= MaxSide
            ? ((int)width, (int)height, channelsFirst)
            : null;
    }

    /// <summary>
    /// Whether an output of this shape holds exactly one value for each pixel: [1, height,
    /// width, 1], [1, 1, height, width] and [1, height, width] all do.
    /// </summary>
    internal static bool HoldsOneValueAPixel(ReadOnlySpan<long> shape, int width, int height)
    {
        if (shape.Length is < 2 or > 4)
        {
            return false;
        }

        long count = 1;
        for (var index = 0; index < shape.Length; index++)
        {
            // Only the first number, how many pictures at once, may be left open.
            var size = index == 0 && shape[index] < 1 ? 1 : shape[index];
            if (size is < 1 or > MaxSide * MaxSide)
            {
                return false;
            }

            count *= size;
            if (count > MaxSide * MaxSide)
            {
                return false;
            }
        }

        return count == (long)width * height;
    }

    /// <summary>Turns a blue-first picture of bytes into the red-first floats from 0 to 1 the model takes.</summary>
    internal static void FillInput(ReadOnlySpan<byte> bgra, Span<float> input, int width, int height, bool channelsFirst)
    {
        const float Scale = 1f / 255f;
        var pixels = width * height;
        if (channelsFirst)
        {
            var red = input[..pixels];
            var green = input.Slice(pixels, pixels);
            var blue = input.Slice(2 * pixels, pixels);
            for (var index = 0; index < pixels; index++)
            {
                var source = index * 4;
                red[index] = bgra[source + 2] * Scale;
                green[index] = bgra[source + 1] * Scale;
                blue[index] = bgra[source] * Scale;
            }
        }
        else
        {
            for (var index = 0; index < pixels; index++)
            {
                var source = index * 4;
                var target = index * 3;
                input[target] = bgra[source + 2] * Scale;
                input[target + 1] = bgra[source + 1] * Scale;
                input[target + 2] = bgra[source] * Scale;
            }
        }
    }

    /// <summary>Turns how sure the model is, from 0 to 1, into bytes. A value that is no number counts as no person.</summary>
    internal static void ToMask(ReadOnlySpan<float> values, Span<byte> mask)
    {
        for (var index = 0; index < values.Length; index++)
        {
            var value = values[index];
            mask[index] = value >= 1 ? (byte)255 : value > 0 ? (byte)((value * 255f) + 0.5f) : (byte)0;
        }
    }

    /// <summary>
    /// Direct access to a tensor's memory. Classic <c>[ComImport]</c> fails to query this under
    /// CsWinRT, so it is source-generated, as for the camera's bitmaps.
    /// </summary>
    [GeneratedComInterface]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    internal partial interface IMemoryBufferByteAccess
    {
        [PreserveSig]
        int GetBuffer(out nint value, out uint capacity);
    }
}
