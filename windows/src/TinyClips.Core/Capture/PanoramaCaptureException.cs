namespace TinyClips.Core.Capture;

public enum PanoramaCaptureError
{
    Cancelled,
    NoMovement,
    OutputTooLarge,
    MemoryLimit,
    NoFrames,
    AlignmentFailed,
}

public sealed class PanoramaCaptureException : Exception
{
    public PanoramaCaptureException(PanoramaCaptureError error)
        : base(Describe(error))
    {
        Error = error;
    }

    public PanoramaCaptureError Error { get; }

    public bool IsCancellation => Error == PanoramaCaptureError.Cancelled;

    private static string Describe(PanoramaCaptureError error) => error switch
    {
        PanoramaCaptureError.Cancelled => "Scrolling capture was cancelled.",
        PanoramaCaptureError.NoMovement => "Scrolling capture stopped because no movement was detected.",
        PanoramaCaptureError.OutputTooLarge => "Scrolling capture reached its maximum output size.",
        PanoramaCaptureError.MemoryLimit => "Scrolling capture reached its memory limit.",
        PanoramaCaptureError.NoFrames => "No frames were captured.",
        PanoramaCaptureError.AlignmentFailed => "Could not align the scrolling frames.",
        _ => "Scrolling capture failed.",
    };
}
