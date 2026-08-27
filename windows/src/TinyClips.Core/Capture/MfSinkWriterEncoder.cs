using System.Runtime.InteropServices;
using SharpGen.Runtime;
using TinyClips.Core.Models;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace TinyClips.Core.Capture;

/// <summary>
/// Push-model MP4 encoder built on <c>IMFSinkWriter</c>. Compared with the <c>MediaTranscoder</c>
/// path it (a) accepts encoder configuration — low-latency mode, no B-frames, a bounded GOP — so the
/// hardware encoder does not hold frames for a long look-ahead window, (b) supports HEVC, and
/// (c) on the GPU pipeline hands out encoder frames from an <c>IMFVideoSampleAllocatorEx</c>, so
/// Media Foundation itself recycles the D3D11 textures when the encoder is done with them (no
/// <c>Processed</c> callback or hand-rolled pool needed).
///
/// Video and audio samples are written from their producer threads; the sink writer interleaves and
/// throttles internally. All members are thread-safe.
/// </summary>
internal sealed class MfSinkWriterEncoder : IDisposable
{
    // Media Foundation / CodecAPI GUIDs not surfaced by Vortice (verified against the Windows SDK
    // 10.0.26100 headers: mftransform.h, mfreadwrite.h, codecapi.h).
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
    private const uint HevcMainProfile = 1;
    private const int MfESampleAllocatorEmpty = unchecked((int)0xC00D4A3E);
    private const int MfESinkNoSamplesProcessed = unchecked((int)0xC00D4A44);

    private readonly object _videoGate = new();
    private readonly object _audioGate = new();
    private readonly object _stateGate = new();
    private readonly IMFSinkWriter _writer;
    private readonly IMFDXGIDeviceManager _deviceManager;
    private readonly IMFMediaType _videoInputType;
    private readonly int _videoStream;
    private readonly int _audioStream;
    private readonly int _width;
    private readonly int _height;
    private bool _began;
    private bool _disposed;
    private long _videoSamples;
    private long _audioSamples;

    private MfSinkWriterEncoder(
        IMFSinkWriter writer,
        IMFDXGIDeviceManager deviceManager,
        IMFMediaType videoInputType,
        int videoStream,
        int audioStream,
        int width,
        int height,
        string description)
    {
        _writer = writer;
        _deviceManager = deviceManager;
        _videoInputType = videoInputType;
        _videoStream = videoStream;
        _audioStream = audioStream;
        _width = width;
        _height = height;
        Description = description;
    }

    /// <summary>Human-readable encoder path for the diagnostics/perf report.</summary>
    public string Description { get; }

    public bool HasAudio => _audioStream >= 0;

    public long VideoSamplesWritten => Interlocked.Read(ref _videoSamples);

    public long AudioSamplesWritten => Interlocked.Read(ref _audioSamples);

    /// <summary>
    /// Creates the sink writer, streams and encoder configuration. The encoder MFTs are
    /// instantiated here (BeginWriting) so <see cref="WriteVideo(GpuFrame, TimeSpan)"/> is cheap
    /// from the first frame; call during the pre-roll, not at the recording start instant.
    /// </summary>
    public static MfSinkWriterEncoder Create(
        string outputPath,
        ID3D11Device device,
        int width,
        int height,
        int fps,
        uint videoBitrate,
        VideoCodec codec,
        bool includeAudio,
        int audioSampleRate,
        int audioChannels,
        int audioBitsPerSample,
        uint audioBitrate)
    {
        MediaFactory.MFStartup(true).CheckError();

        IMFDXGIDeviceManager? deviceManager = null;
        IMFSinkWriter? writer = null;
        IMFMediaType? videoIn = null;
        var created = false;
        try
        {
            deviceManager = MediaFactory.MFCreateDXGIDeviceManager();
            deviceManager.ResetDevice(device).CheckError();

            using var attributes = MediaFactory.MFCreateAttributes(5);
            attributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1u);
            attributes.Set(SinkWriterAttributeKeys.D3DManager, deviceManager);
            attributes.Set(SinkWriterAttributeKeys.LowLatency, 1u);
            // Both streams are produced in real time and paced by their own capture clocks, so the
            // writer's cross-stream throttling (which blocks WriteSample on the stream that is
            // "ahead") would only ever stall the frame pump. Interleaving is handled by the MP4 sink.
            attributes.Set(SinkWriterAttributeKeys.DisableThrottling, 1u);
            writer = MediaFactory.MFCreateSinkWriterFromURL(outputPath, null!, attributes);

            // --- Video output (compressed) ---
            using var videoOut = MediaFactory.MFCreateMediaType();
            videoOut.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            videoOut.Set(MediaTypeAttributeKeys.Subtype, codec == VideoCodec.Hevc ? VideoFormatGuids.Hevc : VideoFormatGuids.H264);
            videoOut.Set(MediaTypeAttributeKeys.AvgBitrate, videoBitrate);
            videoOut.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
            videoOut.Set(MediaTypeAttributeKeys.Mpeg2Profile, codec == VideoCodec.Hevc ? HevcMainProfile : AvcHighProfile);
            MediaFactory.MFSetAttributeSize(videoOut, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height).CheckError();
            MediaFactory.MFSetAttributeRatio(videoOut, MediaTypeAttributeKeys.FrameRate, (uint)fps, 1).CheckError();
            MediaFactory.MFSetAttributeRatio(videoOut, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1).CheckError();
            var videoStream = writer.AddStream(videoOut);

            // --- Video input (uncompressed BGRA, straight from D3D11 textures or memory) ---
            videoIn = MediaFactory.MFCreateMediaType();
            videoIn.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            videoIn.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Argb32);
            videoIn.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
            videoIn.Set(MediaTypeAttributeKeys.AllSamplesIndependent, 1u);
            MediaFactory.MFSetAttributeSize(videoIn, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height).CheckError();
            MediaFactory.MFSetAttributeRatio(videoIn, MediaTypeAttributeKeys.FrameRate, (uint)fps, 1).CheckError();
            MediaFactory.MFSetAttributeRatio(videoIn, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1).CheckError();

            // Encoder tuning: these are the knobs MediaTranscoder does not expose. No B-frames and
            // low-latency mode stop the hardware encoder holding a look-ahead window of input
            // surfaces (measured 50–220 ms on AMD VCN through the transcoder), which is what bounded
            // the GPU pipeline at 60 fps. The GOP is two seconds so seeking/trimming stays snappy.
            // CBR keeps the MP4's bitrate predictable for uploads. Individual keys an encoder does
            // not support are ignored by the sink writer.
            using var encodingParameters = MediaFactory.MFCreateAttributes(6);
            encodingParameters.Set(CodecApiAvLowLatencyMode, 1u);
            encodingParameters.Set(CodecApiAvEncMpvDefaultBPictureCount, 0u);
            encodingParameters.Set(CodecApiAvEncMpvGopSize, (uint)Math.Max(1, fps * 2));
            encodingParameters.Set(CodecApiAvEncCommonRateControlMode, AvEncCommonRateControlModeCbr);
            encodingParameters.Set(CodecApiAvEncCommonMeanBitRate, videoBitrate);
            encodingParameters.Set(CodecApiAvEncCommonQualityVsSpeed, 50u);
            writer.SetInputMediaType(videoStream, videoIn, encodingParameters);

            // --- Audio (PCM in → AAC out) ---
            var audioStream = -1;
            if (includeAudio)
            {
                using var audioOut = MediaFactory.MFCreateMediaType();
                audioOut.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                audioOut.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
                audioOut.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
                audioOut.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)audioSampleRate);
                audioOut.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)audioChannels);
                audioOut.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, audioBitrate / 8);
                audioOut.Set(MediaTypeAttributeKeys.AacPayloadType, 0u);
                audioOut.Set(MediaTypeAttributeKeys.AacAudioProfileLevelIndication, 0x29u);
                audioStream = writer.AddStream(audioOut);

                var blockAlign = (uint)(audioChannels * audioBitsPerSample / 8);
                using var audioIn = MediaFactory.MFCreateMediaType();
                audioIn.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                audioIn.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
                audioIn.Set(MediaTypeAttributeKeys.AudioBitsPerSample, (uint)audioBitsPerSample);
                audioIn.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)audioSampleRate);
                audioIn.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)audioChannels);
                audioIn.Set(MediaTypeAttributeKeys.AudioBlockAlignment, blockAlign);
                audioIn.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, blockAlign * (uint)audioSampleRate);
                audioIn.Set(MediaTypeAttributeKeys.AllSamplesIndependent, 1u);
                writer.SetInputMediaType(audioStream, audioIn, null!);
            }

            writer.BeginWriting();

            var description = $"{(codec == VideoCodec.Hevc ? "HEVC Main" : "H.264 High")} via IMFSinkWriter (hardware, low-latency, no B-frames)";
            var encoder = new MfSinkWriterEncoder(writer, deviceManager, videoIn, videoStream, audioStream, width, height, description)
            {
                _began = true,
            };
            writer = null;
            deviceManager = null;
            videoIn = null;
            created = true;
            return encoder;
        }
        finally
        {
            videoIn?.Dispose();
            writer?.Dispose();
            deviceManager?.Dispose();
            if (!created)
            {
                // Balance the MFStartup above; the encoder instance owns the MFShutdown on success.
                MediaFactory.MFShutdown();
            }
        }
    }

    /// <summary>
    /// Creates an allocator whose frames are D3D11 render targets owned by Media Foundation.
    /// When the encoder releases a sample the texture returns to the allocator automatically,
    /// so the GPU pipeline never has to guess when a texture is free.
    /// </summary>
    public IGpuFrameAllocator CreateFrameAllocator(int initialCount, int maxCount)
    {
        var allocatorPtr = MediaFactory.MFCreateVideoSampleAllocatorEx(IidVideoSampleAllocatorEx);
        var allocator = new IMFVideoSampleAllocatorEx(allocatorPtr);
        try
        {
            allocator.SetDirectXManager(_deviceManager);
            using var attributes = MediaFactory.MFCreateAttributes(3);
            attributes.Set(MfSaD3D11BindFlags, (uint)(BindFlags.RenderTarget | BindFlags.ShaderResource));
            attributes.Set(MfSaD3D11Usage, (uint)ResourceUsage.Default);
            attributes.Set(MfSaBuffersPerSample, 1u);
            allocator.InitializeSampleAllocatorEx(initialCount, maxCount, attributes, _videoInputType);
            return new SampleAllocatorFrames(allocator, _width, _height, maxCount);
        }
        catch
        {
            allocator.Dispose();
            throw;
        }
    }

    /// <summary>Writes a GPU frame obtained from <see cref="CreateFrameAllocator"/>.</summary>
    public void WriteVideo(GpuFrame frame, TimeSpan duration)
    {
        if (frame.BackendSample is not IMFSample sample)
        {
            throw new InvalidOperationException("Frame was not produced by this encoder's allocator.");
        }

        sample.SampleTime = frame.Pts.Ticks;
        sample.SampleDuration = duration.Ticks;
        Write(_videoStream, sample, ref _videoSamples, _videoGate);
    }

    /// <summary>Writes a CPU frame (tightly packed bottom-up BGRA, as Media Foundation expects for RGB32).</summary>
    public unsafe void WriteVideo(byte[] bottomUpBgra, TimeSpan pts, TimeSpan duration)
    {
        using var buffer = MediaFactory.MFCreateMemoryBuffer(bottomUpBgra.Length);
        buffer.Lock(out var data, out _, out _);
        try
        {
            fixed (byte* src = bottomUpBgra)
            {
                Buffer.MemoryCopy(src, (void*)data, bottomUpBgra.Length, bottomUpBgra.Length);
            }
        }
        finally
        {
            buffer.Unlock();
        }

        buffer.CurrentLength = bottomUpBgra.Length;
        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = pts.Ticks;
        sample.SampleDuration = duration.Ticks;
        Write(_videoStream, sample, ref _videoSamples, _videoGate);
    }

    public unsafe void WriteAudio(byte[] pcm, TimeSpan pts, TimeSpan duration)
    {
        if (_audioStream < 0 || pcm.Length == 0)
        {
            return;
        }

        using var buffer = MediaFactory.MFCreateMemoryBuffer(pcm.Length);
        buffer.Lock(out var data, out _, out _);
        try
        {
            fixed (byte* src = pcm)
            {
                Buffer.MemoryCopy(src, (void*)data, pcm.Length, pcm.Length);
            }
        }
        finally
        {
            buffer.Unlock();
        }

        buffer.CurrentLength = pcm.Length;
        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = pts.Ticks;
        sample.SampleDuration = duration.Ticks;
        Write(_audioStream, sample, ref _audioSamples, _audioGate);
    }

    // IMFSinkWriter accepts concurrent WriteSample calls on different streams (that is how its
    // throttling model is meant to be driven); a per-stream lock only serializes same-stream writers.
    private void Write(int stream, IMFSample sample, ref long counter, object gate)
    {
        lock (gate)
        {
            if (!_began || Volatile.Read(ref _finishedFlag) != 0 || _disposed)
            {
                return;
            }

            _writer.WriteSample(stream, sample);
            Interlocked.Increment(ref counter);
        }
    }

    private int _finishedFlag;

    /// <summary>Flushes the encoder and writes the MP4 index. Blocks until the file is complete.</summary>
    public void Finish()
    {
        lock (_stateGate)
        {
            if (!_began || _disposed || Interlocked.Exchange(ref _finishedFlag, 1) != 0)
            {
                return;
            }

            // Take both stream gates so no WriteSample can race Finalize.
            lock (_videoGate)
            lock (_audioGate)
            {
                try
                {
                    _writer.Finalize();
                }
                catch (SharpGenException ex) when (ex.HResult == MfESinkNoSamplesProcessed)
                {
                    // Stopped before any frame reached the encoder; the file is empty by design.
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_stateGate)
        {
            if (_disposed)
            {
                return;
            }
        }

        Finish();

        lock (_stateGate)
        lock (_videoGate)
        lock (_audioGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _videoInputType.Dispose();
            _writer.Dispose();
            _deviceManager.Dispose();
        }

        MediaFactory.MFShutdown();
    }

    /// <summary>Frames backed by an <c>IMFVideoSampleAllocatorEx</c>; releasing returns the texture to MF.</summary>
    private sealed class SampleAllocatorFrames : IGpuFrameAllocator
    {
        private readonly IMFVideoSampleAllocatorEx _allocator;
        private readonly int _width;
        private readonly int _height;
        private readonly object _gate = new();
        private int _highWater;
        private int _outstanding;
        private bool _disposed;

        public SampleAllocatorFrames(IMFVideoSampleAllocatorEx allocator, int width, int height, int maxCount)
        {
            _allocator = allocator;
            _width = width;
            _height = height;
            MaxCapacity = maxCount;
        }

        public int Allocated => Volatile.Read(ref _highWater);

        public int MaxCapacity { get; }

        public bool TryAcquire(out GpuFrame frame)
        {
            frame = null!;
            IMFSample? sample = null;
            IMFMediaBuffer? buffer = null;
            IMFDXGIBuffer? dxgiBuffer = null;
            try
            {
                lock (_gate)
                {
                    if (_disposed)
                    {
                        return false;
                    }

                    try
                    {
                        sample = _allocator.AllocateSample();
                    }
                    catch (SharpGenException ex) when (ex.HResult == MfESampleAllocatorEmpty)
                    {
                        return false;
                    }
                }

                buffer = sample.GetBufferByIndex(0);
                dxgiBuffer = buffer.QueryInterface<IMFDXGIBuffer>();
                var texturePtr = dxgiBuffer.GetResource(typeof(ID3D11Texture2D).GUID);
                var texture = new ID3D11Texture2D(texturePtr);
                buffer.CurrentLength = _width * _height * 4;

                var outstanding = Interlocked.Increment(ref _outstanding);
                var high = Volatile.Read(ref _highWater);
                while (outstanding > high && Interlocked.CompareExchange(ref _highWater, outstanding, high) != high)
                {
                    high = Volatile.Read(ref _highWater);
                }

                frame = new GpuFrame(OnRelease, texture, _width, _height) { BackendSample = sample };
                frame.Rented();
                sample = null;
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                dxgiBuffer?.Dispose();
                buffer?.Dispose();
                sample?.Dispose();
            }
        }

        private void OnRelease(GpuFrame frame)
        {
            Interlocked.Decrement(ref _outstanding);
            // Dropping our references is what hands the tracked sample back to the allocator once
            // the sink writer has released its own.
            frame.Texture.Dispose();
            frame.BackendSample?.Dispose();
            frame.BackendSample = null;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            try
            {
                _allocator.UninitializeSampleAllocator();
            }
            catch
            {
                // Best effort; samples still held by the encoder keep their textures alive.
            }

            _allocator.Dispose();
        }
    }
}
