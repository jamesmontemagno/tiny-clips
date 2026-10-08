using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace TinyClips.Core.Studio.Rendering;

/// <summary>An export or poster that could not be made. The message is a sentence a user can read.</summary>
public sealed class StudioExportException : Exception
{
    public StudioExportException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>Small Media Foundation conveniences shared by the Studio readers, the encoder and the exporter.</summary>
internal static class StudioMediaFoundation
{
    public const long TicksPerSecond = StudioRenderingMath.MediaFoundationTicksPerSecond;

    public const int SampleAllocatorEmpty = unchecked((int)0xC00D4A3E);
    public const int SinkNoSamplesProcessed = unchecked((int)0xC00D4A44);
    public const int InvalidStreamNumber = unchecked((int)0xC00D36B3);
    public const int DiskFull = unchecked((int)0x80070070);
    public const int DxgiDeviceRemoved = unchecked((int)0x887A0005);
    public const int DxgiDeviceHung = unchecked((int)0x887A0006);
    public const int DxgiDeviceReset = unchecked((int)0x887A0007);
    public const int D2DRecreateTarget = unchecked((int)0x8899000C);

    /// <summary>MFNominalRange_0_255: full-range RGB.</summary>
    public const uint NominalRangeFull = 1;

    /// <summary>MFNominalRange_16_235: studio-range YUV, what every player assumes for H.264 and HEVC.</summary>
    public const uint NominalRangeLimited = 2;

    /// <summary>
    /// The tallest frame Media Foundation still treats as standard definition. Its converters
    /// take RGB to YUV with the BT.601 matrix up to this height and with BT.709 above it,
    /// whatever the media types say (measured on the hardware, software and WARP paths at
    /// 360 to 1080 lines; width does not matter). A source without colour tags is decoded by the
    /// same rule, which is why an untagged recording of a small window round-trips. On the way
    /// in, a BT.709 tag is honoured at any size, and a BT.601 tag above this height is not: such
    /// a video is decoded as BT.709 and its colours come out up to 24 of 255 off.
    /// </summary>
    public const int StandardDefinitionMaxHeight = 576;

    /// <summary>
    /// The matrix Media Foundation will use to make YUV of a frame this tall. An export is tagged
    /// with it, so the tag is true: a player that reads tags and a player that guesses from the
    /// frame size both get the colours right.
    /// </summary>
    public static VideoTransferMatrix EncodingMatrix(int height) =>
        height <= StandardDefinitionMaxHeight ? VideoTransferMatrix.Bt601 : VideoTransferMatrix.Bt709;

    private const uint MftEnumSync = 0x1;
    private const uint MftEnumAsync = 0x2;
    private const uint MftEnumHardware = 0x4;
    private const uint MftEnumSortAndFilter = 0x40;

    /// <summary>Keeps Media Foundation started for as long as the returned scope lives.</summary>
    public static Scope Startup()
    {
        MediaFactory.MFStartup(true).CheckError();
        return new Scope();
    }

    public static uint? TryGetUInt32(IMFAttributes attributes, Guid key)
    {
        var result = attributes.GetUInt32(key, out var value);
        return result.Success ? value : null;
    }

    public static Guid? TryGetGuid(IMFAttributes attributes, Guid key)
    {
        var result = attributes.GetGUID(key, out var value);
        return result.Success ? value : null;
    }

    public static string? TryGetString(IMFAttributes attributes, Guid key)
    {
        try
        {
            // Vortice returns an empty string, not an error, for some missing string attributes.
            var value = attributes.GetString(key);
            return string.IsNullOrEmpty(value) ? null : value;
        }
        catch (SharpGenException)
        {
            return null;
        }
    }

    /// <summary>
    /// What went wrong, to put in brackets after a sentence a user reads: a COM failure as its
    /// error number, anything else in its own words.
    /// </summary>
    public static string Describe(Exception exception) =>
        exception is SharpGenException ? $"error 0x{(uint)exception.HResult:X8}" : exception.Message.TrimEnd('.', ' ');

    public static bool IsDeviceLost(Exception exception) =>
        exception is StudioDeviceLostException
        || (exception is SharpGenException && exception.HResult is DxgiDeviceRemoved or DxgiDeviceHung or DxgiDeviceReset or D2DRecreateTarget);

    /// <summary>
    /// The name of the first registered encoder of <paramref name="subtype"/>, hardware ones first,
    /// or null when this PC has none.
    /// </summary>
    public static string? FindEncoder(Guid subtype, bool hardware)
    {
        try
        {
            var output = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = subtype };
            var flags = (hardware ? MftEnumHardware : MftEnumSync | MftEnumAsync) | MftEnumSortAndFilter;
            using var collection = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, flags, null, output);
            foreach (var activate in collection)
            {
                return TryGetString(activate, TransformAttributeKeys.MftFriendlyNameAttribute) ?? "(unnamed encoder)";
            }
        }
        catch (SharpGenException)
        {
            // Enumeration only answers "is there one"; the export itself reports real failures.
        }

        return null;
    }

    /// <summary>"AMDh264Encoder" and whether it is a hardware transform, for a transform inside a sink writer.</summary>
    public static (string? Name, bool Hardware) DescribeEncoder(IMFTransform transform)
    {
        try
        {
            using var attributes = transform.Attributes;

            // A hardware transform carries a "hardware URL", and inside a sink writer that is
            // often all the identity it exposes (for example "AMDh264Encoder").
            var url = TryGetString(attributes, TransformAttributeKeys.MftEnumHardwareUrlAttribute);
            var name = TryGetString(attributes, TransformAttributeKeys.MftFriendlyNameAttribute);
            if (name is null && TryGetGuid(attributes, TransformAttributeKeys.MftTransformClsidAttribute) is { } clsid)
            {
                name = FriendlyName(clsid) ?? clsid.ToString("B");
            }

            name ??= url is { Length: > 60 } ? url[..60] : url;
            return (name, url is not null);
        }
        catch (SharpGenException)
        {
            return (null, false);
        }
    }

    private static string? FriendlyName(Guid clsid)
    {
        try
        {
            using var collection = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, MftEnumSync | MftEnumAsync | MftEnumHardware, null, null);
            foreach (var activate in collection)
            {
                if (TryGetGuid(activate, TransformAttributeKeys.MftTransformClsidAttribute) == clsid)
                {
                    return TryGetString(activate, TransformAttributeKeys.MftFriendlyNameAttribute);
                }
            }
        }
        catch (SharpGenException)
        {
        }

        return null;
    }

    public sealed class Scope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                MediaFactory.MFShutdown();
            }
        }
    }
}
