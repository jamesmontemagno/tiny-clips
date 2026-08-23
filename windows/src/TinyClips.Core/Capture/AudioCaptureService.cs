using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace TinyClips.Core.Capture;

/// <summary>
/// Captures microphone and/or desktop (system "loopback") audio with WASAPI, mixes the
/// enabled sources into a single 48 kHz / 16-bit / stereo PCM stream and exposes it as a
/// pull source. <see cref="ReadChunk"/> always returns a full, silence-padded buffer so the
/// muxing <see cref="Windows.Media.Core.MediaStreamSource"/> never starves while a recording
/// is in progress. Used to add an audio track to the video recorder's MP4 transcode.
/// </summary>
public sealed class AudioCaptureService : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    public const int BitsPerSample = 16;

    private readonly bool _captureSystem;
    private readonly bool _captureMic;
    private readonly string? _micDeviceId;
    private readonly bool _limitMicrophone;
    private readonly TimeSpan _userOffset;
    private readonly object _gate = new();
    private readonly List<TimelineAlignedWaveProvider> _buffers = new();

    // Resampled sources (e.g. 44.1 kHz microphones) feed a WDL resampler that pulls a little more
    // source audio than the 48 kHz frames it produces. Keep this much extra buffered before
    // declaring frames "available" so the resampler never reads past captured data (which would
    // splice in zeros and crackle).
    private static readonly TimeSpan ResamplerReadMargin = TimeSpan.FromMilliseconds(20);

    private TimestampedWasapiCapture? _loopback;
    private TimestampedWasapiCapture? _mic;
    private MixingSampleProvider? _mixer;
    private IWaveProvider? _output;
    private bool _disposed;
    private bool _paused;
    private int _systemMuted;
    private int _microphoneMuted;

    /// <param name="limitMicrophone">
    /// When true, a soft-knee limiter (<see cref="SoftKneeLimiterSampleProvider"/>) is applied to
    /// the microphone source before mixing so hot input rounds off instead of hard-clipping.
    /// System/loopback audio is never limited.
    /// </param>
    /// <param name="userOffset">
    /// Manual A/V correction applied to every source. Positive delays audio relative to video.
    /// </param>
    public AudioCaptureService(bool captureSystem, bool captureMic, string? micDeviceId, bool limitMicrophone, TimeSpan userOffset = default)
    {
        _captureSystem = captureSystem;
        _captureMic = captureMic;
        _micDeviceId = micDeviceId;
        _limitMicrophone = limitMicrophone;
        _userOffset = userOffset;
    }

    /// <summary>True once at least one requested source started successfully.</summary>
    public bool IsActive { get; private set; }

    public bool CanMuteSystemAudio => _loopback is not null;

    public bool CanMuteMicrophone => _mic is not null;

    public bool IsSystemAudioMuted => Volatile.Read(ref _systemMuted) != 0;

    public bool IsMicrophoneMuted => Volatile.Read(ref _microphoneMuted) != 0;

    public void SetSystemAudioMuted(bool muted)
    {
        if (CanMuteSystemAudio)
        {
            Volatile.Write(ref _systemMuted, muted ? 1 : 0);
        }
    }

    public void SetMicrophoneMuted(bool muted)
    {
        if (CanMuteMicrophone)
        {
            Volatile.Write(ref _microphoneMuted, muted ? 1 : 0);
        }
    }

    /// <summary>
    /// Starts the requested capture sources. Each source is best-effort: if the microphone
    /// is denied or a device is missing, the other source still records. Returns true if any
    /// source started.
    /// </summary>
    public bool TryStart()
    {
        _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels))
        {
            ReadFully = true,
        };

        if (_captureSystem)
        {
            TryStartSource(isLoopback: true);
        }

        if (_captureMic)
        {
            TryStartSource(isLoopback: false);
        }

        if (!IsActive)
        {
            return false;
        }

        _output = new SampleToWaveProvider16(_mixer);
        return true;
    }

    private void TryStartSource(bool isLoopback)
    {
        var sourceName = isLoopback ? "system/loopback" : "microphone";
        try
        {
            var capture = CreateCapture(isLoopback);

            var buffer = new TimelineAlignedWaveProvider(capture.WaveFormat, sourceName)
            {
                UserOffset = _userOffset,
            };

            capture.DataAvailable += (data, count, sourceTimestamp, discontinuity) =>
            {
                lock (_gate)
                {
                    if (!_disposed && !_paused)
                    {
                        buffer.AddSamples(data, count, sourceTimestamp, discontinuity);
                    }
                }
            };

            ISampleProvider source = ToStereo48k(buffer.ToSampleProvider());
            if (!isLoopback && _limitMicrophone)
            {
                source = new SoftKneeLimiterSampleProvider(source);
            }

            var provider = new MuteableSampleProvider(
                source,
                () => isLoopback
                    ? Volatile.Read(ref _systemMuted) != 0
                    : Volatile.Read(ref _microphoneMuted) != 0);
            _mixer!.AddMixerInput(provider);

            capture.Start();

            // Now that the audio client is initialized, propagate its capture latency so the
            // provider can advance recorded audio into sync with the video timeline.
            buffer.Latency = capture.CaptureLatency;

            lock (_gate)
            {
                _buffers.Add(buffer);
            }

            if (isLoopback)
            {
                _loopback = capture;
            }
            else
            {
                _mic = capture;
            }

            IsActive = true;
            WebcamDiagnostics.Log($"Audio source '{sourceName}' started: {capture.WaveFormat.SampleRate}Hz {capture.WaveFormat.Channels}ch {capture.WaveFormat.BitsPerSample}bit {capture.WaveFormat.Encoding}{(!isLoopback ? $" limiter={(_limitMicrophone ? "on" : "off")}" : string.Empty)}.");
        }
        catch (Exception ex)
        {
            // Best-effort: a missing/denied device simply means that source is skipped.
            WebcamDiagnostics.Log($"Audio source '{sourceName}' failed to start: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private TimestampedWasapiCapture CreateCapture(bool isLoopback)
    {
        using var enumerator = new MMDeviceEnumerator();
        if (isLoopback)
        {
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return new TimestampedWasapiCapture(device, isLoopback: true);
        }

        if (!string.IsNullOrEmpty(_micDeviceId))
        {
            var device = enumerator.GetDevice(_micDeviceId);
            return new TimestampedWasapiCapture(device, isLoopback: false);
        }

        return new TimestampedWasapiCapture(
            enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console),
            isLoopback: false);
    }

    /// <summary>
    /// Coerces an arbitrary capture source to 48 kHz stereo float so it can feed the mixer.
    /// </summary>
    private static ISampleProvider ToStereo48k(ISampleProvider source)
    {
        if (source.WaveFormat.SampleRate != SampleRate)
        {
            source = new WdlResamplingSampleProvider(source, SampleRate);
        }

        return source.WaveFormat.Channels switch
        {
            1 => new MonoToStereoSampleProvider(source),
            2 => source,
            _ => SelectFirstTwoChannels(source),
        };
    }

    private static ISampleProvider SelectFirstTwoChannels(ISampleProvider source)
    {
        var multiplexer = new MultiplexingSampleProvider(new[] { source }, Channels);
        multiplexer.ConnectInputToOutput(0, 0);
        multiplexer.ConnectInputToOutput(1, 1);
        return multiplexer;
    }

    /// <summary>
    /// The number of fully-captured, timeline-aligned output frames currently ready across all
    /// active sources (the minimum, since the mixer advances every source in lockstep). Used to
    /// pace the muxer to real capture progress so audio is never padded ahead of real time
    /// (which would race the audio track ~1s ahead) nor read from an empty buffer (which splices
    /// in silence and crackles). Audio captured before a pause remains available while paused.
    /// </summary>
    public int AvailableFrames
    {
        get
        {
            lock (_gate)
            {
                if (_disposed || _buffers.Count == 0)
                {
                    return 0;
                }

                var min = TimeSpan.MaxValue;
                foreach (var buffer in _buffers)
                {
                    var buffered = buffer.BufferedDuration;
                    if (buffer.WaveFormat.SampleRate != SampleRate)
                    {
                        buffered -= ResamplerReadMargin;
                    }

                    if (buffered < min)
                    {
                        min = buffered;
                    }
                }

                if (min == TimeSpan.MaxValue || min <= TimeSpan.Zero)
                {
                    return 0;
                }

                return (int)(min.TotalSeconds * SampleRate);
            }
        }
    }

    /// <summary>Snapshot of per-source sync bookkeeping for diagnostics.</summary>
    internal IReadOnlyList<TimelineAlignedWaveProvider.SyncStats> GetSyncStats()
    {
        lock (_gate)
        {
            return _buffers.Select(b => b.GetStats()).ToList();
        }
    }

    /// <summary>Packets the WASAPI drivers flagged as following lost data, across both sources.</summary>
    public long DriverDiscontinuityCount =>
        (_loopback?.DiscontinuityCount ?? 0) + (_mic?.DiscontinuityCount ?? 0);

    /// <summary>
    /// Reads up to <paramref name="frameCount"/> frames (samples per channel) of mixed audio
    /// as interleaved 16-bit stereo PCM. Returns a silence-padded full buffer while active, so
    /// the only <c>null</c> result is after disposal. (Pausing does not block reads: audio that
    /// was captured before the pause still has to reach the muxer.)
    /// </summary>
    public byte[]? ReadChunk(int frameCount)
    {
        lock (_gate)
        {
            if (_disposed || _output is null || frameCount <= 0)
            {
                return null;
            }

            var bytesWanted = frameCount * Channels * (BitsPerSample / 8);
            var buffer = new byte[bytesWanted];
            var read = _output.Read(buffer, 0, bytesWanted);
            if (read <= 0)
            {
                return null;
            }

            if (read < bytesWanted)
            {
                Array.Resize(ref buffer, read);
            }

            return buffer;
        }
    }

    /// <summary>
    /// Anchors every active source to the shared recording clock. Packets captured before the
    /// origin are trimmed; sources that begin later retain their exact leading-silence offset.
    /// </summary>
    internal void BeginTimeline(RecordingTimeline timeline)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            foreach (var buffer in _buffers)
            {
                buffer.BeginTimeline(timeline);
            }
        }
    }

    internal void Pause()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _paused = true;
            foreach (var buffer in _buffers)
            {
                buffer.Pause();
            }
        }
    }

    internal void Resume()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            foreach (var buffer in _buffers)
            {
                buffer.Resume();
            }

            _paused = false;
        }
    }

    public void Stop()
    {
        try
        {
            _loopback?.Stop();
        }
        catch
        {
            // Ignore stop failures during teardown.
        }

        try
        {
            _mic?.Stop();
        }
        catch
        {
            // Ignore stop failures during teardown.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            _disposed = true;
        }

        Stop();
        _loopback?.Dispose();
        _loopback = null;
        _mic?.Dispose();
        _mic = null;
        _output = null;
        _mixer = null;
        _buffers.Clear();
    }

    private sealed class MuteableSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly Func<bool> _isMuted;

        public MuteableSampleProvider(ISampleProvider source, Func<bool> isMuted)
        {
            _source = source;
            _isMuted = isMuted;
        }

        public WaveFormat WaveFormat => _source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            var read = _source.Read(buffer, offset, count);
            if (read > 0 && _isMuted())
            {
                Array.Clear(buffer, offset, read);
            }

            return read;
        }
    }
}
