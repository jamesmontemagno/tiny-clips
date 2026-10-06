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

    // An index is a few megabytes for an hour of video. More than this is not an index worth reading.
    private const long LargestIndex = 256L * 1024 * 1024;

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
    /// The frame that is showing at a time: the last one that has begun by then, counting one
    /// that begins within <paramref name="tolerance"/> after it. -1 while none has begun.
    /// </summary>
    public long FrameAt(long ticks, long tolerance = 0)
    {
        var limit = ticks + tolerance;
        var low = 0;
        var high = _starts.Length;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (_starts[middle] <= limit)
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
            Walk(index, movie, null);
            return FromMovie(movie, out problem);
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
    // ----------------------------------------------------------------------------------------

    private sealed class Movie
    {
        public uint Timescale;
        public bool Fragmented;
        public List<Track> Tracks { get; } = [];
    }

    private sealed class Track
    {
        public bool IsVideo;
        public uint Timescale;
        public bool EditsUnreadable;
        public bool TablesUnreadable;
        public List<(long Duration, long MediaTime, bool AtRateOne)> Edits { get; } = [];
        public List<(uint Count, uint Delta)> TimeToSample { get; } = [];
        public List<(uint Count, long Offset)> CompositionOffsets { get; } = [];
    }

    /// <summary>The contents of the file's movie box, or null when it has none.</summary>
    private static byte[]? ReadIndex(Stream file, out string? problem)
    {
        problem = null;
        var header = new byte[16];
        var length = file.Length;
        long position = 0;
        var fragments = false;
        while (position + 8 <= length)
        {
            file.Position = position;
            file.ReadExactly(header, 0, 8);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
            var headerLength = 8;
            if (size == 1)
            {
                if (position + 16 > length)
                {
                    break;
                }

                file.ReadExactly(header, 8, 8);
                size = checked((long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8)));
                headerLength = 16;
            }
            else if (size == 0)
            {
                size = length - position;
            }

            if (size < headerLength || position + size > length)
            {
                problem = "a part of it is longer than the file";
                return null;
            }

            if (type == Code("moov"))
            {
                if (size - headerLength > LargestIndex)
                {
                    problem = "its index is too large";
                    return null;
                }

                var payload = new byte[size - headerLength];
                file.ReadExactly(payload);
                if (fragments)
                {
                    problem = "its index is in fragments";
                    return null;
                }

                return payload;
            }

            fragments |= type == Code("moof");
            position += size;
        }

        problem = "it has no index";
        return null;
    }

    private static void Walk(ReadOnlySpan<byte> data, Movie movie, Track? track)
    {
        var position = 0;
        while (position + 8 <= data.Length)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(data[position..]);
            var type = BinaryPrimitives.ReadUInt32BigEndian(data[(position + 4)..]);
            var headerLength = 8;
            if (size == 1 && position + 16 <= data.Length)
            {
                size = checked((long)BinaryPrimitives.ReadUInt64BigEndian(data[(position + 8)..]));
                headerLength = 16;
            }
            else if (size == 0)
            {
                size = data.Length - position;
            }

            if (size < headerLength || position + size > data.Length)
            {
                return;
            }

            var payload = data.Slice(position + headerLength, (int)size - headerLength);
            if (type == Code("trak"))
            {
                var inner = new Track();
                movie.Tracks.Add(inner);
                Walk(payload, movie, inner);
            }
            else if (type == Code("edts") || type == Code("mdia") || type == Code("minf") || type == Code("stbl"))
            {
                Walk(payload, movie, track);
            }
            else if (type == Code("mvex"))
            {
                movie.Fragmented = true;
            }
            else
            {
                Leaf(type, payload, movie, track);
            }

            position += (int)size;
        }
    }

    private static void Leaf(uint type, ReadOnlySpan<byte> box, Movie movie, Track? track)
    {
        if (box.Length < 4)
        {
            return;
        }

        var version = box[0];
        var body = box[4..];
        if (type == Code("mvhd"))
        {
            if (version == 1 && body.Length >= 20)
            {
                movie.Timescale = BinaryPrimitives.ReadUInt32BigEndian(body[16..]);
            }
            else if (version != 1 && body.Length >= 12)
            {
                movie.Timescale = BinaryPrimitives.ReadUInt32BigEndian(body[8..]);
            }

            return;
        }

        if (track is null)
        {
            return;
        }

        if (type == Code("mdhd"))
        {
            if (version == 1 && body.Length >= 20)
            {
                track.Timescale = BinaryPrimitives.ReadUInt32BigEndian(body[16..]);
            }
            else if (version != 1 && body.Length >= 12)
            {
                track.Timescale = BinaryPrimitives.ReadUInt32BigEndian(body[8..]);
            }
        }
        else if (type == Code("hdlr") && body.Length >= 8)
        {
            track.IsVideo = BinaryPrimitives.ReadUInt32BigEndian(body[4..]) == Code("vide");
        }
        else if (type == Code("elst") && body.Length >= 4)
        {
            var count = BinaryPrimitives.ReadUInt32BigEndian(body);
            var entry = version == 1 ? 20 : 12;
            if (count > (uint)((body.Length - 4) / entry))
            {
                track.EditsUnreadable = true;
                return;
            }

            for (var index = 0; index < count; index++)
            {
                var at = body[(4 + (index * entry))..];
                long duration;
                long mediaTime;
                ReadOnlySpan<byte> rate;
                if (version == 1)
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

                track.Edits.Add((duration, mediaTime, BinaryPrimitives.ReadInt16BigEndian(rate) == 1 && BinaryPrimitives.ReadUInt16BigEndian(rate[2..]) == 0));
            }
        }
        else if (type == Code("stts") && body.Length >= 4)
        {
            var count = BinaryPrimitives.ReadUInt32BigEndian(body);
            if (count > (uint)((body.Length - 4) / 8))
            {
                track.TablesUnreadable = true;
                return;
            }

            for (var index = 0; index < count; index++)
            {
                var at = body[(4 + (index * 8))..];
                track.TimeToSample.Add((BinaryPrimitives.ReadUInt32BigEndian(at), BinaryPrimitives.ReadUInt32BigEndian(at[4..])));
            }
        }
        else if (type == Code("ctts") && body.Length >= 4)
        {
            var count = BinaryPrimitives.ReadUInt32BigEndian(body);
            if (count > (uint)((body.Length - 4) / 8))
            {
                track.TablesUnreadable = true;
                return;
            }

            for (var index = 0; index < count; index++)
            {
                var at = body[(4 + (index * 8))..];
                long offset = version == 0 ? BinaryPrimitives.ReadUInt32BigEndian(at[4..]) : BinaryPrimitives.ReadInt32BigEndian(at[4..]);
                track.CompositionOffsets.Add((BinaryPrimitives.ReadUInt32BigEndian(at), offset));
            }
        }
    }

    private static StudioPreviewFrameTimes? FromMovie(Movie movie, out string? problem)
    {
        problem = null;
        if (movie.Fragmented)
        {
            problem = "its index is in fragments";
            return null;
        }

        Track? video = null;
        foreach (var track in movie.Tracks)
        {
            if (track.IsVideo)
            {
                video = track;
                break;
            }
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

        if (video.TablesUnreadable)
        {
            problem = "its table of frame times is cut short";
            return null;
        }

        // The edit list moves the track on the movie's timeline: stretches of nothing first, then
        // the track from some time of its own. More than that is not a recording, and not read.
        // How long the edit list shows the track for is not looked at.
        long lead = 0;
        long mediaStart = 0;
        if (video.EditsUnreadable)
        {
            problem = "its edit list could not be read";
            return null;
        }

        long nothing = 0;
        var shows = 0;
        foreach (var (duration, mediaTime, atRateOne) in video.Edits)
        {
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

            if (++shows > 1 || !atRateOne)
            {
                problem = shows > 1 ? "its edit list has more than one part" : "its edit list changes the speed";
                return null;
            }

            lead = Ticks(nothing, movie.Timescale);
            mediaStart = mediaTime;
        }

        if (shows == 0 && video.Edits.Count > 0)
        {
            problem = "its edit list shows nothing of the video";
            return null;
        }

        long samples = 0;
        foreach (var (count, _) in video.TimeToSample)
        {
            samples += count;
        }

        if (samples == 0)
        {
            problem = "it has no frames";
            return null;
        }

        if (samples > MostFrames)
        {
            problem = $"it has {samples} frames";
            return null;
        }

        // When each sample is decoded, then how much later it is shown.
        var times = new long[samples];
        long decode = 0;
        long lastDelta = 0;
        var sample = 0;
        foreach (var (count, delta) in video.TimeToSample)
        {
            for (var index = 0; index < count; index++)
            {
                times[sample++] = decode;
                decode += delta;
            }

            lastDelta = delta;
        }

        var reordered = video.CompositionOffsets.Count > 0;
        if (reordered)
        {
            sample = 0;
            foreach (var (count, offset) in video.CompositionOffsets)
            {
                for (var index = 0; index < count && sample < times.Length; index++, sample++)
                {
                    times[sample] += offset;
                }
            }

            // The samples are stored in the order they are decoded; they are shown in the order of their times.
            Array.Sort(times);
        }

        // A frame the edit list puts before the start: whether a player shows it, and until when,
        // is more than the index says.
        if (times[0] < mediaStart)
        {
            problem = "its edit list begins inside the video";
            return null;
        }

        // In the track's own units from where the edit list starts it, then in 100 ns, then
        // after the stretch of nothing: so that each time is rounded once.
        var last = times[^1];
        for (var index = 0; index < times.Length; index++)
        {
            times[index] = Ticks(times[index] - mediaStart, video.Timescale) + lead;
        }

        var end = reordered ? 0 : Ticks(last + lastDelta - mediaStart, video.Timescale) + lead;
        return TryCreate(times, end, out problem);
    }

    private static long Ticks(long units, uint timescale) =>
        (long)Math.Round(units * (decimal)StudioPreviewTimeMath.TicksPerSecond / timescale, MidpointRounding.AwayFromZero);

    private static uint Code(string type) => ((uint)type[0] << 24) | ((uint)type[1] << 16) | ((uint)type[2] << 8) | type[3];
}
