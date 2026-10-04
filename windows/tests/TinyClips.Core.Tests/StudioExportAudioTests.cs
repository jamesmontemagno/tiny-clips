using System.Runtime.InteropServices;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Rendering;

namespace TinyClips.Core.Tests;

/// <summary>Which samples of the screen recording's sound an export keeps, and that it keeps exactly those.</summary>
public sealed class StudioExportAudioTests
{
    // Ranges

    [Fact]
    public void BuildAudioRanges_ComputesSampleAccurateTrimAndCuts()
    {
        var project = Project() with
        {
            Edits = new StudioEdits
            {
                TrimStart = 1.25,
                TrimEnd = 4.75,
                Cuts = [new StudioTimeRange { Start = 2, End = 3 }],
            },
        };

        var ranges = StudioRenderingMath.BuildAudioRanges(project, 48000);

        Assert.Equal(2, ranges.Count);
        Assert.Equal(new StudioAudioSampleRange(60_000, 96_000, 0), ranges[0]);
        Assert.Equal(new StudioAudioSampleRange(144_000, 228_000, 36_000), ranges[1]);
    }

    [Fact]
    public void BuildAudioRanges_TrimPointsBetweenSamplesGoToTheNearestSample()
    {
        // 0.525 s is sample 25200 exactly; 5.14 s is 246720; at 44.1 kHz they are 23152.5 and 226674.
        var project = Project() with { Edits = new StudioEdits { TrimStart = 0.525, TrimEnd = 5.14 } };

        Assert.Equal([new StudioAudioSampleRange(25_200, 246_720, 0)], StudioRenderingMath.BuildAudioRanges(project, 48000));
        Assert.Equal([new StudioAudioSampleRange(23_153, 226_674, 0)], StudioRenderingMath.BuildAudioRanges(project, 44100));
    }

    [Fact]
    public void BuildAudioRanges_WithoutEditsIsTheWholeTrack()
    {
        Assert.Equal([new StudioAudioSampleRange(0, 480_000, 0)], StudioRenderingMath.BuildAudioRanges(Project(), 48000));
    }

    [Fact]
    public void BuildAudioRanges_ReturnsEmptyWhenMuted()
    {
        Assert.Empty(StudioRenderingMath.BuildAudioRanges(Project() with { Audio = new StudioAudio { Muted = true } }, 48000));
    }

    [Fact]
    public void BuildAudioRanges_RejectsAnImpossibleSampleRate()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StudioRenderingMath.BuildAudioRanges(Project(), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => StudioRenderingMath.BuildAudioPlan(Project(), 0));
    }

    // Speed: sound only where the video plays at the recording's own speed

    [Fact]
    public void BuildAudioPlan_WithoutSpeed_IsTheRangesAndWhereTheLastOneEnds()
    {
        var project = Project() with
        {
            Edits = new StudioEdits { TrimStart = 1.25, TrimEnd = 4.75, Cuts = [new StudioTimeRange { Start = 2, End = 3 }] },
        };

        var plan = StudioRenderingMath.BuildAudioPlan(project, 48000);

        Assert.Equal([new StudioAudioSampleRange(60_000, 96_000, 0), new StudioAudioSampleRange(144_000, 228_000, 36_000)], plan.Ranges);
        Assert.Equal(120_000, plan.TotalSamples);
        Assert.Equal(new StudioAudioPlan([], 0).TotalSamples, StudioRenderingMath.BuildAudioPlan(Project() with { Audio = new StudioAudio { Muted = true } }, 48000).TotalSamples);
        Assert.Empty(StudioRenderingMath.BuildAudioPlan(Project() with { Audio = new StudioAudio { Muted = true } }, 48000).Ranges);
    }

    [Fact]
    public void BuildAudioPlan_Speed_KeepsTheSoundOfWhatPlaysAtItsOwnSpeed_AndLeavesRoomForTheRest()
    {
        // Ten seconds: twice as fast from 2 to 4, which takes one second of video, half as fast
        // from 6 to 7, which takes two, and a cut from 8 to 9.
        var project = Project() with
        {
            Edits = new StudioEdits
            {
                Cuts = [new StudioTimeRange { Start = 8, End = 9 }],
                Speed = [new StudioSpeedRange { Start = 2, End = 4, Rate = 2 }, new StudioSpeedRange { Start = 6, End = 7, Rate = 0.5 }],
            },
        };

        var plan = StudioRenderingMath.BuildAudioPlan(project, 48000);

        // The video: 0 to 2 with sound, 2 to 3 silent, 3 to 5 with the sound of 4 to 6, 5 to 7
        // silent, 7 to 8 with the sound of 7 to 8, and 8 to 9 with the sound of 9 to 10.
        Assert.Equal(
            [
                new StudioAudioSampleRange(0, 96_000, 0),
                new StudioAudioSampleRange(192_000, 288_000, 144_000),
                new StudioAudioSampleRange(336_000, 384_000, 336_000),
                new StudioAudioSampleRange(432_000, 480_000, 384_000),
            ],
            plan.Ranges);
        Assert.Equal(432_000, plan.TotalSamples);
        Assert.Equal(plan.Ranges, StudioRenderingMath.BuildAudioRanges(project, 48000));
    }

    [Fact]
    public void BuildAudioPlan_WhenTheVideoEndsFaster_TheTrackLastsAsLongAsTheVideo()
    {
        // Six seconds as they are, then four at four times the speed: one more second, silent.
        var project = Project() with { Edits = new StudioEdits { Speed = [new StudioSpeedRange { Start = 6, End = 10, Rate = 4 }] } };

        var plan = StudioRenderingMath.BuildAudioPlan(project, 48000);

        Assert.Equal([new StudioAudioSampleRange(0, 288_000, 0)], plan.Ranges);
        Assert.Equal(336_000, plan.TotalSamples);
    }

    [Fact]
    public void BuildAudioPlan_AllAtAnotherSpeed_HasNoRangesAndTheLengthOfTheVideo()
    {
        var project = Project() with { Edits = new StudioEdits { Speed = [new StudioSpeedRange { Start = 0, End = 10, Rate = 8 }] } };

        var plan = StudioRenderingMath.BuildAudioPlan(project, 48000);

        Assert.Empty(plan.Ranges);
        Assert.Equal(60_000, plan.TotalSamples);
    }

    // How many samples go next to the picture

    [Theory]
    [InlineData(288_000, 180, 30, 1, 48000, 287_744)]
    [InlineData(221_520, 138, 30, 1, 48000, 220_800)]
    [InlineData(192_192, 120, 30000, 1001, 48000, 192_192)]
    [InlineData(4_800, 3, 30, 1, 48000, 4_800)]
    [InlineData(100_000, 180, 30, 1, 48000, 100_000)]
    [InlineData(132_300, 90, 30, 1, 44100, 132_096)]
    [InlineData(48_000, 1, 60, 1, 48000, 800)]
    public void AudioSampleCount_EndsTheTrackWithinHalfAnAacFrameOfThePicture(long kept, long frames, int numerator, int denominator, int sampleRate, long expected)
    {
        Assert.Equal(expected, StudioRenderingMath.AudioSampleCount(kept, frames, new StudioFrameRate(numerator, denominator), sampleRate));
    }

    [Theory]
    [InlineData(0, 180)]
    [InlineData(-5, 180)]
    [InlineData(48_000, 0)]
    public void AudioSampleCount_IsZeroWhenThereIsNothingToWrite(long kept, long frames)
    {
        Assert.Equal(0, StudioRenderingMath.AudioSampleCount(kept, frames, new StudioFrameRate(30, 1), 48000));
    }

    [Fact]
    public void AudioSampleCount_ThePaddedTrackNeverEndsMoreThanHalfAnAacFrameFromThePicture()
    {
        foreach (var rate in new[] { new StudioFrameRate(30, 1), new StudioFrameRate(60, 1), new StudioFrameRate(30000, 1001), new StudioFrameRate(24000, 1001), new StudioFrameRate(25, 2) })
        {
            foreach (var sampleRate in new[] { 44100, 48000 })
            {
                for (var frames = 1; frames <= 2500; frames++)
                {
                    var videoEnd = (long)Math.Round(frames * (double)rate.Denominator * sampleRate / rate.Numerator, MidpointRounding.AwayFromZero);
                    var count = StudioRenderingMath.AudioSampleCount(long.MaxValue, frames, rate, sampleRate);

                    // An AAC track is padded up to a whole number of 1024-sample frames.
                    var padded = (count + 1023) / 1024 * 1024;
                    Assert.True(count <= videoEnd + 1, $"{frames} frames at {rate}: {count} samples for a picture of {videoEnd}");
                    Assert.True(Math.Abs(padded - videoEnd) <= 513, $"{frames} frames at {rate}, {sampleRate} Hz: the track ends {padded - videoEnd} samples from the picture");
                }
            }
        }
    }

    [Fact]
    public void LimitAudioRanges_CutsTheRangesOffAfterTheCount()
    {
        StudioAudioSampleRange[] ranges = [new(60_000, 96_000, 0), new(144_000, 228_000, 36_000)];

        Assert.Equal(ranges, StudioRenderingMath.LimitAudioRanges(ranges, 120_000));
        Assert.Equal(ranges, StudioRenderingMath.LimitAudioRanges(ranges, 500_000));
        Assert.Equal([ranges[0], new StudioAudioSampleRange(144_000, 148_000, 36_000)], StudioRenderingMath.LimitAudioRanges(ranges, 40_000));
        Assert.Equal([ranges[0]], StudioRenderingMath.LimitAudioRanges(ranges, 36_000));
        Assert.Equal([new StudioAudioSampleRange(60_000, 60_010, 0)], StudioRenderingMath.LimitAudioRanges(ranges, 10));
        Assert.Empty(StudioRenderingMath.LimitAudioRanges(ranges, 0));
    }

    [Fact]
    public void AudioFormat_BlockAlignAndAacBitrate()
    {
        Assert.Equal(4, new StudioAudioFormat(48000, 2).BlockAlign);
        Assert.Equal(2, new StudioAudioFormat(44100, 1).BlockAlign);
        Assert.Equal(192_000u, new StudioAudioFormat(48000, 2).AacBitrate);
        Assert.Equal(96_000u, new StudioAudioFormat(44100, 1).AacBitrate);
    }

    // The pump: decoded blocks in, exactly the kept samples out

    [Fact]
    public void Pump_WholeTrack_CopiesEverySampleWithoutSeeking()
    {
        var source = new FakeSource(total: 10_000, blockSamples: 1024);
        var sink = new Sink();
        var pump = new StudioAudioPump(source, [new StudioAudioSampleRange(0, 10_000, 0)], 2, sink.Write);

        pump.PumpToEnd(CancellationToken.None);

        Assert.True(pump.Done);
        Assert.Equal(10_000, pump.TotalSamples);
        Assert.Equal(10_000, pump.OutputSamples);
        sink.AssertIs(Enumerable.Range(0, 10_000));
        Assert.Empty(source.Seeks);
    }

    [Fact]
    public void Pump_Trimmed_KeepsExactlyTheRangeAndDropsRunInAndTail()
    {
        var source = new FakeSource(total: 60_000, blockSamples: 1024, runIn: 4096);
        var sink = new Sink();
        var pump = new StudioAudioPump(source, [new StudioAudioSampleRange(12_345, 54_321, 0)], 2, sink.Write);

        pump.PumpToEnd(CancellationToken.None);

        sink.AssertIs(Enumerable.Range(12_345, 54_321 - 12_345));
        Assert.Equal([12_345L], source.Seeks);
        Assert.Equal(54_321 - 12_345, pump.OutputSamples);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(37)]
    [InlineData(800)]
    [InlineData(1024)]
    [InlineData(1600)]
    [InlineData(5000)]
    public void Pump_FedInSmallSteps_NeverDropsTheRestOfABlock(int step)
    {
        // The inherited exporter threw away what was left of a decoded block once a step had
        // enough; every sample of a block has to come out, whatever the step.
        var source = new FakeSource(total: 30_000, blockSamples: 1024, runIn: 4096);
        var sink = new Sink();
        var pump = new StudioAudioPump(source, [new StudioAudioSampleRange(2_000, 27_777, 0)], 2, sink.Write);

        for (long target = step; !pump.Done; target += step)
        {
            pump.PumpTo(target, CancellationToken.None);
            Assert.True(pump.OutputSamples >= Math.Min(target, pump.TotalSamples));

            // Never more than one block ahead of what was asked for.
            Assert.True(pump.OutputSamples <= target + 1024);
        }

        sink.AssertIs(Enumerable.Range(2_000, 25_777));
    }

    [Fact]
    public void Pump_WithACut_JoinsTheRangesAndSeeksToTheSecond()
    {
        var source = new FakeSource(total: 40_000, blockSamples: 1000, runIn: 4096);
        var sink = new Sink();
        StudioAudioSampleRange[] ranges = [new(1_000, 5_500, 0), new(20_000, 26_250, 4_500)];
        var pump = new StudioAudioPump(source, ranges, 2, sink.Write);

        pump.PumpToEnd(CancellationToken.None);

        sink.AssertIs(Enumerable.Range(1_000, 4_500).Concat(Enumerable.Range(20_000, 6_250)));
        Assert.Equal([1_000L, 20_000L], source.Seeks);
        Assert.Equal(10_750, pump.TotalSamples);
    }

    [Fact]
    public void Pump_WhenTheSoundEndsEarly_WritesSilenceToTheEndOfTheRange()
    {
        var source = new FakeSource(total: 7_000, blockSamples: 1024);
        var sink = new Sink();
        var pump = new StudioAudioPump(source, [new StudioAudioSampleRange(0, 10_000, 0)], 2, sink.Write);

        pump.PumpToEnd(CancellationToken.None);

        sink.AssertIs(Enumerable.Range(0, 7_000).Concat(Enumerable.Repeat(FakeSource.Silence, 3_000)));
    }

    [Fact]
    public void Pump_WhenTheSoundStartsLateOrHasAHole_WritesSilenceForWhatIsMissing()
    {
        // Sound from sample 2500 only, and nothing between 6000 and 7500.
        var source = new FakeSource(total: 12_000, blockSamples: 1000, firstSample: 2_500, hole: (6_000, 7_500));
        var sink = new Sink();
        var pump = new StudioAudioPump(source, [new StudioAudioSampleRange(1_000, 11_000, 0)], 2, sink.Write);

        pump.PumpToEnd(CancellationToken.None);

        sink.AssertIs(
            Enumerable.Repeat(FakeSource.Silence, 1_500)
                .Concat(Enumerable.Range(2_500, 3_500))
                .Concat(Enumerable.Repeat(FakeSource.Silence, 1_500))
                .Concat(Enumerable.Range(7_500, 3_500)));
    }

    [Fact]
    public void Pump_WhereARangeBeginsLaterInTheOutput_WritesSilenceUpToIt()
    {
        var source = new FakeSource(total: 40_000, blockSamples: 1000, runIn: 4096);
        var sink = new Sink();

        // 2,000 samples, 9,000 of nothing, which is more than two blocks of silence, then 3,000 more.
        StudioAudioSampleRange[] ranges = [new(1_000, 3_000, 0), new(20_000, 23_000, 11_000)];
        var pump = new StudioAudioPump(source, ranges, 2, sink.Write);

        pump.PumpToEnd(CancellationToken.None);

        sink.AssertIs(Enumerable.Range(1_000, 2_000).Concat(Enumerable.Repeat(FakeSource.Silence, 9_000)).Concat(Enumerable.Range(20_000, 3_000)));
        Assert.Equal(14_000, pump.TotalSamples);
        Assert.Equal(14_000, pump.OutputSamples);
        Assert.True(pump.Done);
        Assert.Equal([1_000L, 20_000L], source.Seeks);
    }

    [Fact]
    public void Pump_ARangeThatDoesNotBeginTheTrack_HasSilenceBeforeIt()
    {
        var source = new FakeSource(total: 40_000, blockSamples: 1000);
        var sink = new Sink();
        var pump = new StudioAudioPump(source, [new StudioAudioSampleRange(5_000, 6_000, 3_000)], 2, sink.Write);

        pump.PumpToEnd(CancellationToken.None);

        sink.AssertIs(Enumerable.Repeat(FakeSource.Silence, 3_000).Concat(Enumerable.Range(5_000, 1_000)));
        Assert.Equal(4_000, pump.TotalSamples);
    }

    [Fact]
    public void Pump_WithALengthPastItsLastRange_WritesSilenceToTheEnd()
    {
        var source = new FakeSource(total: 40_000, blockSamples: 1000);
        var sink = new Sink();
        var pump = new StudioAudioPump(source, [new StudioAudioSampleRange(100, 2_100, 0)], 2, sink.Write, totalSamples: 7_500);

        pump.PumpToEnd(CancellationToken.None);

        sink.AssertIs(Enumerable.Range(100, 2_000).Concat(Enumerable.Repeat(FakeSource.Silence, 5_500)));
        Assert.Equal(7_500, pump.TotalSamples);
        Assert.Equal(7_500, pump.OutputSamples);
        Assert.True(pump.Done);
    }

    [Fact]
    public void Pump_WithNoRangesAndALength_WritesThatMuchSilence_AndReadsNothing()
    {
        var source = new FakeSource(total: 40_000, blockSamples: 1000);
        var sink = new Sink();
        var pump = new StudioAudioPump(source, [], 2, sink.Write, totalSamples: 5_000);
        Assert.False(pump.Done);

        pump.PumpToEnd(CancellationToken.None);

        sink.AssertIs(Enumerable.Repeat(FakeSource.Silence, 5_000));
        Assert.True(pump.Done);
        Assert.Equal(0, source.Reads);
        Assert.Empty(source.Seeks);
    }

    [Fact]
    public void Pump_ALengthShorterThanTheRanges_ChangesNothing()
    {
        var source = new FakeSource(total: 40_000, blockSamples: 1000);
        var sink = new Sink();
        var pump = new StudioAudioPump(source, [new StudioAudioSampleRange(100, 2_100, 0)], 2, sink.Write, totalSamples: 500);

        pump.PumpToEnd(CancellationToken.None);

        sink.AssertIs(Enumerable.Range(100, 2_000));
        Assert.Equal(2_000, pump.TotalSamples);
    }

    [Fact]
    public void PumpTo_StopsInsideTheSilence_AndGoesOnFromThere()
    {
        var source = new FakeSource(total: 40_000, blockSamples: 1000);
        var sink = new Sink();
        StudioAudioSampleRange[] ranges = [new(100, 1_100, 0), new(10_000, 11_000, 20_000)];
        var pump = new StudioAudioPump(source, ranges, 2, sink.Write);

        pump.PumpTo(5_000, CancellationToken.None);

        // Never less than was asked for, and no more than one block of silence past it.
        Assert.InRange(pump.OutputSamples, 5_000, 5_000 + 4_096);
        Assert.False(pump.Done);
        Assert.Equal(sink.Count, pump.OutputSamples);

        pump.PumpToEnd(CancellationToken.None);

        sink.AssertIs(Enumerable.Range(100, 1_000).Concat(Enumerable.Repeat(FakeSource.Silence, 19_000)).Concat(Enumerable.Range(10_000, 1_000)));
        Assert.Equal(21_000, pump.TotalSamples);
        Assert.True(pump.Done);
    }

    [Fact]
    public void Pump_WhenTheSecondRangeLiesPastTheEndOfTheSound_WritesSilenceForIt()
    {
        var source = new FakeSource(total: 5_000, blockSamples: 1000);
        var sink = new Sink();
        StudioAudioSampleRange[] ranges = [new(0, 2_000, 0), new(8_000, 9_000, 2_000)];
        var pump = new StudioAudioPump(source, ranges, 2, sink.Write);

        pump.PumpToEnd(CancellationToken.None);

        sink.AssertIs(Enumerable.Range(0, 2_000).Concat(Enumerable.Repeat(FakeSource.Silence, 1_000)));
    }

    [Fact]
    public void Pump_ABlockDeliveredTwice_IsUsedOnce()
    {
        var source = new FakeSource(total: 6_000, blockSamples: 1000, repeatBlockAt: 3_000);
        var sink = new Sink();
        var pump = new StudioAudioPump(source, [new StudioAudioSampleRange(500, 5_500, 0)], 2, sink.Write);

        pump.PumpToEnd(CancellationToken.None);

        sink.AssertIs(Enumerable.Range(500, 5_000));
    }

    [Fact]
    public void Pump_Stereo_KeepsBothChannelsTogether()
    {
        var source = new FakeSource(total: 9_000, blockSamples: 1024, runIn: 4096, channels: 2);
        var sink = new Sink(channels: 2);
        var pump = new StudioAudioPump(source, [new StudioAudioSampleRange(3_333, 8_001, 0)], 4, sink.Write);

        for (long target = 480; !pump.Done; target += 480)
        {
            pump.PumpTo(target, CancellationToken.None);
        }

        sink.AssertIs(Enumerable.Range(3_333, 8_001 - 3_333));
    }

    [Fact]
    public void Pump_WithNoRanges_IsDoneAtOnce()
    {
        var source = new FakeSource(total: 1_000, blockSamples: 100);
        var sink = new Sink();
        var pump = new StudioAudioPump(source, [], 2, sink.Write);

        Assert.True(pump.Done);
        pump.PumpToEnd(CancellationToken.None);
        Assert.Equal(0, pump.TotalSamples);
        Assert.Equal(0, sink.Count);
        Assert.Equal(0, source.Reads);
    }

    [Fact]
    public void Pump_StopsWhenCancelled()
    {
        var source = new FakeSource(total: 100_000, blockSamples: 1024);
        var sink = new Sink();
        var pump = new StudioAudioPump(source, [new StudioAudioSampleRange(0, 100_000, 0)], 2, sink.Write);
        using var cancel = new CancellationTokenSource();
        pump.PumpTo(5_000, cancel.Token);
        cancel.Cancel();

        Assert.Throws<OperationCanceledException>(() => pump.PumpToEnd(cancel.Token));
        Assert.True(sink.Count < 10_000);
    }

    private static StudioProject Project() =>
        new()
        {
            Id = "p",
            Sources = new StudioSources { Screen = new StudioScreenSource { Width = 1920, Height = 1080, Duration = 10, FrameRate = 30 } },
        };

    /// <summary>What the pump wrote: each sample's left value is the number of the source sample it came from.</summary>
    private sealed class Sink(int channels = 1)
    {
        private readonly List<int> _samples = [];

        public int Count => _samples.Count;

        public void Write(ReadOnlySpan<byte> pcm, long outputStartSample)
        {
            // The position handed to the encoder is the running total: no gap and no overlap.
            Assert.Equal(_samples.Count, outputStartSample);
            var values = MemoryMarshal.Cast<byte, short>(pcm);
            Assert.Equal(0, values.Length % channels);
            for (var index = 0; index < values.Length; index += channels)
            {
                _samples.Add((ushort)values[index]);
                if (channels == 2)
                {
                    // The right channel carries the same number, inverted.
                    Assert.Equal((short)~values[index], values[index + 1]);
                }
            }
        }

        public void AssertIs(IEnumerable<int> expected)
        {
            var want = expected.ToArray();
            Assert.Equal(want.Length, _samples.Count);
            for (var index = 0; index < want.Length; index++)
            {
                if (want[index] != _samples[index])
                {
                    Assert.Fail($"Output sample {index} is source sample {_samples[index]}, want {want[index]}.");
                }
            }
        }
    }

    /// <summary>
    /// A decoder whose sample at position n has the value n, handed out in blocks as a source
    /// reader does: a seek lands on a block boundary a run-in before the target.
    /// </summary>
    private sealed class FakeSource(
        int total,
        int blockSamples,
        int runIn = 0,
        int channels = 1,
        int firstSample = 0,
        (int From, int To)? hole = null,
        int repeatBlockAt = -1) : IStudioPcmSource
    {
        /// <summary>Position 0 is the only sample whose value is 0, so silence is told apart by its position.</summary>
        public const int Silence = 0;

        private long _cursor = firstSample;
        private bool _repeated;
        private byte[] _block = [];

        public List<long> Seeks { get; } = [];

        public int Reads { get; private set; }

        public void Seek(long sample)
        {
            Seeks.Add(sample);
            var target = Math.Max(firstSample, sample - runIn);
            _cursor = firstSample + ((target - firstSample) / blockSamples * blockSamples);
        }

        public bool TryRead(out long startSample, out ReadOnlySpan<byte> pcm)
        {
            Reads++;
            if (hole is { } gap && _cursor >= gap.From && _cursor < gap.To)
            {
                _cursor = gap.To;
            }

            if (_cursor >= total)
            {
                startSample = 0;
                pcm = default;
                return false;
            }

            var start = _cursor;
            var end = Math.Min(total, start + blockSamples);
            if (hole is { } ahead && start < ahead.From && end > ahead.From)
            {
                end = ahead.From;
            }

            var values = new short[(end - start) * channels];
            for (var index = 0; index < end - start; index++)
            {
                values[index * channels] = (short)(ushort)(start + index);
                if (channels == 2)
                {
                    values[(index * channels) + 1] = (short)~values[index * channels];
                }
            }

            _block = MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
            startSample = start;
            pcm = _block;
            if (repeatBlockAt >= start && repeatBlockAt < end && !_repeated)
            {
                // Hand the same block out again on the next read.
                _repeated = true;
            }
            else
            {
                _cursor = end;
            }

            return true;
        }
    }
}
