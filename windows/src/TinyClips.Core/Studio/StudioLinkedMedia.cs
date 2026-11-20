using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TinyClips.Core.Studio;

// Only used under StudioProjectDestination's save lock. A hit is proof about two file
// revisions, not permission to follow a name or replace anything in the destination.
internal sealed partial class StudioLinkedMedia : IDisposable
{
    private sealed record Revision(StudioExportTarget.FileVersion File, long Usn);
    private static readonly Dictionary<string, (Revision Source, Revision Destination)> Verified = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileStream> _destinations = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileStream> _sources = [];

    public bool CanKeep(string sourcePath, string destinationPath, Action<double>? compared, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Guard changed destinations too: a file that was replaceable at preflight must not
        // be exchanged for a different file during a potentially long comparison or copy.
        var destination = OpenForComparison(destinationPath);
        _destinations.Add(destinationPath, destination);
        if (StudioProjectFolder.IsLink(destinationPath))
        {
            throw new IOException($"The saved asset {destinationPath} is a link. Use Save as.");
        }

        FileStream? source = null;
        try
        {
            try
            {
                source = OpenForComparison(sourcePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A recorder may still hold the source open for writing. Do the ordinary
                // shared-read copy instead of treating its current bytes as immutable.
                return false;
            }

            if (source.Length != destination.Length) return false;

            var sourceVersion = OperatingSystem.IsWindows() ? StudioExportTarget.ReadFileVersion(source.SafeFileHandle) : null;
            var destinationVersion = OperatingSystem.IsWindows() ? StudioExportTarget.ReadFileVersion(destination.SafeFileHandle) : null;
            if (sourceVersion is not null && (sourceVersion.IdLow != 0 || sourceVersion.IdHigh != 0)
                && sourceVersion == destinationVersion)
            {
                throw new IOException("The saved asset is the source file, not an independent copy. Use Save as.");
            }

            var from = ReadRevision(source.SafeFileHandle, sourceVersion);
            var to = ReadRevision(destination.SafeFileHandle, destinationVersion);
            if (from is null || to is null || !Verified.TryGetValue(destinationPath, out var previous)
                || previous.Source != from || previous.Destination != to)
            {
                if (!HaveSameContent(source, destination, compared, token)) return false;
                if (from is not null && to is not null)
                {
                    if (Verified.Count >= 128) Verified.Clear();
                    Verified[destinationPath] = (from, to);
                }
            }

            // Neither file can be written, deleted or renamed between verification and
            // committing the manifest. No open handles are kept between saves.
            _sources.Add(source);
            source = null;
            return true;
        }
        finally
        {
            source?.Dispose();
        }
    }

    public void ReleaseForReplacement(string path)
    {
        if (_destinations.Remove(path, out var stream)) stream.Dispose();
    }

    private static FileStream OpenForComparison(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, FileOptions.SequentialScan);

    private static bool HaveSameContent(FileStream source, FileStream destination, Action<double>? compared, CancellationToken token)
    {
        var length = source.Length;
        if (length == 0) return true;
        var piece = (int)Math.Min(1 << 20, length);
        var first = ArrayPool<byte>.Shared.Rent(piece);
        var second = ArrayPool<byte>.Shared.Rent(piece);
        try
        {
            for (var remaining = length; remaining > 0;)
            {
                token.ThrowIfCancellationRequested();
                var count = (int)Math.Min(piece, remaining);
                source.ReadExactly(first.AsSpan(0, count));
                destination.ReadExactly(second.AsSpan(0, count));
                if (!first.AsSpan(0, count).SequenceEqual(second.AsSpan(0, count))) return false;
                remaining -= count;
                compared?.Invoke((double)(length - remaining) / length);
            }
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(first);
            ArrayPool<byte>.Shared.Return(second);
        }
    }

    // A timestamp (even together with size) is not a content version. Where Windows has
    // no per-file change journal record, fall back to bounded, cancellable comparison.
    private static unsafe Revision? ReadRevision(SafeFileHandle handle, StudioExportTarget.FileVersion? file)
    {
        if (file is null) return null;
        const uint readFileUsnData = 0x000900EB;
        const int capacity = 1024;
        byte* buffer = stackalloc byte[capacity];
        if (!DeviceIoControl(handle, readFileUsnData, 0, 0, buffer, capacity, out var returned, 0)
            || returned < 32 || returned > capacity) return null;
        var record = new ReadOnlySpan<byte>(buffer, (int)returned);
        // USN_RECORD_V2: after the header and the two 64-bit file references.
        if (BinaryPrimitives.ReadUInt16LittleEndian(record[4..]) != 2) return null;
        var usn = BinaryPrimitives.ReadInt64LittleEndian(record[24..]);
        return usn > 0 ? new(file, usn) : null;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool DeviceIoControl(SafeFileHandle handle, uint code, nint input, uint inputSize,
        byte* output, uint outputSize, out uint returned, nint overlapped);

    public void Dispose()
    {
        foreach (var stream in _sources) stream.Dispose();
        foreach (var stream in _destinations.Values) stream.Dispose();
    }
}
