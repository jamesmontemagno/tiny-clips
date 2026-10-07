using System.Security;
using System.Text;
using System.Text.Json;

namespace TinyClips.Core.Studio;

/// <summary>Why a project could not be saved as a folder, or opened from one.</summary>
public enum StudioProjectFolderProblem
{
    /// <summary>The project is built around a video that is kept elsewhere, which a folder would not hold.</summary>
    ExternalSource,

    /// <summary>
    /// A recording the project names is not there: beside the <c>.tinyclips</c> file when one is
    /// opened, in the project's own folder when one is saved.
    /// </summary>
    MissingFile,

    /// <summary>
    /// Something is already where the folder would go, and it is not a saved project, or it is
    /// one and replacing it was not asked for.
    /// </summary>
    DestinationExists,

    /// <summary>
    /// What was chosen is not a <c>.tinyclips</c> file, or a folder with exactly one in it, or
    /// the file is larger than a project file gets.
    /// </summary>
    NotAProjectFile,

    /// <summary>
    /// The project file names a recording by something that is not the name of a file in its
    /// own folder: a path, a name Windows would take for something else, or a link that leads
    /// elsewhere.
    /// </summary>
    FileOutsideFolder,

    /// <summary>The project file was written by a later version of Tiny Clips than this one reads.</summary>
    NewerVersion,

    /// <summary>The project file cannot be read, or what is in it is not a project.</summary>
    Unreadable,
}

/// <summary>
/// Thrown when a project cannot be saved as a folder or opened from one, for a reason the
/// project or the folder gives. <see cref="Problem"/> says which, for the app to put into its
/// own words. A disk that is full, a folder that may not be written, or a recording that may
/// not be read while it is copied is not one of them: those are the <see cref="IOException"/>
/// or <see cref="UnauthorizedAccessException"/> the system gave.
/// </summary>
public sealed class StudioProjectFolderException : Exception
{
    private StudioProjectFolderException(StudioProjectFolderProblem problem, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Problem = problem;
    }

    public StudioProjectFolderProblem Problem { get; }

    /// <summary>The name of the file that is not there, for <see cref="StudioProjectFolderProblem.MissingFile"/>.</summary>
    public string? FileName { get; private init; }

    /// <summary>
    /// The property whose value is not the name of a file in the folder, such as
    /// <c>sources.screen.file</c>, for <see cref="StudioProjectFolderProblem.FileOutsideFolder"/>.
    /// </summary>
    public string? Property { get; private init; }

    /// <summary>The version the file says it is, for <see cref="StudioProjectFolderProblem.NewerVersion"/>.</summary>
    public int? SchemaVersion { get; private init; }

    internal static StudioProjectFolderException ExternalSource() =>
        new(
            StudioProjectFolderProblem.ExternalSource,
            "This project is built around a video that is kept somewhere else, so it cannot be saved as a folder or opened from one.");

    internal static StudioProjectFolderException MissingFile(string fileName) =>
        new(
            StudioProjectFolderProblem.MissingFile,
            $"{fileName} is not in the project's folder. A .tinyclips file opens only next to the recordings it was saved with.")
        {
            FileName = fileName,
        };

    internal static StudioProjectFolderException DestinationExists() =>
        new(
            StudioProjectFolderProblem.DestinationExists,
            "There is already something with that name, and it is not a saved Tiny Clips project. Choose another name.");

    internal static StudioProjectFolderException NotAProjectFile() =>
        new(StudioProjectFolderProblem.NotAProjectFile, "Choose a .tinyclips file, or the folder that holds one.");

    internal static StudioProjectFolderException FileOutsideFolder(string property, Exception? innerException = null) =>
        new(
            StudioProjectFolderProblem.FileOutsideFolder,
            $"This project file names a recording that is not a file in its own folder ({property}), so it was not opened.",
            innerException)
        {
            Property = property,
        };

    internal static StudioProjectFolderException NewerVersion(int schemaVersion, Exception? innerException = null) =>
        new(
            StudioProjectFolderProblem.NewerVersion,
            "This project was saved by a newer version of Tiny Clips. Update Tiny Clips to open it.",
            innerException)
        {
            SchemaVersion = schemaVersion,
        };

    internal static StudioProjectFolderException Unreadable(Exception? innerException = null) =>
        new(
            StudioProjectFolderProblem.Unreadable,
            "This file could not be read as a Tiny Clips project. It may be damaged.",
            innerException);

    internal static StudioProjectFolderException SameFileTwice(string fileName) =>
        new(
            StudioProjectFolderProblem.Unreadable,
            $"This file could not be read as a Tiny Clips project: it names {fileName} for more than one thing.");
}

/// <summary>
/// What a project saved as a folder is made of (section 14 of the project format): the file
/// that carries its metadata, the name of its folder, and which names in it are followed.
/// </summary>
public static class StudioProjectFolder
{
    /// <summary>The extension of the file that carries the metadata of a project saved as a folder.</summary>
    public const string ProjectFileExtension = ".tinyclips";

    /// <summary>The largest <c>.tinyclips</c> file that is read. A project's own is a few kilobytes.</summary>
    public const long LargestProjectFileBytes = 16L << 20;

    /// <summary>What a folder is called when the project's name leaves nothing to call it.</summary>
    public const string DefaultFolderName = "Tiny Clips Project";

    /// <summary>
    /// The longest name <see cref="FolderName"/> gives. The name is in the path twice, as the
    /// folder and as the file in it, and a path may only be so long.
    /// </summary>
    public const int LongestFolderName = 80;

    private static readonly char[] InvalidFileNameCharacters = Path.GetInvalidFileNameChars();

    // What Windows takes for a device and not a file, whatever folder it is in and whatever
    // extension follows it.
    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM\u00B9", "COM\u00B2", "COM\u00B3",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT\u00B9", "LPT\u00B2", "LPT\u00B3",
    };

    /// <summary>
    /// A name the folder of a saved project can have: the project's, without what a path cannot
    /// hold, without the dots and spaces Windows drops from the ends of a name, and not one
    /// that would hide the folder or that Windows takes for a device.
    /// </summary>
    public static string FolderName(string? projectName)
    {
        var builder = new StringBuilder(projectName?.Length ?? 0);
        foreach (var character in projectName ?? string.Empty)
        {
            // The three ends of a line that a file name may hold and should not.
            var kept = Array.IndexOf(InvalidFileNameCharacters, character) < 0 && character is not ('\u0085' or '\u2028' or '\u2029');
            builder.Append(kept ? character : '-');
        }

        var cleaned = TrimFolderName(builder.ToString());
        if (cleaned.Length > LongestFolderName)
        {
            // Not through the middle of a character that takes two.
            var length = char.IsHighSurrogate(cleaned[LongestFolderName - 1]) ? LongestFolderName - 1 : LongestFolderName;
            cleaned = TrimFolderName(cleaned[..length]);
        }

        if (IsDeviceName(cleaned))
        {
            cleaned = cleaned.Insert(DeviceNameLength(cleaned), "-");
        }

        return cleaned.Length == 0 ? DefaultFolderName : cleaned;
    }

    /// <summary>
    /// The <c>.tinyclips</c> file that was chosen: itself, or the only one in a folder that was.
    /// A folder counts every entry with that extension, hidden ones too, and has to have exactly
    /// one, which has to be a file.
    /// </summary>
    /// <returns>The full path of the file.</returns>
    /// <exception cref="StudioProjectFolderException">
    /// <see cref="StudioProjectFolderProblem.NotAProjectFile"/>: there is nothing at the path,
    /// or a file of another kind, or a folder with no such file or more than one.
    /// </exception>
    public static string FindProjectFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw StudioProjectFolderException.NotAProjectFile();
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw StudioProjectFolderException.NotAProjectFile();
        }

        if (File.Exists(fullPath))
        {
            return HasProjectFileExtension(fullPath) ? fullPath : throw StudioProjectFolderException.NotAProjectFile();
        }

        if (!Directory.Exists(fullPath))
        {
            throw StudioProjectFolderException.NotAProjectFile();
        }

        var found = Directory.EnumerateFileSystemEntries(fullPath).Where(HasProjectFileExtension).Take(2).ToArray();
        return found.Length == 1 && File.Exists(found[0]) ? found[0] : throw StudioProjectFolderException.NotAProjectFile();
    }

    /// <summary>
    /// Whether a folder is a project that was saved as one: a folder with exactly one
    /// <c>.tinyclips</c> file in it. Only such a folder is ever replaced by a save. A folder
    /// that cannot be looked into is not one.
    /// </summary>
    public static bool IsSavedProjectFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return false;
        }

        try
        {
            return Directory.Exists(folder) && FindProjectFile(folder).Length > 0;
        }
        catch (Exception ex) when (ex is StudioProjectFolderException or IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether a name from a project file is followed: a plain file name (section 1 of the
    /// format) that Windows takes for a file of that name in the folder and nothing else. A
    /// colon would name a drive or another stream of a file, a dot or a space at the end is
    /// dropped by Windows so that the name means another file, and a name such as
    /// <c>NUL</c> is a device.
    /// </summary>
    internal static bool IsFileNameInFolder(string? fileName) =>
        fileName is not null
        && StudioProjectJson.IsPlainFileName(fileName)
        && fileName.IndexOfAny(InvalidFileNameCharacters) < 0
        && fileName[^1] is not ('.' or ' ')
        && !IsDeviceName(fileName);

    /// <summary>
    /// Whether what is at a path is a link to somewhere else. Copying it would copy what it
    /// leads to, which need not be in the folder.
    /// </summary>
    internal static bool IsLink(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads a <c>.tinyclips</c> file as a project, by the rules <c>project.json</c> is read by.
    /// </summary>
    /// <exception cref="StudioProjectFolderException">
    /// The file is larger than a project file gets, cannot be read, is not a project, names a
    /// recording by more than a file name, or was written by a later version.
    /// </exception>
    internal static StudioProject ReadProjectFile(string file)
    {
        string text;
        try
        {
            // No one may write to it meanwhile, so the length is the length that is read.
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > LargestProjectFileBytes)
            {
                throw StudioProjectFolderException.NotAProjectFile();
            }

            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or NotSupportedException)
        {
            throw StudioProjectFolderException.Unreadable(ex);
        }

        try
        {
            return StudioProjectJson.ReadProject(text);
        }
        catch (StudioUnsupportedSchemaVersionException ex)
        {
            throw StudioProjectFolderException.NewerVersion(ex.SchemaVersion, ex);
        }
        catch (StudioProjectInvalidException ex) when (ex.FileNameProperty is { } property)
        {
            throw StudioProjectFolderException.FileOutsideFolder(property, ex);
        }
        catch (Exception ex) when (ex is StudioProjectInvalidException or JsonException)
        {
            throw StudioProjectFolderException.Unreadable(ex);
        }
    }

    private static bool HasProjectFileExtension(string path) =>
        string.Equals(Path.GetExtension(path), ProjectFileExtension, StringComparison.OrdinalIgnoreCase);

    private static bool IsDeviceName(string name) => DeviceNames.Contains(name[..DeviceNameLength(name)].TrimEnd(' '));

    // A device is named by what comes before the first dot.
    private static int DeviceNameLength(string name)
    {
        var dot = name.IndexOf('.');
        return dot < 0 ? name.Length : dot;
    }

    // Windows drops dots and spaces from the end of a name, and a dot at the front hides a
    // folder on a Mac, where the same folder is meant to open.
    private static string TrimFolderName(string name)
    {
        while (true)
        {
            var trimmed = name.Trim().TrimStart('.').TrimEnd('.');
            if (trimmed.Length == name.Length)
            {
                return trimmed;
            }

            name = trimmed;
        }
    }
}
