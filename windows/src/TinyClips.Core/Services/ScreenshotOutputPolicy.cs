namespace TinyClips.Core.Services;

public enum ScreenshotOutput
{
    SaveToDisk,
    CopyToClipboard,
    OpenEditor,
}

/// <summary>A snapshot of the three independent interactive screenshot outputs.</summary>
public readonly record struct ScreenshotOutputSelection(bool SaveToDisk, bool CopyToClipboard, bool OpenEditor)
{
    public bool HasAnyOutput => SaveToDisk || CopyToClipboard || OpenEditor;
}

/// <summary>The outputs that can be presented for this particular captured image.</summary>
public readonly record struct ScreenshotCapturePlan(ScreenshotOutputSelection Selection, bool FitsEditor)
{
    public bool OpenEditor => Selection.OpenEditor && FitsEditor;
    public bool EditorUnavailable => Selection.OpenEditor && !FitsEditor;

    public static ScreenshotCapturePlan Create(ScreenshotOutputSelection selection, bool fitsEditor)
    {
        if (!selection.HasAnyOutput)
        {
            throw new InvalidOperationException(ScreenshotOutputPolicy.LastOutputExplanation);
        }

        return new ScreenshotCapturePlan(selection, fitsEditor);
    }
}

/// <summary>Applies an individual screenshot-output change without disabling the last output.</summary>
public static class ScreenshotOutputPolicy
{
    public const string LastOutputExplanation =
        "Turn on another screenshot option first. Saving, copying, or the editor must stay on.";

    public static ScreenshotOutputSelection Read(ICaptureSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new(settings.SaveScreenshotToDisk, settings.CopyScreenshotToClipboard, settings.ShowScreenshotEditor);
    }

    /// <summary>Returns false without writing any settings when the last enabled output would be disabled.</summary>
    public static bool TrySet(ICaptureSettings settings, ScreenshotOutput output, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var (current, hasOtherOutput) = output switch
        {
            ScreenshotOutput.SaveToDisk =>
                (settings.SaveScreenshotToDisk, settings.CopyScreenshotToClipboard || settings.ShowScreenshotEditor),
            ScreenshotOutput.CopyToClipboard =>
                (settings.CopyScreenshotToClipboard, settings.SaveScreenshotToDisk || settings.ShowScreenshotEditor),
            ScreenshotOutput.OpenEditor =>
                (settings.ShowScreenshotEditor, settings.SaveScreenshotToDisk || settings.CopyScreenshotToClipboard),
            _ => throw new ArgumentOutOfRangeException(nameof(output)),
        };

        if (current == enabled)
        {
            return true;
        }

        if (!enabled && !hasOtherOutput)
        {
            return false;
        }

        switch (output)
        {
            case ScreenshotOutput.SaveToDisk:
                settings.SaveScreenshotToDisk = enabled;
                break;
            case ScreenshotOutput.CopyToClipboard:
                settings.CopyScreenshotToClipboard = enabled;
                break;
            case ScreenshotOutput.OpenEditor:
                settings.ShowScreenshotEditor = enabled;
                break;
        }

        return true;
    }
}
