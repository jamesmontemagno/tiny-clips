using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace StudioEngineSpike.Engine;

/// <summary>Video side of a sink writer.</summary>
/// <param name="Hardware">Allow hardware MFTs (<c>MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS</c>).</param>
/// <param name="UseDevice">Give the writer the shared D3D11 device so frames can be textures.</param>
/// <param name="LowLatency">
/// The real-time profile from Core: <c>MF_LOW_LATENCY</c> and <c>CODECAPI_AVLowLatencyMode</c>, so
/// the encoder holds one frame instead of a look-ahead window.
/// </param>
/// <param name="DisableThrottling">
/// True for capture (producers are already paced by their own clocks); false for an offline
/// export, where <c>WriteSample</c> blocking is the back-pressure that keeps memory bounded.
/// </param>
internal sealed record VideoEncoderSettings(
    int Width,
    int Height,
    int Fps,
    uint Bitrate,
    bool Hardware = true,
    bool UseDevice = true,
    bool LowLatency = true,
    bool DisableThrottling = true)
{
    /// <summary>The bitrate rule TinyClips uses for H.264: width × height × fps / 10, clamped to 2–24 Mbit/s.</summary>
    public static uint DefaultBitrate(int width, int height, int fps) => (uint)Math.Clamp((long)width * height * fps / 10, 2_000_000, 24_000_000);
}

internal sealed record AudioEncoderSettings(int SampleRate = 48000, int Channels = 2, int BitsPerSample = 16, uint Bitrate = 192_000)
{
    public int BlockAlign => Channels * BitsPerSample / 8;
}

/// <summary>An encoder-owned BGRA render target, valid until it is written or disposed.</summary>
internal sealed class EncoderFrame : IDisposable
{
    internal EncoderFrame(IMFSample sample, ID3D11Texture2D texture)
    {
        Sample = sample;
        Texture = texture;
    }

    public IMFSample Sample { get; }

    public ID3D11Texture2D Texture { get; }

    public void Dispose()
    {
        // Dropping these references is what returns the tracked sample to the allocator once the
        // sink writer has released its own.
        Texture.Dispose();
        Sample.Dispose();
    }
}

/// <summary>
/// Push-model H.264 + AAC MP4 writer on <c>IMFSinkWriter</c>, copied from
/// <c>TinyClips.Core.Capture.MfSinkWriterEncoder</c> (the spike cannot reference Core) and made
/// configurable so the same class serves the offline export, the real-time dual-encoder test and
/// the software-encoder comparison. Media types, encoder parameters and the sample allocator are
/// unchanged from Core.
/// </summary>
internal sealed class SinkWriterEncoder : IDisposable
{
    // GUIDs not surfaced by Vortice (Windows SDK 10.0.26100: mftransform.h, mfreadwrite.h, codecapi.h).
    private static readonly Guid MfSaD3D11BindFlags = new("EACF97AD-065C-4408-BEE3-FDCBFD128BE2");
    private static readonly Guid MfSaD3D11Usage = new("E85FE442-2CA3-486E-A9C7-109DDA609880");
    private static readonly Guid MfSaBuffersPerSample = new("873C5171-1E3D-4E25-988D-B433CE041983");
    private static readonly Guid CodecApiAvLowLatencyMode = new("9C27891A-ED7A-40E1-88E8-B22727A024EE");
    private static readonly Guid CodecApiAvEncMpvDefaultBPictureCount = new("8D390AAC-DC5C-4200-B57F-814D04BABAB2");
    private static readonly Guid CodecApiAvEncMpvGopSize = new("95F31B26-95A4-41AA-9303-246A7FC6EEF1");
    private static readonly Guid CodecApiAvEncCommonRateControlMode = new("1C0608E9-370C-4710-8A58-CB6181C42423");
    private static readonly Guid CodecApiAvEncCommonMeanBitRate = new("F7222374-2144-4815-B550-A37F8E12EE52");
    private static readonly Guid CodecApiAvEncCommonQualityVsSpeed = new("98332DF8-03CD-476B-89FA-3F9E442DEC9F");
    private static readonly Guid IidVideoSampleAllocatorEx = new("545B3A48-3283-4F62-866F-A62D8F598F9F");
    private const uint AvEncCommonRateControlModeCbr = 0;
    private const uint AvcHighProfile = 100;

    private readonly object _videoGate = new();
    private readonly object _audioGate = new();
    private readonly IMFSinkWriter _writer;
    private readonly IMFDXGIDeviceManager? _deviceManager;
    private readonly IMFMediaType _videoInputType;
    private readonly int _videoStream;
    private readonly int _audioStream;
    private IMFVideoSampleAllocatorEx? _allocator;
    private bool _finished;
    private bool _disposed;

    private SinkWriterEncoder(IMFSinkWriter writer, IMFDXGIDeviceManager? deviceManager, IMFMediaType videoInputType, int videoStream, int audioStream, VideoEncoderSettings video, AudioEncoderSettings? audio)
    {
        _writer = writer;
        _deviceManager = deviceManager;
        _videoInputType = videoInputType;
        _videoStream = videoStream;
        _audioStream = audioStream;
        Video = video;
        Audio = audio;
    }

    public VideoEncoderSettings Video { get; }

    public AudioEncoderSettings? Audio { get; }

    public long VideoSamplesWritten { get; private set; }

    public long AudioSamplesWritten { get; private set; }

    /// <summary>Times <see cref="TryAcquireFrame"/> found every allocator texture still held by the encoder.</summary>
    public long AllocatorEmpty { get; private set; }

    /// <summary>Creates the writer and its streams; <c>BeginWriting</c> instantiates the encoder MFTs here.</summary>
    public static SinkWriterEncoder Create(string outputPath, ID3D11Device device, VideoEncoderSettings video, AudioEncoderSettings? audio)
    {
        IMFDXGIDeviceManager? deviceManager = null;
        IMFSinkWriter? writer = null;
        IMFMediaType? videoIn = null;
        try
        {
            using var attributes = MediaFactory.MFCreateAttributes(5);
            if (video.Hardware)
            {
                attributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1u);
            }

            if (video.UseDevice)
            {
                deviceManager = MediaFactory.MFCreateDXGIDeviceManager();
                deviceManager.ResetDevice(device).CheckError();
                attributes.Set(SinkWriterAttributeKeys.D3DManager, deviceManager);
            }

            if (video.LowLatency)
            {
                attributes.Set(SinkWriterAttributeKeys.LowLatency, 1u);
            }

            if (video.DisableThrottling)
            {
                attributes.Set(SinkWriterAttributeKeys.DisableThrottling, 1u);
            }

            writer = MediaFactory.MFCreateSinkWriterFromURL(outputPath, null!, attributes);

            using var videoOut = MediaFactory.MFCreateMediaType();
            videoOut.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            videoOut.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
            videoOut.Set(MediaTypeAttributeKeys.AvgBitrate, video.Bitrate);
            videoOut.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
            videoOut.Set(MediaTypeAttributeKeys.Mpeg2Profile, AvcHighProfile);
            MediaFactory.MFSetAttributeSize(videoOut, MediaTypeAttributeKeys.FrameSize, (uint)video.Width, (uint)video.Height).CheckError();
            MediaFactory.MFSetAttributeRatio(videoOut, MediaTypeAttributeKeys.FrameRate, (uint)video.Fps, 1).CheckError();
            MediaFactory.MFSetAttributeRatio(videoOut, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1).CheckError();
            var videoStream = writer.AddStream(videoOut);

            videoIn = MediaFactory.MFCreateMediaType();
            videoIn.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            videoIn.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Argb32);
            videoIn.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
            videoIn.Set(MediaTypeAttributeKeys.AllSamplesIndependent, 1u);
            MediaFactory.MFSetAttributeSize(videoIn, MediaTypeAttributeKeys.FrameSize, (uint)video.Width, (uint)video.Height).CheckError();
            MediaFactory.MFSetAttributeRatio(videoIn, MediaTypeAttributeKeys.FrameRate, (uint)video.Fps, 1).CheckError();
            MediaFactory.MFSetAttributeRatio(videoIn, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1).CheckError();

            using var encodingParameters = MediaFactory.MFCreateAttributes(6);
            if (video.LowLatency)
            {
                encodingParameters.Set(CodecApiAvLowLatencyMode, 1u);
            }

            encodingParameters.Set(CodecApiAvEncMpvDefaultBPictureCount, 0u);
            encodingParameters.Set(CodecApiAvEncMpvGopSize, (uint)Math.Max(1, video.Fps * 2));
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
                audioOut.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, audio.Bitrate / 8);
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

            var encoder = new SinkWriterEncoder(writer, deviceManager, videoIn, videoStream, audioStream, video, audio);
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
    /// Sets up the pool of encoder-owned D3D11 render targets (as Core does). Media Foundation
    /// recycles a texture when the encoder releases its sample, so the pool size bounds how many
    /// frames can be in flight.
    /// </summary>
    public void CreateFrameAllocator(int initialCount, int maxCount)
    {
        if (_deviceManager is null)
        {
            throw new InvalidOperationException("This encoder was created without a D3D device.");
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

    /// <summary>A texture to draw the next frame into, or null when the encoder still holds them all.</summary>
    public EncoderFrame? TryAcquireFrame()
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
        catch (SharpGenException ex) when (ex.HResult == MfHelpers.MfESampleAllocatorEmpty)
        {
            AllocatorEmpty++;
            return null;
        }

        try
        {
            using var buffer = sample.GetBufferByIndex(0);
            using var dxgiBuffer = buffer.QueryInterface<IMFDXGIBuffer>();
            var texture = new ID3D11Texture2D(dxgiBuffer.GetResource(typeof(ID3D11Texture2D).GUID));
            buffer.CurrentLength = Video.Width * Video.Height * 4;
            return new EncoderFrame(sample, texture);
        }
        catch
        {
            sample.Dispose();
            throw;
        }
    }

    /// <summary>Hands a drawn frame to the encoder and releases it. Times are in 100 ns units.</summary>
    public void WriteVideo(EncoderFrame frame, long time, long duration)
    {
        try
        {
            frame.Sample.SampleTime = time;
            frame.Sample.SampleDuration = duration;
            lock (_videoGate)
            {
                _writer.WriteSample(_videoStream, frame.Sample);
                VideoSamplesWritten++;
            }
        }
        finally
        {
            frame.Dispose();
        }
    }

    /// <summary>Writes a CPU frame: tightly packed bottom-up BGRA, as Media Foundation expects for RGB32.</summary>
    public unsafe void WriteVideoMemory(ReadOnlySpan<byte> bottomUpBgra, long time, long duration)
    {
        using var sample = CreateMemorySample(bottomUpBgra, time, duration);
        lock (_videoGate)
        {
            _writer.WriteSample(_videoStream, sample);
            VideoSamplesWritten++;
        }
    }

    public void WriteAudio(ReadOnlySpan<byte> pcm, long time, long duration)
    {
        if (_audioStream < 0 || pcm.Length == 0)
        {
            return;
        }

        using var sample = CreateMemorySample(pcm, time, duration);
        lock (_audioGate)
        {
            _writer.WriteSample(_audioStream, sample);
            AudioSamplesWritten++;
        }
    }

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
        sample.AddBuffer(buffer);
        sample.SampleTime = time;
        sample.SampleDuration = duration;
        return sample;
    }

    /// <summary>
    /// IMFSinkWriter::GetStatistics for the video stream. Vortice's wrapper passes the structure
    /// with <c>cb</c> left at zero, which Media Foundation rejects with E_INVALIDARG, so the method
    /// (vtable slot 13) is called directly with the size filled in.
    /// </summary>
    public unsafe SinkWriterStatistics VideoStatistics()
    {
        var statistics = new SinkWriterStatistics { Cb = sizeof(SinkWriterStatistics) };
        var vtable = *(void***)_writer.NativePointer;
        var result = ((delegate* unmanaged[Stdcall]<nint, int, SinkWriterStatistics*, int>)vtable[13])(_writer.NativePointer, _videoStream, &statistics);
        if (result < 0)
        {
            throw new InvalidOperationException($"IMFSinkWriter.GetStatistics failed: 0x{result:X8}");
        }

        return statistics;
    }

    /// <summary>The transforms the sink writer built for the video stream (encoder, colour converter).</summary>
    public string DescribeVideoPipeline() => DescribeStream(_videoStream);

    public string DescribeAudioPipeline() => _audioStream < 0 ? "(no audio)" : DescribeStream(_audioStream);

    private string DescribeStream(int stream)
    {
        using var extended = _writer.QueryInterfaceOrNull<IMFSinkWriterEx>();
        if (extended is null)
        {
            return "(IMFSinkWriterEx not available)";
        }

        var parts = new List<string>();
        for (var index = 0; index < 8; index++)
        {
            try
            {
                extended.GetTransformForStream(stream, index, out var category, out var transform);
                using (transform)
                {
                    parts.Add(MfHelpers.DescribeTransform(category, transform));
                }
            }
            catch (SharpGenException)
            {
                break;
            }
        }

        return parts.Count == 0 ? "(no transforms)" : string.Join(" → ", parts);
    }

    /// <summary>Drains the encoder and writes the MP4 index. Blocks until the file is complete.</summary>
    public void Finish()
    {
        lock (_videoGate)
        {
            lock (_audioGate)
            {
                if (_finished || _disposed)
                {
                    return;
                }

                _finished = true;
                try
                {
                    _writer.Finalize();
                }
                catch (SharpGenException ex) when (ex.HResult == MfHelpers.MfESinkNoSamplesProcessed)
                {
                    // Nothing reached the encoder; the file is empty by design.
                }
            }
        }
    }

    public void Dispose()
    {
        Finish();
        lock (_videoGate)
        {
            lock (_audioGate)
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
                        // Samples still held by the encoder keep their textures alive.
                    }

                    _allocator.Dispose();
                }

                _videoInputType.Dispose();
                _writer.Dispose();
                _deviceManager?.Dispose();
            }
        }
    }
}
