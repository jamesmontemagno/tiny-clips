using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Channels;
using TinyClips.Core.Capture;
using Windows.Media.Core;
using Windows.Storage.Streams;

namespace TinyClips.Core.Tests;

[CollectionDefinition("CPU recording buffers", DisableParallelization = true)]
public sealed class CpuRecordingBufferCollection
{
}

[Collection("CPU recording buffers")]
public sealed class CpuRecordingBufferTests
{
    [Fact]
    public void PrivateReadbackAndBorrowedProcessingReuseExactSizeBuffers()
    {
        var buffers = new CpuCaptureBuffers();
        var first = Update(buffers, 8, 6, 1);
        var borrowed = buffers.CopyLatest(borrow: true)!;
        var second = Update(buffers, 8, 6, 2);
        var next = buffers.CopyLatest(borrow: true)!;

        Assert.Same(first, second);
        Assert.Same(borrowed, next);
        Assert.NotSame(second.BgraPixels, next.BgraPixels);
        Assert.Equal(8 * 6 * 4, next.BgraPixels.Length);
        Assert.All(next.BgraPixels, pixel => Assert.Equal(2, pixel));
    }

    [Fact]
    public void ArrivedSnapshotsRemainValidAfterFurtherCaptureAndUnsubscribe()
    {
        var buffers = new CpuCaptureBuffers();
        var retained = Update(buffers, 4, 4, 1, publish: true);
        var nextRetained = Update(buffers, 4, 4, 2, publish: true);
        Update(buffers, 4, 4, 3, publish: false);
        Update(buffers, 4, 4, 4, publish: false);

        Assert.All(retained.BgraPixels, pixel => Assert.Equal(1, pixel));
        Assert.All(nextRetained.BgraPixels, pixel => Assert.Equal(2, pixel));
    }

    [Fact]
    public void ReadySnapshotsRemainValidAfterOverlaysAndChangingCapture()
    {
        var buffers = new CpuCaptureBuffers();
        Update(buffers, 4, 4, 1);
        var retained = buffers.CopyLatest(borrow: false)!;
        var borrowed = buffers.CopyLatest(borrow: true)!;
        Array.Fill(borrowed.BgraPixels, (byte)99);
        var repeated = buffers.CopyLatest(borrow: true)!;
        Assert.All(repeated.BgraPixels, pixel => Assert.Equal(1, pixel));

        Update(buffers, 4, 4, 2);
        buffers.CopyLatest(borrow: true);
        Assert.All(retained.BgraPixels, pixel => Assert.Equal(1, pixel));
    }

    [Fact]
    public void StaticSourceStillProducesFreshSamplesAndDoesNotAccumulateOverlays()
    {
        var buffers = new CpuCaptureBuffers();
        Update(buffers, 4, 4, 1);
        var samples = new List<MediaStreamSample>();
        for (var tick = 0; tick < 8; tick++)
        {
            var borrowed = buffers.CopyLatest(borrow: true)!;
            Assert.Equal(1, borrowed.BgraPixels[0]);
            borrowed.BgraPixels[0] = (byte)(tick + 2);
            var pts = TimeSpan.FromTicks(tick * 333_333L);
            samples.Add(MediaStreamSample.CreateFromBuffer(CpuVideoBuffer.Create(borrowed), pts));
        }

        Assert.Equal(8, samples.Count);
        for (var tick = 0; tick < samples.Count; tick++)
        {
            var pixels = samples[tick].Buffer.ToArray();
            Assert.Equal(tick + 2, pixels[3 * 4 * 4]);
            Assert.Equal(TimeSpan.FromTicks(tick * 333_333L), samples[tick].Timestamp);
        }
    }

    [Fact]
    public void ResizeUsesPhysicalPixelDimensionsAndDoesNotReuseWrongSizeBuffers()
    {
        var buffers = new CpuCaptureBuffers();
        Update(buffers, 8, 6, 1);
        var old = buffers.CopyLatest(borrow: true)!;
        Update(buffers, 4, 2, 2);
        var resized = buffers.CopyLatest(borrow: true)!;

        Assert.NotSame(old.BgraPixels, resized.BgraPixels);
        Assert.Equal(4, resized.Width);
        Assert.Equal(2, resized.Height);
        Assert.Equal(32, resized.BgraPixels.Length);
        Assert.All(old.BgraPixels, pixel => Assert.Equal(1, pixel));
    }

    [Fact]
    public void OwnedTranscoderBufferIsBottomUpAndIndependentOfBorrowedPixels()
    {
        var frame = CoordinateFrame(4, 3);
        var buffer = CpuVideoBuffer.Create(frame);
        var expected = BottomUpBytes(frame);
        Array.Fill(frame.BgraPixels, (byte)99);

        Assert.Equal((uint)expected.Length, buffer.Length);
        Assert.Equal(expected, buffer.ToArray());
    }

    [Fact]
    public void CroppedRegionPreservesCoordinatesAndBottomUpOrientation()
    {
        var cropped = CoordinateFrame(8, 6).Crop(new PixelRect(2, 1, 4, 2));
        var pixels = CpuVideoBuffer.Create(cropped).ToArray();
        Assert.Equal(4 * 2 * 4, pixels.Length);
        Assert.Equal(2, pixels[0]);
        Assert.Equal(2, pixels[1]);
        Assert.Equal(2, pixels[4 * 4]);
        Assert.Equal(1, pixels[(4 * 4) + 1]);
    }

    [Fact]
    public void SinkWriterCopiesDirectlyIntoIndependentNativeBottomUpMemory()
    {
        var frame = CoordinateFrame(4, 3);
        var expected = BottomUpBytes(frame);
        var destination = Marshal.AllocHGlobal(expected.Length);
        try
        {
            CpuVideoBuffer.CopyBottomUp(frame, destination, expected.Length);
            Array.Fill(frame.BgraPixels, (byte)99);
            var actual = new byte[expected.Length];
            Marshal.Copy(destination, actual, 0, actual.Length);
            Assert.Equal(expected, actual);
            Assert.Throws<ArgumentException>(() => CpuVideoBuffer.CopyBottomUp(frame, destination, expected.Length - 1));
        }
        finally
        {
            Marshal.FreeHGlobal(destination);
        }
    }

    [Fact]
    public void VideoBuffersContainOnlyPhysicalPixelsNotTrailingArrayCapacity()
    {
        var frame = new CapturedFrame(new byte[128], 4, 2);
        var buffer = CpuVideoBuffer.Create(frame);
        Assert.Equal(32u, buffer.Length);
        Assert.Throws<ArgumentException>(() => CpuVideoBuffer.Create(new CapturedFrame(new byte[31], 4, 2)));
    }

    [Fact]
    public void DroppedFramesCannotOverwriteQueuedOrEncoderRetainedSamples()
    {
        var buffers = new CpuCaptureBuffers();
        var dropped = 0;
        var channel = Channel.CreateBounded<(IBuffer Pixels, TimeSpan Pts)>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite },
            _ => dropped++);
        Update(buffers, 4, 4, 1);
        var retained = CpuVideoBuffer.Create(buffers.CopyLatest(borrow: true)!);
        var sample = MediaStreamSample.CreateFromBuffer(retained, TimeSpan.Zero);
        channel.Writer.TryWrite((retained, TimeSpan.Zero));

        for (var version = 2; version <= 8; version++)
        {
            Update(buffers, 4, 4, (byte)version);
            channel.Writer.TryWrite((CpuVideoBuffer.Create(buffers.CopyLatest(borrow: true)!), TimeSpan.FromTicks(version)));
        }

        Assert.Equal(7, dropped);
        Assert.True(channel.Reader.TryRead(out var queued));
        Assert.Equal(TimeSpan.Zero, queued.Pts);
        Assert.All(queued.Pixels.ToArray(), pixel => Assert.Equal(1, pixel));
        Assert.All(sample.Buffer.ToArray(), pixel => Assert.Equal(1, pixel));
        Assert.False(channel.Reader.TryRead(out _));
    }

    [Fact]
    public async Task CancellationAndQueueDrainDoNotInvalidateEncoderOwnedPixels()
    {
        var buffers = new CpuCaptureBuffers();
        Update(buffers, 4, 4, 1);
        var retained = CpuVideoBuffer.Create(buffers.CopyLatest(borrow: true)!);
        var channel = Channel.CreateBounded<IBuffer>(2);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await channel.Reader.ReadAsync(cancellation.Token));

        channel.Writer.TryWrite(retained);
        channel.Writer.TryComplete();
        while (channel.Reader.TryRead(out _))
        {
        }

        buffers.Clear();
        Update(buffers, 4, 4, 2);
        Assert.False(channel.Writer.TryWrite(CpuVideoBuffer.Create(buffers.CopyLatest(borrow: true)!)));
        Assert.All(retained.ToArray(), pixel => Assert.Equal(1, pixel));
        await channel.Reader.Completion;
    }

    [Fact]
    public void EncoderExceptionReleasesBorrowedProcessingOwnership()
    {
        var gate = new CpuFrameProcessingGate();
        gate.Start();
        var buffers = new CpuCaptureBuffers();
        Update(buffers, 4, 4, 1);
        IBuffer? retained = null;
        Assert.Throws<IOException>(() => gate.TryProcess(() =>
        {
            retained = CpuVideoBuffer.Create(buffers.CopyLatest(borrow: true)!);
            throw new IOException("Synthetic encoder failure");
        }));

        Update(buffers, 4, 4, 2);
        Assert.True(gate.TryProcess(() => CpuVideoBuffer.Create(buffers.CopyLatest(borrow: true)!)));
        Assert.All(retained!.ToArray(), pixel => Assert.Equal(1, pixel));
        gate.Stop();
    }

    [Fact]
    public async Task OverlappingTicksCannotMutateBorrowedPixelsAndStopWaitsForConsumer()
    {
        var gate = new CpuFrameProcessingGate();
        gate.Start();
        var buffers = new CpuCaptureBuffers();
        Update(buffers, 4, 4, 1);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var stopEntered = new ManualResetEventSlim();
        var processing = Task.Run(() => gate.TryProcess(() =>
        {
            var borrowed = buffers.CopyLatest(borrow: true)!;
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            Assert.All(borrowed.BgraPixels, pixel => Assert.Equal(1, pixel));
        }), TestContext.Current.CancellationToken);

        Assert.True(entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Task? stopping = null;
        try
        {
            Update(buffers, 4, 4, 2);
            Assert.False(gate.TryProcess(() => buffers.CopyLatest(borrow: true)));
            stopping = Task.Run(() =>
            {
                stopEntered.Set();
                gate.Stop();
            }, TestContext.Current.CancellationToken);
            Assert.True(stopEntered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.False(stopping.IsCompleted);
        }
        finally
        {
            release.Set();
            await processing.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            if (stopping is not null)
            {
                await stopping.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            }
        }

        Assert.False(gate.TryProcess(() => throw new InvalidOperationException("Stopped pump ran")));
    }

    [Fact]
    public void RepeatedStartStopDoesNotCarryStaleFramesIntoNextRecording()
    {
        var buffers = new CpuCaptureBuffers();
        var gate = new CpuFrameProcessingGate();
        var retained = new List<IBuffer>();
        for (var session = 1; session <= 20; session++)
        {
            Assert.Null(buffers.CopyLatest(borrow: true));
            gate.Start();
            Update(buffers, 4, 4, (byte)session);
            Assert.True(gate.TryProcess(() => retained.Add(CpuVideoBuffer.Create(buffers.CopyLatest(borrow: true)!))));
            gate.Stop();
            gate.Stop();
            buffers.Clear();
        }

        for (var session = 1; session <= retained.Count; session++)
        {
            Assert.All(retained[session - 1].ToArray(), pixel => Assert.Equal(session, pixel));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EqualSubmittedFrameCountAndDimensionsReduceManagedAllocations(bool sinkWriter)
    {
        const int width = 1280;
        const int height = 720;
        const int submittedFrames = 12;
        const int frameBytes = width * height * 4;
        var buffers = new CpuCaptureBuffers();
        Update(buffers, width, height, 1);
        var warmed = buffers.CopyLatest(borrow: true)!;
        var destination = Marshal.AllocHGlobal(frameBytes);
        try
        {
            CpuVideoBuffer.Create(warmed);
            CpuVideoBuffer.CopyBottomUp(warmed, destination, frameBytes);
            var before = GC.GetAllocatedBytesForCurrentThread();
            IBuffer? last = null;
            for (var submitted = 0; submitted < submittedFrames; submitted++)
            {
                Update(buffers, width, height, (byte)(submitted + 1));
                var borrowed = buffers.CopyLatest(borrow: true)!;
                if (sinkWriter)
                {
                    CpuVideoBuffer.CopyBottomUp(borrowed, destination, frameBytes);
                }
                else
                {
                    last = CpuVideoBuffer.Create(borrowed);
                }
            }

            var bytesPerSubmittedFrame = (GC.GetAllocatedBytesForCurrentThread() - before) / submittedFrames;
            var expectedPixelAllocation = sinkWriter ? 0 : frameBytes;
            Assert.True(bytesPerSubmittedFrame < expectedPixelAllocation + 16_384,
                $"Managed bytes per submitted {width}x{height} frame: {bytesPerSubmittedFrame}; previous three arrays alone: {3L * frameBytes}.");
            if (sinkWriter)
            {
                Assert.Equal(submittedFrames, Marshal.ReadByte(destination));
                Assert.Equal(submittedFrames, Marshal.ReadByte(destination, frameBytes - 1));
            }
            else
            {
                Assert.Equal((uint)frameBytes, last!.Length);
                Assert.Equal(-1, last.ToArray().AsSpan().IndexOfAnyExcept((byte)submittedFrames));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(destination);
        }
    }

    private static CapturedFrame Update(CpuCaptureBuffers buffers, int width, int height, byte version, bool publish = false)
    {
        var frame = buffers.GetReadbackFrame(width, height, publish);
        Array.Fill(frame.BgraPixels, version);
        return frame;
    }

    private static CapturedFrame CoordinateFrame(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
        {
            for (var column = 0; column < width; column++)
            {
                var offset = ((row * width) + column) * 4;
                pixels[offset] = (byte)column;
                pixels[offset + 1] = (byte)row;
                pixels[offset + 2] = 123;
                pixels[offset + 3] = 255;
            }
        }

        return new CapturedFrame(pixels, width, height);
    }

    private static byte[] BottomUpBytes(CapturedFrame frame)
    {
        var bytes = new byte[frame.Width * frame.Height * 4];
        var stride = frame.Width * 4;
        for (var row = 0; row < frame.Height; row++)
        {
            System.Buffer.BlockCopy(frame.BgraPixels, (frame.Height - 1 - row) * stride, bytes, row * stride, stride);
        }

        return bytes;
    }
}
