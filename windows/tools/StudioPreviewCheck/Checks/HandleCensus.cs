using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TinyClips.Tools.StudioPreviewCheck.Checks;

/// <summary>
/// The kernel handles of this process, counted by object type (Event, Section, Thread, File, ...).
/// A bare handle count says that something stays behind; the types say what.
/// </summary>
internal static partial class HandleCensus
{
    private const int ProcessHandleInformation = 51;
    private const int ObjectTypeInformation = 2;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    // PROCESS_HANDLE_TABLE_ENTRY_INFO on a 64-bit process: the handle first, the type index at 28.
    private const int EntrySize = 40;
    private const int EntryTypeIndexOffset = 28;
    private const int SnapshotHeaderSize = 16;

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(nint process, int informationClass, nint buffer, int length, out int returned);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryObject(nint handle, int informationClass, nint buffer, int length, out int returned);

    /// <summary>Counts the process's handles by type name. Empty when the system does not answer.</summary>
    public static unsafe Dictionary<string, int> Take()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (nint.Size != 8)
        {
            return counts;
        }

        using var process = Process.GetCurrentProcess();
        var names = new Dictionary<uint, string>();
        var length = 1 << 18;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                var status = NtQueryInformationProcess(process.Handle, ProcessHandleInformation, buffer, length, out var needed);
                if (status == StatusInfoLengthMismatch)
                {
                    length = Math.Max(length * 2, needed + 8192);
                    continue;
                }

                if (status != 0)
                {
                    return counts;
                }

                var count = (long)*(nuint*)buffer;
                var entries = (byte*)buffer + SnapshotHeaderSize;
                var typeBuffer = Marshal.AllocHGlobal(4096);
                try
                {
                    for (long index = 0; index < count; index++)
                    {
                        var entry = entries + (index * EntrySize);
                        var typeIndex = *(uint*)(entry + EntryTypeIndexOffset);
                        if (!names.TryGetValue(typeIndex, out var name))
                        {
                            name = TypeName(*(nint*)entry, typeBuffer) ?? $"type {typeIndex}";
                            names[typeIndex] = name;
                        }

                        counts[name] = counts.GetValueOrDefault(name) + 1;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(typeBuffer);
                }

                return counts;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return counts;
    }

    public static int Total(Dictionary<string, int> census) => census.Values.Sum();

    // Handles of the thread pool and of I/O in progress. They come and go by a dozen between two
    // looks, whatever the process is doing.
    private static readonly HashSet<string> Passing = new(StringComparer.Ordinal)
    {
        "Thread", "IoCompletion", "WaitCompletionPacket", "TpWorkerFactory", "IRTimer", "Timer", "ALPC Port", "EtwRegistration", "File",
    };

    /// <summary>The handles that stay once work is done: every type except those of the thread pool and of I/O in progress.</summary>
    public static int Lasting(Dictionary<string, int> census) =>
        census.Where(entry => !Passing.Contains(entry.Key)).Sum(entry => entry.Value);

    /// <summary>The types whose count changed, largest change first: "Event +64, Section +40".</summary>
    public static string Difference(Dictionary<string, int> before, Dictionary<string, int> after)
    {
        var changes = after.Keys.Union(before.Keys)
            .Select(name => (Name: name, Change: after.GetValueOrDefault(name) - before.GetValueOrDefault(name)))
            .Where(change => change.Change != 0)
            .OrderByDescending(change => Math.Abs(change.Change))
            .Select(change => $"{change.Name} {(change.Change > 0 ? "+" : string.Empty)}{change.Change}")
            .ToList();
        return changes.Count == 0 ? "none" : string.Join(", ", changes);
    }

    // OBJECT_TYPE_INFORMATION starts with the type's name as a UNICODE_STRING: length in bytes,
    // capacity, and a pointer to the characters, which lie further on in the same buffer.
    private static unsafe string? TypeName(nint handle, nint buffer)
    {
        if (NtQueryObject(handle, ObjectTypeInformation, buffer, 4096, out _) != 0)
        {
            return null;
        }

        var characters = *(ushort*)buffer / 2;
        var text = *(char**)((byte*)buffer + 8);
        return text is null || characters == 0 ? null : new string(text, 0, characters);
    }
}
