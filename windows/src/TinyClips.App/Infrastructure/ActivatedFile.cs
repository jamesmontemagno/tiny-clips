using TinyClips.Core.Studio;

namespace TinyClips.App;

/// <summary>What the app does with a file it was handed: by Explorer, by "Open with", or by a later launch.</summary>
internal enum ActivatedFileKind
{
    /// <summary>Not a file the app opens.</summary>
    None,

    /// <summary>A picture, which opens in the screenshot editor.</summary>
    Image,

    /// <summary>The <c>.tinyclips</c> file of a Studio project that was saved as a folder, which opens in Studio.</summary>
    StudioProject,
}

/// <summary>
/// Tells the files the app is associated with apart, by their extension. Kept apart from the
/// app so that it can be tested: the two package manifests name the same extensions.
/// </summary>
internal static class ActivatedFile
{
    /// <summary>The pictures the screenshot editor opens, as the manifests' image association lists them.</summary>
    public static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp"];

    public static ActivatedFileKind KindOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return ActivatedFileKind.None;
        }

        string extension;
        try
        {
            extension = Path.GetExtension(path);
        }
        catch (ArgumentException)
        {
            return ActivatedFileKind.None;
        }

        if (extension.Equals(StudioProjectFolder.ProjectFileExtension, StringComparison.OrdinalIgnoreCase))
        {
            return ActivatedFileKind.StudioProject;
        }

        return ImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) ? ActivatedFileKind.Image : ActivatedFileKind.None;
    }

    /// <summary>
    /// The one file of an activation the app opens: the first it has an editor for, as it
    /// always was for pictures. Null when there is none.
    /// </summary>
    public static (ActivatedFileKind Kind, string Path)? FirstSupported(IEnumerable<string?> paths)
    {
        foreach (var path in paths)
        {
            if (KindOf(path) is var kind and not ActivatedFileKind.None)
            {
                return (kind, path!);
            }
        }

        return null;
    }
}
