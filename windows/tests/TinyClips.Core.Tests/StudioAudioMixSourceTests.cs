using System.Runtime.InteropServices;
using TinyClips.Core.Studio.Rendering;

namespace TinyClips.Core.Tests;

public sealed class StudioAudioMixSourceTests
{
    [Fact]
    public void MixesEveryTrackAtItsTimestampAndClampsClipping()
    {
        using var mixer = new StudioAudioMixSource(
            [new Source((0, [100, 200, 30000, 400])), new Source((2, [10000, -100, 500]))],
            new StudioAudioFormat(48000, 1), 6);
        Assert.True(mixer.TryRead(out var start, out var pcm));
        Assert.Equal(0, start);
        Assert.Equal<short>([100, 200, short.MaxValue, 300, 500, 0], MemoryMarshal.Cast<byte, short>(pcm).ToArray());
        Assert.False(mixer.TryRead(out _, out _));
    }

    [Fact]
    public void KeepsFutureBlocksAcrossReadsAndResetsAllTracksOnSeek()
    {
        using var mixer = new StudioAudioMixSource(
            [new Source((4095, [10, 20, 30])), new Source((4096, [100, 200]))],
            new StudioAudioFormat(48000, 1), 4098);
        Assert.True(mixer.TryRead(out _, out var first));
        Assert.Equal(10, MemoryMarshal.Cast<byte, short>(first)[4095]);
        Assert.True(mixer.TryRead(out var next, out var second));
        Assert.Equal(4096, next);
        Assert.Equal<short>([120, 230], MemoryMarshal.Cast<byte, short>(second).ToArray());

        mixer.Seek(4096);
        Assert.True(mixer.TryRead(out next, out second));
        Assert.Equal(4096, next);
        Assert.Equal<short>([120, 230], MemoryMarshal.Cast<byte, short>(second).ToArray());
    }

    [Fact]
    public void MixesStereoChannelsIndependentlyAcrossGaps()
    {
        using var mixer = new StudioAudioMixSource(
            [new Source((0, [10, 20]), (2, [30, 40])), new Source((1, [100, 200, 300, 400]))],
            new StudioAudioFormat(48000, 2), 3);
        Assert.True(mixer.TryRead(out _, out var pcm));
        Assert.Equal<short>([10, 20, 100, 200, 330, 440], MemoryMarshal.Cast<byte, short>(pcm).ToArray());
    }

    private sealed class Source(params (long Start, short[] Values)[] blocks) : IStudioPcmSource
    {
        private int _index;
        public void Seek(long sample) => _index = 0;
        public bool TryRead(out long startSample, out ReadOnlySpan<byte> pcm)
        {
            if (_index == blocks.Length)
            {
                startSample = 0;
                pcm = default;
                return false;
            }
            var block = blocks[_index++];
            startSample = block.Start;
            pcm = MemoryMarshal.AsBytes(block.Values.AsSpan());
            return true;
        }
    }
}
