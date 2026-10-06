namespace TinyClips.Core.Capture;

/// <summary>
/// Session-private, exact-size BGRA buffers. The capture lock protects readback/copy operations;
/// the pump must serialize borrowed-frame processing until the synchronous consumer returns.
/// Published snapshots are never reused, even after their subscriber unsubscribes.
/// </summary>
internal sealed class CpuCaptureBuffers
{
    private CapturedFrame? _latest;
    private CapturedFrame? _processing;
    private bool _latestPublished;

    public CapturedFrame GetReadbackFrame(int width, int height, bool publishSnapshot)
    {
        if (_latestPublished || !Matches(_latest, width, height))
        {
            _latest = CreateFrame(width, height);
        }

        _latestPublished = publishSnapshot;
        return _latest!;
    }

    public CapturedFrame? CopyLatest(bool borrow)
    {
        if (_latest is not { } latest)
        {
            return null;
        }

        if (!borrow)
        {
            return new CapturedFrame((byte[])latest.BgraPixels.Clone(), latest.Width, latest.Height);
        }

        if (!Matches(_processing, latest.Width, latest.Height))
        {
            _processing = CreateFrame(latest.Width, latest.Height);
        }

        latest.BgraPixels.CopyTo(_processing!.BgraPixels, 0);
        return _processing;
    }

    public void Clear()
    {
        _latest = null;
        _processing = null;
        _latestPublished = false;
    }

    private static bool Matches(CapturedFrame? frame, int width, int height) =>
        frame is not null && frame.Width == width && frame.Height == height;

    private static CapturedFrame CreateFrame(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        return new CapturedFrame(new byte[checked(width * height * 4)], width, height);
    }
}
