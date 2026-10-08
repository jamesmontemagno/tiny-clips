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
    /// Something is already where the folder would go, and it is not a saved project whose
    /// project file can be read, or it is one and replacing it was not asked for.
    /// </summary>
    DestinationExists,

    /// <summary>
    /// A saved project is where the folder would go, replacing it was asked for, and it holds
    /// something a save does not write: a file the project in it does not name, a folder, or
    /// a link. Replacing the folder would take that with it, so it is left as it is.
    /// </summary>
    DestinationHasOtherFiles,

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

    /// <summary>
    /// The name of the file that is not there, for <see cref="StudioProjectFolderProblem.MissingFile"/>.
    /// For <see cref="StudioProjectFolderProblem.DestinationHasOtherFiles"/>, the name of what
    /// is in the folder and is not part of the project: the first by name, so that it is the
    /// same one each time.
    /// </summary>
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

    internal static StudioProjectFolderException DestinationExists(Exception? innerException = null) =>
        new(
            StudioProjectFolderProblem.DestinationExists,
            "There is already something with that name, and it is not a saved Tiny Clips project. Choose another name.",
            innerException);

    internal static StudioProjectFolderException DestinationHasOtherFiles(string fileName) =>
        new(
            StudioProjectFolderProblem.DestinationHasOtherFiles,
            $"This folder holds {fileName}, which is not part of the project, so it was not replaced. Choose another name.")
        {
            FileName = fileName,
        };

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

    // What a system leaves in a folder by itself: the Finder's and Explorer's own files. A
    // name that starts with "._" is what macOS writes beside a file on a volume that is not
    // its own.
    private static readonly string[] SystemLitter = [".DS_Store", "Thumbs.db", "desktop.ini"];
    private const string SystemLitterPrefix = "._";

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
    /// A folder has to have exactly one entry with that extension, which has to be a file. An
    /// entry whose name starts with a dot is not counted: a Mac writes <c>._Name.tinyclips</c>
    /// beside the file on a volume that is not its own, and does not count such names itself.
    /// A file that is only hidden counts like any other, and a file that is chosen itself is
    /// taken whatever its name.
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

        var found = Directory.EnumerateFileSystemEntries(fullPath).Where(IsCountedProjectFile).Take(2).ToArray();
        return found.Length == 1 && File.Exists(found[0]) ? found[0] : throw StudioProjectFolderException.NotAProjectFile();
    }

    /// <summary>
    /// Whether a folder is a project that was saved as one: a folder with exactly one
    /// <c>.tinyclips</c> file in it, counted as <see cref="FindProjectFile"/> counts. Only such
    /// a folder is ever replaced by a save, and not every one is:
    /// <see cref="WhyASaveWouldNotReplace"/> says whether this one would be. A folder that
    /// cannot be looked into is not one.
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
    /// Why a save that was asked to replace what is at a path would not, or null when it would
    /// (and when nothing is there). Asked by a save before it copies and again before it puts
    /// its folder in place, and there for the app to ask before it puts the question.
    /// </summary>
    /// <remarks>
    /// A folder is replaced only when it holds nothing a save does not write. It has exactly
    /// one counted <c>.tinyclips</c> file, that file reads as a project, and everything else
    /// in the folder, hidden or not, is a plain file, not a folder and not a link, that the
    /// project names (its screen recording, its camera recording when it has a camera, its
    /// events file or <c>events.json</c> when it names none, its background image) or that is
    /// the poster, or one of the files a system leaves in folders by itself:
    /// <c>.DS_Store</c>, <c>Thumbs.db</c>, <c>desktop.ini</c>, and any name that starts with
    /// <c>._</c>. Names are compared without case. So a folder that only happens to have one
    /// <c>.tinyclips</c> file in it is never taken with everything else it holds.
    /// </remarks>
    /// <returns>
    /// Null, or the refusal a save would throw:
    /// <see cref="StudioProjectFolderProblem.DestinationHasOtherFiles"/> with the first name
    /// that is not part of the project, or
    /// <see cref="StudioProjectFolderProblem.DestinationExists"/> for a file, a folder that is
    /// not a saved project, one whose project file cannot be read as a project (what the
    /// reader said is the inner exception), and a folder that cannot be looked into.
    /// </returns>
    public static StudioProjectFolderException? WhyASaveWouldNotReplace(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(folder);
            if (File.Exists(fullPath))
            {
                return StudioProjectFolderException.DestinationExists();
            }

            if (!Directory.Exists(fullPath))
            {
                return null;
            }

            // What is in the file says what belongs to it. A file that cannot be read says nothing.
            string projectFile;
            StudioProject project;
            try
            {
                projectFile = FindProjectFile(fullPath);
                project = ReadProjectFile(projectFile);
            }
            catch (StudioProjectFolderException ex)
            {
                return StudioProjectFolderException.DestinationExists(ex);
            }

            var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.GetFileName(projectFile),
                project.Sources.Screen.File,
                project.Sources.Events ?? StudioProjectStore.EventsFileName,
                StudioProjectStore.PosterFileName,
            };
            if (project.Sources.Camera is { } camera)
            {
                written.Add(camera.File);
            }

            if (project.Canvas.Background.Image is { } image)
            {
                written.Add(image);
            }

            var other = Directory.EnumerateFileSystemEntries(fullPath)
                .Where(entry => !IsPlainFile(entry) || !(written.Contains(Path.GetFileName(entry)) || IsSystemLitter(Path.GetFileName(entry))))
                .Select(entry => Path.GetFileName(entry))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ThenBy(static name => name, StringComparer.Ordinal)
                .FirstOrDefault();
            return other is null ? null : StudioProjectFolderException.DestinationHasOtherFiles(other);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException)
        {
            return StudioProjectFolderException.DestinationExists(ex);
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

    private static bool IsPlainFile(string path) => File.Exists(path) && !IsLink(path);

    private static bool IsSystemLitter(string name) =>
        name.StartsWith(SystemLitterPrefix, StringComparison.Ordinal)
        || SystemLitter.Contains(name, StringComparer.OrdinalIgnoreCase);

    // What a folder is searched for: not a name that starts with a dot.
    private static bool IsCountedProjectFile(string path) =>
        HasProjectFileExtension(path) && !Path.GetFileName(path).StartsWith('.');

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
