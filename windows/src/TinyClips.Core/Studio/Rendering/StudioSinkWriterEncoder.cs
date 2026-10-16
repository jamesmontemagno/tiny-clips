using SharpGen.Runtime;
using TinyClips.Core.Models;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace TinyClips.Core.Studio.Rendering;

/// <summary>The video side of an export.</summary>
/// <param name="AllowHardware">Let Media Foundation use a hardware encoder (<c>MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS</c>).</param>
/// <param name="GpuFrames">
/// Frames are D3D11 textures from the writer's own allocator. False means frames arrive as
/// top-down BGRA in system memory, which is the path for a software (WARP) device.
/// </param>
internal sealed record StudioVideoEncoderSettings(
    int Width,
    int Height,
    StudioFrameRate FrameRate,
    uint Bitrate,
    VideoCodec Codec,
    bool AllowHardware,
    bool GpuFrames);

/// <summary>Interleaved PCM as the exporter reads and writes it.</summary>
internal sealed record StudioAudioFormat(int SampleRate, int Channels, int BitsPerSample = 16)
{
    public int BlockAlign => Channels * BitsPerSample / 8;

    /// <summary>The recorder's 192 kbit/s for stereo; half of that for a single channel.</summary>
    public uint AacBitrate => Channels >= 2 ? 192_000u : 96_000u;
}

/// <summary>An encoder-owned BGRA render target, valid until it is written or disposed.</summary>
internal sealed class StudioEncoderFrame : IDisposable
{
    internal StudioEncoderFrame(IMFSample sample, ID3D11Texture2D texture)
    {
        Sample = sample;
        Texture = texture;
    }

    public IMFSample Sample { get; }

    public ID3D11Texture2D Texture { get; }

    public void Dispose()
    {
        // Dropping these references is what returns the texture to the allocator once the sink
        // writer has released its own.
        Texture.Dispose();
        Sample.Dispose();
    }
}

/// <summary>
/// The MP4 writer of an export: an <c>IMFSinkWriter</c> set up for offline work. Unlike the
/// recorder's encoder it is not low latency and leaves the writer's throttling on, which the
/// engine spike measured as 1.3 to 3.8 times faster for the same bytes, and <c>WriteSample</c>
/// blocking is what keeps a faster-than-real-time loop from running ahead of the encoder.
/// The stream is tagged with its primaries, transfer function and the matrix that was really
/// used (see <see cref="StudioMediaFoundation.EncodingMatrix"/>), so players do not have to guess.
/// </summary>
/// <remarks>Not thread-safe: the export loop owns it.</remarks>
internal sealed class StudioSinkWriterEncoder : IDisposable
{
    // GUIDs not surfaced by Vortice (Windows SDK 10.0.26100: mftransform.h, mfreadwrite.h, codecapi.h).
    private static readonly Guid MfSaD3D11BindFlags = new("EACF97AD-065C-4408-BEE3-FDCBFD128BE2");
    private static readonly Guid MfSaD3D11Usage = new("E85FE442-2CA3-486E-A9C7-109DDA609880");
    private static readonly Guid MfSaBuffersPerSample = new("873C5171-1E3D-4E25-988D-B433CE041983");
    private static readonly Guid CodecApiAvEncMpvDefaultBPictureCount = new("8D390AAC-DC5C-4200-B57F-814D04BABAB2");
    private static readonly Guid CodecApiAvEncMpvGopSize = new("95F31B26-95A4-41AA-9303-246A7FC6EEF1");
    private static readonly Guid CodecApiAvEncCommonRateControlMode = new("1C0608E9-370C-4710-8A58-CB6181C42423");
    private static readonly Guid CodecApiAvEncCommonMeanBitRate = new("F7222374-2144-4815-B550-A37F8E12EE52");
    private static readonly Guid CodecApiAvEncCommonQualityVsSpeed = new("98332DF8-03CD-476B-89FA-3F9E442DEC9F");
    private static readonly Guid IidVideoSampleAllocatorEx = new("545B3A48-3283-4F62-866F-A62D8F598F9F");
    private const uint AvEncCommonRateControlModeCbr = 0;
    private const uint AvcHighProfile = 100;
    private const uint HevcMainProfile = 1;

    private readonly IMFSinkWriter _writer;
    private readonly IMFDXGIDeviceManager? _deviceManager;
    private readonly IMFMediaType _videoInputType;
    private readonly int _videoStream;
    private readonly int _audioStream;
    private IMFVideoSampleAllocatorEx? _allocator;
    private bool _finished;
    private bool _disposed;

    private StudioSinkWriterEncoder(
        IMFSinkWriter writer,
        IMFDXGIDeviceManager? deviceManager,
        IMFMediaType videoInputType,
        int videoStream,
        int audioStream,
        StudioVideoEncoderSettings video)
    {
        _writer = writer;
        _deviceManager = deviceManager;
        _videoInputType = videoInputType;
        _videoStream = videoStream;
        _audioStream = audioStream;
        Video = video;
        (EncoderName, IsHardware) = FindVideoEncoder();
    }

    public StudioVideoEncoderSettings Video { get; }

    public bool HasAudio => _audioStream >= 0;

    /// <summary>The encoder Media Foundation actually put in the writer, read back from it.</summary>
    public string EncoderName { get; }

    public bool IsHardware { get; }

    public long VideoFramesWritten { get; private set; }

    public long AudioSamplesWritten { get; private set; }

    /// <summary>
    /// Creates the writer and its streams; <c>BeginWriting</c> instantiates the encoder here. The
    /// container is named explicitly, so <paramref name="outputPath"/> may have any extension.
    /// </summary>
    public static StudioSinkWriterEncoder Create(string outputPath, ID3D11Device? device, StudioVideoEncoderSettings video, StudioAudioFormat? audio)
    {
        if (video.GpuFrames && device is null)
        {
            throw new ArgumentNullException(nameof(device));
        }

        IMFDXGIDeviceManager? deviceManager = null;
        IMFSinkWriter? writer = null;
        IMFMediaType? videoIn = null;
        try
        {
            using var attributes = MediaFactory.MFCreateAttributes(4);
            attributes.Set(TranscodeAttributeKeys.TranscodeContainertype, TranscodeContainerTypeGuids.Mpeg4);
            if (video.AllowHardware)
            {
                attributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1u);
            }

            if (video.GpuFrames)
            {
                deviceManager = MediaFactory.MFCreateDXGIDeviceManager();
                deviceManager.ResetDevice(device!).CheckError();
                attributes.Set(SinkWriterAttributeKeys.D3DManager, deviceManager);
            }

            writer = MediaFactory.MFCreateSinkWriterFromURL(outputPath, null!, attributes);

            var rate = video.FrameRate;
            var matrix = (uint)StudioMediaFoundation.EncodingMatrix(video.Height);
            using var videoOut = MediaFactory.MFCreateMediaType();
            videoOut.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            videoOut.Set(MediaTypeAttributeKeys.Subtype, video.Codec == VideoCodec.Hevc ? VideoFormatGuids.Hevc : VideoFormatGuids.H264);
            videoOut.Set(MediaTypeAttributeKeys.AvgBitrate, video.Bitrate);
            videoOut.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
            videoOut.Set(MediaTypeAttributeKeys.Mpeg2Profile, video.Codec == VideoCodec.Hevc ? HevcMainProfile : AvcHighProfile);
            videoOut.Set(MediaTypeAttributeKeys.VideoPrimaries, (uint)VideoPrimaries.Bt709);
            videoOut.Set(MediaTypeAttributeKeys.TransferFunction, (uint)VideoTransferFunction.Func709);
            videoOut.Set(MediaTypeAttributeKeys.YuvMatrix, matrix);
            videoOut.Set(MediaTypeAttributeKeys.VideoNominalRange, StudioMediaFoundation.NominalRangeLimited);
            MediaFactory.MFSetAttributeSize(videoOut, MediaTypeAttributeKeys.FrameSize, (uint)video.Width, (uint)video.Height).CheckError();
            MediaFactory.MFSetAttributeRatio(videoOut, MediaTypeAttributeKeys.FrameRate, (uint)rate.Numerator, (uint)rate.Denominator).CheckError();
            MediaFactory.MFSetAttributeRatio(videoOut, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1).CheckError();
            var videoStream = writer.AddStream(videoOut);

            videoIn = MediaFactory.MFCreateMediaType();
            videoIn.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            videoIn.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Argb32);
            videoIn.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
            videoIn.Set(MediaTypeAttributeKeys.AllSamplesIndependent, 1u);
            videoIn.Set(MediaTypeAttributeKeys.VideoPrimaries, (uint)VideoPrimaries.Bt709);
            videoIn.Set(MediaTypeAttributeKeys.TransferFunction, (uint)VideoTransferFunction.Func709);
            videoIn.Set(MediaTypeAttributeKeys.YuvMatrix, matrix);
            videoIn.Set(MediaTypeAttributeKeys.VideoNominalRange, StudioMediaFoundation.NominalRangeFull);
            if (!video.GpuFrames)
            {
                // Without this, which way up an RGB memory buffer is taken to be depends on the
                // converter Media Foundation picks: the video processor reads it top-down, the
                // older colour converter bottom-up. A positive stride says top-down to both.
                videoIn.Set(MediaTypeAttributeKeys.DefaultStride, (uint)(video.Width * 4));
            }

            MediaFactory.MFSetAttributeSize(videoIn, MediaTypeAttributeKeys.FrameSize, (uint)video.Width, (uint)video.Height).CheckError();
            MediaFactory.MFSetAttributeRatio(videoIn, MediaTypeAttributeKeys.FrameRate, (uint)rate.Numerator, (uint)rate.Denominator).CheckError();
            MediaFactory.MFSetAttributeRatio(videoIn, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1).CheckError();

            // As the recorder: no B-frames, a keyframe every two seconds, constant bitrate. Low
            // latency is deliberately left off. Keys an encoder does not know are ignored.
            using var encodingParameters = MediaFactory.MFCreateAttributes(5);
            encodingParameters.Set(CodecApiAvEncMpvDefaultBPictureCount, 0u);
            encodingParameters.Set(CodecApiAvEncMpvGopSize, (uint)Math.Max(1, (int)Math.Round(rate.FramesPerSecond * 2)));
            encodingParameters.Set(CodecApiAvEncCommonRateControlMode, AvEncCommonRateControlModeCbr);
            encodingParameters.Set(CodecApiAvEncCommonMeanBitRate, video.Bitrate);
            encodingParameters.Set(CodecApiAvEncCommonQualityVsSpeed, 50u);
            writer.SetInputMediaType(videoStream, videoIn, encodingParameters);

            var audioStream = -1;
            if (audio is not null)
            {
                using var audioOut = MediaFactory.MFCreateMediaType();
                audioOut.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                audioOut.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
                audioOut.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
                audioOut.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)audio.SampleRate);
                audioOut.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)audio.Channels);
                audioOut.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, audio.AacBitrate / 8);
                audioOut.Set(MediaTypeAttributeKeys.AacPayloadType, 0u);
                audioOut.Set(MediaTypeAttributeKeys.AacAudioProfileLevelIndication, 0x29u);
                audioStream = writer.AddStream(audioOut);

                using var audioIn = MediaFactory.MFCreateMediaType();
                audioIn.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                audioIn.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
                audioIn.Set(MediaTypeAttributeKeys.AudioBitsPerSample, (uint)audio.BitsPerSample);
                audioIn.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)audio.SampleRate);
                audioIn.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)audio.Channels);
                audioIn.Set(MediaTypeAttributeKeys.AudioBlockAlignment, (uint)audio.BlockAlign);
                audioIn.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(audio.BlockAlign * audio.SampleRate));
                audioIn.Set(MediaTypeAttributeKeys.AllSamplesIndependent, 1u);
                writer.SetInputMediaType(audioStream, audioIn, null!);
            }

            writer.BeginWriting();

            var encoder = new StudioSinkWriterEncoder(writer, deviceManager, videoIn, videoStream, audioStream, video);
            writer = null;
            deviceManager = null;
            videoIn = null;
            return encoder;
        }
        finally
        {
            videoIn?.Dispose();
            writer?.Dispose();
            deviceManager?.Dispose();
        }
    }

    /// <summary>
    /// Sets up the pool of encoder-owned D3D11 render targets. Media Foundation recycles a texture
    /// when the encoder releases its sample, so the pool size bounds the frames in flight.
    /// </summary>
    public void CreateFrameAllocator(int initialCount, int maxCount)
    {
        if (_deviceManager is null)
        {
            throw new InvalidOperationException("This encoder takes frames from system memory.");
        }

        var allocator = new IMFVideoSampleAllocatorEx(MediaFactory.MFCreateVideoSampleAllocatorEx(IidVideoSampleAllocatorEx));
        try
        {
            allocator.SetDirectXManager(_deviceManager);
            using var attributes = MediaFactory.MFCreateAttributes(3);
            attributes.Set(MfSaD3D11BindFlags, (uint)(BindFlags.RenderTarget | BindFlags.ShaderResource));
            attributes.Set(MfSaD3D11Usage, (uint)ResourceUsage.Default);
            attributes.Set(MfSaBuffersPerSample, 1u);
            allocator.InitializeSampleAllocatorEx(initialCount, maxCount, attributes, _videoInputType);
            _allocator = allocator;
        }
        catch
        {
            allocator.Dispose();
            throw;
        }
    }

    /// <summary>A texture to draw the next frame into, or null while the encoder still holds them all.</summary>
    public StudioEncoderFrame? TryAcquireFrame()
    {
        if (_allocator is null)
        {
            throw new InvalidOperationException("Call CreateFrameAllocator first.");
        }

        IMFSample sample;
        try
        {
            sample = _allocator.AllocateSample();
        }
        catch (SharpGenException ex) when (ex.HResult == StudioMediaFoundation.SampleAllocatorEmpty)
        {
            return null;
        }

        try
        {
            using var buffer = sample.GetBufferByIndex(0);
            using var dxgiBuffer = buffer.QueryInterface<IMFDXGIBuffer>();
            var texture = new ID3D11Texture2D(dxgiBuffer.GetResource(typeof(ID3D11Texture2D).GUID));
            buffer.CurrentLength = Video.Width * Video.Height * 4;
            return new StudioEncoderFrame(sample, texture);
        }
        catch
        {
            sample.Dispose();
            throw;
        }
    }

    /// <summary>Hands a drawn frame to the encoder and releases it. Times are in 100 ns units.</summary>
    public void WriteVideo(StudioEncoderFrame frame, long time, long duration)
    {
        try
        {
            frame.Sample.SampleTime = time;
            frame.Sample.SampleDuration = duration;
            _writer.WriteSample(_videoStream, frame.Sample);
            VideoFramesWritten++;
        }
        finally
        {
            frame.Dispose();
        }
    }

    /// <summary>Writes a frame from system memory: tightly packed BGRA, top row first.</summary>
    public void WriteVideoMemory(ReadOnlySpan<byte> topDownBgra, long time, long duration)
    {
        using var sample = CreateMemorySample(topDownBgra, time, duration);
        _writer.WriteSample(_videoStream, sample);
        VideoFramesWritten++;
    }

    /// <summary>Writes interleaved PCM. <paramref name="startSample"/> is its position in the output track.</summary>
    public void WriteAudio(ReadOnlySpan<byte> pcm, long startSample, StudioAudioFormat format)
    {
        if (_audioStream < 0 || pcm.Length == 0)
        {
            return;
        }

        var samples = pcm.Length / format.BlockAlign;
        var time = SamplesToTicks(startSample, format.SampleRate);
        using var sample = CreateMemorySample(pcm, time, SamplesToTicks(startSample + samples, format.SampleRate) - time);
        _writer.WriteSample(_audioStream, sample);
        AudioSamplesWritten += samples;
    }

    /// <summary>Drains the encoder and writes the MP4 index. Blocks until the file is complete.</summary>
    public void Finish()
    {
        if (_finished || _disposed)
        {
            return;
        }

        _finished = true;
        _writer.Finalize();
    }

    /// <summary>
    /// Releases the writer and its file. Without a <see cref="Finish"/> first the file is not a
    /// valid MP4; that is the path a failed or cancelled export takes before deleting it.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_allocator is not null)
        {
            try
            {
                _allocator.UninitializeSampleAllocator();
            }
            catch (SharpGenException)
            {
                // Samples still held by the encoder keep their textures alive until it lets go.
            }

            _allocator.Dispose();
        }

        _videoInputType.Dispose();
        _writer.Dispose();
        _deviceManager?.Dispose();
    }

    private static long SamplesToTicks(long samples, int sampleRate) =>
        (long)(((Int128)samples * StudioMediaFoundation.TicksPerSecond) / sampleRate);

    private static unsafe IMFSample CreateMemorySample(ReadOnlySpan<byte> data, long time, long duration)
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
        try
        {
            sample.AddBuffer(buffer);
            sample.SampleTime = time;
            sample.SampleDuration = duration;
            return sample;
        }
        catch
        {
            sample.Dispose();
            throw;
        }
    }

    private (string Name, bool Hardware) FindVideoEncoder()
    {
        using var extended = _writer.QueryInterfaceOrNull<IMFSinkWriterEx>();
        if (extended is not null)
        {
            for (var index = 0; index < 8; index++)
            {
                try
                {
                    extended.GetTransformForStream(_videoStream, index, out var category, out var transform);
                    using (transform)
                    {
                        if (category == TransformCategoryGuids.VideoEncoder)
                        {
                            var (name, hardware) = StudioMediaFoundation.DescribeEncoder(transform);

                            // A software transform inside a writer says nothing about itself. With
                            // hardware transforms out of the picture, it is the registered encoder.
                            if (name is null && !hardware)
                            {
                                name = StudioMediaFoundation.FindEncoder(Video.Codec == VideoCodec.Hevc ? VideoFormatGuids.Hevc : VideoFormatGuids.H264, hardware: false);
                            }

                            return (name ?? (hardware ? "hardware encoder" : "software encoder"), hardware);
                        }
                    }
                }
                catch (SharpGenException)
                {
                    break;
                }
            }
        }

        return (Video.AllowHardware ? "system encoder" : "software encoder", false);
    }
}
