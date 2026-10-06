using TinyClips.Core.Capture;

namespace TinyClips.Core.Tests;

public sealed class BrandingOverlayCompositorTests
{
    [Fact]
    public void PreparedAccess_DoesNotRasterizeOrDrawBeforePreparation()
    {
        var calls = 0;
        var compositor = new BrandingOverlayCompositor(_ =>
        {
            calls++;
            return Badge();
        });
        var pixels = new byte[10 * 10 * 4];

        compositor.DrawPrepared(pixels, 10, 10);

        Assert.False(compositor.TryGetPreparedBadge(10, out var badge, out var width, out var height, out var margin));
        Assert.Empty(badge);
        Assert.Equal(0, width);
        Assert.Equal(0, height);
        Assert.Equal(0, margin);
        Assert.Equal(0, calls);
        Assert.All(pixels, pixel => Assert.Equal((byte)0, pixel));
    }

    [Fact]
    public async Task PrepareAsync_CachesOnceForSameHeightAndDifferentFrameWidths()
    {
        var calls = 0;
        var badge = Badge();
        var compositor = new BrandingOverlayCompositor(_ =>
        {
            calls++;
            return badge;
        });

        Assert.True(await compositor.PrepareAsync(10, TestContext.Current.CancellationToken));
        Assert.True(await compositor.PrepareAsync(10, TestContext.Current.CancellationToken));
        compositor.DrawPrepared(new byte[10 * 10 * 4], 10, 10);
        compositor.DrawPrepared(new byte[20 * 10 * 4], 20, 10);

        Assert.True(compositor.TryGetPreparedBadge(10, out var pixels, out _, out _, out _));
        Assert.Same(badge.Bgra, pixels);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task PreparedDraw_PreservesBlendPlacementAndFrameAlpha()
    {
        var compositor = new BrandingOverlayCompositor(_ => Badge());
        await compositor.PrepareAsync(10, TestContext.Current.CancellationToken);
        var pixels = Enumerable.Repeat((byte)20, 10 * 10 * 4).ToArray();

        compositor.DrawPrepared(pixels, 10, 10);

        var origin = ((8 * 10) + 8) * 4;
        Assert.Equal((byte)110, pixels[origin]);
        Assert.Equal((byte)60, pixels[origin + 1]);
        Assert.Equal((byte)35, pixels[origin + 2]);
        Assert.Equal((byte)20, pixels[origin + 3]);
        Assert.Equal((byte)20, pixels[origin - 4]);
        Assert.Equal((byte)20, pixels[origin + 4]);
    }

    [Fact]
    public async Task PreparedDraw_ClipsBadgeOnSmallFrame()
    {
        var compositor = new BrandingOverlayCompositor(_ =>
            new BrandingBadge(Enumerable.Repeat((byte)255, 3 * 3 * 4).ToArray(), 3, 3, 0));
        await compositor.PrepareAsync(2, TestContext.Current.CancellationToken);
        var pixels = new byte[2 * 2 * 4];

        compositor.DrawPrepared(pixels, 2, 2);

        for (var i = 0; i < pixels.Length; i += 4)
        {
            Assert.Equal((byte)255, pixels[i]);
            Assert.Equal((byte)255, pixels[i + 1]);
            Assert.Equal((byte)255, pixels[i + 2]);
            Assert.Equal((byte)0, pixels[i + 3]);
        }
    }

    [Fact]
    public async Task HeightChange_RequiresPreparationAndReplacesOldBadge()
    {
        var calls = 0;
        var compositor = new BrandingOverlayCompositor(_ =>
        {
            calls++;
            return Badge();
        });
        await compositor.PrepareAsync(10, TestContext.Current.CancellationToken);
        var pixels = new byte[10 * 20 * 4];

        compositor.DrawPrepared(pixels, 10, 20);
        Assert.All(pixels, pixel => Assert.Equal((byte)0, pixel));
        Assert.False(compositor.TryGetPreparedBadge(20, out _, out _, out _, out _));
        Assert.Equal(1, calls);

        Assert.True(await compositor.PrepareAsync(20, TestContext.Current.CancellationToken));
        Assert.False(compositor.TryGetPreparedBadge(10, out _, out _, out _, out _));
        Assert.True(compositor.TryGetPreparedBadge(20, out _, out _, out _, out _));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task FailedPreparation_IsBestEffortAndIsNotRetriedByFramesOrSameSizePreparation()
    {
        var calls = 0;
        var compositor = new BrandingOverlayCompositor(_ =>
        {
            calls++;
            throw new InvalidOperationException("Synthetic rasterizer failure.");
        });

        Assert.False(await compositor.PrepareAsync(10, TestContext.Current.CancellationToken));
        Assert.False(await compositor.PrepareAsync(10, TestContext.Current.CancellationToken));
        var pixels = new byte[10 * 10 * 4];
        compositor.DrawPrepared(pixels, 10, 10);
        compositor.Draw(pixels, 10, 10);
        Assert.False(compositor.TryGetBadge(10, out _, out _, out _, out _));
        Assert.Equal(1, calls);
        Assert.All(pixels, pixel => Assert.Equal((byte)0, pixel));

        Assert.False(await compositor.PrepareAsync(20, TestContext.Current.CancellationToken));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task FailedNewSize_DoesNotReusePreviousBadge()
    {
        var compositor = new BrandingOverlayCompositor(height => height == 10 ? Badge() : null);
        Assert.True(await compositor.PrepareAsync(10, TestContext.Current.CancellationToken));

        Assert.False(await compositor.PrepareAsync(20, TestContext.Current.CancellationToken));

        Assert.False(compositor.TryGetPreparedBadge(10, out _, out _, out _, out _));
        Assert.False(compositor.TryGetPreparedBadge(20, out _, out _, out _, out _));
    }

    [Fact]
    public async Task NewRecordingCompositor_DoesNotShareBadgeCacheOrFailure()
    {
        var calls = 0;
        BrandingBadge? Rasterize(int _) => ++calls == 1 ? null : Badge();
        var first = new BrandingOverlayCompositor(Rasterize);
        var second = new BrandingOverlayCompositor(Rasterize);

        Assert.False(await first.PrepareAsync(10, TestContext.Current.CancellationToken));
        Assert.True(await second.PrepareAsync(10, TestContext.Current.CancellationToken));
        Assert.False(first.TryGetPreparedBadge(10, out _, out _, out _, out _));
        Assert.True(second.TryGetPreparedBadge(10, out _, out _, out _, out _));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task PreCancelledPreparation_DoesNotRasterize()
    {
        var calls = 0;
        var compositor = new BrandingOverlayCompositor(_ =>
        {
            calls++;
            return Badge();
        });
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => compositor.PrepareAsync(10, cancellation.Token));

        Assert.Equal(0, calls);
        Assert.False(compositor.TryGetPreparedBadge(10, out _, out _, out _, out _));
    }

    [Fact]
    public async Task CancellationDuringRasterization_WaitsForWorkerAndDoesNotPublishLateResult()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var calls = 0;
        var compositor = new BrandingOverlayCompositor(_ =>
        {
            if (++calls == 1)
            {
                entered.SetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken))
                {
                    throw new TimeoutException("Synthetic rasterizer was not released.");
                }
            }

            return Badge();
        });
        var preparing = compositor.PrepareAsync(10, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            cancellation.Cancel();
            Assert.False(preparing.IsCompleted);
        }
        finally
        {
            release.Set();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparing);
        Assert.False(compositor.TryGetPreparedBadge(10, out _, out _, out _, out _));
        Assert.True(await compositor.PrepareAsync(10, TestContext.Current.CancellationToken));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CancelledSizeChange_PreservesPreviouslyPreparedBadge()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var compositor = new BrandingOverlayCompositor(height =>
        {
            if (height == 20)
            {
                cancellation.Cancel();
            }

            return Badge();
        });
        await compositor.PrepareAsync(10, TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => compositor.PrepareAsync(20, cancellation.Token));

        Assert.True(compositor.TryGetPreparedBadge(10, out _, out _, out _, out _));
        Assert.False(compositor.TryGetPreparedBadge(20, out _, out _, out _, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task PrepareAsync_RejectsInvalidHeight(int height)
    {
        var compositor = new BrandingOverlayCompositor(_ => throw new InvalidOperationException("Must not rasterize."));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => compositor.PrepareAsync(height, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(360)]
    [InlineData(1080)]
    [InlineData(2160)]
    public async Task PreparedBadge_MatchesLazyRenderingAndScaling(int frameHeight)
    {
        var prepared = new BrandingOverlayCompositor();
        var lazy = new BrandingOverlayCompositor();
        Assert.True(await prepared.PrepareAsync(frameHeight, TestContext.Current.CancellationToken));
        Assert.True(lazy.TryGetBadge(frameHeight, out var lazyBadge, out var width, out var height, out var margin));
        Assert.True(prepared.TryGetPreparedBadge(frameHeight, out var preparedBadge, out var preparedWidth, out var preparedHeight, out var preparedMargin));
        Assert.Equal(width, preparedWidth);
        Assert.Equal(height, preparedHeight);
        Assert.Equal(margin, preparedMargin);
        Assert.Equal((int)Math.Round(Math.Clamp(frameHeight / 50f, 12f, 28f)), margin);
        Assert.Equal(lazyBadge, preparedBadge);

        var frameWidth = Math.Max(width + (2 * margin), 320);
        var lazyPixels = new byte[frameWidth * frameHeight * 4];
        var preparedPixels = new byte[lazyPixels.Length];
        lazy.Draw(lazyPixels, frameWidth, frameHeight);
        prepared.DrawPrepared(preparedPixels, frameWidth, frameHeight);
        Assert.Equal(lazyPixels, preparedPixels);
        Assert.Contains(preparedPixels, pixel => pixel > 0);
    }

    [Fact]
    public void GpuCaptureStart_RequiresInitializationWithoutAccessingHardware()
    {
        using var session = new GpuCaptureSession(CaptureTarget.Monitor(0), null, 30, includeCursor: true);

        var exception = Assert.Throws<InvalidOperationException>(session.Start);

        Assert.Equal("Initialize must be called before Start.", exception.Message);
        Assert.Equal(0, session.EmittedFrameCount);
    }

    private static BrandingBadge Badge() => new([200, 100, 50, 128], 1, 1, 1);
}
