using System.Buffers.Binary;

namespace TinyClips.Core.Studio.Preview;

/// <summary>
/// When each frame of a clip begins, in the clip's own time and in 100 ns units, in the order
/// the frames are shown.
/// <para>
/// A recording's frames do not sit on a grid. The recorder stamps a screen frame with the wall
/// clock a moment after its pacer's tick, a tick it misses leaves no frame at all, and a camera's
/// frames carry the camera's own times, with a gap wherever the camera stalled. A player shows
/// each frame from its time until the next frame's, and so does the export: a frame of the
/// video shows the last frame of the file that began at or before its middle
/// (<c>StudioVideoSource.GetFrame</c>). With the times at hand the preview can count in frames
/// of the file, where the frame after a gap is simply the next one, and say for each which
/// frame of the video it belongs to.
/// </para>
/// <para>
/// The times come from the file's index (<see cref="TryRead(string, out string?)"/>), which is
/// read without decoding anything. They are taken to be the times a player goes by: the index
/// is what a player reads them from. That is an assumption about Media Foundation's reading
/// of a file, and the check tool holds it against what Media Foundation's own reader hands out
/// (<c>StudioPreviewCheck --investigate recordings --scenario files</c>).
/// </para>
/// </summary>
internal sealed class StudioPreviewFrameTimes
{
    /// <summary>A file with more frames than this is not read: ten hours at 120 frames a second.</summary>
    internal const int MostFrames = 4_320_000;

    /// <summary>An index is a few megabytes for an hour of video. More than this is not an index worth reading.</summary>
    internal const long LargestIndex = 256L * 1024 * 1024;

    /// <summary>A file has a handful of tracks: picture, sound, perhaps some of each. More than this is not a file a camera or a recorder writes.</summary>
    internal const int MostTracks = 64;

    /// <summary>
    /// The most parts a file, or one part of its index, is gone through: a file has a few, and so
    /// has each part of an index. It bounds the work a file can ask for that is all parts and nothing in them.
    /// </summary>
    internal const int MostParts = 4096;

    private readonly long[] _starts;

    private StudioPreviewFrameTimes(long[] starts, long end, long typicalSpacing)
    {
        _starts = starts;
        End = end;
        TypicalSpacing = typicalSpacing;
    }

    /// <summary>Frames in the file.</summary>
    public int Count => _starts.Length;

    /// <summary>When the last frame ends: its start and how long the file says it lasts.</summary>
    public long End { get; }

    /// <summary>The usual time from one frame to the next: the middle one of all of them.</summary>
    public long TypicalSpacing { get; }

    /// <summary>When a frame begins. A number outside the file stands for the first or the last frame.</summary>
    public long Start(long frame) => _starts[Math.Clamp(frame, 0, _starts.Length - 1)];

    /// <summary>
    /// The frame that is showing at a time: the last one that begins at or before it, which is
    /// the export's rule (<c>StudioVideoSource.GetFrame</c>). -1 while none has begun. There is
    /// no allowance: a frame that begins a 100 ns unit after the time has not begun.
    /// <para>
    /// The times are rounded to the nearest unit, as the export's reader was measured to hand
    /// them out (the spike's findings: 0, 333333, 666667 for the RGB output the export asks
    /// for). A player was measured to change frames within one unit of a frame's start, on the
    /// early side for some frames, so for a time within a unit of a frame's start this and a
    /// player can differ. The middle of a slot is not such a time in a recording's screen
    /// track: a file's frames begin at whole numbers of the file's own unit, a 30000th of a
    /// second in what Media Foundation's writer makes, and half a frame at 30 or 60 a second
    /// is a whole number of those. So a frame begins at the middle of a slot or a whole unit
    /// of the file from it, which is 33 millionths of a second. A camera's offset can put the
    /// middle anywhere, and there the two can differ for a frame that begins within a unit of it.
    /// </para>
    /// </summary>
    public long FrameAt(long ticks)
    {
        var low = 0;
        var high = _starts.Length;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (_starts[middle] <= ticks)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low - 1;
    }

    /// <summary>
    /// Makes the table from frame times in shown order. Null, with the reason, when they are not
    /// times a file's frames can have: none at all, a frame before zero, or two that do not
    /// follow each other.
    /// </summary>
    /// <param name="end">When the last frame ends. Anything at or before its start stands for one usual spacing after it.</param>
    public static StudioPreviewFrameTimes? TryCreate(ReadOnlySpan<long> starts, long end, out string? problem)
    {
        problem = null;
        if (starts.Length == 0)
        {
            problem = "it has no frames";
            return null;
        }

        if (starts.Length > MostFrames)
        {
            problem = $"it has {starts.Length} frames";
            return null;
        }

        if (starts[0] < 0)
        {
            problem = "its first frame begins before the start";
            return null;
        }

        for (var index = 1; index < starts.Length; index++)
        {
            if (starts[index] <= starts[index - 1])
            {
                problem = $"frame {index} does not begin after frame {index - 1}";
                return null;
            }
        }

        var copy = starts.ToArray();
        var typical = TypicalSpacingOf(copy);
        if (typical <= 0)
        {
            // One frame only: as long as the file says it lasts, or a thirtieth of a second.
            typical = end > copy[0] ? end - copy[0] : StudioPreviewTimeMath.TicksPerSecond / 30;
        }

        return new StudioPreviewFrameTimes(copy, end > copy[^1] ? end : copy[^1] + typical, typical);
    }

    /// <summary>
    /// Reads the frame times of a file's video from its index. Null, with the reason, when the
    /// file has no index this can read, or one whose times it cannot be sure of.
    /// </summary>
    public static StudioPreviewFrameTimes? TryRead(string path, out string? problem)
    {
        try
        {
            // Whoever else has the file open keeps every right to it.
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return TryRead(file, out problem);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            problem = $"it could not be read ({ex.GetType().Name})";
            return null;
        }
    }

    /// <summary>The same from a stream that can seek, positioned anywhere.</summary>
    public static StudioPreviewFrameTimes? TryRead(Stream file, out string? problem)
    {
        try
        {
            var index = ReadIndex(file, out problem);
            if (index is null)
            {
                return null;
            }

            var movie = new Movie();
            return ReadMovie(index, movie, ref problem) ? FromMovie(index, movie, out problem) : null;
        }
        catch (Exception ex) when (ex is IOException or OverflowException or ArgumentException or IndexOutOfRangeException or InvalidDataException or OutOfMemoryException)
        {
            problem = $"its index could not be read ({ex.GetType().Name})";
            return null;
        }
    }

    private static long TypicalSpacingOf(long[] starts)
    {
        if (starts.Length < 2)
        {
            return 0;
        }

        var spacings = new long[starts.Length - 1];
        for (var index = 1; index < starts.Length; index++)
        {
            spacings[index - 1] = starts[index] - starts[index - 1];
        }

        Array.Sort(spacings);
        return spacings[spacings.Length / 2];
    }

    // ----------------------------------------------------------------------------------------
    // The index of an MP4 file: the movie box, the video track's time units, its edit list, and
    // the two tables that say when each sample is decoded and how much later it is shown.
    //
    // What is read is what a file's index is made of and nothing else. The parts have their
    // places: moov holds mvhd and the tracks; a trak holds edts, with elst in it, and mdia; mdia
    // holds mdhd, hdlr and minf; minf holds stbl; stbl holds stts and ctts. Each is looked for
    // where it belongs and only there, so nothing here calls itself and no file can make it go
    // deeper than that, and what a file can make it do is counted: the parts of the file and of
    // each part of the index (MostParts), the tracks (MostTracks), the frames before room is
    // made for them (MostFrames), the index itself (LargestIndex).
    //
    // A part that does not fit the part it is in, bytes that are left over, one of these parts
    // in another place or twice, a table that is shorter or longer than it says: the index is
    // damaged, and a damaged index is not read in part. What stays possible is a part this has
    // no use for whose length takes in the part after it: that hides the part, and nothing in
    // the file says it should not. A file whose times this cannot be sure of is played as
    // every file was before: on the grid.
    // ----------------------------------------------------------------------------------------

    private static readonly uint Moov = Code("moov");
    private static readonly uint Moof = Code("moof");
    private static readonly uint Mvhd = Code("mvhd");
    private static readonly uint Mvex = Code("mvex");
    private static readonly uint Trak = Code("trak");
    private static readonly uint Edts = Code("edts");
    private static readonly uint Elst = Code("elst");
    private static readonly uint Mdia = Code("mdia");
    private static readonly uint Mdhd = Code("mdhd");
    private static readonly uint Hdlr = Code("hdlr");
    private static readonly uint Minf = Code("minf");
    private static readonly uint Stbl = Code("stbl");
    private static readonly uint Stts = Code("stts");
    private static readonly uint Ctts = Code("ctts");

    /// <summary>Stands for the file itself where the place of a part is asked for: no part has this for a name.</summary>
    private const uint TheFile = uint.MaxValue;

    /// <summary>A stretch of the index: from a byte of it up to another.</summary>
    private readonly record struct Part(int Start, int End)
    {
        public int Length => End - Start;
    }

    /// <summary>A table of the index: its kind, how many entries it says it has, and where they are.</summary>
    private readonly record struct Table(byte Version, uint Count, Part Entries);

    private sealed class Movie
    {
        public bool HasHeader;
        public uint Timescale;
        public List<Track> Tracks { get; } = [];
    }

    private sealed class Track
    {
        public bool HasEdits;
        public bool HasMedia;
        public bool HasMediaHeader;
        public bool HasHandler;
        public bool HasMediaInformation;
        public bool HasSampleTable;
        public bool IsVideo;
        public uint Timescale;
        public Table? EditList;
        public Table? TimeToSample;
        public Table? CompositionOffsets;
    }

    /// <summary>
    /// The contents of the file's movie box, or null when it has none, more than one, or one
    /// that is in fragments. The whole file is gone through, part by part, and nothing of it is
    /// read but the parts' names and lengths and the movie box.
    /// </summary>
    private static byte[]? ReadIndex(Stream file, out string? problem)
    {
        problem = null;
        var header = new byte[16];
        var length = file.Length;
        long position = 0;
        long indexAt = -1;
        long indexLength = 0;
        var parts = 0;
        while (length - position >= 8)
        {
            if (++parts > MostParts)
            {
                problem = $"it is in more than {MostParts} parts";
                return null;
            }

            file.Position = position;
            file.ReadExactly(header, 0, 8);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
            var headerLength = 8;
            if (size == 1)
            {
                // The length is in the 64 bits after the name.
                if (length - position < 16)
                {
                    problem = "a part of it does not fit the file";
                    return null;
                }

                file.ReadExactly(header, 8, 8);
                var large = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
                if (large > long.MaxValue)
                {
                    problem = "a part of it does not fit the file";
                    return null;
                }

                size = (long)large;
                headerLength = 16;
            }
            else if (size == 0)
            {
                // The last part of a file may leave its length out: it goes to the end.
                size = length - position;
            }

            if (size < headerLength || size > length - position)
            {
                problem = "a part of it does not fit the file";
                return null;
            }

            if (type == Moof)
            {
                // Before the index or after it, with or without the index saying so.
                problem = "its index is in fragments";
                return null;
            }

            var place = PlaceOf(type);
            if (place != 0 && place != TheFile)
            {
                problem = $"its index is damaged: '{Name(type)}' is outside it";
                return null;
            }

            if (type == Moov)
            {
                if (indexAt >= 0)
                {
                    problem = "it has two indexes";
                    return null;
                }

                if (size - headerLength > LargestIndex)
                {
                    problem = "its index is too large";
                    return null;
                }

                indexAt = position + headerLength;
                indexLength = size - headerLength;
            }

            position += size;
        }

        // Fewer than eight bytes after the last part are no part, and say nothing about the index.
        if (indexAt < 0)
        {
            problem = "it has no index";
            return null;
        }

        var payload = new byte[indexLength];
        file.Position = indexAt;
        file.ReadExactly(payload);
        return payload;
    }

    /// <summary>
    /// Takes the next part of a stretch of the index. False at the end of the stretch; false
    /// with <paramref name="problem"/> set when what is there is not a part that fits it.
    /// </summary>
    /// <param name="where">The name of the part the stretch is the contents of, for the reason.</param>
    /// <param name="position">Where the next part begins; moved past it.</param>
    /// <param name="count">How many parts of the stretch have been taken.</param>
    private static bool Next(byte[] index, Part within, string where, ref int position, ref int count, out uint type, out Part contents, ref string? problem)
    {
        type = 0;
        contents = default;
        var left = within.End - position;
        if (left == 0)
        {
            return false;
        }

        if (left < 8)
        {
            problem = $"its index is damaged: bytes are left over in '{where}'";
            return false;
        }

        if (++count > MostParts)
        {
            problem = $"its index is damaged: '{where}' is in more than {MostParts} parts";
            return false;
        }

        ulong size = BinaryPrimitives.ReadUInt32BigEndian(index.AsSpan(position));
        type = BinaryPrimitives.ReadUInt32BigEndian(index.AsSpan(position + 4));
        var headerLength = 8;
        if (size == 1)
        {
            if (left < 16)
            {
                problem = $"its index is damaged: a part of '{where}' does not fit it";
                return false;
            }

            size = BinaryPrimitives.ReadUInt64BigEndian(index.AsSpan(position + 8));
            headerLength = 16;
        }

        // A length of 0, which stands for "to the end of the file", is for the last part of a
        // file and has no meaning inside the index.
        if (size < (ulong)headerLength || size > (ulong)left)
        {
            problem = $"its index is damaged: a part of '{where}' does not fit it";
            return false;
        }

        contents = new Part(position + headerLength, position + (int)size);
        position += (int)size;
        return true;
    }

    /// <summary>
    /// The part of the index a part belongs in, when it has one place only; 0 otherwise. The
    /// index itself and a fragment belong in the file and in no part of the index.
    /// </summary>
    private static uint PlaceOf(uint type) =>
        type == Moov || type == Moof ? TheFile
        : type == Mvhd || type == Mvex || type == Trak ? Moov
        : type == Edts || type == Mdia ? Trak
        : type == Elst ? Edts
        : type == Mdhd || type == Minf ? Mdia
        : type == Stbl ? Minf
        : type == Stts || type == Ctts ? Stbl
        : 0;

    /// <summary>True, with the reason, for a part that is met where it does not belong.</summary>
    private static bool IsMisplaced(uint type, uint where, ref string? problem)
    {
        var place = PlaceOf(type);
        if (place == 0 || place == where)
        {
            return false;
        }

        problem = $"its index is damaged: '{Name(type)}' has no place in '{Name(where)}'";
        return true;
    }

    /// <summary>Notes that a part of which there is one was met; false, with the reason, when it was met before.</summary>
    private static bool Once(ref bool met, uint type, uint where, ref string? problem)
    {
        if (met)
        {
            problem = $"its index is damaged: '{Name(where)}' has '{Name(type)}' twice";
            return false;
        }

        met = true;
        return true;
    }

    private static bool ReadMovie(byte[] index, Movie movie, ref string? problem)
    {
        var all = new Part(0, index.Length);
        var position = 0;
        var count = 0;
        while (Next(index, all, "moov", ref position, ref count, out var type, out var contents, ref problem))
        {
            if (type == Mvhd)
            {
                if (!Once(ref movie.HasHeader, type, Moov, ref problem) || !ReadTimescale(index, contents, type, out movie.Timescale, ref problem))
                {
                    return false;
                }
            }
            else if (type == Trak)
            {
                if (movie.Tracks.Count == MostTracks)
                {
                    problem = $"it has more than {MostTracks} tracks";
                    return false;
                }

                var track = new Track();
                movie.Tracks.Add(track);
                if (!ReadTrack(index, contents, track, ref problem))
                {
                    return false;
                }
            }
            else if (type == Mvex)
            {
                problem = "its index is in fragments";
                return false;
            }
            else if (IsMisplaced(type, Moov, ref problem))
            {
                return false;
            }
        }

        return problem is null;
    }

    private static bool ReadTrack(byte[] index, Part trak, Track track, ref string? problem)
    {
        var position = trak.Start;
        var count = 0;
        while (Next(index, trak, "trak", ref position, ref count, out var type, out var contents, ref problem))
        {
            if (type == Edts)
            {
                if (!Once(ref track.HasEdits, type, Trak, ref problem) || !ReadEdits(index, contents, track, ref problem))
                {
                    return false;
                }
            }
            else if (type == Mdia)
            {
                if (!Once(ref track.HasMedia, type, Trak, ref problem) || !ReadMedia(index, contents, track, ref problem))
                {
                    return false;
                }
            }
            else if (IsMisplaced(type, Trak, ref problem))
            {
                return false;
            }
        }

        return problem is null;
    }

    private static bool ReadEdits(byte[] index, Part edts, Track track, ref string? problem)
    {
        var position = edts.Start;
        var count = 0;
        var met = false;
        while (Next(index, edts, "edts", ref position, ref count, out var type, out var contents, ref problem))
        {
            if (type == Elst)
            {
                if (!Once(ref met, type, Edts, ref problem) || !ReadTable(index, contents, type, lastVersion: 1, entryOf: version => version == 1 ? 20 : 12, out track.EditList, ref problem))
                {
                    return false;
                }
            }
            else if (IsMisplaced(type, Edts, ref problem))
            {
                return false;
            }
        }

        return problem is null;
    }

    private static bool ReadMedia(byte[] index, Part mdia, Track track, ref string? problem)
    {
        var position = mdia.Start;
        var count = 0;
        while (Next(index, mdia, "mdia", ref position, ref count, out var type, out var contents, ref problem))
        {
            if (type == Mdhd)
            {
                if (!Once(ref track.HasMediaHeader, type, Mdia, ref problem) || !ReadTimescale(index, contents, type, out track.Timescale, ref problem))
                {
                    return false;
                }
            }
            else if (type == Hdlr)
            {
                // What the track is. A file of QuickTime's kind has another one in minf, which
                // says where its data is: that one is not asked.
                if (!Once(ref track.HasHandler, type, Mdia, ref problem))
                {
                    return false;
                }

                if (contents.Length < 12)
                {
                    problem = CutShort(type);
                    return false;
                }

                track.IsVideo = BinaryPrimitives.ReadUInt32BigEndian(index.AsSpan(contents.Start + 8)) == Code("vide");
            }
            else if (type == Minf)
            {
                if (!Once(ref track.HasMediaInformation, type, Mdia, ref problem) || !ReadMediaInformation(index, contents, track, ref problem))
                {
                    return false;
                }
            }
            else if (IsMisplaced(type, Mdia, ref problem))
            {
                return false;
            }
        }

        return problem is null;
    }

    private static bool ReadMediaInformation(byte[] index, Part minf, Track track, ref string? problem)
    {
        var position = minf.Start;
        var count = 0;
        while (Next(index, minf, "minf", ref position, ref count, out var type, out var contents, ref problem))
        {
            if (type == Stbl)
            {
                if (!Once(ref track.HasSampleTable, type, Minf, ref problem) || !ReadSampleTable(index, contents, track, ref problem))
                {
                    return false;
                }
            }
            else if (IsMisplaced(type, Minf, ref problem))
            {
                return false;
            }
        }

        return problem is null;
    }

    private static bool ReadSampleTable(byte[] index, Part stbl, Track track, ref string? problem)
    {
        var position = stbl.Start;
        var count = 0;
        var times = false;
        var offsets = false;
        while (Next(index, stbl, "stbl", ref position, ref count, out var type, out var contents, ref problem))
        {
            if (type == Stts)
            {
                if (!Once(ref times, type, Stbl, ref problem) || !ReadTable(index, contents, type, lastVersion: 0, entryOf: _ => 8, out track.TimeToSample, ref problem))
                {
                    return false;
                }
            }
            else if (type == Ctts)
            {
                if (!Once(ref offsets, type, Stbl, ref problem) || !ReadTable(index, contents, type, lastVersion: 1, entryOf: _ => 8, out track.CompositionOffsets, ref problem))
                {
                    return false;
                }
            }
            else if (IsMisplaced(type, Stbl, ref problem))
            {
                return false;
            }
        }

        return problem is null;
    }

    /// <summary>The time units of a movie's or a track's header, which has them in one of two places by its kind.</summary>
    private static bool ReadTimescale(byte[] index, Part contents, uint type, out uint timescale, ref string? problem)
    {
        timescale = 0;
        if (contents.Length < 4)
        {
            problem = CutShort(type);
            return false;
        }

        // After the kind and its flags: two times of 32 bits, or of 64, and then the units.
        var version = index[contents.Start];
        if (version > 1)
        {
            problem = UnknownKind(type);
            return false;
        }

        var at = 4 + (version == 1 ? 16 : 8);
        if (contents.Length < at + 4)
        {
            problem = CutShort(type);
            return false;
        }

        timescale = BinaryPrimitives.ReadUInt32BigEndian(index.AsSpan(contents.Start + at));
        return true;
    }

    /// <summary>
    /// A table: its kind, the number of entries it says it has, and the entries, which have to be
    /// there and to be all there is. A table of a kind this does not know, one with fewer
    /// entries than it says, or one with more in it than its entries, is not a table to go by:
    /// what follows a table's last entry is where the next part of the index would begin, and
    /// a table that has taken that in has hidden it.
    /// </summary>
    private static bool ReadTable(byte[] index, Part contents, uint type, byte lastVersion, Func<byte, int> entryOf, out Table? table, ref string? problem)
    {
        table = null;
        if (contents.Length < 8)
        {
            problem = CutShort(type);
            return false;
        }

        var version = index[contents.Start];
        if (version > lastVersion)
        {
            problem = UnknownKind(type);
            return false;
        }

        var count = BinaryPrimitives.ReadUInt32BigEndian(index.AsSpan(contents.Start + 4));
        var entries = new Part(contents.Start + 8, contents.End);
        var said = (long)count * entryOf(version);
        if (said > entries.Length)
        {
            problem = CutShort(type);
            return false;
        }

        if (said < entries.Length)
        {
            problem = $"its index is damaged: '{Name(type)}' is longer than it says";
            return false;
        }

        table = new Table(version, count, entries);
        return true;
    }

    private static string CutShort(uint type) => type == Stts
        ? "its table of frame times is cut short"
        : $"its index is damaged: '{Name(type)}' is cut short";

    private static string UnknownKind(uint type) => $"its index has a '{Name(type)}' of a kind that is not known";

    private static StudioPreviewFrameTimes? FromMovie(byte[] index, Movie movie, out string? problem)
    {
        problem = null;
        Track? video = null;
        foreach (var track in movie.Tracks)
        {
            if (!track.IsVideo)
            {
                continue;
            }

            if (video is not null)
            {
                // Which of them a player plays is not known, and they need not have the same times.
                problem = "it has more than one video track";
                return null;
            }

            video = track;
        }

        if (video is null)
        {
            problem = "it has no video";
            return null;
        }

        if (movie.Timescale == 0 || video.Timescale == 0)
        {
            problem = "its index does not say what its times count in";
            return null;
        }

        // The edit list moves the track on the movie's timeline: stretches of nothing first, then
        // the track from some time of its own. More than that is not a recording, and not read.
        // How long the edit list shows the track for is not looked at.
        long lead = 0;
        long mediaStart = 0;
        if (video.EditList is { } editList)
        {
            long nothing = 0;
            var shows = 0;
            var entry = editList.Version == 1 ? 20 : 12;
            for (var number = 0; number < editList.Count; number++)
            {
                var at = index.AsSpan(editList.Entries.Start + (number * entry), entry);
                long duration;
                long mediaTime;
                ReadOnlySpan<byte> rate;
                if (editList.Version == 1)
                {
                    duration = checked((long)BinaryPrimitives.ReadUInt64BigEndian(at));
                    mediaTime = BinaryPrimitives.ReadInt64BigEndian(at[8..]);
                    rate = at[16..];
                }
                else
                {
                    duration = BinaryPrimitives.ReadUInt32BigEndian(at);
                    mediaTime = BinaryPrimitives.ReadInt32BigEndian(at[4..]);
                    rate = at[8..];
                }

                if (mediaTime < 0)
                {
                    if (shows > 0)
                    {
                        problem = "its edit list has a stretch of nothing in the middle";
                        return null;
                    }

                    nothing = checked(nothing + duration);
                    continue;
                }

                var atRateOne = BinaryPrimitives.ReadInt16BigEndian(rate) == 1 && BinaryPrimitives.ReadUInt16BigEndian(rate[2..]) == 0;
                if (++shows > 1 || !atRateOne)
                {
                    problem = shows > 1 ? "its edit list has more than one part" : "its edit list changes the speed";
                    return null;
                }

                lead = Ticks(nothing, movie.Timescale);
                mediaStart = mediaTime;
            }

            if (shows == 0 && editList.Count > 0)
            {
                problem = "its edit list shows nothing of the video";
                return null;
            }
        }

        // How many frames, before anything is made for them.
        long samples = 0;
        if (video.TimeToSample is { } runs)
        {
            for (var number = 0; number < runs.Count; number++)
            {
                samples += BinaryPrimitives.ReadUInt32BigEndian(index.AsSpan(runs.Entries.Start + (number * 8)));
                if (samples > MostFrames)
                {
                    problem = $"it has more than {MostFrames} frames";
                    return null;
                }
            }
        }

        if (samples == 0)
        {
            problem = "it has no frames";
            return null;
        }

        // When each sample is decoded, then how much later it is shown.
        var times = new long[samples];
        long decode = 0;
        long lastDelta = 0;
        var sample = 0;
        for (var number = 0; number < video.TimeToSample!.Value.Count; number++)
        {
            var at = index.AsSpan(video.TimeToSample.Value.Entries.Start + (number * 8), 8);
            var count = BinaryPrimitives.ReadUInt32BigEndian(at);
            var delta = BinaryPrimitives.ReadUInt32BigEndian(at[4..]);
            for (var one = 0; one < count; one++)
            {
                times[sample++] = decode;
                decode += delta;
            }

            if (count > 0)
            {
                lastDelta = delta;
            }
        }

        var reordered = video.CompositionOffsets is not null;
        if (video.CompositionOffsets is { } offsets)
        {
            // An offset for every sample, no fewer and no more: a table that covers part of the
            // file leaves the rest to a guess.
            sample = 0;
            for (var number = 0; number < offsets.Count; number++)
            {
                var at = index.AsSpan(offsets.Entries.Start + (number * 8), 8);
                var count = BinaryPrimitives.ReadUInt32BigEndian(at);
                long offset = offsets.Version == 0 ? BinaryPrimitives.ReadUInt32BigEndian(at[4..]) : BinaryPrimitives.ReadInt32BigEndian(at[4..]);
                if (count > (uint)(times.Length - sample))
                {
                    problem = "its table of when frames are shown is for more frames than it has";
                    return null;
                }

                for (var one = 0; one < count; one++)
                {
                    times[sample++] += offset;
                }
            }

            if (sample != times.Length)
            {
                problem = "its table of when frames are shown is for fewer frames than it has";
                return null;
            }

            // The samples are stored in the order they are decoded; they are shown in the order of their times.
            Array.Sort(times);
        }

        // A frame that is shown before the track begins, because the edit list starts the track
        // after it or because its offset goes backwards past zero: whether a player shows it,
        // and until when, is more than the index says.
        if (times[0] < mediaStart)
        {
            problem = mediaStart > 0 ? "its edit list begins inside the video" : "its first frame is shown before its track begins";
            return null;
        }

        // In the track's own units from where the edit list starts it, then in 100 ns, then
        // after the stretch of nothing: so that each time is rounded once.
        var last = times[^1];
        for (var number = 0; number < times.Length; number++)
        {
            times[number] = Ticks(times[number] - mediaStart, video.Timescale) + lead;
        }

        var end = reordered ? 0 : Ticks(last + lastDelta - mediaStart, video.Timescale) + lead;
        return TryCreate(times, end, out problem);
    }

    private static long Ticks(long units, uint timescale) =>
        (long)Math.Round(units * (decimal)StudioPreviewTimeMath.TicksPerSecond / timescale, MidpointRounding.AwayFromZero);

    private static uint Code(string type) => ((uint)type[0] << 24) | ((uint)type[1] << 16) | ((uint)type[2] << 8) | type[3];

    /// <summary>A part's name as it is written, for a reason; what is not a letter or a digit comes out as a question mark.</summary>
    private static string Name(uint type) => string.Create(4, type, static (text, code) =>
    {
        for (var index = 0; index < 4; index++)
        {
            var letter = (char)((code >> (24 - (index * 8))) & 0xFF);
            text[index] = char.IsAsciiLetterOrDigit(letter) ? letter : '?';
        }
    });
}