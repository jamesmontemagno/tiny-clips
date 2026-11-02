using TinyClips.Core.Studio;
using Windows.Media.MediaProperties;
using Windows.Storage;

namespace TinyClips.App.Services.Studio;

/// <summary>Reads what a flat Studio project needs to know about an existing video.</summary>
internal static class StudioVideoProbe
{
    private const string FrameRateProperty = "System.Video.FrameRate";

    /// <exception cref="InvalidOperationException">The file has no readable video.</exception>
    public static async Task<StudioRecordingSourceInfo> ReadAsync(string videoPath)
    {
        var file = await StorageFile.GetFileFromPathAsync(videoPath);
        var video = await file.Properties.GetVideoPropertiesAsync();
        var width = (int)video.Width;
        var height = (int)video.Height;
        // The picture is measured as it is shown, so a sideways video swaps its sides.
        if (video.Orientation is VideoOrientation.Rotate90 or VideoOrientation.Rotate270)
        {
            (width, height) = (height, width);
        }

        var seconds = video.Duration.TotalSeconds;
        if (width < 1 || height < 1 || !double.IsFinite(seconds) || seconds <= 0)
        {
            throw new InvalidOperationException("The file has no readable video.");
        }

        var frameRate = 30.0;
        try
        {
            var values = await file.Properties.RetrievePropertiesAsync([FrameRateProperty]);
            // Stored in thousandths of a frame per second.
            if (values.TryGetValue(FrameRateProperty, out var raw) && raw is uint thousandths && thousandths > 0)
            {
                frameRate = thousandths / 1000.0;
            }
        }
        catch (Exception ex) when (ex is InvalidCastException or System.Runtime.InteropServices.COMException)
        {
            // The default stands when the rate is not stored.
        }

        return new StudioRecordingSourceInfo(width, height, seconds, frameRate);
    }
}
