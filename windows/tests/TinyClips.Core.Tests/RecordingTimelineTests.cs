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
    public void Pause_DropsSamplesUntilResume()
    {
        var format = new WaveFormat(1000, 16, 1);
        var origin = TimeSpan.FromSeconds(10);
        var provider = new TimelineAlignedWaveProvider(format);
        provider.BeginTimeline(origin);
        provider.AddSamples(ToBytes(1, 2), 4, origin);

        Assert.Equal(new short[] { 1, 2 }, ReadSamples(provider, 2));

        provider.Pause();
        provider.AddSamples(ToBytes(1, 2), 4, origin);
        provider.Resume();
        provider.AddSamples(ToBytes(3, 4), 4, origin + TimeSpan.FromSeconds(1));

        Assert.Equal(new short[] { 3, 4 }, ReadSamples(provider, 2));
    }

    [Fact]
    public void Resume_AppendsContiguouslyWithoutRealigningFirstPacket()
    {
        var format = new WaveFormat(1000, 16, 1);
        var origin = TimeSpan.FromSeconds(10);
        var provider = new TimelineAlignedWaveProvider(format)
        {
            Latency = TimeSpan.FromMilliseconds(3),
        };
        provider.BeginTimeline(origin);
        provider.AddSamples(ToBytes(1, 2, 3, 4, 5), 10, origin);

        Assert.Equal(new short[] { 4, 5 }, ReadSamples(provider, 2));

        provider.Pause();
        provider.Resume();
        provider.AddSamples(ToBytes(6, 7), 4, origin + TimeSpan.FromSeconds(1));

        Assert.Equal(new short[] { 6, 7 }, ReadSamples(provider, 2));
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
        Assert.Equal(bytes.Length, provider.Read(bytes, 0, bytes.Length));
        var samples = new short[count];
        Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
        return samples;
    }

    private static TimeSpan SystemRelativeNow() =>
        Stopwatch.GetElapsedTime(0, Stopwatch.GetTimestamp());
}
