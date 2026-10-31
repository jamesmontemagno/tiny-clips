namespace TinyClips.App.Tests;

public sealed class VideoFrameCopyOperationTests
{
    [Fact]
    public async Task SnapshotsPosition_AndRejectsConcurrentInvocation()
    {
        var probe = new CopyProbe { CaptureGate = new() };
        var copy = probe.CopyAsync();
        Assert.True(probe.Operation.IsBusy);
        probe.Position = TimeSpan.FromSeconds(9);

        Assert.False(await probe.CopyAsync());
        Assert.Equal([TimeSpan.FromSeconds(2)], probe.CapturedPositions);
        Assert.Equal(0, probe.Encodes);
        Assert.Equal(0, probe.Publications);

        probe.CaptureGate.SetResult(probe.Frame);
        Assert.True(await copy);
        Assert.Equal(1, probe.Encodes);
        Assert.Equal(1, probe.Publications);
        AssertDisposedAndIdle(probe);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloseOrDeleteDuringCapture_RejectsLateFrame_WithoutEncoding(bool close)
    {
        var probe = new CopyProbe { CaptureGate = new() };
        var copy = probe.CopyAsync();
        var idle = probe.Operation.WhenIdle;
        if (close) probe.Operation.Close();
        else probe.Operation.Cancel();

        Assert.False(idle.IsCompleted);
        Assert.False(await probe.CopyAsync());
        Assert.False(probe.Frame.Disposed);
        probe.CaptureGate.SetResult(probe.Frame);

        Assert.False(await copy);
        await idle;
        Assert.True(probe.Frame.Disposed);
        Assert.Equal(0, probe.Encodes);
        Assert.Equal(0, probe.Publications);
        Assert.False(probe.Operation.IsBusy);
        if (close)
        {
            Assert.False(await probe.CopyAsync());
            Assert.Single(probe.CapturedPositions);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloseOrDeleteDuringEncoding_RejectsLateImage_AndWaitsToDispose(bool close)
    {
        var probe = new CopyProbe { EncodeGate = new() };
        var copy = probe.CopyAsync();
        if (close) probe.Operation.Close();
        else probe.Operation.Cancel();

        Assert.True(probe.EncodingToken.IsCancellationRequested);
        Assert.False(probe.Frame.Disposed);
        Assert.False(probe.Image.Disposed);
        Assert.False(probe.Operation.WhenIdle.IsCompleted);
        // The native adapter may finish successfully despite cancellation.
        probe.EncodeGate.SetResult(probe.Image);

        Assert.False(await copy);
        Assert.Equal(0, probe.Publications);
        AssertDisposedAndIdle(probe);
    }

    [Fact]
    public async Task CanceledDelete_AllowsFreshCopyAfterOldOperationSettles()
    {
        var operation = new VideoFrameCopyOperation();
        var first = new CopyProbe(operation) { EncodeGate = new() };
        var stale = first.CopyAsync();
        operation.Cancel();
        first.EncodeGate.SetResult(first.Image);
        Assert.False(await stale);

        var next = new CopyProbe(operation) { Position = TimeSpan.FromSeconds(5) };
        Assert.True(await next.CopyAsync());
        Assert.False(next.EncodingToken.IsCancellationRequested);
        Assert.Equal([TimeSpan.FromSeconds(5)], next.CapturedPositions);
        Assert.Equal(0, first.Publications);
        Assert.Equal(1, next.Publications);
        AssertDisposedAndIdle(first);
        AssertDisposedAndIdle(next);
    }

    [Theory]
    [InlineData("capture")]
    [InlineData("encode")]
    [InlineData("publish")]
    public async Task FailuresPropagateForNotification_ReleaseAcquiredResources_AndAllowRetry(string stage)
    {
        var probe = new CopyProbe { FailAt = stage };
        var failure = await Assert.ThrowsAsync<IOException>(() => probe.CopyAsync());
        Assert.Same(probe.Failure, failure);
        Assert.False(probe.Operation.IsBusy);
        Assert.True(probe.Operation.WhenIdle.IsCompleted);
        Assert.Equal(stage != "capture", probe.Frame.Disposed);
        Assert.Equal(stage == "publish", probe.Image.Disposed);
        Assert.Equal(0, probe.Publications);

        var next = new CopyProbe(probe.Operation);
        Assert.True(await next.CopyAsync());
        AssertDisposedAndIdle(next);
    }

    [Fact]
    public async Task UnrequestedCancellationIsAnError_NotSuccessfulOrSilentlyCanceled()
    {
        var operation = new VideoFrameCopyOperation();
        using var frame = new Resource();
        await Assert.ThrowsAsync<OperationCanceledException>(() => operation.CopyAsync<Resource, Resource>(
            TimeSpan.Zero,
            _ => Task.FromResult(frame),
            (_, _) => throw new OperationCanceledException(),
            _ => Assert.Fail("Must not publish.")));
        Assert.True(frame.Disposed);
        Assert.False(operation.IsBusy);
    }

    [Fact]
    public async Task SuccessfulCopyPublishesBeforeDisposal_WithoutMutatingFrame()
    {
        var probe = new CopyProbe();
        Assert.True(await probe.CopyAsync());
        Assert.Equal([3, 1, 4], probe.Frame.Pixels);
        Assert.NotNull(probe.PublishedPixels);
        Assert.Equal([3, 1, 4], probe.PublishedPixels);
        Assert.Equal(1, probe.Publications);
        AssertDisposedAndIdle(probe);
    }

    [Fact]
    public async Task ClosedOperationDoesNoWork()
    {
        var probe = new CopyProbe();
        probe.Operation.Close();
        Assert.False(await probe.CopyAsync());
        Assert.Empty(probe.CapturedPositions);
        Assert.Equal(0, probe.Encodes);
        Assert.Equal(0, probe.Publications);
    }

    private static void AssertDisposedAndIdle(CopyProbe probe)
    {
        Assert.True(probe.Frame.Disposed);
        Assert.True(probe.Image.Disposed);
        Assert.False(probe.Operation.IsBusy);
        Assert.True(probe.Operation.WhenIdle.IsCompletedSuccessfully);
    }

    private sealed class Resource : IDisposable
    {
        public bool Disposed { get; private set; }
        public int[] Pixels { get; } = [3, 1, 4];
        public void Dispose() => Disposed = true;
    }

    private sealed class CopyProbe(VideoFrameCopyOperation? operation = null)
    {
        public VideoFrameCopyOperation Operation { get; } = operation ?? new();
        public TimeSpan Position { get; set; } = TimeSpan.FromSeconds(2);
        public Resource Frame { get; } = new();
        public Resource Image { get; } = new();
        public TaskCompletionSource<Resource>? CaptureGate { get; set; }
        public TaskCompletionSource<Resource>? EncodeGate { get; set; }
        public List<TimeSpan> CapturedPositions { get; } = [];
        public CancellationToken EncodingToken { get; private set; }
        public int Encodes { get; private set; }
        public int Publications { get; private set; }
        public int[]? PublishedPixels { get; private set; }
        public string? FailAt { get; init; }
        public IOException Failure { get; } = new("Frame copy failed.");

        public Task<bool> CopyAsync() => Operation.CopyAsync(
            Position,
            position =>
            {
                CapturedPositions.Add(position);
                if (FailAt == "capture") throw Failure;
                return CaptureGate?.Task ?? Task.FromResult(Frame);
            },
            (frame, token) =>
            {
                Assert.Same(Frame, frame);
                Assert.False(frame.Disposed);
                Encodes++;
                EncodingToken = token;
                if (FailAt == "encode") throw Failure;
                return EncodeGate?.Task ?? Task.FromResult(Image);
            },
            image =>
            {
                Assert.False(Frame.Disposed);
                Assert.False(image.Disposed);
                if (FailAt == "publish") throw Failure;
                PublishedPixels = image.Pixels.ToArray();
                Publications++;
            });
    }
}
