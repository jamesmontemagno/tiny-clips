using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage.Streams;

namespace TinyClips.Core.Capture;

/// <summary>
/// Copies borrowed top-down capture pixels into independently owned encoder memory. No encoder,
/// queued sample, or retained snapshot can reference the reusable capture/overlay buffer.
/// </summary>
internal static class CpuVideoBuffer
{
    public static IBuffer Create(CapturedFrame frame)
    {
        var length = GetLength(frame);
        // The transcoder may retain its IBuffer asynchronously. Keep one independently owned,
        // GC-accounted array rather than pooling it or hiding per-frame memory in WinRT buffers.
        var pixels = GC.AllocateUninitializedArray<byte>(length);
        var rowBytes = frame.Width * 4;
        for (var row = 0; row < frame.Height; row++)
        {
            System.Buffer.BlockCopy(
                frame.BgraPixels, (frame.Height - 1 - row) * rowBytes,
                pixels, row * rowBytes, rowBytes);
        }

        return pixels.AsBuffer();
    }

    public static unsafe void CopyBottomUp(CapturedFrame frame, IntPtr destination, int capacity)
    {
        var length = GetLength(frame);
        if (destination == IntPtr.Zero || capacity < length)
        {
            throw new ArgumentException("The encoder buffer cannot hold the captured frame.", nameof(destination));
        }

        var rowBytes = frame.Width * 4;
        fixed (byte* source = frame.BgraPixels)
        {
            for (var row = 0; row < frame.Height; row++)
            {
                System.Buffer.MemoryCopy(
                    source + ((long)(frame.Height - 1 - row) * rowBytes),
                    (byte*)destination + ((long)row * rowBytes),
                    rowBytes,
                    rowBytes);
            }
        }
    }

    public static int GetLength(CapturedFrame frame)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frame.Width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frame.Height);
        var length = checked(frame.Width * frame.Height * 4);
        if (frame.BgraPixels.Length < length)
        {
            throw new ArgumentException("The captured frame does not contain tightly packed BGRA pixels.", nameof(frame));
        }

        return length;
    }
}
