namespace TinyClips.Core.Models;

/// <summary>The capture experience shown by the picker, independently of its output format.</summary>
public enum CapturePickerIntent
{
    Screenshot,
    Video,
    Gif,
    Studio,
}

public static class CapturePickerIntentExtensions
{
    public static CaptureType GetCaptureType(this CapturePickerIntent intent) => intent switch
    {
        CapturePickerIntent.Screenshot => CaptureType.Screenshot,
        CapturePickerIntent.Video or CapturePickerIntent.Studio => CaptureType.Video,
        CapturePickerIntent.Gif => CaptureType.Gif,
        _ => throw new ArgumentOutOfRangeException(nameof(intent), intent, null),
    };

    public static string GetLabel(this CapturePickerIntent intent) => intent switch
    {
        CapturePickerIntent.Screenshot => "Screenshot",
        CapturePickerIntent.Video => "Video",
        CapturePickerIntent.Gif => "GIF",
        CapturePickerIntent.Studio => "Studio",
        _ => throw new ArgumentOutOfRangeException(nameof(intent), intent, null),
    };
}
