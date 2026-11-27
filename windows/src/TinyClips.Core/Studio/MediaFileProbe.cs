using Vortice.MediaFoundation;

namespace TinyClips.Core.Studio;

public static class MediaFileProbe
{
    public static StudioRecordingSourceInfo Probe(string path, double fallbackFrameRate = 30, bool requireDecodableFrame = false)
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
            using var attributes = MediaFactory.MFCreateAttributes(1);
            if (requireDecodableFrame) attributes.Set(SourceReaderAttributeKeys.EnableVideoProcessing, 1u);
            reader = MediaFactory.MFCreateSourceReaderFromURL(path, attributes);
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

            var rotation = mediaType.GetUInt32(MediaTypeAttributeKeys.VideoRotation, out var degrees).Success ? degrees : 0;
            if (requireDecodableFrame)
            {
                reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
                reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);
                using var decodedType = MediaFactory.MFCreateMediaType();
                decodedType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                decodedType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
                reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, decodedType);
                var decoded = false;
                for (var attempt = 0; attempt < 64; attempt++)
                {
                    using var sample = reader.ReadSample(SourceReaderIndex.FirstVideoStream,
                        SourceReaderControlFlag.None, out _, out var flags, out _);
                    if (sample is not null && sample.BufferCount > 0)
                    {
                        using var buffer = sample.ConvertToContiguousBuffer();
                        decoded = buffer.CurrentLength > 0;
                        if (decoded) break;
                    }
                    if ((flags & (SourceReaderFlag.EndOfStream | SourceReaderFlag.Error)) != 0) break;
                }
                if (!decoded) throw new InvalidDataException("The imported video has no decodable video frames.");
            }
            return new StudioRecordingSourceInfo((int)width, (int)height, durationSeconds, frameRate)
            {
                RotationDegrees = rotation,
            };
        }
        finally
        {
            reader?.Dispose();
            MediaFactory.MFShutdown();
        }
    }
}
