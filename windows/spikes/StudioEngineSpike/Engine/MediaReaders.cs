using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace StudioEngineSpike.Engine;

internal enum VideoOutputFormat
{
    /// <summary>MFVideoFormat_RGB32: BGRX, alpha undefined.</summary>
    Rgb32,

    /// <summary>MFVideoFormat_ARGB32: BGRA.</summary>
    Argb32,

    /// <summary>The decoder's own output, no colour conversion.</summary>
    Nv12,
}

/// <summary>How a <see cref="VideoReader"/> is set up; the export experiments vary these.</summary>
/// <param name="UseDevice">Pass the shared device's DXGI manager (<c>MF_SOURCE_READER_D3D_MANAGER</c>): DXVA decode, samples are textures.</param>
/// <param name="AdvancedProcessing"><c>MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING</c>: the GPU video processor converts to RGB.</param>
/// <param name="OutputWidth">When set, ask the video processor to scale to this size as well.</param>
internal sealed record VideoReaderSettings(
    VideoOutputFormat Format = VideoOutputFormat.Rgb32,
    bool UseDevice = true,
    bool HardwareTransforms = true,
    bool AdvancedProcessing = true,
    int? OutputWidth = null,
    int? OutputHeight = null);

/// <summary>One decoded video frame as the source reader handed it over.</summary>
internal sealed class VideoSample : IDisposable
{
    private VideoSample(IMFSample sample, long time)
    {
        Sample = sample;
        Time = time;
    }

    public IMFSample Sample { get; }

    /// <summary>Presentation time in 100 ns units.</summary>
    public long Time { get; }

    public long Duration { get; private set; }

    /// <summary>The D3D11 texture behind the sample, or null when the frame is in system memory.</summary>
    public ID3D11Texture2D? Texture { get; private set; }

    /// <summary>Which slice of <see cref="Texture"/> holds this frame (decoders hand out texture arrays).</summary>
    public uint Subresource { get; private set; }

    public int BufferCount { get; private set; }

    internal static VideoSample From(IMFSample sample, long time)
    {
        var result = new VideoSample(sample, time) { BufferCount = sample.BufferCount };
        try
        {
            result.Duration = sample.SampleDuration;
        }
        catch (SharpGenException)
        {
            // Some samples carry no duration.
        }

        using var buffer = sample.GetBufferByIndex(0);
        using var dxgi = buffer.QueryInterfaceOrNull<IMFDXGIBuffer>();
        if (dxgi is not null)
        {
            result.Texture = new ID3D11Texture2D(dxgi.GetResource(typeof(ID3D11Texture2D).GUID));
            result.Subresource = dxgi.SubresourceIndex;
        }

        return result;
    }

    public void Dispose()
    {
        Texture?.Dispose();
        Sample.Dispose();
    }
}

/// <summary>Synchronous <c>IMFSourceReader</c> over the first video stream of a file.</summary>
internal sealed class VideoReader : IDisposable
{
    private readonly IMFSourceReader _reader;

    private VideoReader(IMFSourceReader reader, VideoReaderSettings settings)
    {
        _reader = reader;
        Settings = settings;
    }

    public VideoReaderSettings Settings { get; }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public long SamplesRead { get; private set; }

    /// <summary>Stream flags seen so far besides end of stream (format changes, gaps).</summary>
    public SourceReaderFlag FlagsSeen { get; private set; }

    public static VideoReader Open(string path, IMFDXGIDeviceManager? deviceManager, VideoReaderSettings settings)
    {
        using var attributes = MediaFactory.MFCreateAttributes(4);
        if (settings.UseDevice && deviceManager is not null)
        {
            attributes.Set(SourceReaderAttributeKeys.D3DManager, deviceManager);
        }

        if (settings.HardwareTransforms)
        {
            // MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS is shared by the reader and the writer.
            attributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1u);
        }

        if (settings.Format != VideoOutputFormat.Nv12)
        {
            // The advanced (GPU, XVP) processor and the legacy (CPU) one are mutually exclusive.
            attributes.Set(settings.AdvancedProcessing ? SourceReaderAttributeKeys.EnableAdvancedVideoProcessing : SourceReaderAttributeKeys.EnableVideoProcessing, 1u);
        }

        var reader = MediaFactory.MFCreateSourceReaderFromURL(path, attributes);
        try
        {
            reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
            reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);

            using var type = MediaFactory.MFCreateMediaType();
            type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            type.Set(MediaTypeAttributeKeys.Subtype, settings.Format switch
            {
                VideoOutputFormat.Rgb32 => VideoFormatGuids.Rgb32,
                VideoOutputFormat.Argb32 => VideoFormatGuids.Argb32,
                _ => VideoFormatGuids.NV12,
            });
            if (settings.OutputWidth is { } width && settings.OutputHeight is { } height)
            {
                MediaFactory.MFSetAttributeSize(type, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height).CheckError();
            }

            reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, type);
            var result = new VideoReader(reader, settings);
            result.ReadCurrentType();
            return result;
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    /// <summary>The next frame, or null at the end of the stream.</summary>
    public VideoSample? Read()
    {
        while (true)
        {
            var sample = _reader.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None, out _, out var flags, out var time);
            if ((flags & SourceReaderFlag.EndOfStream) != 0)
            {
                sample?.Dispose();
                return null;
            }

            if ((flags & (SourceReaderFlag.CurrentMediaTypeChanged | SourceReaderFlag.NativeMediaTypeChanged)) != 0)
            {
                ReadCurrentType();
            }

            FlagsSeen |= flags;
            if ((flags & SourceReaderFlag.Error) != 0)
            {
                sample?.Dispose();
                throw new InvalidOperationException("The source reader reported an error.");
            }

            if (sample is null)
            {
                // A stream tick (gap); ask again.
                continue;
            }

            SamplesRead++;
            return VideoSample.From(sample, time);
        }
    }

    /// <summary>
    /// Repositions the reader. The next sample is the keyframe at or before <paramref name="time"/>,
    /// not the frame at that time; the caller reads forward and discards (see <see cref="FrameCursor"/>).
    /// </summary>
    public void Seek(long time) => _reader.SetCurrentPosition(time);

    public string DescribeType()
    {
        using var type = _reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
        var subtype = MfHelpers.TryGetGuid(type, MediaTypeAttributeKeys.Subtype) ?? Guid.Empty;
        var name = subtype == VideoFormatGuids.Rgb32 ? "RGB32" : subtype == VideoFormatGuids.Argb32 ? "ARGB32" : subtype == VideoFormatGuids.NV12 ? "NV12" : subtype.ToString();
        var stride = MfHelpers.TryGetUInt32(type, MediaTypeAttributeKeys.DefaultStride);
        MediaFactory.MFGetAttributeRatio(type, MediaTypeAttributeKeys.FrameRate, out var numerator, out var denominator);
        var range = MfHelpers.TryGetUInt32(type, MediaTypeAttributeKeys.VideoNominalRange);
        var matrix = MfHelpers.TryGetUInt32(type, MediaTypeAttributeKeys.YuvMatrix);
        return $"{name} {Width}x{Height}, {numerator}/{denominator} fps, stride {(stride is { } s ? unchecked((int)s).ToString() : "?")}, nominal range {(range?.ToString() ?? "unset")}, YUV matrix {(matrix?.ToString() ?? "unset")}";
    }

    /// <summary>The transforms the reader inserted for the video stream (decoder, video processor).</summary>
    public string DescribePipeline()
    {
        using var extended = _reader.QueryInterfaceOrNull<IMFSourceReaderEx>();
        if (extended is null)
        {
            return "(IMFSourceReaderEx not available)";
        }

        var parts = new List<string>();
        for (var index = 0; index < 8; index++)
        {
            var result = extended.GetTransformForStream((int)SourceReaderIndex.FirstVideoStream, index, out var category, out var transform);
            if (result.Failure || transform is null)
            {
                break;
            }

            using (transform)
            {
                parts.Add(MfHelpers.DescribeTransform(category, transform));
            }
        }

        return parts.Count == 0 ? "(no transforms)" : string.Join(" → ", parts);
    }

    private void ReadCurrentType()
    {
        using var type = _reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
        MediaFactory.MFGetAttributeSize(type, MediaTypeAttributeKeys.FrameSize, out var width, out var height).CheckError();
        Width = (int)width;
        Height = (int)height;
    }

    public void Dispose() => _reader.Dispose();
}

/// <summary>
/// "The frame showing at time t" on top of a forward-only reader: keeps the current and the next
/// sample, advances while the next one has started, and seeks only when asked or when time goes
/// backwards.
/// </summary>
internal sealed class FrameCursor : IDisposable
{
    private readonly VideoReader _reader;
    private VideoSample? _current;
    private VideoSample? _next;
    private bool _currentReturned;
    private bool _primed;
    private bool _ended;

    public FrameCursor(VideoReader reader) => _reader = reader;

    /// <summary>Samples read and thrown away without being shown (after seeks and across cuts).</summary>
    public long Discarded { get; private set; }

    public long Seeks { get; private set; }

    /// <summary>Time of the frame last returned, or -1 before the first one.</summary>
    public long CurrentTime => _current?.Time ?? -1;

    /// <summary>The frame whose time is the latest one not after <paramref name="time"/>, or null before the first frame.</summary>
    /// <remarks>The returned sample stays valid until the next call.</remarks>
    public VideoSample? Get(long time)
    {
        if (_current is not null && time < _current.Time)
        {
            Seek(time);
        }

        if (!_primed)
        {
            _next = _reader.Read();
            _ended = _next is null;
            _primed = true;
        }

        while (_next is not null && _next.Time <= time)
        {
            if (_current is not null && !_currentReturned)
            {
                Discarded++;
            }

            _current?.Dispose();
            _current = _next;
            _currentReturned = false;
            _next = _ended ? null : _reader.Read();
            _ended = _next is null;
        }

        if (_current is not null && _current.Time <= time)
        {
            _currentReturned = true;
            return _current;
        }

        return null;
    }

    /// <summary>Jumps the reader to the keyframe at or before <paramref name="time"/>.</summary>
    public void Seek(long time)
    {
        _current?.Dispose();
        _next?.Dispose();
        _current = null;
        _next = null;
        _currentReturned = false;
        _primed = false;
        _ended = false;
        _reader.Seek(time);
        Seeks++;
    }

    public void Dispose()
    {
        _current?.Dispose();
        _next?.Dispose();
        _current = null;
        _next = null;
    }
}

/// <summary>PCM audio from the first audio stream of a file.</summary>
internal sealed class AudioReader : IDisposable
{
    private readonly IMFSourceReader _reader;

    private AudioReader(IMFSourceReader reader, AudioEncoderSettings format)
    {
        _reader = reader;
        Format = format;
    }

    public AudioEncoderSettings Format { get; }

    public static AudioReader Open(string path, AudioEncoderSettings format)
    {
        var reader = MediaFactory.MFCreateSourceReaderFromURL(path, null!);
        try
        {
            reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
            reader.SetStreamSelection(SourceReaderIndex.FirstAudioStream, true);
            using var type = MediaFactory.MFCreateMediaType();
            type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
            type.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
            type.Set(MediaTypeAttributeKeys.AudioBitsPerSample, (uint)format.BitsPerSample);
            type.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)format.SampleRate);
            type.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)format.Channels);
            type.Set(MediaTypeAttributeKeys.AudioBlockAlignment, (uint)format.BlockAlign);
            type.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(format.BlockAlign * format.SampleRate));
            reader.SetCurrentMediaType(SourceReaderIndex.FirstAudioStream, type);
            return new AudioReader(reader, format);
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    /// <summary>The next block of interleaved PCM and its start time (100 ns units), or null at the end.</summary>
    public (byte[] Pcm, long Time)? Read()
    {
        while (true)
        {
            var sample = _reader.ReadSample(SourceReaderIndex.FirstAudioStream, SourceReaderControlFlag.None, out _, out var flags, out var time);
            if ((flags & SourceReaderFlag.EndOfStream) != 0)
            {
                sample?.Dispose();
                return null;
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
                    var pcm = new byte[length];
                    System.Runtime.InteropServices.Marshal.Copy(pointer, pcm, 0, length);
                    return (pcm, time);
                }
                finally
                {
                    buffer.Unlock();
                }
            }
        }
    }

    public void Seek(long time) => _reader.SetCurrentPosition(time);

    public void Dispose() => _reader.Dispose();
}
