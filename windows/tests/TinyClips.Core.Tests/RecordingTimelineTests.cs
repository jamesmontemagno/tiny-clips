using System.Diagnostics;
using NAudio.Wave;
using TinyClips.Core.Capture;
using TinyClips.Core.Models;

namespace TinyClips.Core.Tests;

public sealed class RecordingTimelineTests
{
    [Fact]
    public void Normalize_UsesSharedSystemRelativeOrigin()
    {
        var origin = TimeSpan.FromSeconds(42);
        var timeline = RecordingTimeline.FromOrigin(origin);

        Assert.Equal(TimeSpan.FromMilliseconds(125), timeline.Normalize(origin + TimeSpan.FromMilliseconds(125)));
        Assert.Equal(TimeSpan.FromMilliseconds(-25), timeline.Normalize(origin - TimeSpan.FromMilliseconds(25)));
    }

    [Fact]
    public void Normalize_SubtractsPausedDuration()
    {
        var origin = SystemRelativeNow();
        var timeline = RecordingTimeline.FromOrigin(origin);

        timeline.Pause();
        Thread.Sleep(50);
        timeline.Resume();

        var sampleTimestamp = SystemRelativeNow() + TimeSpan.FromMilliseconds(100);
        var rawElapsed = sampleTimestamp - origin;
        var normalized = timeline.Normalize(sampleTimestamp);

        Assert.InRange((rawElapsed - normalized).TotalMilliseconds, 20, 5000);
    }

    [Fact]
    public void AlignedProviders_PreserveDifferentSourceStartOffsets()
    {
        var format = new WaveFormat(1000, 16, 1);
        var origin = TimeSpan.FromSeconds(10);
        var first = new TimelineAlignedWaveProvider(format);
        var second = new TimelineAlignedWaveProvider(format);
        first.BeginTimeline(origin);
        second.BeginTimeline(origin);

        first.AddSamples(ToBytes(100, 101), 4, origin);
        second.AddSamples(ToBytes(200, 201), 4, origin + TimeSpan.FromMilliseconds(2));

        Assert.Equal(new short[] { 100, 101, 0, 0 }, ReadSamples(first, 4));
        Assert.Equal(new short[] { 0, 0, 200, 201 }, ReadSamples(second, 4));
    }

    [Fact]
    public void AddSamples_AppendsLaterPacketsContiguouslyIgnoringPerPacketTimestamps()
    {
        // Only the first packet is aligned to the origin. Later packets are appended contiguously
        // so per-packet timestamp jitter never inserts or drops samples (which caused crackle).
        var format = new WaveFormat(1000, 16, 1);
        var origin = TimeSpan.FromSeconds(10);
        var provider = new TimelineAlignedWaveProvider(format);
        provider.BeginTimeline(origin);
        provider.AddSamples(ToBytes(1, 2, 3, 4), 8, origin);

        Assert.Equal(new short[] { 1, 2 }, ReadSamples(provider, 2));

        // A later packet arrives with a jittery timestamp; it must still append right after the
        // previously buffered audio rather than being trimmed or padded.
        provider.AddSamples(ToBytes(9, 10, 11), 6, origin + TimeSpan.FromMilliseconds(3));

        Assert.Equal(new short[] { 3, 4, 9, 10 }, ReadSamples(provider, 4));
    }

    [Fact]
    public void AddSamples_TrimsFirstPacketFramesRecordedBeforeOrigin()
    {
        // A first packet that began before the origin has its pre-origin frames dropped so the
        // stream still starts exactly at the shared origin.
        var format = new WaveFormat(1000, 16, 1);
        var origin = TimeSpan.FromSeconds(10);
        var provider = new TimelineAlignedWaveProvider(format);
        provider.BeginTimeline(origin);

        // Packet starts 2 ms (2 frames at 1000 Hz) before the origin.
        provider.AddSamples(ToBytes(1, 2, 3, 4), 8, origin - TimeSpan.FromMilliseconds(2));

        Assert.Equal(new short[] { 3, 4 }, ReadSamples(provider, 2));
    }

    [Fact]
    public void AddSamples_DropsEntirePreOriginPacketsThenAlignsFirstStraddlingPacket()
    {
        // With a larger capture buffer, several whole packets can predate the origin. Each fully
        // pre-origin packet must be dropped (not appended at the origin), so only audio at/after
        // the origin is kept — otherwise stale pre-roll would delay all real audio.
        var format = new WaveFormat(1000, 16, 1);
        var origin = TimeSpan.FromSeconds(10);
        var provider = new TimelineAlignedWaveProvider(format);
        provider.BeginTimeline(origin);

        // Entirely before the origin (4 frames ending 6 ms before origin): dropped.
        provider.AddSamples(ToBytes(1, 2, 3, 4), 8, origin - TimeSpan.FromMilliseconds(10));
        // Straddles the origin by 2 frames: first two dropped, {30, 40} kept.
        provider.AddSamples(ToBytes(10, 20, 30, 40), 8, origin - TimeSpan.FromMilliseconds(2));

        Assert.Equal(new short[] { 30, 40 }, ReadSamples(provider, 2));
    }

    [Fact]
    public void AddSamples_AdvancesAudioByCaptureLatency()
    {
        // A source with capture latency reports timestamps that trail real capture time, so audio
        // is advanced by the latency: frames within the latency window are trimmed from the front.
        var format = new WaveFormat(1000, 16, 1);
        var origin = TimeSpan.FromSeconds(10);
        var provider = new TimelineAlignedWaveProvider(format)
        {
            Latency = TimeSpan.FromMilliseconds(3),
        };
        provider.BeginTimeline(origin);

        // Packet reported exactly at the origin, but 3 ms (3 frames) of latency means the first
        // 3 frames actually predate the true capture instant and are trimmed.
        provider.AddSamples(ToBytes(1, 2, 3, 4, 5), 10, origin);

        Assert.Equal(new short[] { 4, 5 }, ReadSamples(provider, 2));
    }

    [Fact]
    public void Pause_DropsSamplesUntilResume_ThenPlacesResumedPacketOnTimeline()
    {
        var format = new WaveFormat(1000, 16, 1);
        var origin = TimeSpan.FromSeconds(10);
        var provider = new TimelineAlignedWaveProvider(format);
        provider.BeginTimeline(origin);
        provider.AddSamples(ToBytes(1, 2), 4, origin);

        Assert.Equal(new short[] { 1, 2 }, ReadSamples(provider, 2));

        // Packets during the pause are dropped.
        provider.Pause();
        provider.AddSamples(ToBytes(7, 8), 4, origin + TimeSpan.FromMilliseconds(2));
        provider.Resume();

        // The timeline here has no paused interval (FromOrigin), so a packet stamped 1 s after the
        // origin belongs at frame 1000; the provider must pad the 998-frame gap rather than append
        // it right after frame 2 (which would have pulled all later audio ~1 s early).
        provider.AddSamples(ToBytes(3, 4), 4, origin + TimeSpan.FromSeconds(1));

        var stats = provider.GetStats();
        Assert.Equal(1, stats.CorrectionCount);
        Assert.Equal(998, stats.PaddedFrames);
        var all = ReadSamples(provider, 1000);
        Assert.All(all.Take(998), s => Assert.Equal(0, s));
        Assert.Equal(new short[] { 3, 4 }, all.Skip(998).ToArray());
    }

    [Fact]
    public void Pause_RetainsCapturedAudioForTheMuxer()
    {
        // Audio captured before a pause belongs to pre-pause video time; clearing it would shift
        // everything after the pause earlier by the discarded amount.
        var format = new WaveFormat(1000, 16, 1);
        var origin = TimeSpan.FromSeconds(10);
        var provider = new TimelineAlignedWaveProvider(format);
        provider.BeginTimeline(origin);
        provider.AddSamples(ToBytes(1, 2, 3, 4), 8, origin);

        provider.Pause();

        Assert.Equal(TimeSpan.FromMilliseconds(4), provider.BufferedDuration);
        Assert.Equal(new short[] { 1, 2, 3, 4 }, ReadSamples(provider, 4));
    }

    [Fact]
    public void Resume_WithPausedTimeline_AppendsContiguouslyWhenWithinTolerance()
    {
        // With a real paused interval on the timeline, a packet captured right after resume
        // normalizes to just after the pause point, i.e. right where the pre-pause audio ended, so
        // no correction is needed.
        var format = new WaveFormat(1000, 16, 1);
        var origin = SystemRelativeNow();
        var timeline = RecordingTimeline.FromOrigin(origin);
        var provider = new TimelineAlignedWaveProvider(format);
        provider.BeginTimeline(timeline);
        provider.AddSamples(ToBytes(1, 2, 3, 4, 5), 10, origin);

        Assert.Equal(new short[] { 1, 2, 3, 4, 5 }, ReadSamples(provider, 5));

        provider.Pause();
        timeline.Pause();
        Thread.Sleep(60);
        timeline.Resume();
        provider.Resume();

        // Stamped at "now": normalizes to (now - origin - paused) ≈ 5 ms, which is where frame 5 sits.
        provider.AddSamples(ToBytes(6, 7), 4, SystemRelativeNow());

        var stats = provider.GetStats();
        Assert.Equal(0, stats.CorrectionCount);
        Assert.True(stats.LastDeviation.Duration() < TimelineAlignedWaveProvider.DriftTolerance);
        Assert.Equal(new short[] { 6, 7 }, ReadSamples(provider, 2));
    }

    [Fact]
    public void AddSamples_IgnoresJitterBelowTolerance()
    {
        // Per-packet timestamp jitter (a few frames either way) never triggers a correction, so
        // the crackle from per-packet micro-edits cannot return.
        var format = new WaveFormat(1000, 16, 1);
        var origin = TimeSpan.FromSeconds(10);
        var provider = new TimelineAlignedWaveProvider(format);
        provider.BeginTimeline(origin);

        var expected = new List<short>();
        var jitter = new[] { 0, 2, -3, 5, -4, 1, -1, 3, -2, 0 };
        for (var i = 0; i < jitter.Length; i++)
        {
            var samples = Enumerable.Range(i * 10, 10).Select(v => (short)v).ToArray();
            expected.AddRange(samples);
            var ts = origin + TimeSpan.FromMilliseconds(i * 10 + jitter[i]);
            provider.AddSamples(ToBytes(samples), samples.Length * 2, ts);
        }

        var stats = provider.GetStats();
        Assert.Equal(0, stats.CorrectionCount);
        Assert.Equal(0, stats.PaddedFrames);
        Assert.Equal(0, stats.TrimmedFrames);
        Assert.Equal(expected.ToArray(), ReadSamples(provider, expected.Count));
    }

    [Fact]
    public void AddSamples_PadsGapBeyondTolerance()
    {
        // A dropped packet (buffer overrun) leaves a gap in timestamps; exactly that much silence
        // is inserted so later audio stays on the video clock.
        var format = new WaveFormat(1000, 16, 1);
        var origin = TimeSpan.FromSeconds(10);
        var provider = new TimelineAlignedWaveProvider(format);
        provider.BeginTimeline(origin);
        provider.AddSamples(ToBytes(1, 2, 3, 4, 5, 6, 7, 8, 9, 10), 20, origin);

        // Next packet should have been at 10 ms; it arrives stamped at 60 ms (50 frames lost).
        provider.AddSamples(ToBytes(11, 12), 4, origin + TimeSpan.FromMilliseconds(60));

        var stats = provider.GetStats();
        Assert.Equal(1, stats.CorrectionCount);
        Assert.Equal(50, stats.PaddedFrames);
        var all = ReadSamples(provider, 62);
        Assert.Equal(10, all[9]);
        Assert.All(all.Skip(10).Take(50), s => Assert.Equal(0, s));
        Assert.Equal(new short[] { 11, 12 }, all.Skip(60).ToArray());
    }

    [Fact]
    public void AddSamples_TrimsOverlapBeyondTolerance_AcrossPackets()
    {
        // A source running ahead of the timeline (fast device clock) is trimmed back; when the
        // overlap exceeds one packet, the remainder is trimmed from the following packets.
        var format = new WaveFormat(1000, 16, 1);
        var origin = TimeSpan.FromSeconds(10);
        var provider = new TimelineAlignedWaveProvider(format);
        provider.BeginTimeline(origin);
        var first = Enumerable.Range(1, 100).Select(v => (short)v).ToArray();
        provider.AddSamples(ToBytes(first), 200, origin);

        // Written = 100 frames, but the device claims this packet belongs at 60 ms → 40 frames ahead:
        // the whole 10-frame packet is trimmed.
        provider.AddSamples(ToBytes(201, 202, 203, 204, 205, 206, 207, 208, 209, 210), 20, origin + TimeSpan.FromMilliseconds(60));
        // Written still 100; next packet stamped 65 ms → 35 ahead, trimmed too. Then 80 ms → 20 ahead (within tolerance: kept).
        provider.AddSamples(ToBytes(301, 302, 303, 304, 305, 306, 307, 308, 309, 310), 20, origin + TimeSpan.FromMilliseconds(65));
        provider.AddSamples(ToBytes(401, 402), 4, origin + TimeSpan.FromMilliseconds(80));

        var stats = provider.GetStats();
        Assert.Equal(2, stats.CorrectionCount);
        Assert.Equal(20, stats.TrimmedFrames);
        var all = ReadSamples(provider, 102);
        Assert.Equal(100, all[99]);
        Assert.Equal(new short[] { 401, 402 }, all.Skip(100).ToArray());
    }

    [Fact]
    public void AddSamples_SlowDrift_IsCorrectedOnceToleranceIsCrossed()
    {
        // Device clock ~10% fast: each packet stamped 10 ms apart actually carries 11 frames, so the
        // written cursor gains one frame per packet on the timeline. Nothing happens until the
        // deviation passes 30 frames (packet 31); that packet is then trimmed entirely (11 frames),
        // pulling the deviation back inside tolerance, and appending resumes.
        var format = new WaveFormat(1000, 16, 1);
        var origin = TimeSpan.FromSeconds(10);
        var provider = new TimelineAlignedWaveProvider(format);
        provider.BeginTimeline(origin);

        for (var i = 0; i < 40; i++)
        {
            var samples = Enumerable.Repeat((short)(i + 1), 11).ToArray();
            var ts = origin + TimeSpan.FromMilliseconds(i * 10);
            provider.AddSamples(ToBytes(samples), samples.Length * 2, ts);

            var statsSoFar = provider.GetStats();
            if (i < 31)
            {
                Assert.Equal(0, statsSoFar.CorrectionCount);
            }
        }

        var stats = provider.GetStats();
        Assert.Equal(1, stats.CorrectionCount);
        Assert.Equal(11, stats.TrimmedFrames);
        Assert.True(stats.LastDeviation.Duration() <= TimelineAlignedWaveProvider.DriftTolerance);
    }

    [Fact]
    public void AddSamples_DiscontinuityFlagForcesCorrectionBelowTolerance()
    {
        var format = new WaveFormat(1000, 16, 1);
        var origin = TimeSpan.FromSeconds(10);
        var provider = new TimelineAlignedWaveProvider(format);
        provider.BeginTimeline(origin);
        provider.AddSamples(ToBytes(1, 2, 3, 4, 5, 6, 7, 8, 9, 10), 20, origin);

        // 5-frame gap is inside tolerance, but the driver says data was lost: pad it exactly.
        provider.AddSamples(ToBytes(11, 12), 4, origin + TimeSpan.FromMilliseconds(15), discontinuity: true);

        var stats = provider.GetStats();
        Assert.Equal(1, stats.CorrectionCount);
        Assert.Equal(5, stats.PaddedFrames);
        var all = ReadSamples(provider, 17);
        Assert.All(all.Skip(10).Take(5), s => Assert.Equal(0, s));
        Assert.Equal(new short[] { 11, 12 }, all.Skip(15).ToArray());
    }

    [Fact]
    public void UserOffset_ShiftsWhereAudioLands()
    {
        var format = new WaveFormat(1000, 16, 1);
        var origin = TimeSpan.FromSeconds(10);

        // Positive offset delays audio: a packet at the origin lands 4 frames later.
        var delayed = new TimelineAlignedWaveProvider(format) { UserOffset = TimeSpan.FromMilliseconds(4) };
        delayed.BeginTimeline(origin);
        delayed.AddSamples(ToBytes(1, 2), 4, origin);
        Assert.Equal(new short[] { 0, 0, 0, 0, 1, 2 }, ReadSamples(delayed, 6));

        // Negative offset advances audio: the first 3 frames are treated as pre-origin and trimmed.
        var advanced = new TimelineAlignedWaveProvider(format) { UserOffset = TimeSpan.FromMilliseconds(-3) };
        advanced.BeginTimeline(origin);
        advanced.AddSamples(ToBytes(1, 2, 3, 4, 5), 10, origin);
        Assert.Equal(new short[] { 4, 5 }, ReadSamples(advanced, 2));
    }

    [Fact]
    public void Read_Underrun_AdvancesTimelineSoNextPacketIsTrimmed()
    {
        // If the muxer reads past captured audio, the zero padding it received occupies timeline
        // positions. The next packet must not be appended behind that padding (it would play late);
        // it is trimmed so its content still lands at its real time.
        var format = new WaveFormat(1000, 16, 1);
        var origin = TimeSpan.FromSeconds(10);
        var provider = new TimelineAlignedWaveProvider(format);
        provider.BeginTimeline(origin);
        provider.AddSamples(ToBytes(1, 2, 3, 4, 5, 6, 7, 8, 9, 10), 20, origin);

        // Muxer reads 60 frames although only 10 are buffered → 50 frames of underrun padding.
        var read = ReadSamples(provider, 60);
        Assert.Equal(10, read[9]);
        Assert.Equal(0, read[59]);
        Assert.Equal(60, provider.FramesWritten);
        Assert.Equal(50, provider.GetStats().UnderrunFrames);

        // Next packet is stamped at 10 ms (frame 10), but frames 10..59 were already emitted as
        // silence, so its first 50 frames are trimmed and the rest lands at frame 60.
        var packet = Enumerable.Range(11, 55).Select(v => (short)v).ToArray();
        provider.AddSamples(ToBytes(packet), packet.Length * 2, origin + TimeSpan.FromMilliseconds(10));

        Assert.Equal(50, provider.GetStats().TrimmedFrames);
        Assert.Equal(new short[] { 61, 62, 63, 64, 65 }, ReadSamples(provider, 5));
    }

    [Fact]
    public void WebcamPlacements_ApplyEveryCornerAtItsRecordingTime()
    {
        var placements = new WebcamPlacementTimeline(WebcamCornerPosition.BottomRight);

        placements.Add(TimeSpan.FromSeconds(2), WebcamCornerPosition.TopLeft);
        placements.Add(TimeSpan.FromSeconds(5), WebcamCornerPosition.BottomLeft);

        Assert.Equal(WebcamCornerPosition.BottomRight, placements.CornerAt(TimeSpan.FromSeconds(1)));
        Assert.Equal(WebcamCornerPosition.TopLeft, placements.CornerAt(TimeSpan.FromSeconds(2)));
        Assert.Equal(WebcamCornerPosition.TopLeft, placements.CornerAt(TimeSpan.FromSeconds(4)));
        Assert.Equal(WebcamCornerPosition.BottomLeft, placements.CornerAt(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void WebcamPlacements_ReplaceChangesAtSamePausedTime()
    {
        var placements = new WebcamPlacementTimeline(WebcamCornerPosition.BottomRight);

        placements.Add(TimeSpan.FromSeconds(2), WebcamCornerPosition.TopLeft);
        placements.Add(TimeSpan.FromSeconds(2), WebcamCornerPosition.TopRight);

        Assert.Equal(WebcamCornerPosition.TopRight, placements.CornerAt(TimeSpan.FromSeconds(2)));
        Assert.Equal(2, placements.Events.Count);
    }

    private static byte[] ToBytes(params short[] samples)
    {
        var bytes = new byte[samples.Length * sizeof(short)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static short[] ReadSamples(IWaveProvider provider, int count)
    {
        var bytes = new byte[count * sizeof(short)];
        Assert.Equal(bytes.Length, provider.Read(bytes.AsSpan()));
        var samples = new short[count];
        Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
        return samples;
    }

    private static TimeSpan SystemRelativeNow() =>
        Stopwatch.GetElapsedTime(0, Stopwatch.GetTimestamp());
}
