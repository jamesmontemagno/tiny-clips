using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace TinyClips.Tools.StudioPreviewCheck.Media;

/// <summary>One entry of a track's edit list, as the file has it.</summary>
/// <param name="SegmentDuration">How long the entry lasts on the movie's timeline, in the movie's time units.</param>
/// <param name="MediaTime">Where in the track it starts, in the track's time units; -1 for a stretch in which the track shows nothing.</param>
internal readonly record struct Mp4Edit(long SegmentDuration, long MediaTime, double Rate);

/// <summary>What an MP4 file's index says about one track. Nothing is decoded.</summary>
internal sealed class Mp4Track
{
    /// <summary>"vide" for video, "soun" for sound.</summary>
    public string Handler { get; set; } = string.Empty;

    /// <summary>The track's time units in a second.</summary>
    public uint Timescale { get; set; }

    /// <summary>The track's length in its own time units (mdhd).</summary>
    public long MediaDuration { get; set; }

    /// <summary>The track's length on the movie's timeline, in the movie's time units (tkhd).</summary>
    public long TrackDuration { get; set; }

    public List<Mp4Edit> Edits { get; } = [];

    /// <summary>Runs of samples that each last the same time (stts).</summary>
    public List<(uint Count, uint Delta)> TimeToSample { get; } = [];

    /// <summary>Runs of samples whose presentation time is this much after their decode time (ctts). Empty when the two are the same.</summary>
    public List<(uint Count, long Offset)> CompositionOffsets { get; } = [];

    /// <summary>The samples a decoder can start from, counted from 1 (stss). Empty when every sample is one.</summary>
    public List<int> SyncSamples { get; } = [];

    /// <summary>How many samples the track has (stsz or stz2).</summary>
    public int SampleCount { get; set; }

    /// <summary>
    /// When each sample is presented, in the track's own time units and in the order the samples
    /// are stored: before the edit list has its say.
    /// </summary>
    public long[] MediaTimes()
    {
        var times = new List<long>(Math.Max(0, SampleCount));
        long decode = 0;
        foreach (var (count, delta) in TimeToSample)
        {
            for (var index = 0; index < count; index++)
            {
                times.Add(decode);
                decode += delta;
            }
        }

        var sample = 0;
        foreach (var (count, offset) in CompositionOffsets)
        {
            for (var index = 0; index < count && sample < times.Count; index++, sample++)
            {
                times[sample] += offset;
            }
        }

        return [.. times];
    }

    /// <summary>
    /// How far the edit list moves the track on the movie's timeline, in 100 ns units: the
    /// stretches of nothing before the first entry that shows the track, less where in the track
    /// that entry starts. 0 for a track without an edit list.
    /// </summary>
    public long EditShift(uint movieTimescale)
    {
        long empty = 0;
        foreach (var edit in Edits)
        {
            if (edit.MediaTime < 0)
            {
                empty += edit.SegmentDuration;
                continue;
            }

            return Ticks(empty, movieTimescale) - Ticks(edit.MediaTime, Timescale);
        }

        return 0;
    }

    /// <summary>When each sample is presented on the movie's timeline, in 100 ns units, in stored order.</summary>
    public long[] Times(uint movieTimescale)
    {
        var shift = EditShift(movieTimescale);
        var media = MediaTimes();
        var times = new long[media.Length];
        for (var index = 0; index < media.Length; index++)
        {
            times[index] = Ticks(media[index], Timescale) + shift;
        }

        return times;
    }

    internal static long Ticks(long units, uint timescale) =>
        timescale == 0 ? 0 : (long)Math.Round(units * 10_000_000m / timescale, MidpointRounding.AwayFromZero);
}

/// <summary>
/// The index of an MP4 file, read from its boxes: the movie's time units, and for each track its
/// time units, its edit list and the table that says how long each sample lasts. It is what a
/// file says about its frames' times before any player or decoder has made something of it.
/// Only files with their index in one piece (a <c>moov</c> box, no fragments), which is what
/// Media Foundation's writer makes.
/// </summary>
internal sealed class Mp4Index
{
    private static readonly HashSet<string> Containers = new(StringComparer.Ordinal) { "moov", "trak", "edts", "mdia", "minf", "stbl" };

    /// <summary>The movie's time units in a second (mvhd).</summary>
    public uint Timescale { get; private set; }

    /// <summary>The movie's length in its time units (mvhd).</summary>
    public long Duration { get; private set; }

    public List<Mp4Track> Tracks { get; } = [];

    /// <summary>The file keeps its index in fragments, which this does not read.</summary>
    public bool Fragmented { get; private set; }

    /// <summary>The top-level boxes in file order, for the report: where the index is.</summary>
    public List<string> TopLevel { get; } = [];

    public Mp4Track? Video => Tracks.FirstOrDefault(track => track.Handler == "vide");

    public Mp4Track? Audio => Tracks.FirstOrDefault(track => track.Handler == "soun");

    public static Mp4Index Read(string path)
    {
        var index = new Mp4Index();
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var header = new byte[16];
        while (file.Position + 8 <= file.Length)
        {
            var start = file.Position;
            file.ReadExactly(header, 0, 8);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = Encoding.ASCII.GetString(header, 4, 4);
            var headerLength = 8;
            if (size == 1)
            {
                file.ReadExactly(header, 8, 8);
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
                headerLength = 16;
            }
            else if (size == 0)
            {
                size = file.Length - start;
            }

            if (size < headerLength || start + size > file.Length)
            {
                throw new InvalidDataException($"{Path.GetFileName(path)}: the box '{type}' at {start} says it is {size} bytes long, which the file is not.");
            }

            index.TopLevel.Add(type);
            if (type == "moov")
            {
                var payload = new byte[size - headerLength];
                file.ReadExactly(payload);
                index.Walk(payload, null);
            }
            else if (type == "moof")
            {
                index.Fragmented = true;
            }

            file.Position = start + size;
        }

        if (index.Timescale == 0)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} has no index (no 'moov' box with an 'mvhd' in it).");
        }

        return index;
    }

    /// <summary>What the index says, in a few lines.</summary>
    public IEnumerable<string> Describe()
    {
        var c = CultureInfo.InvariantCulture;
        yield return string.Create(c, $"boxes: {string.Join(' ', TopLevel)}; the movie counts {Timescale} units a second and lasts {Seconds(Duration, Timescale):0.0000} s{(Fragmented ? "; it has fragments, which are not read here" : string.Empty)}");
        foreach (var track in Tracks)
        {
            var media = track.MediaTimes();
            var deltas = string.Join(", ", track.TimeToSample.Take(6).Select(run => string.Create(c, $"{run.Count} of {run.Delta}")));
            var edits = track.Edits.Count == 0
                ? "no edit list"
                : "edit list: " + string.Join("; ", track.Edits.Select(edit => edit.MediaTime < 0
                    ? string.Create(c, $"nothing for {Seconds(edit.SegmentDuration, Timescale) * 1000:0.###} ms")
                    : string.Create(c, $"{Seconds(edit.SegmentDuration, Timescale):0.0000} s of the track from {Seconds(edit.MediaTime, track.Timescale) * 1000:0.###} ms of it{(edit.Rate == 1 ? string.Empty : string.Create(c, $" at rate {edit.Rate:0.###}"))}")));
            var first = media.Length == 0 ? "no samples" : string.Create(c, $"first sample at {Seconds(media[0], track.Timescale) * 1000:0.####} ms of the track, {(track.EditShift(Timescale) + Mp4Track.Ticks(media[0], track.Timescale)) / 10000.0:0.####} ms of the movie");
            yield return string.Create(c, $"track '{track.Handler}': {track.Timescale} units a second, {track.SampleCount} samples, {Seconds(track.MediaDuration, track.Timescale):0.0000} s of media, {Seconds(track.TrackDuration, Timescale):0.0000} s on the movie's timeline; {edits}; {first}; {track.TimeToSample.Count} runs of equal sample lengths{(deltas.Length == 0 ? string.Empty : $" ({deltas}{(track.TimeToSample.Count > 6 ? ", ..." : string.Empty)})")}{(track.CompositionOffsets.Count == 0 ? string.Empty : "; presentation times differ from decode times (ctts)")}; {(track.SyncSamples.Count == 0 ? "every sample is a key frame" : string.Create(c, $"{track.SyncSamples.Count} key frames"))}");
        }
    }

    private static double Seconds(long units, uint timescale) => timescale == 0 ? 0 : units / (double)timescale;

    private void Walk(ReadOnlySpan<byte> data, Mp4Track? track)
    {
        var position = 0;
        while (position + 8 <= data.Length)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(data[position..]);
            var type = Encoding.ASCII.GetString(data.Slice(position + 4, 4));
            var headerLength = 8;
            if (size == 1 && position + 16 <= data.Length)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(data[(position + 8)..]);
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
            if (type == "trak")
            {
                var inner = new Mp4Track();
                Tracks.Add(inner);
                Walk(payload, inner);
            }
            else if (Containers.Contains(type))
            {
                Walk(payload, track);
            }
            else if (type == "mvex")
            {
                Fragmented = true;
            }
            else
            {
                Leaf(type, payload, track);
            }

            position += (int)size;
        }
    }

    private void Leaf(string type, ReadOnlySpan<byte> box, Mp4Track? track)
    {
        if (box.Length < 4)
        {
            return;
        }

        var version = box[0];
        var body = box[4..];
        switch (type)
        {
            case "mvhd" when version == 1 && body.Length >= 28:
                Timescale = BinaryPrimitives.ReadUInt32BigEndian(body[16..]);
                Duration = (long)BinaryPrimitives.ReadUInt64BigEndian(body[20..]);
                break;

            case "mvhd" when body.Length >= 16:
                Timescale = BinaryPrimitives.ReadUInt32BigEndian(body[8..]);
                Duration = BinaryPrimitives.ReadUInt32BigEndian(body[12..]);
                break;

            case "tkhd" when track is not null && version == 1 && body.Length >= 32:
                track.TrackDuration = (long)BinaryPrimitives.ReadUInt64BigEndian(body[24..]);
                break;

            case "tkhd" when track is not null && body.Length >= 20:
                track.TrackDuration = BinaryPrimitives.ReadUInt32BigEndian(body[16..]);
                break;

            case "mdhd" when track is not null && version == 1 && body.Length >= 28:
                track.Timescale = BinaryPrimitives.ReadUInt32BigEndian(body[16..]);
                track.MediaDuration = (long)BinaryPrimitives.ReadUInt64BigEndian(body[20..]);
                break;

            case "mdhd" when track is not null && body.Length >= 16:
                track.Timescale = BinaryPrimitives.ReadUInt32BigEndian(body[8..]);
                track.MediaDuration = BinaryPrimitives.ReadUInt32BigEndian(body[12..]);
                break;

            case "hdlr" when track is not null && body.Length >= 8:
                track.Handler = Encoding.ASCII.GetString(body.Slice(4, 4));
                break;

            case "elst" when track is not null && body.Length >= 4:
            {
                var count = (int)BinaryPrimitives.ReadUInt32BigEndian(body);
                var entry = version == 1 ? 20 : 12;
                for (var index = 0; index < count && 4 + ((index + 1) * entry) <= body.Length; index++)
                {
                    var at = body[(4 + (index * entry))..];
                    long duration;
                    long mediaTime;
                    ReadOnlySpan<byte> rate;
                    if (version == 1)
                    {
                        duration = (long)BinaryPrimitives.ReadUInt64BigEndian(at);
                        mediaTime = BinaryPrimitives.ReadInt64BigEndian(at[8..]);
                        rate = at[16..];
                    }
                    else
                    {
                        duration = BinaryPrimitives.ReadUInt32BigEndian(at);
                        mediaTime = BinaryPrimitives.ReadInt32BigEndian(at[4..]);
                        rate = at[8..];
                    }

                    track.Edits.Add(new Mp4Edit(duration, mediaTime, BinaryPrimitives.ReadInt16BigEndian(rate) + (BinaryPrimitives.ReadUInt16BigEndian(rate[2..]) / 65536.0)));
                }

                break;
            }

            case "stts" when track is not null && body.Length >= 4:
            {
                var count = (int)BinaryPrimitives.ReadUInt32BigEndian(body);
                for (var index = 0; index < count && 4 + ((index + 1) * 8) <= body.Length; index++)
                {
                    var at = body[(4 + (index * 8))..];
                    track.TimeToSample.Add((BinaryPrimitives.ReadUInt32BigEndian(at), BinaryPrimitives.ReadUInt32BigEndian(at[4..])));
                }

                break;
            }

            case "ctts" when track is not null && body.Length >= 4:
            {
                var count = (int)BinaryPrimitives.ReadUInt32BigEndian(body);
                for (var index = 0; index < count && 4 + ((index + 1) * 8) <= body.Length; index++)
                {
                    var at = body[(4 + (index * 8))..];
                    long offset = version == 0 ? BinaryPrimitives.ReadUInt32BigEndian(at[4..]) : BinaryPrimitives.ReadInt32BigEndian(at[4..]);
                    track.CompositionOffsets.Add((BinaryPrimitives.ReadUInt32BigEndian(at), offset));
                }

                break;
            }

            case "stss" when track is not null && body.Length >= 4:
            {
                var count = (int)BinaryPrimitives.ReadUInt32BigEndian(body);
                for (var index = 0; index < count && 4 + ((index + 1) * 4) <= body.Length; index++)
                {
                    track.SyncSamples.Add((int)BinaryPrimitives.ReadUInt32BigEndian(body[(4 + (index * 4))..]));
                }

                break;
            }

            case "stsz" when track is not null && body.Length >= 8:
                track.SampleCount = (int)BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
                break;

            case "stz2" when track is not null && body.Length >= 8:
                track.SampleCount = (int)BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
                break;
        }
    }
}
