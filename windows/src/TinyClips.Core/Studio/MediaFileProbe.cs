using Vortice.MediaFoundation;

namespace TinyClips.Core.Studio;

public static class MediaFileProbe
{
    public static StudioRecordingSourceInfo Probe(string path, double fallbackFrameRate = 30)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Media file not found.", path);
        }

        MediaFactory.MFStartup(true).CheckError();
        IMFSourceReader? reader = null;
        try
        {
            reader = MediaFactory.MFCreateSourceReaderFromURL(path, null!);
            using var mediaType = reader.GetNativeMediaType(SourceReaderIndex.FirstVideoStream, 0);

            MediaFactory.MFGetAttributeSize(mediaType, MediaTypeAttributeKeys.FrameSize, out var width, out var height).CheckError();

            // A file can hold frames larger than its picture: HEVC keeps 1088 rows for a
            // 1080-line video and says which part is picture. The picture is what gets shown.
            // MFVideoArea is two MFOffset { ushort fraction; short value } and then a SIZE.
            var aperture = new byte[16];
            if (mediaType.GetBlob(MediaTypeAttributeKeys.MinimumDisplayAperture, aperture).Success)
            {
                var apertureWidth = BitConverter.ToInt32(aperture, 8);
                var apertureHeight = BitConverter.ToInt32(aperture, 12);
                if (apertureWidth > 0 && apertureHeight > 0 && apertureWidth <= width && apertureHeight <= height)
                {
                    width = (uint)apertureWidth;
                    height = (uint)apertureHeight;
                }
            }

            var frameRate = fallbackFrameRate;
            if (MediaFactory.MFGetAttributeRatio(mediaType, MediaTypeAttributeKeys.FrameRate, out var numerator, out var denominator).Success &&
                denominator != 0)
            {
                frameRate = numerator / (double)denominator;
            }

            var durationSeconds = 0d;
            var durationVariant = reader.GetPresentationAttribute(SourceReaderIndex.MediaSource, PresentationDescriptionAttributeKeys.Duration);
            if (durationVariant.Value is ulong duration)
            {
                durationSeconds = duration / 10_000_000.0;
            }
            else if (durationVariant.Value is long signedDuration && signedDuration > 0)
            {
                durationSeconds = signedDuration / 10_000_000.0;
            }

            return new StudioRecordingSourceInfo((int)width, (int)height, durationSeconds, frameRate);
        }
        finally
        {
            reader?.Dispose();
            MediaFactory.MFShutdown();
        }
    }
}
