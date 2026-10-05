using TinyClips.Core.Studio.Rendering;

namespace TinyClips.Core.Studio.Preview;

/// <summary>
/// What an open that failed is worth: one more attempt, when what went wrong is likely to pass.
/// </summary>
/// <remarks>
/// <para>
/// Opening can be tried again at no cost to anybody but time: nothing has been shown, and nobody
/// has been told anything. Three kinds of failure are tried again, once, after a wait
/// (<see cref="StudioPreviewOptions.SecondAttemptWait"/>).
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
/// A player that stopped decoding before every player had handed over a frame: it reported that
/// it could not decode what it had opened, which is not what it says of a file that is no video.
/// Seen from the editor's window: two editors opened 0.4 s apart both failed that way within
/// 0.3 s (<c>DecodingError</c>), and the third, 0.4 s later, opened. A file that really cannot
/// be decoded from its first frame on costs the wait and a second look before the user is told.
/// </para>
/// <para>
/// The wait is there because what passes does not pass at once. Measured in 4,000 previews
/// opened straight after another was closed: one lost its device in a player's copy, and so
/// did the second attempt, made at once, 0.3 s later. The preview opened after that, whose
/// players made their first copies half a second after the first failure, was fine.
/// </para>
/// <para>
/// Everything else fails at once: a file that is missing, a file that is no video, a file no
/// player gets a frame out of in the time allowed, an open that timed out or was cancelled.
/// </para>
/// </remarks>
internal static class StudioPreviewOpenFailure
{
    /// <summary>
    /// Why <paramref name="failure"/> is worth another attempt to open, in words for the trace,
    /// or null when it is not.
    /// </summary>
    /// <param name="everyPlayerDeliveredAFrame">Whether each player had handed over at least one frame before the open failed.</param>
    /// <param name="aPlayerStoppedDecoding">
    /// Whether a player reported that it could not decode what it had opened
    /// (<c>MediaPlayerError.DecodingError</c>, or an error it could not name), as against a
    /// source it does not support.
    /// </param>
    public static string? WorthAnotherAttempt(Exception failure, bool everyPlayerDeliveredAFrame, bool aPlayerStoppedDecoding = false)
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

        if (failure is not InvalidDataException)
        {
            return null;
        }

        return everyPlayerDeliveredAFrame ? "a player failed after every player had handed over a frame"
            : aPlayerStoppedDecoding ? "a player stopped decoding before every player had handed over a frame"
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
