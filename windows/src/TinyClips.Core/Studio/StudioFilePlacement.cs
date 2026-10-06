namespace TinyClips.Core.Studio;

/// <summary>Gives a finished file the name it is saved under, without ever replacing a file.</summary>
internal static class StudioFilePlacement
{
    /// <summary>How many names a finished file is tried under before it is given up.</summary>
    public const int Attempts = 5;

    /// <summary>
    /// Renames <paramref name="stagedPath"/> to <paramref name="wantedPath"/> and returns the name
    /// it got. It never takes the place of a file that is there: when something else was saved
    /// under the name in the meantime, that keeps it, and this file gets the name the app would
    /// give one saved now.
    /// </summary>
    /// <param name="createOutputPath">Asked for another name each time one turns out to be taken.</param>
    /// <param name="whileWhat">What took the time in which the name was taken, for the error: "the video was being made".</param>
    public static string Place(string stagedPath, string wantedPath, Func<string> createOutputPath, string whileWhat)
    {
        var path = wantedPath;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // Without the right to replace: this fails where a file already has the name.
                File.Move(stagedPath, path);
                return path;
            }
            catch (IOException) when (File.Exists(path) && File.Exists(stagedPath))
            {
                if (attempt >= Attempts)
                {
                    throw new IOException(
                        $"Another file was saved as {Path.GetFileName(path)} while {whileWhat}, and no free name was found for the video.");
                }

                path = createOutputPath();
            }
        }
    }
}
