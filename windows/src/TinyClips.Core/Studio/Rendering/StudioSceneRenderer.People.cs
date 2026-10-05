using System.Numerics;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.DXGI;
using Vortice.Mathematics;
using D2DAlphaMode = Vortice.DCommon.AlphaMode;
using D2DInterpolationMode = Vortice.Direct2D1.InterpolationMode;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;

namespace TinyClips.Core.Studio.Rendering;

/// <summary>
/// The camera with its background blurred or removed (the person cutout of section 6.7): the
/// camera frame is put together anew, at its own size, from the frame and a picture of where
/// the people in it are, and that takes the frame's place in the layer.
/// </summary>
public sealed partial class StudioSceneRenderer
{
    /// <summary>
    /// How far everything but the people is blurred: the Gaussian's standard deviation as a
    /// share of the longer side of the camera frame.
    /// </summary>
    internal const double PeopleBlurShare = 0.02;

    /// <summary>The widest blur Direct2D's Gaussian takes, in pixels.</summary>
    private const double MaxPeopleBlur = 250;

    private readonly Func<IStudioPersonFinder?>? _personFinderFactory;
    private IStudioPersonFinder? _personFinder;
    private bool _personFinderAsked;

    /// <summary>The camera frame at the finder's size: where it is drawn, where the processor reads it, and its bytes.</summary>
    private ID2D1Bitmap1? _peopleInput;
    private ID2D1Bitmap1? _peopleRead;
    private byte[] _peoplePixels = [];

    /// <summary>Where the people are: as the finder gives it, and as a picture whose every channel is that value.</summary>
    private byte[] _peopleMask = [];
    private byte[] _peopleMaskPixels = [];
    private ID2D1Bitmap1? _peopleMaskBitmap;
    private ID2D1BitmapBrush1? _peopleMaskBrush;

    /// <summary>The camera frame with its background blurred or gone, and the brush that paints it.</summary>
    private ID2D1Bitmap1? _peoplePicture;
    private ID2D1BitmapBrush1? _peopleBrush;
    private GaussianBlur? _peopleBlur;
    private PeopleKey _peopleKey;

    /// <summary>
    /// How many camera frames the people have been looked for in. A frame that names itself
    /// (<see cref="StudioGpuVideoFrame.Stamp"/>) is looked at once however often it is drawn.
    /// </summary>
    public int PeopleSearchCount { get; private set; }

    /// <summary>
    /// The camera frame with everything but the people blurred or taken away, as a brush in the
    /// frame's own pixels, or null when the people could not be found. The layer is then drawn
    /// as if nothing had been asked for.
    /// </summary>
    private ID2D1BitmapBrush1? PreparePeople(ID2D1Bitmap1 picture, in StudioGpuVideoFrame frame, StudioCameraCutout cutout)
    {
        var key = new PeopleKey(frame.Texture.NativePointer, frame.Subresource, frame.Stamp, frame.Width, frame.Height, cutout);
        if (frame.Stamp != 0 && key == _peopleKey && _peopleBrush is not null)
        {
            return _peopleBrush;
        }

        _peopleKey = default;
        if (GetPersonFinder() is not { } finder || !FindPeople(finder, picture, frame.Width, frame.Height))
        {
            return null;
        }

        ComposePeople(picture, frame.Width, frame.Height, cutout, finder.Width, finder.Height);
        _peopleKey = key;
        return _peopleBrush;
    }

    /// <summary>The finder, made the first time one is needed. Null from then on when there is none.</summary>
    private IStudioPersonFinder? GetPersonFinder()
    {
        if (_personFinderAsked)
        {
            return _personFinder;
        }

        _personFinderAsked = true;
        IStudioPersonFinder? finder = null;
        try
        {
            finder = _personFinderFactory is null ? StudioPersonFinders.CreateDefault() : _personFinderFactory();
            if (finder is { Width: >= 1 and <= StudioPersonFinders.MaxSide, Height: >= 1 and <= StudioPersonFinders.MaxSide })
            {
                _personFinder = finder;
                finder = null;
            }
        }
        catch (Exception)
        {
            // Without a finder the background is kept, which is what the format asks for.
        }
        finally
        {
            finder?.Dispose();
        }

        return _personFinder;
    }

    /// <summary>
    /// Scales the camera frame to the finder's size, hands it over, and keeps what comes back
    /// as a bitmap. False when the finder could not look at the frame.
    /// </summary>
    private unsafe bool FindPeople(IStudioPersonFinder finder, ID2D1Bitmap1 picture, int width, int height)
    {
        var maskWidth = finder.Width;
        var maskHeight = finder.Height;
        if (_peopleInput is null || _peopleInput.PixelSize.Width != maskWidth || _peopleInput.PixelSize.Height != maskHeight)
        {
            ForgetPeopleInput();
            var format = new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied);
            _peopleInput = CreateLayerBitmap(maskWidth, maskHeight);
            _peopleRead = _context.CreateBitmap(new SizeI(maskWidth, maskHeight), nint.Zero, 0, new BitmapProperties1(format, 96, 96, BitmapOptions.CpuRead | BitmapOptions.CannotDraw));
            _peopleMaskBitmap = _context.CreateBitmap(new SizeI(maskWidth, maskHeight), nint.Zero, 0, new BitmapProperties1(format, 96, 96, BitmapOptions.None));

            // The mask is far smaller than the frame. A cubic filter keeps its edge smooth when it is laid over the frame.
            _peopleMaskBrush = _context.CreateBitmapBrush(_peopleMaskBitmap, new BitmapBrushProperties1(ExtendMode.Clamp, ExtendMode.Clamp, D2DInterpolationMode.Cubic), null);
            _peoplePixels = new byte[maskWidth * maskHeight * 4];
            _peopleMask = new byte[maskWidth * maskHeight];
            _peopleMaskPixels = new byte[maskWidth * maskHeight * 4];
        }

        // The whole frame at the finder's size, filtered on the way down.
        BeginInto(_peopleInput);
        var drawn = false;
        try
        {
            _context.DrawBitmap(picture, new RawRectF(0, 0, maskWidth, maskHeight), 1f, D2DInterpolationMode.HighQualityCubic, new RawRectF(0, 0, width, height), null);
            drawn = true;
        }
        finally
        {
            EndInto(drawn);
        }

        _peopleRead!.CopyFromBitmap(_peopleInput).CheckError();
        var mapped = _peopleRead.Map(MapOptions.Read);
        try
        {
            var rowBytes = maskWidth * 4;
            fixed (byte* pixels = _peoplePixels)
            {
                for (var row = 0; row < maskHeight; row++)
                {
                    Buffer.MemoryCopy((byte*)mapped.Bits + ((long)row * mapped.Pitch), pixels + ((long)row * rowBytes), rowBytes, rowBytes);
                }
            }
        }
        finally
        {
            _peopleRead.Unmap();
        }

        PeopleSearchCount++;
        bool found;
        try
        {
            found = finder.TryFind(_peoplePixels, _peopleMask);
        }
        catch (Exception)
        {
            // One frame the finder cannot look at is drawn with its background kept.
            found = false;
        }

        if (!found)
        {
            return false;
        }

        // Premultiplied, so every channel carries the mask's value and the bitmap serves as an opacity brush.
        for (var index = 0; index < _peopleMask.Length; index++)
        {
            var value = _peopleMask[index];
            var target = index * 4;
            _peopleMaskPixels[target] = value;
            _peopleMaskPixels[target + 1] = value;
            _peopleMaskPixels[target + 2] = value;
            _peopleMaskPixels[target + 3] = value;
        }

        _peopleMaskBitmap!.CopyFromMemory(_peopleMaskPixels, (uint)(maskWidth * 4)).CheckError();
        return true;
    }

    /// <summary>
    /// Puts the frame together again at its own size: first everything blurred, or nothing at
    /// all, and then the people as they are on top of that.
    /// </summary>
    private void ComposePeople(ID2D1Bitmap1 picture, int width, int height, StudioCameraCutout cutout, int maskWidth, int maskHeight)
    {
        if (_peoplePicture is null || _peoplePicture.PixelSize.Width != width || _peoplePicture.PixelSize.Height != height)
        {
            _peopleBrush?.Dispose();
            _peopleBrush = null;
            _peoplePicture?.Dispose();
            _peoplePicture = null;
            _peoplePicture = CreateLayerBitmap(width, height);
            _peopleBrush = _context.CreateBitmapBrush(_peoplePicture, new BitmapBrushProperties1(ExtendMode.Clamp, ExtendMode.Clamp, D2DInterpolationMode.Linear), null);
        }

        BeginInto(_peoplePicture);
        var drawn = false;
        try
        {
            if (cutout == StudioCameraCutout.Blur)
            {
                // A hard border keeps the blurred frame the size of the frame, and opaque to its edges.
                _peopleBlur ??= new GaussianBlur(_context) { BorderMode = BorderMode.Hard, Optimization = GaussianBlurOptimization.Balanced };
                _peopleBlur.SetInput(0, picture, true);
                _peopleBlur.StandardDeviation = (float)PeopleBlurDeviation(width, height);
                _context.DrawImage(_peopleBlur, D2DInterpolationMode.NearestNeighbor, CompositeMode.SourceCopy);
            }
            else
            {
                _context.Clear(Transparent);
            }

            _peopleMaskBrush!.Transform = Matrix3x2.CreateScale((float)width / maskWidth, (float)height / maskHeight);
            var parameters = new LayerParameters1
            {
                ContentBounds = new RawRectF(0, 0, width, height),
                GeometricMask = null,
                MaskAntialiasMode = AntialiasMode.PerPrimitive,
                MaskTransform = Matrix3x2.Identity,
                Opacity = 1,
                OpacityBrush = _peopleMaskBrush,
                LayerOptions = LayerOptions1.None,
            };
            _context.PushLayer(parameters, null!);
            try
            {
                _context.DrawBitmap(picture, new RawRectF(0, 0, width, height), 1f, D2DInterpolationMode.NearestNeighbor, new RawRectF(0, 0, width, height), null);
            }
            finally
            {
                _context.PopLayer();
            }

            drawn = true;
        }
        finally
        {
            try
            {
                EndInto(drawn);
            }
            finally
            {
                // The effect would otherwise hold on to a decoder's texture after ForgetSources.
                _peopleBlur?.SetInput(0, null!, true);
            }
        }
    }

    /// <summary>The standard deviation, in camera pixels, of the blur for a frame of this size.</summary>
    internal static double PeopleBlurDeviation(int width, int height) =>
        Math.Min(MaxPeopleBlur, PeopleBlurShare * Math.Max(width, height));

    /// <summary>Makes <paramref name="target"/> the thing drawn into until <see cref="EndInto"/>.</summary>
    private void BeginInto(ID2D1Image target)
    {
        _context.Target = target;
        try
        {
            _context.BeginDraw();
            _context.Transform = Matrix3x2.Identity;
        }
        catch
        {
            _context.Target = null;
            throw;
        }
    }

    private void EndInto(bool drawn)
    {
        try
        {
            if (drawn)
            {
                _context.Transform = Matrix3x2.Identity;
                _context.EndDraw().CheckError();
            }
            else
            {
                EndDrawQuietly();
            }
        }
        finally
        {
            _context.Target = null;
        }
    }

    /// <summary>
    /// Forgets which frame the kept picture was made from. A source texture that is released
    /// can come back at the same address with another picture in it.
    /// </summary>
    private void ForgetPeopleFrame() => _peopleKey = default;

    private void ForgetPeopleInput()
    {
        _peopleMaskBrush?.Dispose();
        _peopleMaskBrush = null;
        _peopleMaskBitmap?.Dispose();
        _peopleMaskBitmap = null;
        _peopleRead?.Dispose();
        _peopleRead = null;
        _peopleInput?.Dispose();
        _peopleInput = null;
    }

    private void DisposePeople()
    {
        ForgetPeopleFrame();
        ForgetPeopleInput();
        _peopleBlur?.Dispose();
        _peopleBlur = null;
        _peopleBrush?.Dispose();
        _peopleBrush = null;
        _peoplePicture?.Dispose();
        _peoplePicture = null;
        _personFinder?.Dispose();
        _personFinder = null;
    }

    private readonly record struct PeopleKey(nint Texture, uint Subresource, long Stamp, int Width, int Height, StudioCameraCutout Cutout);
}
