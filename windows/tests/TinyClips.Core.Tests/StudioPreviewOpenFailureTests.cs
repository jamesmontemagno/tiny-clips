using System.Runtime.InteropServices;
using TinyClips.Core.Studio.Preview;
using TinyClips.Core.Studio.Rendering;

namespace TinyClips.Core.Tests;

/// <summary>
/// Which failures of an open are tried once more, because what went wrong may pass, and which
/// fail at once.
/// </summary>
public sealed class StudioPreviewOpenFailureTests
{
    private const int DeviceRemoved = unchecked((int)0x887A0005);
    private const int InvalidRequest = unchecked((int)0xC00D36B2);
    private const int InvalidMediaType = unchecked((int)0xC00D36B4);

    [Theory]
    [InlineData(unchecked((int)0x887A0005))] // DXGI_ERROR_DEVICE_REMOVED
    [InlineData(unchecked((int)0x887A0006))] // DXGI_ERROR_DEVICE_HUNG
    [InlineData(unchecked((int)0x887A0007))] // DXGI_ERROR_DEVICE_RESET
    [InlineData(unchecked((int)0x887A0020))] // DXGI_ERROR_DRIVER_INTERNAL_ERROR
    [InlineData(unchecked((int)0x8899000C))] // D2DERR_RECREATE_TARGET
    public void AnErrorCodeThatSaysTheDeviceIsGone_IsALostDevice(int code)
    {
        Assert.True(StudioPreviewOpenFailure.IsDeviceLost(new COMException("lost", code)));
    }

    [Theory]
    [InlineData(unchecked((int)0x80004005))] // E_FAIL
    [InlineData(InvalidRequest)]
    [InlineData(InvalidMediaType)]
    [InlineData(0)]
    public void AnyOtherErrorCode_IsNot(int code)
    {
        Assert.False(StudioPreviewOpenFailure.IsDeviceLost(new COMException("something else", code)));
    }

    [Fact]
    public void ALostDevice_IsFoundUnderTheExceptionsThatWrapIt()
    {
        var copy = new COMException("the copy failed", DeviceRemoved);
        var open = new InvalidOperationException("The graphics device was lost and the preview could not be restarted.", copy);

        Assert.True(StudioPreviewOpenFailure.IsDeviceLost(open));
        Assert.True(StudioPreviewOpenFailure.IsDeviceLost(new InvalidOperationException("outer", open)));
        Assert.True(StudioPreviewOpenFailure.IsDeviceLost(new StudioDeviceLostException("simulated")));
        Assert.True(StudioPreviewOpenFailure.IsDeviceLost(new InvalidOperationException("outer", new StudioDeviceLostException("removed", new COMException("other", InvalidRequest)))));
        Assert.False(StudioPreviewOpenFailure.IsDeviceLost(null));
        Assert.False(StudioPreviewOpenFailure.IsDeviceLost(new InvalidOperationException("no device underneath")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ALostDevice_IsWorthAnotherAttempt_WhateverThePlayersHadDone(bool everyPlayerDeliveredAFrame)
    {
        var failure = new InvalidOperationException("The graphics device was lost and the preview could not be restarted.", new COMException("the copy failed", DeviceRemoved));

        Assert.NotNull(StudioPreviewOpenFailure.WorthAnotherAttempt(failure, everyPlayerDeliveredAFrame));
    }

    [Fact]
    public void APlayerThatFailedAfterEveryPlayerHadHandedOverAFrame_IsWorthAnotherAttempt()
    {
        // The frames show that the files can be decoded: the failure is the player's.
        var failure = new InvalidDataException("The camera recording could not be decoded (DecodingError).", new COMException(string.Empty, InvalidRequest));

        var why = StudioPreviewOpenFailure.WorthAnotherAttempt(failure, everyPlayerDeliveredAFrame: true);

        Assert.NotNull(why);
        Assert.Contains("player", why, StringComparison.Ordinal);
    }

    [Fact]
    public void APlayerThatFailedBeforeEveryPlayerHadAFrame_FailsAtOnce()
    {
        // A file that is not a video, or one no frame comes out of: nothing says it would do better.
        var notAVideo = new InvalidDataException("The screen recording could not be decoded (SourceNotSupported).", new COMException(string.Empty, InvalidMediaType));
        var noFrame = new InvalidDataException("The camera recording could not be decoded: it delivered no frame.");

        Assert.Null(StudioPreviewOpenFailure.WorthAnotherAttempt(notAVideo, everyPlayerDeliveredAFrame: false));
        Assert.Null(StudioPreviewOpenFailure.WorthAnotherAttempt(noFrame, everyPlayerDeliveredAFrame: false));
        Assert.Null(StudioPreviewOpenFailure.WorthAnotherAttempt(noFrame, everyPlayerDeliveredAFrame: false, aPlayerStoppedDecoding: false));
    }

    [Fact]
    public void APlayerThatStoppedDecodingBeforeEveryPlayerHadAFrame_IsWorthAnotherAttempt()
    {
        // It opened the file and then said it could not decode it: that has been seen to pass.
        var failure = new InvalidDataException("The screen recording could not be decoded (DecodingError).", new COMException(string.Empty, InvalidRequest));

        var why = StudioPreviewOpenFailure.WorthAnotherAttempt(failure, everyPlayerDeliveredAFrame: false, aPlayerStoppedDecoding: true);

        Assert.NotNull(why);
        Assert.Contains("stopped decoding", why, StringComparison.Ordinal);
    }

    [Fact]
    public void WhatAPlayerReported_DoesNotMakeAMissingFileOrATimeoutWorthAnotherAttempt()
    {
        Assert.Null(StudioPreviewOpenFailure.WorthAnotherAttempt(new FileNotFoundException("The screen recording is missing: screen.mp4", "screen.mp4"), everyPlayerDeliveredAFrame: false, aPlayerStoppedDecoding: true));
        Assert.Null(StudioPreviewOpenFailure.WorthAnotherAttempt(new TimeoutException("The recording did not open in time."), everyPlayerDeliveredAFrame: false, aPlayerStoppedDecoding: true));
        Assert.Null(StudioPreviewOpenFailure.WorthAnotherAttempt(new OperationCanceledException(), everyPlayerDeliveredAFrame: false, aPlayerStoppedDecoding: true));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AMissingFile_ATimeout_AndNoDeviceAtAll_FailAtOnce(bool everyPlayerDeliveredAFrame)
    {
        Exception[] failures =
        [
            new FileNotFoundException("The screen recording is missing: screen.mp4", "screen.mp4"),
            new TimeoutException("The recording did not open in time."),
            new InvalidOperationException("The preview needs a graphics device, and none could be created.", new InvalidOperationException("none")),
            new InvalidOperationException("The preview ran into a problem and stopped.", new NullReferenceException()),
        ];

        foreach (var failure in failures)
        {
            Assert.Null(StudioPreviewOpenFailure.WorthAnotherAttempt(failure, everyPlayerDeliveredAFrame));
        }
    }

    [Fact]
    public void ACancelledOpen_IsNotTriedAgain_EvenWithALostDeviceUnderneath()
    {
        var cancelled = new OperationCanceledException("cancelled", new COMException("the copy failed", DeviceRemoved));

        Assert.Null(StudioPreviewOpenFailure.WorthAnotherAttempt(cancelled, everyPlayerDeliveredAFrame: true));
        Assert.Null(StudioPreviewOpenFailure.WorthAnotherAttempt(new TaskCanceledException(), everyPlayerDeliveredAFrame: true));
    }

    [Fact]
    public void WithoutAFailure_ThereIsNothingToAsk()
    {
        Assert.Throws<ArgumentNullException>(() => StudioPreviewOpenFailure.WorthAnotherAttempt(null!, everyPlayerDeliveredAFrame: true));
    }
}
