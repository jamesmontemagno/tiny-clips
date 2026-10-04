using TinyClips.Core.Studio.Rendering;

namespace TinyClips.Core.Studio.Preview;

/// <summary>
/// What an open that failed is worth: one more attempt, when what went wrong is likely to pass.
/// </summary>
/// <remarks>
/// <para>
/// Opening can be tried again at no cost to anybody: nothing has been shown, and nobody has been
/// told anything. Two kinds of failure are tried again, once.
/// </para>
/// <para>
/// A graphics device that was lost. One that is lost once the preview is open is rebuilt, once,
/// and this is the same answer. Measured on the graphics hardware: a player's first copy took
/// 93 ms and failed with <c>DXGI_ERROR_DEVICE_REMOVED</c> in two of some 2,400 previews opened one
/// straight after the other, with nothing wrong with the adapter, and the open that followed
/// each succeeded.
/// </para>
/// <para>
/// A player that failed although every player had already handed over a frame. The frames show
/// that the files can be decoded, so the failure is the player's and not the file's. Measured on
/// the graphics hardware: the camera's player reported <c>MF_E_INVALIDREQUEST</c> during the
/// first position change, once in several thousand previews opened one straight after the other.
/// </para>
/// <para>
/// Everything else fails at once: a file that is missing, a file no player gets a frame out of,
/// an open that timed out or was cancelled.
/// </para>
/// </remarks>
internal static class StudioPreviewOpenFailure
{
    /// <summary>
    /// Why <paramref name="failure"/> is worth another attempt to open, in words for the trace,
    /// or null when it is not.
    /// </summary>
    /// <param name="everyPlayerDeliveredAFrame">Whether each player had handed over at least one frame before the open failed.</param>
    public static string? WorthAnotherAttempt(Exception failure, bool everyPlayerDeliveredAFrame)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (failure is OperationCanceledException)
        {
            return null;
        }

        if (IsDeviceLost(failure))
        {
            return "a graphics device was lost";
        }

        return failure is InvalidDataException && everyPlayerDeliveredAFrame
            ? "a player failed after every player had handed over a frame"
            : null;
    }

    /// <summary>Whether an exception, or one it was caused by, says that the graphics device has to be created again.</summary>
    public static bool IsDeviceLost(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is StudioDeviceLostException)
            {
                return true;
            }

            switch (unchecked((uint)current.HResult))
            {
                case 0x887A0005: // DXGI_ERROR_DEVICE_REMOVED
                case 0x887A0006: // DXGI_ERROR_DEVICE_HUNG
                case 0x887A0007: // DXGI_ERROR_DEVICE_RESET
                case 0x887A0020: // DXGI_ERROR_DRIVER_INTERNAL_ERROR
                case 0x8899000C: // D2DERR_RECREATE_TARGET
                    return true;
            }
        }

        return false;
    }
}
