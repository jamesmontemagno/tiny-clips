namespace TinyClips.Core.Studio;

/// <summary>
/// Where Save project and Save project as start: the folder chosen in Settings, or a
/// <c>Studio</c> folder inside the videos folder. A project already saved to a folder keeps
/// saving there; this is only where the picker opens. Drafts and recordings stay in the app's
/// own storage.
/// </summary>
public static class StudioProjectSaveLocation
{
    /// <summary>The name of the default folder, inside the videos folder.</summary>
    public const string DefaultFolderName = "Studio";

    /// <summary>The default folder: <c>Studio</c> inside <paramref name="videoFolder"/>.</summary>
    public static string GetDefaultFolder(string videoFolder) =>
        Path.Combine(videoFolder, DefaultFolderName);

    /// <summary>Whether <paramref name="configured"/> names a folder of the user's own choosing.</summary>
    public static bool IsCustom(string? configured) =>
        !string.IsNullOrWhiteSpace(configured) && Path.IsPathFullyQualified(configured);

    /// <summary>
    /// The folder Save project starts in: the chosen one, or the default when none was chosen.
    /// Does not touch the disk.
    /// </summary>
    public static string Resolve(string? configured, string videoFolder, bool useDefaults = false) =>
        !useDefaults && IsCustom(configured) ? configured! : GetDefaultFolder(videoFolder);

    /// <summary>
    /// The folder Save project starts in, made ready to open: a chosen folder that is gone falls
    /// back to the default, the default is created when missing, and when it cannot be the
    /// videos folder and then Documents are used instead.
    /// </summary>
    public static string Prepare(string? configured, string videoFolder, bool useDefaults = false)
    {
        if (!useDefaults && IsCustom(configured) && Directory.Exists(configured))
        {
            return configured!;
        }

        var studio = GetDefaultFolder(videoFolder);
        try
        {
            Directory.CreateDirectory(studio);
            return studio;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Directory.Exists(videoFolder)
                ? videoFolder
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }
    }
}
