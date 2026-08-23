namespace TinyClips.Core.Capture;

/// <summary>Reason a scrolling capture stopped growing before the user asked it to.</summary>
public enum PanoramaCaptureLimitReason
{
    Memory,
    OutputHeight,
    FrameCount,
}

public static class PanoramaCaptureLimitReasonExtensions
{
    public static string ToMessage(this PanoramaCaptureLimitReason reason) => reason switch
    {
        PanoramaCaptureLimitReason.Memory => "Memory limit reached, saving what was captured",
        PanoramaCaptureLimitReason.OutputHeight => "Maximum height reached, saving what was captured",
        PanoramaCaptureLimitReason.FrameCount => "Frame limit reached, saving what was captured",
        _ => "Limit reached, saving what was captured",
    };
}
