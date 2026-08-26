namespace TinyClips.Core.Services.ClipsLibrary;

/// <summary>Resolves where the Clips Library keeps its private data (metadata index, thumbnails).</summary>
public static class ClipsLibraryPaths
{
    /// <summary>
    /// Packaged apps get the package-scoped LocalFolder; unpackaged or test hosts fall back to
    /// %LocalAppData%. Both are per-user and survive app updates.
    /// </summary>
    public static string LocalDataDirectory()
    {
        try
        {
            return Windows.Storage.ApplicationData.Current.LocalFolder.Path;
        }
        catch
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }
    }

    public static string ThumbnailCacheDirectory() => Path.Combine(LocalDataDirectory(), "TinyClips", "thumbnails");
}
