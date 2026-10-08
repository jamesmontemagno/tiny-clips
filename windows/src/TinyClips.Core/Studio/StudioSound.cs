namespace TinyClips.Core.Studio;

/// <summary>
/// How loud the video's sound is (section 7 of the project format). Muting is not part of it: a
/// muted project has no sound at all, whatever its volume.
/// </summary>
public static class StudioSound
{
    /// <summary>A stored volume as it is used: between 0 and 1, and 1 when it is not a number.</summary>
    public static double Volume(double stored) =>
        double.IsNaN(stored) ? 1 : Math.Min(1, Math.Max(0, stored));

    /// <summary>
    /// What the sound of the video is multiplied by (<c>audio.volume</c>), from 0 (silent) to 1
    /// (as recorded). The preview and the export both take it from here.
    /// </summary>
    public static double Volume(StudioAudio audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        return Volume(audio.Volume);
    }
}
