using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TinyClips.Core.Studio;

/// <summary>An explicit user-selected filename, including the file whose replacement was approved.</summary>
public sealed partial record StudioExportTarget(string Path, long? Length, DateTime? LastWrite)
{
    private string ResolvedPath { get; init; } = Path;
    private FileVersion? Version { get; init; }

    public static StudioExportTarget Capture(string path)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        var resolved = ResolvePath(fullPath);
        if (!File.Exists(resolved))
        {
            return new(fullPath, null, null) { ResolvedPath = resolved };
        }

        using var handle = File.OpenHandle(resolved, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileIdentity(handle, FileInformationClass.Id, out var identity, (uint)Marshal.SizeOf<FileIdentity>())
            || !GetFileBasicInfo(handle, FileInformationClass.Basic, out var basic, (uint)Marshal.SizeOf<FileBasicInfo>()))
        {
            throw new IOException($"The selected output file could not be inspected: {fullPath}",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }
        return new(fullPath, RandomAccess.GetLength(handle), DateTime.FromFileTimeUtc(basic.LastWriteTime))
        {
            ResolvedPath = resolved,
            Version = new(identity.VolumeSerialNumber, identity.IdLow, identity.IdHigh, basic.ChangeTime),
        };
    }

    public void Commit(string stagedPath)
    {
        var current = Capture(Path);
        if (current != this)
        {
            throw new IOException("The selected output file changed while exporting. Export again to confirm its replacement.");
        }

        File.Move(stagedPath, current.ResolvedPath, overwrite: Length.HasValue);
    }

    public void Validate(StudioProject project, StudioProjectPaths paths, string? savedProjectFile)
    {
        var protectedPaths = StudioProjectDestination.OwnedFiles(project)
            .Select(name => System.IO.Path.Combine(paths.ProjectDirectory, name)).ToList();
        protectedPaths.Add(System.IO.Path.Combine(paths.ProjectDirectory, StudioProjectStore.ProjectFileName));
        if (project.Sources.Screen.External) protectedPaths.Add(project.Sources.Screen.File);
        if (savedProjectFile is not null)
        {
            var saved = File.Exists(savedProjectFile) ? StudioProjectFolder.ReadProjectFile(savedProjectFile) : project;
            protectedPaths.AddRange(StudioProjectDestination.OwnedFiles(saved)
                .Select(name => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(savedProjectFile)!, name)));
            protectedPaths.Add(savedProjectFile);
        }

        var resolved = ResolvePath(Path);
        var projectDirectory = ResolvePath(paths.ProjectDirectory);
        if (StudioProjectFolder.IsLink(Path)
            || protectedPaths.Any(file => string.Equals(ResolvePath(file), resolved, StringComparison.OrdinalIgnoreCase))
            || string.Equals(resolved, projectDirectory, StringComparison.OrdinalIgnoreCase)
            || resolved.StartsWith(System.IO.Path.TrimEndingDirectorySeparator(projectDirectory) + System.IO.Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Choose another filename: exporting here would overwrite project data or a source recording.");
        }
    }

    internal static string ResolvePath(string path, int depth = 0)
    {
        if (depth >= 64) throw new IOException("The selected path contains too many directory aliases.");
        var fullPath = System.IO.Path.GetFullPath(path);
        var root = System.IO.Path.GetPathRoot(fullPath)!;
        var resolved = root;
        foreach (var part in fullPath[root.Length..].Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = System.IO.Path.Combine(resolved, part);
            FileSystemInfo info = Directory.Exists(candidate) ? new DirectoryInfo(candidate) : new FileInfo(candidate);
            resolved = info.LinkTarget is null
                ? candidate
                : ResolvePath(info.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                    ?? throw new IOException($"The directory alias could not be resolved: {candidate}"), depth + 1);
        }
        return System.IO.Path.TrimEndingDirectorySeparator(resolved);
    }

    private sealed record FileVersion(ulong VolumeSerialNumber, ulong IdLow, ulong IdHigh, long ChangeTime);

    private enum FileInformationClass
    {
        Basic = 0,
        Id = 18,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdentity
    {
        public ulong VolumeSerialNumber;
        public ulong IdLow;
        public ulong IdHigh;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInfo
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileIdentity(SafeFileHandle handle, FileInformationClass infoClass, out FileIdentity info, uint size);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileBasicInfo(SafeFileHandle handle, FileInformationClass infoClass, out FileBasicInfo info, uint size);
}
