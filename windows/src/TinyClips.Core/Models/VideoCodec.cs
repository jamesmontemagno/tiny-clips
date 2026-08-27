namespace TinyClips.Core.Models;

/// <summary>Video codec used for MP4 recordings.</summary>
public enum VideoCodec
{
    /// <summary>H.264 / AVC — universally playable.</summary>
    H264 = 0,

    /// <summary>H.265 / HEVC — ~40% smaller files at equal quality; needs an HEVC decoder to play.</summary>
    Hevc = 1,
}

/// <summary>Which Media Foundation front-end writes the MP4.</summary>
public enum VideoEncoderBackend
{
    /// <summary>
    /// <c>MediaTranscoder</c> + <c>MediaStreamSource</c> (pull model). Mature, but exposes no
    /// encoder tuning knobs and holds input frames for its full look-ahead window.
    /// </summary>
    Transcoder = 0,

    /// <summary>
    /// <c>IMFSinkWriter</c> with an <c>IMFDXGIDeviceManager</c> (push model). Enables low-latency
    /// encoder configuration (no B-frames, bounded GOP), HEVC, and MF-owned GPU frame recycling.
    /// </summary>
    SinkWriter = 1,
}
