using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace TinyClips.Tools.StudioRenderCheck;

/// <summary>What a file says about itself, read from its native stream types.</summary>
internal sealed record MediaInfo(
    int Width,
    int Height,
    uint FrameRateNumerator,
    uint FrameRateDenominator,
    bool IsHevc,
    uint? Primaries,
    uint? Transfer,
    uint? Matrix,
    uint? NominalRange,
    double DurationSeconds,
    bool HasAudio,
    int AudioSampleRate,
    int AudioChannels)
{
    public double FramesPerSecond => FrameRateDenominator == 0 ? 0 : FrameRateNumerator / (double)FrameRateDenominator;

    public string ColorTags => $"primaries {Primaries?.ToString() ?? "unset"}, transfer {Transfer?.ToString() ?? "unset"}, matrix {Matrix?.ToString() ?? "unset"}, range {NominalRange?.ToString() ?? "unset"}";
}

/// <summary>One decoded frame: when it shows, and its picture.</summary>
internal readonly record struct DecodedFrame(int Index, long Time, Picture Picture);

/// <summary>Decoded audio laid out on the timeline by the timestamps the reader reports.</summary>
internal sealed record DecodedAudio(short[] Left, short[] Right, int SampleRate, int Channels, long FirstSample)
{
    public int Length => Left.Length;

    public double DurationSeconds => Left.Length / (double)SampleRate;
}

/// <summary>
/// The tool's own Media Foundation code: it writes the test clips and reads files back, and
/// shares nothing with the exporter. Reading is plain software decoding to NV12 with no Direct3D
/// device, so what comes back is the YUV that is in the file.
/// </summary>
/// <summary>How a test clip is encoded and labelled.</summary>
/// <param name="Hevc">HEVC instead of H.264.</param>
/// <param name="Bt601">Its YUV is made with the BT.601 matrix instead of BT.709.</param>
/// <param name="Tagged">The stream says which primaries, transfer, matrix and range it has.</param>
internal sealed record ClipEncoding(bool Hevc = false, bool Bt601 = false, bool Tagged = true)
{
    public static readonly ClipEncoding Tagged709 = new();

    /// <summary>
    /// What Tiny Clips' recorder writes: no colour tags, and the matrix Media Foundation picks
    /// by frame height, BT.601 up to 576 lines and BT.709 above.
    /// </summary>
    public static ClipEncoding Recorder(int height, bool hevc = false) => new(hevc, height <= 576, Tagged: false);

    public string Describe() => $"{(Hevc ? "HEVC" : "H.264")}, {(Bt601 ? "BT.601" : "BT.709")} {(Tagged ? "tagged" : "untagged")}";
}

internal static class Media
{
    public const int TicksPerSecond = 10_000_000;
    public const int AudioRate = 48000;
    public const int AudioChannels = 2;

    private static readonly Guid CodecApiAvEncMpvDefaultBPictureCount = new("8D390AAC-DC5C-4200-B57F-814D04BABAB2");
    private static readonly Guid CodecApiAvEncMpvGopSize = new("95F31B26-95A4-41AA-9303-246A7FC6EEF1");
    private const int InvalidStreamNumber = unchecked((int)0xC00D36B3);

    public static long FrameTime(long index, uint numerator, uint denominator) =>
        (long)((((Int128)index * TicksPerSecond * denominator) + (numerator / 2)) / numerator);

    /// <summary>
    /// Writes a test clip from limited-range NV12 frames the tool converts itself, so no Media
    /// Foundation colour conversion or row order is involved on the way in.
    /// </summary>
    /// <param name="audio">Interleaved 16-bit PCM, or null for a clip without sound.</param>
    /// <param name="frameTimes">
    /// When each frame starts, in 100 ns units, for a clip whose frames are not evenly spaced (a
    /// recording that dropped frames). One more entry than there are frames: the last is the end.
    /// </param>
    public static void WriteClip(string path, ClipSpec clip, uint numerator, uint denominator, int frameCount, short[]? audio, ClipEncoding? encoding = null, int audioRate = AudioRate, int audioChannels = AudioChannels, long[]? frameTimes = null)
    {
        encoding ??= ClipEncoding.Tagged709;
        if (frameTimes is not null && frameTimes.Length != frameCount + 1)
        {
            throw new ArgumentException("One time for each frame and one for the end.", nameof(frameTimes));
        }
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        var fps = numerator / (double)denominator;
        using var attributes = MediaFactory.MFCreateAttributes(1);
        attributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1u);
        using var writer = MediaFactory.MFCreateSinkWriterFromURL(path, null!, attributes);

        using var videoOut = MediaFactory.MFCreateMediaType();
        videoOut.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        videoOut.Set(MediaTypeAttributeKeys.Subtype, encoding.Hevc ? VideoFormatGuids.Hevc : VideoFormatGuids.H264);
        videoOut.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)Math.Clamp((long)(clip.Width * (double)clip.Height * fps / 8), 4_000_000, 24_000_000));
        videoOut.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);

        // H.264 High, or HEVC Main.
        videoOut.Set(MediaTypeAttributeKeys.Mpeg2Profile, encoding.Hevc ? 1u : 100u);
        SetVideoShape(videoOut, clip, numerator, denominator, encoding);
        var videoStream = writer.AddStream(videoOut);

        using var videoIn = MediaFactory.MFCreateMediaType();
        videoIn.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        videoIn.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
        videoIn.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
        videoIn.Set(MediaTypeAttributeKeys.AllSamplesIndependent, 1u);
        videoIn.Set(MediaTypeAttributeKeys.DefaultStride, (uint)clip.Width);
        SetVideoShape(videoIn, clip, numerator, denominator, encoding);

        using var parameters = MediaFactory.MFCreateAttributes(2);
        parameters.Set(CodecApiAvEncMpvDefaultBPictureCount, 0u);
        parameters.Set(CodecApiAvEncMpvGopSize, (uint)Math.Max(1, (int)Math.Round(fps * 2)));
        writer.SetInputMediaType(videoStream, videoIn, parameters);

        var audioStream = -1;
        if (audio is not null)
        {
            using var audioOut = AacType(audioRate, audioChannels);
            audioStream = writer.AddStream(audioOut);

            using var audioIn = MediaFactory.MFCreateMediaType();
            audioIn.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
            audioIn.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
            audioIn.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
            audioIn.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)audioRate);
            audioIn.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)audioChannels);
            audioIn.Set(MediaTypeAttributeKeys.AudioBlockAlignment, (uint)(audioChannels * 2));
            audioIn.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(audioChannels * 2 * audioRate));
            audioIn.Set(MediaTypeAttributeKeys.AllSamplesIndependent, 1u);
            if (audioChannels == 6)
            {
                // Front left, front right, centre, low frequency, back left, back right.
                audioIn.Set(MediaTypeAttributeKeys.AudioChannelMask, 0x3Fu);
            }

            writer.SetInputMediaType(audioStream, audioIn, null!);
        }

        writer.BeginWriting();

        var frame = clip.DrawNv12Background(encoding.Bt601);
        var audioFrames = audio is null ? 0 : audio.Length / audioChannels;
        var audioCursor = 0;
        for (var index = 0; index < frameCount; index++)
        {
            clip.StampNv12(frame, index);
            var time = frameTimes?[index] ?? FrameTime(index, numerator, denominator);
            var end = frameTimes?[index + 1] ?? FrameTime(index + 1, numerator, denominator);
            using (var sample = MemorySample(MemoryMarshal.AsBytes(frame.AsSpan()), time, end - time))
            {
                writer.WriteSample(videoStream, sample);
            }

            // Sound goes in half a second ahead of the picture, in 1024-sample pieces.
            var target = Math.Min(audioFrames, (int)((end * audioRate / TicksPerSecond) + (audioRate / 2)));
            WriteAudio(writer, audioStream, audio, ref audioCursor, target, audioRate, audioChannels);
        }

        WriteAudio(writer, audioStream, audio, ref audioCursor, audioFrames, audioRate, audioChannels);
        writer.Finalize();
    }

    /// <summary>
    /// The AAC type for a track. 44.1 and 48 kHz in mono or stereo are what every Windows AAC
    /// encoder takes, and are written out here; any other shape is taken from the encoder's own
    /// list, and a PC whose encoder does not have it cannot make that clip.
    /// </summary>
    private static IMFMediaType AacType(int rate, int channels)
    {
        if (rate is 44100 or 48000 && channels is 1 or 2)
        {
            var type = MediaFactory.MFCreateMediaType();
            type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
            type.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
            type.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
            type.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)rate);
            type.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)channels);
            type.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, channels == 1 ? 12000u : 24000u);
            type.Set(MediaTypeAttributeKeys.AacPayloadType, 0u);
            type.Set(MediaTypeAttributeKeys.AacAudioProfileLevelIndication, 0x29u);
            return type;
        }

        const int allEncoders = 0x3F;
        IMFMediaType? best = null;
        uint bestBytes = 0;
        if (MediaFactory.MFTranscodeGetAudioOutputAvailableTypes(AudioFormatGuids.Aac, allEncoders, null!, out var collection).Success && collection is not null)
        {
            using (collection)
            {
                for (var index = 0; index < collection.ElementCount; index++)
                {
                    using var element = (ComObject)collection.GetElement(index);
                    var type = element.QueryInterface<IMFMediaType>();
                    var bytes = UInt32(type, MediaTypeAttributeKeys.AudioAvgBytesPerSecond) ?? 0;

                    // Raw AAC, as an MP4 file holds it, at the highest rate up to 64 kbit/s a channel.
                    if (UInt32(type, MediaTypeAttributeKeys.AudioSamplesPerSecond) == (uint)rate
                        && UInt32(type, MediaTypeAttributeKeys.AudioNumChannels) == (uint)channels
                        && (UInt32(type, MediaTypeAttributeKeys.AacPayloadType) ?? 0) == 0
                        && bytes <= 8000u * (uint)channels
                        && bytes > bestBytes)
                    {
                        best?.Dispose();
                        best = type;
                        bestBytes = bytes;
                    }
                    else
                    {
                        type.Dispose();
                    }
                }
            }
        }

        return best ?? throw new CheckSkippedException($"This PC's AAC encoder does not write {rate} Hz sound with {channels} channels, so the clip that needs it cannot be made.");
    }

    public static MediaInfo Probe(string path)
    {
        using var reader = MediaFactory.MFCreateSourceReaderFromURL(path, null!);
        using var video = reader.GetNativeMediaType(SourceReaderIndex.FirstVideoStream, 0);
        MediaFactory.MFGetAttributeSize(video, MediaTypeAttributeKeys.FrameSize, out var width, out var height).CheckError();
        MediaFactory.MFGetAttributeRatio(video, MediaTypeAttributeKeys.FrameRate, out var numerator, out var denominator);
        video.GetGUID(MediaTypeAttributeKeys.Subtype, out var subtype);

        // For HEVC the size is the coded one (1088 rows for 1080) and the picture is the aperture.
        var area = new byte[16];
        if (video.GetBlob(MediaTypeAttributeKeys.MinimumDisplayAperture, area).Success && BitConverter.ToInt32(area, 8) > 0 && BitConverter.ToInt32(area, 12) > 0)
        {
            width = (uint)BitConverter.ToInt32(area, 8);
            height = (uint)BitConverter.ToInt32(area, 12);
        }

        var duration = 0.0;
        var value = reader.GetPresentationAttribute(SourceReaderIndex.MediaSource, PresentationDescriptionAttributeKeys.Duration).Value;
        if (value is ulong unsigned)
        {
            duration = unsigned / (double)TicksPerSecond;
        }
        else if (value is long signed)
        {
            duration = signed / (double)TicksPerSecond;
        }

        var hasAudio = false;
        var audioRate = 0;
        var audioChannels = 0;
        try
        {
            using var audio = reader.GetNativeMediaType(SourceReaderIndex.FirstAudioStream, 0);
            hasAudio = true;
            audioRate = (int)(UInt32(audio, MediaTypeAttributeKeys.AudioSamplesPerSecond) ?? 0);
            audioChannels = (int)(UInt32(audio, MediaTypeAttributeKeys.AudioNumChannels) ?? 0);
        }
        catch (SharpGenException ex) when (ex.HResult == InvalidStreamNumber)
        {
        }

        return new MediaInfo(
            (int)width,
            (int)height,
            numerator,
            denominator,
            subtype == VideoFormatGuids.Hevc || subtype == VideoFormatGuids.HevcEs,
            UInt32(video, MediaTypeAttributeKeys.VideoPrimaries),
            UInt32(video, MediaTypeAttributeKeys.TransferFunction),
            UInt32(video, MediaTypeAttributeKeys.YuvMatrix),
            UInt32(video, MediaTypeAttributeKeys.VideoNominalRange),
            duration,
            hasAudio,
            audioRate,
            audioChannels);
    }

    /// <summary>
    /// Decodes every video frame in order and hands each to <paramref name="visit"/>, which returns
    /// false to stop early. Returns the number of frames decoded.
    /// </summary>
    /// <param name="chroma">Also keep the chroma plane, for colour checks; luma alone reads the strips.</param>
    public static unsafe int ReadFrames(string path, bool chroma, Func<DecodedFrame, bool> visit)
    {
        using var reader = MediaFactory.MFCreateSourceReaderFromURL(path, null!);
        reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
        reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);
        using (var type = MediaFactory.MFCreateMediaType())
        {
            type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
            reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, type);
        }

        var (codedWidth, codedHeight, width, height, offsetX, offsetY, defaultStride) = OutputShape(reader);

        // Colours are read as a player reads them: with the matrix the stream is tagged with, and
        // for a stream without a tag, BT.601 up to 576 lines and BT.709 above.
        bool bt601;
        using (var native = reader.GetNativeMediaType(SourceReaderIndex.FirstVideoStream, 0))
        {
            bt601 = UInt32(native, MediaTypeAttributeKeys.YuvMatrix) switch
            {
                (uint)VideoTransferMatrix.Bt601 => true,
                (uint)VideoTransferMatrix.Bt709 => false,
                _ => height <= 576,
            };
        }

        var count = 0;
        while (true)
        {
            var sample = reader.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None, out _, out var flags, out var time);
            if ((flags & SourceReaderFlag.Error) != 0)
            {
                sample?.Dispose();
                throw new InvalidOperationException($"The reader reported an error decoding {Path.GetFileName(path)}.");
            }

            if ((flags & (SourceReaderFlag.CurrentMediaTypeChanged | SourceReaderFlag.NativeMediaTypeChanged)) != 0)
            {
                (codedWidth, codedHeight, width, height, offsetX, offsetY, defaultStride) = OutputShape(reader);
            }

            if ((flags & SourceReaderFlag.EndOfStream) != 0)
            {
                sample?.Dispose();
                break;
            }

            if (sample is null)
            {
                continue;
            }

            Picture picture;
            using (sample)
            {
                using var buffer = sample.BufferCount == 1 ? sample.GetBufferByIndex(0) : sample.ConvertToContiguousBuffer();
                var luma = new byte[width * height];
                var chromaPlane = chroma ? new byte[width * (height / 2)] : null;
                using var buffer2D = buffer.QueryInterfaceOrNull<IMF2DBuffer>();
                if (buffer2D is not null)
                {
                    buffer2D.Lock2D(out var scanline0, out var pitch);
                    try
                    {
                        CopyPlanes((byte*)scanline0, pitch, codedHeight, width, height, offsetX, offsetY, luma, chromaPlane);
                    }
                    finally
                    {
                        buffer2D.Unlock2D();
                    }
                }
                else
                {
                    buffer.Lock(out var pointer, out _, out _);
                    try
                    {
                        CopyPlanes((byte*)pointer, defaultStride > 0 ? defaultStride : codedWidth, codedHeight, width, height, offsetX, offsetY, luma, chromaPlane);
                    }
                    finally
                    {
                        buffer.Unlock();
                    }
                }

                picture = Picture.FromNv12(luma, chromaPlane, width, height, bt601);
            }

            if (!visit(new DecodedFrame(count++, time, picture)))
            {
                break;
            }
        }

        return count;
    }

    /// <summary>Decodes the whole first audio track to PCM in the track's own format, or returns null when there is none.</summary>
    public static DecodedAudio? ReadAudio(string path)
    {
        using var reader = MediaFactory.MFCreateSourceReaderFromURL(path, null!);
        reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
        try
        {
            using var native = reader.GetNativeMediaType(SourceReaderIndex.FirstAudioStream, 0);
        }
        catch (SharpGenException ex) when (ex.HResult == InvalidStreamNumber)
        {
            return null;
        }

        reader.SetStreamSelection(SourceReaderIndex.FirstAudioStream, true);
        using (var type = MediaFactory.MFCreateMediaType())
        {
            type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
            type.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
            type.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
            reader.SetCurrentMediaType(SourceReaderIndex.FirstAudioStream, type);
        }

        int rate;
        int channels;
        using (var current = reader.GetCurrentMediaType(SourceReaderIndex.FirstAudioStream))
        {
            rate = (int)(UInt32(current, MediaTypeAttributeKeys.AudioSamplesPerSecond) ?? 0);
            channels = (int)(UInt32(current, MediaTypeAttributeKeys.AudioNumChannels) ?? 0);
        }

        var blocks = new List<(long Start, short[] Pcm)>();
        long first = -1;
        long end = 0;
        long expected = -1;
        while (true)
        {
            var sample = reader.ReadSample(SourceReaderIndex.FirstAudioStream, SourceReaderControlFlag.None, out _, out var flags, out var time);
            if ((flags & SourceReaderFlag.EndOfStream) != 0)
            {
                sample?.Dispose();
                break;
            }

            if (sample is null)
            {
                continue;
            }

            using (sample)
            {
                using var buffer = sample.ConvertToContiguousBuffer();
                buffer.Lock(out var pointer, out _, out var length);
                try
                {
                    var pcm = new short[length / 2];
                    Marshal.Copy(pointer, pcm, 0, pcm.Length);
                    var stamped = (long)Math.Round(time * (double)rate / TicksPerSecond, MidpointRounding.AwayFromZero);
                    var start = expected >= 0 && Math.Abs(stamped - expected) <= 2 ? expected : stamped;
                    expected = start + (pcm.Length / channels);
                    blocks.Add((start, pcm));
                    first = first < 0 ? start : first;
                    end = Math.Max(end, expected);
                }
                finally
                {
                    buffer.Unlock();
                }
            }
        }

        var left = new short[Math.Max(end, 0)];
        var right = new short[left.Length];
        foreach (var (start, pcm) in blocks)
        {
            for (var index = 0; index < pcm.Length / channels; index++)
            {
                var at = start + index;
                if (at >= 0 && at < left.Length)
                {
                    left[at] = pcm[index * channels];
                    right[at] = pcm[(index * channels) + (channels > 1 ? 1 : 0)];
                }
            }
        }

        return new DecodedAudio(left, right, rate, channels, Math.Max(first, 0));
    }

    /// <summary>The names of the encoders this PC has for H.264 or HEVC.</summary>
    /// <param name="hardware">True for hardware encoders only, false for software only, null for both.</param>
    public static List<string> Encoders(bool hevc, bool? hardware)
    {
        var names = new List<string>();
        try
        {
            var output = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = hevc ? VideoFormatGuids.Hevc : VideoFormatGuids.H264 };
            const uint sync = 0x1;
            const uint async = 0x2;
            const uint hardwareFlag = 0x4;
            const uint sortAndFilter = 0x40;
            var flags = sortAndFilter | hardware switch { true => hardwareFlag, false => sync | async, null => sync | async | hardwareFlag };
            using var collection = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, flags, null, output);
            foreach (var activate in collection)
            {
                string? name = null;
                try
                {
                    name = activate.GetString(TransformAttributeKeys.MftFriendlyNameAttribute);
                }
                catch (SharpGenException)
                {
                }

                names.Add(string.IsNullOrEmpty(name) ? "(unnamed)" : name);
            }
        }
        catch (SharpGenException)
        {
        }

        return names;
    }

    public static bool HasEncoder(bool hevc, bool? hardware) => Encoders(hevc, hardware).Count > 0;

    private static void SetVideoShape(IMFMediaType type, ClipSpec clip, uint numerator, uint denominator, ClipEncoding encoding)
    {
        if (encoding.Tagged)
        {
            type.Set(MediaTypeAttributeKeys.VideoPrimaries, (uint)VideoPrimaries.Bt709);
            type.Set(MediaTypeAttributeKeys.TransferFunction, (uint)VideoTransferFunction.Func709);
            type.Set(MediaTypeAttributeKeys.YuvMatrix, (uint)(encoding.Bt601 ? VideoTransferMatrix.Bt601 : VideoTransferMatrix.Bt709));
            type.Set(MediaTypeAttributeKeys.VideoNominalRange, 2u);
        }

        MediaFactory.MFSetAttributeSize(type, MediaTypeAttributeKeys.FrameSize, (uint)clip.Width, (uint)clip.Height).CheckError();
        MediaFactory.MFSetAttributeRatio(type, MediaTypeAttributeKeys.FrameRate, numerator, denominator).CheckError();
        MediaFactory.MFSetAttributeRatio(type, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1).CheckError();
    }

    private static void WriteAudio(IMFSinkWriter writer, int stream, short[]? audio, ref int cursor, int target, int audioRate, int audioChannels)
    {
        if (audio is null || stream < 0)
        {
            return;
        }

        while (cursor < target)
        {
            var count = Math.Min(1024, target - cursor);
            var time = (long)cursor * TicksPerSecond / audioRate;
            var end = (long)(cursor + count) * TicksPerSecond / audioRate;
            using var sample = MemorySample(MemoryMarshal.AsBytes(audio.AsSpan(cursor * audioChannels, count * audioChannels)), time, end - time);
            writer.WriteSample(stream, sample);
            cursor += count;
        }
    }

    private static unsafe IMFSample MemorySample(ReadOnlySpan<byte> data, long time, long duration)
    {
        using var buffer = MediaFactory.MFCreateMemoryBuffer(data.Length);
        buffer.Lock(out var pointer, out _, out _);
        try
        {
            data.CopyTo(new Span<byte>((void*)pointer, data.Length));
        }
        finally
        {
            buffer.Unlock();
        }

        buffer.CurrentLength = data.Length;
        var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = time;
        sample.SampleDuration = duration;
        return sample;
    }

    private static (int CodedWidth, int CodedHeight, int Width, int Height, int OffsetX, int OffsetY, int Stride) OutputShape(IMFSourceReader reader)
    {
        using var type = reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
        MediaFactory.MFGetAttributeSize(type, MediaTypeAttributeKeys.FrameSize, out var codedWidth, out var codedHeight).CheckError();
        var width = (int)codedWidth;
        var height = (int)codedHeight;
        var offsetX = 0;
        var offsetY = 0;
        var area = new byte[16];
        if (type.GetBlob(MediaTypeAttributeKeys.MinimumDisplayAperture, area).Success)
        {
            var x = BitConverter.ToInt16(area, 2);
            var y = BitConverter.ToInt16(area, 6);
            var areaWidth = BitConverter.ToInt32(area, 8);
            var areaHeight = BitConverter.ToInt32(area, 12);
            if (x >= 0 && y >= 0 && areaWidth > 0 && areaHeight > 0 && x + areaWidth <= width && y + areaHeight <= height)
            {
                (offsetX, offsetY, width, height) = (x, y, areaWidth, areaHeight);
            }
        }

        var stride = UInt32(type, MediaTypeAttributeKeys.DefaultStride) is { } value ? unchecked((int)value) : (int)codedWidth;
        return ((int)codedWidth, (int)codedHeight, width, height, offsetX, offsetY, stride);
    }

    private static unsafe void CopyPlanes(byte* scanline0, int pitch, int codedHeight, int width, int height, int offsetX, int offsetY, byte[] luma, byte[]? chroma)
    {
        fixed (byte* target = luma)
        {
            for (var row = 0; row < height; row++)
            {
                Buffer.MemoryCopy(scanline0 + ((long)(row + offsetY) * pitch) + offsetX, target + ((long)row * width), width, width);
            }
        }

        if (chroma is null)
        {
            return;
        }

        // In NV12 the interleaved chroma plane follows the full coded height of luma.
        var chromaStart = scanline0 + ((long)codedHeight * pitch);
        fixed (byte* target = chroma)
        {
            for (var row = 0; row < height / 2; row++)
            {
                Buffer.MemoryCopy(chromaStart + ((long)(row + (offsetY / 2)) * pitch) + (offsetX & ~1), target + ((long)row * width), width, width);
            }
        }
    }

    private static uint? UInt32(IMFAttributes attributes, Guid key)
    {
        var result = attributes.GetUInt32(key, out var value);
        return result.Success ? value : null;
    }
}
