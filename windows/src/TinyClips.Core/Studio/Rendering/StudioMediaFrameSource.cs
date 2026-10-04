using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.MediaFoundation;

namespace TinyClips.Core.Studio.Rendering;

/// <summary>
/// The video of one source file as "the frame showing at time t", on top of a synchronous
/// <c>IMFSourceReader</c>. With the shared device the reader decodes on the GPU and converts to
/// RGB32 there, and the frame is the reader's own texture, wrapped by the renderer without a copy.
/// Without a usable device it decodes in software and each frame is uploaded.
/// </summary>
/// <remarks>
/// Requests normally move forward in time, and the reader just decodes forward. It seeks only
/// when the next request is more than a second ahead (a seek restarts from the keyframe before
/// the target, so across a short gap decoding through is faster) or behind what it holds.
/// Frames keep the times the file gives them, which need not be evenly spaced: a recording
/// that dropped frames has gaps, and a frame shows until the next one starts.
/// A returned frame stays valid until the next call. Not thread-safe: the export loop owns it.
/// </remarks>
internal sealed class StudioVideoSource : IDisposable
{
    internal const long SeekThresholdTicks = StudioMediaFoundation.TicksPerSecond;
    private const int InvalidPosition = unchecked((int)0xC00D36E5);

    private readonly IMFSourceReader _reader;
    private readonly StudioGraphicsDevice _graphics;
    private readonly string _description;
    private Held? _current;
    private Held? _next;
    private bool _primed;
    private bool _ended;
    private ID3D11Texture2D? _ownTexture;
    private Format _ownTextureFormat;
    private Held? _ownTextureHolds;
    private byte[]? _scratch;
    private int _codedHeight;
    private int _offsetX;
    private int _offsetY;
    private int _stride;
    private uint _seenTextureWidth;
    private uint _seenTextureHeight;

    private StudioVideoSource(IMFSourceReader reader, StudioGraphicsDevice graphics, string description, bool usesDevice)
    {
        _reader = reader;
        _graphics = graphics;
        _description = description;
        UsesDevice = usesDevice;
    }

    /// <summary>The visible frame size in pixels.</summary>
    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>The length of the file in 100 ns units, or 0 when it does not say.</summary>
    public long DurationTicks { get; private set; }

    /// <summary>Whether the reader was given the Direct3D device (GPU decode) or decodes in software.</summary>
    public bool UsesDevice { get; }

    public long Seeks { get; private set; }

    public long FramesRead { get; private set; }

    /// <summary>What the decoder hands out, for diagnostics: "1920×1088 B8G8R8X8_UNorm texture", or "system memory".</summary>
    public string FrameStorage { get; private set; } = "nothing yet";

    /// <summary>Whether the reader's video processor was told to leave frame times as the file has them.</summary>
    public bool KeepsFrameTimes { get; private init; }

    /// <summary>Whether the picture is copied out of a texture larger than it before it is drawn.</summary>
    public bool CopiesPicture { get; private set; }

    /// <param name="description">What the file is, for messages: "screen recording" or "camera recording".</param>
    public static StudioVideoSource Open(string path, string description, StudioGraphicsDevice graphics, IMFDXGIDeviceManager? deviceManager)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"The {description} is missing: {path}", path);
        }

        try
        {
            try
            {
                return OpenCore(path, description, graphics, deviceManager);
            }
            catch (SharpGenException) when (deviceManager is not null)
            {
                // The device cannot decode or convert this file; software can still try.
                return OpenCore(path, description, graphics, null);
            }
        }
        catch (SharpGenException ex) when (!StudioMediaFoundation.IsDeviceLost(ex))
        {
            throw new StudioExportException($"The {description} could not be opened as a video: {Path.GetFileName(path)}.", ex);
        }
    }

    /// <summary>
    /// The frame showing at <paramref name="time"/> (100 ns units): the latest one that starts at
    /// or before it. Before the first frame that is the first frame, and after the last, the last.
    /// </summary>
    public StudioGpuVideoFrame GetFrame(long time, CancellationToken cancellationToken)
    {
        try
        {
            if (_current is not null && time < _current.Time)
            {
                Seek(time);
            }
            else if (!_ended && time - (_next?.Time ?? _current?.Time ?? 0) > SeekThresholdTicks)
            {
                Seek(time);
            }

            if (!_primed)
            {
                _next = Read();
                _ended = _next is null;
                _primed = true;
            }

            while (_next is not null && (_next.Time <= time || _current is null))
            {
                cancellationToken.ThrowIfCancellationRequested();
                _current?.Dispose();
                _current = _next;
                _next = _ended ? null : Read();
                _ended = _next is null;
            }

            if (_current is null)
            {
                throw new StudioExportException($"The {_description} has no video frames.");
            }

            return ToFrame(_current);
        }
        catch (SharpGenException ex) when (!StudioMediaFoundation.IsDeviceLost(ex))
        {
            throw new StudioExportException($"The {_description} could not be decoded.", ex);
        }
    }

    public void Dispose()
    {
        _current?.Dispose();
        _next?.Dispose();
        _current = null;
        _next = null;
        _ownTexture?.Dispose();
        _ownTexture = null;
        _reader.Dispose();
    }

    private static StudioVideoSource OpenCore(string path, string description, StudioGraphicsDevice graphics, IMFDXGIDeviceManager? deviceManager)
    {
        using var attributes = MediaFactory.MFCreateAttributes(3);
        if (deviceManager is not null)
        {
            attributes.Set(SourceReaderAttributeKeys.D3DManager, deviceManager);

            // MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS is shared by the reader and the writer.
            attributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1u);
        }

        // The video processor converts the decoder's NV12 to RGB32: on the GPU when the reader
        // has the device, which is what makes its output an ordinary texture Direct2D can wrap.
        attributes.Set(SourceReaderAttributeKeys.EnableAdvancedVideoProcessing, 1u);

        var reader = MediaFactory.MFCreateSourceReaderFromURL(path, attributes);
        try
        {
            reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
            reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);

            using var type = MediaFactory.MFCreateMediaType();
            type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);

            // A decoder that pads its frames (HEVC decodes 1080 lines into 1088) says where the
            // picture is with an aperture. Left to choose, the reader converts into a frame of
            // the padded size and letterboxes the picture inside it: black rows above and below.
            // Asked for a frame the size of the picture, it delivers the picture.
            if (PaddedPictureSize(reader) is { } picture)
            {
                MediaFactory.MFSetAttributeSize(type, MediaTypeAttributeKeys.FrameSize, picture.Width, picture.Height).CheckError();
            }

            reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, type);
            var keepsFrameTimes = KeepFrameTimes(reader);

            var source = new StudioVideoSource(reader, graphics, description, deviceManager is not null) { KeepsFrameTimes = keepsFrameTimes };
            source.ReadCurrentType();
            source.DurationTicks = ReadDuration(reader);
            return source;
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Turns off frame rate conversion in the reader's video processor. Left on, the processor
    /// re-times what it delivers onto an even grid at the file's average frame rate, so a
    /// recording that dropped frames (the recorder stamps frames with the wall clock) comes out
    /// with other frames at other times than the file has.
    /// </summary>
    /// <returns>Whether a video processor was found and told.</returns>
    private static bool KeepFrameTimes(IMFSourceReader reader)
    {
        using var extended = reader.QueryInterfaceOrNull<IMFSourceReaderEx>();
        if (extended is null)
        {
            return false;
        }

        var told = false;
        for (var index = 0; index < 16; index++)
        {
            if (extended.GetTransformForStream((int)SourceReaderIndex.FirstVideoStream, index, out var category, out var transform).Failure || transform is null)
            {
                break;
            }

            using (transform)
            {
                if (category == TransformCategoryGuids.VideoProcessor)
                {
                    try
                    {
                        using var attributes = transform.Attributes;
                        told |= attributes.Set(MediaAttributeKeys.XvpDisableFrcGuid, 1u).Success;
                    }
                    catch (SharpGenException)
                    {
                        // A processor that keeps no attributes has no such switch.
                    }
                }
            }
        }

        return told;
    }

    /// <summary>The size of the picture when the file's frames are larger than it, otherwise null.</summary>
    private static (uint Width, uint Height)? PaddedPictureSize(IMFSourceReader reader)
    {
        try
        {
            using var native = reader.GetNativeMediaType(SourceReaderIndex.FirstVideoStream, 0);
            if (MediaFactory.MFGetAttributeSize(native, MediaTypeAttributeKeys.FrameSize, out var width, out var height).Failure)
            {
                return null;
            }

            // MFVideoArea: two MFOffset { ushort fract; short value }, then a SIZE.
            var area = new byte[16];
            if (native.GetBlob(MediaTypeAttributeKeys.MinimumDisplayAperture, area).Failure)
            {
                return null;
            }

            var x = BitConverter.ToInt16(area, 2);
            var y = BitConverter.ToInt16(area, 6);
            var areaWidth = BitConverter.ToInt32(area, 8);
            var areaHeight = BitConverter.ToInt32(area, 12);
            var valid = x >= 0 && y >= 0 && areaWidth > 0 && areaHeight > 0 && x + areaWidth <= width && y + areaHeight <= height;
            return valid && (areaWidth != width || areaHeight != height) ? ((uint)areaWidth, (uint)areaHeight) : null;
        }
        catch (SharpGenException)
        {
            return null;
        }
    }

    internal static long ReadDuration(IMFSourceReader reader)
    {
        try
        {
            var value = reader.GetPresentationAttribute(SourceReaderIndex.MediaSource, PresentationDescriptionAttributeKeys.Duration).Value;
            return value switch
            {
                ulong unsigned => (long)Math.Min(unsigned, long.MaxValue),
                long signed => Math.Max(0, signed),
                _ => 0,
            };
        }
        catch (SharpGenException)
        {
            return 0;
        }
    }

    private void ReadCurrentType()
    {
        using var type = _reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
        MediaFactory.MFGetAttributeSize(type, MediaTypeAttributeKeys.FrameSize, out var width, out var height).CheckError();
        Width = (int)width;
        Height = (int)height;
        _codedHeight = (int)height;
        _offsetX = 0;
        _offsetY = 0;

        // A decoder may hand out frames padded to whole macroblocks (1088 rows for 1080) and say
        // which part is picture. MFVideoArea: two MFOffset { ushort fract; short value }, then a SIZE.
        var area = new byte[16];
        if (type.GetBlob(MediaTypeAttributeKeys.MinimumDisplayAperture, area).Success)
        {
            var x = BitConverter.ToInt16(area, 2);
            var y = BitConverter.ToInt16(area, 6);
            var areaWidth = BitConverter.ToInt32(area, 8);
            var areaHeight = BitConverter.ToInt32(area, 12);
            if (x >= 0 && y >= 0 && areaWidth > 0 && areaHeight > 0 && x + areaWidth <= Width && y + areaHeight <= Height)
            {
                _offsetX = x;
                _offsetY = y;
                Width = areaWidth;
                Height = areaHeight;
            }
        }

        // Negative for a bottom-up memory buffer.
        _stride = StudioMediaFoundation.TryGetUInt32(type, MediaTypeAttributeKeys.DefaultStride) is { } stride ? unchecked((int)stride) : (int)width * 4;
    }

    private Held? Read()
    {
        while (true)
        {
            var sample = _reader.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None, out _, out var flags, out var time);
            if ((flags & SourceReaderFlag.Error) != 0)
            {
                sample?.Dispose();
                throw new StudioExportException($"The {_description} could not be decoded.");
            }

            if ((flags & (SourceReaderFlag.CurrentMediaTypeChanged | SourceReaderFlag.NativeMediaTypeChanged)) != 0)
            {
                // The reader may have built a new video processor for the new format.
                ReadCurrentType();
                KeepFrameTimes(_reader);
            }

            if ((flags & SourceReaderFlag.EndOfStream) != 0)
            {
                sample?.Dispose();
                return null;
            }

            if (sample is null)
            {
                // A stream tick marks a gap; the next read has the next frame.
                continue;
            }

            FramesRead++;
            return Held.From(sample, time);
        }
    }

    /// <summary>
    /// Repositions the reader. The next frame it delivers is the keyframe at or before the target,
    /// and <see cref="GetFrame"/> decodes forward from there.
    /// </summary>
    private void Seek(long time)
    {
        _current?.Dispose();
        _next?.Dispose();
        _current = null;
        _next = null;
        _ownTextureHolds = null;
        _primed = false;
        _ended = false;

        // A position past the end is refused (MF_E_INVALID_POSITION), so stay inside the file.
        var target = Math.Max(0, DurationTicks > 0 ? Math.Min(time, DurationTicks - 1) : time);
        try
        {
            _reader.SetCurrentPosition(target);
            Seeks++;
        }
        catch (SharpGenException ex) when (ex.HResult == InvalidPosition)
        {
            // The reader stays where it was, and decoding forward from there still arrives.
        }
    }

    private StudioGpuVideoFrame ToFrame(Held held)
    {
        if (held.Texture is { } texture)
        {
            var description = texture.Description;
            if (description.Width != _seenTextureWidth || description.Height != _seenTextureHeight)
            {
                _seenTextureWidth = description.Width;
                _seenTextureHeight = description.Height;
                FrameStorage = $"{description.Width}×{description.Height} {description.Format} texture{(description.ArraySize > 1 ? $" array of {description.ArraySize}" : string.Empty)}";
            }

            if (description.Width == Width && description.Height == Height && _offsetX == 0 && _offsetY == 0)
            {
                return new StudioGpuVideoFrame(texture, held.Subresource, Width, Height);
            }

            // Padded: copy the picture out, so that sampling at its edge never reaches the padding.
            CopiesPicture = true;
            var own = OwnTexture(description.Format);
            if (!ReferenceEquals(_ownTextureHolds, held))
            {
                lock (_graphics.Gate)
                {
                    _graphics.Context.CopySubresourceRegion(own, 0, 0, 0, 0, texture, held.Subresource, new Box(_offsetX, _offsetY, 0, _offsetX + Width, _offsetY + Height, 1));
                }

                _ownTextureHolds = held;
            }

            return new StudioGpuVideoFrame(own, 0, Width, Height);
        }

        FrameStorage = "system memory";
        var uploaded = OwnTexture(Format.B8G8R8X8_UNorm);
        if (!ReferenceEquals(_ownTextureHolds, held))
        {
            Upload(held, uploaded);
            _ownTextureHolds = held;
        }

        return new StudioGpuVideoFrame(uploaded, 0, Width, Height);
    }

    private ID3D11Texture2D OwnTexture(Format format)
    {
        if (_ownTexture is not null)
        {
            var description = _ownTexture.Description;
            if (description.Width == Width && description.Height == Height && _ownTextureFormat == format)
            {
                return _ownTexture;
            }

            _ownTexture.Dispose();
            _ownTexture = null;
        }

        _ownTextureHolds = null;
        _ownTextureFormat = format;
        _ownTexture = _graphics.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        });
        return _ownTexture;
    }

    private unsafe void Upload(Held held, ID3D11Texture2D target)
    {
        using var buffer = held.Sample.BufferCount == 1 ? held.Sample.GetBufferByIndex(0) : held.Sample.ConvertToContiguousBuffer();
        using var buffer2D = buffer.QueryInterfaceOrNull<IMF2DBuffer>();
        if (buffer2D is not null)
        {
            // Scanline 0 is the top row whichever way up the buffer is stored.
            buffer2D.Lock2D(out var scanline0, out var pitch);
            try
            {
                UploadRows((byte*)scanline0, pitch, target);
            }
            finally
            {
                buffer2D.Unlock2D();
            }

            return;
        }

        buffer.Lock(out var pointer, out _, out var length);
        try
        {
            var rowBytes = Math.Abs(_stride);
            if (rowBytes < (_offsetX + Width) * 4 || length < (long)rowBytes * _codedHeight)
            {
                throw new StudioExportException($"The {_description} decoded to a frame of an unexpected size.");
            }

            var top = _stride >= 0 ? (byte*)pointer : (byte*)pointer + ((long)rowBytes * (_codedHeight - 1));
            UploadRows(top, _stride, target);
        }
        finally
        {
            buffer.Unlock();
        }
    }

    private unsafe void UploadRows(byte* scanline0, int pitch, ID3D11Texture2D target)
    {
        var first = scanline0 + ((long)_offsetY * pitch) + (_offsetX * 4);
        var rowBytes = Width * 4;
        if (pitch >= rowBytes)
        {
            lock (_graphics.Gate)
            {
                _graphics.Context.UpdateSubresource(target, 0, null, (nint)first, (uint)pitch, 0);
            }

            return;
        }

        if (_scratch is null || _scratch.Length != rowBytes * Height)
        {
            _scratch = new byte[rowBytes * Height];
        }

        fixed (byte* scratch = _scratch)
        {
            for (var row = 0; row < Height; row++)
            {
                Buffer.MemoryCopy(first + ((long)row * pitch), scratch + ((long)row * rowBytes), rowBytes, rowBytes);
            }

            lock (_graphics.Gate)
            {
                _graphics.Context.UpdateSubresource(target, 0, null, (nint)scratch, (uint)rowBytes, 0);
            }
        }
    }

    /// <summary>One decoded sample, held so its texture is not recycled while it is being drawn.</summary>
    private sealed class Held : IDisposable
    {
        private Held(IMFSample sample, long time)
        {
            Sample = sample;
            Time = time;
        }

        public IMFSample Sample { get; }

        public long Time { get; }

        public ID3D11Texture2D? Texture { get; private set; }

        public uint Subresource { get; private set; }

        public static Held From(IMFSample sample, long time)
        {
            var held = new Held(sample, time);
            try
            {
                using var buffer = sample.GetBufferByIndex(0);
                using var dxgi = buffer.QueryInterfaceOrNull<IMFDXGIBuffer>();
                if (dxgi is not null)
                {
                    held.Texture = new ID3D11Texture2D(dxgi.GetResource(typeof(ID3D11Texture2D).GUID));
                    held.Subresource = dxgi.SubresourceIndex;
                }

                return held;
            }
            catch
            {
                held.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            Texture?.Dispose();
            Texture = null;
            Sample.Dispose();
        }
    }
}

/// <summary>Decoded PCM by sample position. The seam the export's audio logic is tested through.</summary>
internal interface IStudioPcmSource
{
    /// <summary>
    /// Moves so that the blocks that follow reach <paramref name="sample"/>. They may start before
    /// it; the reader of the blocks drops what it does not need.
    /// </summary>
    void Seek(long sample);

    /// <summary>
    /// The next block of interleaved PCM and the position of its first sample, or false at the
    /// end. The block is valid until the next call.
    /// </summary>
    bool TryRead(out long startSample, out ReadOnlySpan<byte> pcm);
}

/// <summary>The first audio track of a file, decoded to 16-bit PCM by a source reader.</summary>
internal sealed class StudioAudioSource : IStudioPcmSource, IDisposable
{
    /// <summary>
    /// How far before a target a seek lands. An AAC frame overlaps the one before it, so the
    /// first frame decoded after a seek is not exact; four frames of run-in are thrown away.
    /// </summary>
    internal const int SeekRunInSamples = 4 * StudioRenderingMath.AacFrameSamples;

    private const int InvalidPosition = unchecked((int)0xC00D36E5);

    private readonly IMFSourceReader _reader;
    private readonly string _description;
    private readonly long _durationTicks;
    private byte[] _block = new byte[16 * 1024];
    private long _expectedStart = -1;
    private bool _ended;

    private StudioAudioSource(IMFSourceReader reader, StudioAudioFormat format, string description)
    {
        _reader = reader;
        _description = description;
        _durationTicks = StudioVideoSource.ReadDuration(reader);
        Format = format;
    }

    public StudioAudioFormat Format { get; }

    /// <summary>Opens the file's first audio track, or returns null when it has none.</summary>
    public static StudioAudioSource? TryOpen(string path, string description)
    {
        IMFSourceReader? reader = null;
        try
        {
            reader = MediaFactory.MFCreateSourceReaderFromURL(path, null!);
            reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);

            uint nativeRate;
            uint nativeChannels;
            try
            {
                using var native = reader.GetNativeMediaType(SourceReaderIndex.FirstAudioStream, 0);
                nativeRate = StudioMediaFoundation.TryGetUInt32(native, MediaTypeAttributeKeys.AudioSamplesPerSecond) ?? 48000;
                nativeChannels = StudioMediaFoundation.TryGetUInt32(native, MediaTypeAttributeKeys.AudioNumChannels) ?? 2;
            }
            catch (SharpGenException ex) when (ex.HResult == StudioMediaFoundation.InvalidStreamNumber)
            {
                return null;
            }

            reader.SetStreamSelection(SourceReaderIndex.FirstAudioStream, true);

            // The AAC encoder takes 44.1 or 48 kHz, one or two channels. A track that is already
            // one of those passes through untouched; anything else is converted by the reader.
            var rate = nativeRate is 44100 or 48000 ? nativeRate : 48000;
            var channels = nativeChannels is 1 or 2 ? nativeChannels : 2;
            using (var type = MediaFactory.MFCreateMediaType())
            {
                type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                type.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
                type.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
                type.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, rate);
                type.Set(MediaTypeAttributeKeys.AudioNumChannels, channels);
                type.Set(MediaTypeAttributeKeys.AudioBlockAlignment, channels * 2);
                type.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, channels * 2 * rate);
                reader.SetCurrentMediaType(SourceReaderIndex.FirstAudioStream, type);
            }

            using var current = reader.GetCurrentMediaType(SourceReaderIndex.FirstAudioStream);
            var format = new StudioAudioFormat(
                (int)(StudioMediaFoundation.TryGetUInt32(current, MediaTypeAttributeKeys.AudioSamplesPerSecond) ?? rate),
                (int)(StudioMediaFoundation.TryGetUInt32(current, MediaTypeAttributeKeys.AudioNumChannels) ?? channels),
                (int)(StudioMediaFoundation.TryGetUInt32(current, MediaTypeAttributeKeys.AudioBitsPerSample) ?? 16));
            if (format.BitsPerSample != 16 || format.Channels is < 1 or > 2 || format.SampleRate is not (44100 or 48000))
            {
                throw new StudioExportException($"The sound in the {description} is in a format that cannot be exported ({format.SampleRate} Hz, {format.Channels} channels).");
            }

            var source = new StudioAudioSource(reader, format, description);
            reader = null;
            return source;
        }
        catch (SharpGenException ex)
        {
            throw new StudioExportException($"The sound in the {description} could not be decoded.", ex);
        }
        finally
        {
            reader?.Dispose();
        }
    }

    public void Seek(long sample)
    {
        var target = Math.Max(0, sample - SeekRunInSamples);
        var ticks = (long)(((Int128)target * StudioMediaFoundation.TicksPerSecond) / Format.SampleRate);
        if (_durationTicks > 0)
        {
            ticks = Math.Min(ticks, _durationTicks - 1);
        }

        _expectedStart = -1;
        _ended = false;
        try
        {
            _reader.SetCurrentPosition(Math.Max(0, ticks));
        }
        catch (SharpGenException ex) when (ex.HResult == InvalidPosition)
        {
            _ended = true;
        }
        catch (SharpGenException ex)
        {
            throw new StudioExportException($"The sound in the {_description} could not be decoded.", ex);
        }
    }

    public bool TryRead(out long startSample, out ReadOnlySpan<byte> pcm)
    {
        startSample = 0;
        pcm = default;
        try
        {
            while (!_ended)
            {
                var sample = _reader.ReadSample(SourceReaderIndex.FirstAudioStream, SourceReaderControlFlag.None, out _, out var flags, out var time);
                if ((flags & SourceReaderFlag.Error) != 0)
                {
                    sample?.Dispose();
                    throw new StudioExportException($"The sound in the {_description} could not be decoded.");
                }

                if ((flags & SourceReaderFlag.EndOfStream) != 0)
                {
                    sample?.Dispose();
                    _ended = true;
                    break;
                }

                if (sample is null)
                {
                    continue;
                }

                int length;
                using (sample)
                {
                    using var buffer = sample.ConvertToContiguousBuffer();
                    buffer.Lock(out var pointer, out _, out length);
                    try
                    {
                        if (_block.Length < length)
                        {
                            _block = new byte[length];
                        }

                        Marshal.Copy(pointer, _block, 0, length);
                    }
                    finally
                    {
                        buffer.Unlock();
                    }
                }

                var samples = length / Format.BlockAlign;
                if (samples == 0)
                {
                    continue;
                }

                // Timestamps are in 100 ns units, coarser than a sample. Within a run of blocks
                // the position is counted, so rounding never opens a one-sample gap or overlap.
                var stamped = (long)Math.Round(time * (double)Format.SampleRate / StudioMediaFoundation.TicksPerSecond, MidpointRounding.AwayFromZero);
                startSample = _expectedStart >= 0 && Math.Abs(stamped - _expectedStart) <= 2 ? _expectedStart : stamped;
                _expectedStart = startSample + samples;
                pcm = _block.AsSpan(0, samples * Format.BlockAlign);
                return true;
            }

            return false;
        }
        catch (SharpGenException ex)
        {
            throw new StudioExportException($"The sound in the {_description} could not be decoded.", ex);
        }
    }

    public void Dispose() => _reader.Dispose();
}
