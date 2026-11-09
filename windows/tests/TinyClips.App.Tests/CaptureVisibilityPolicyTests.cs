namespace TinyClips.App.Tests;

public sealed class CaptureVisibilityPolicyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Capture_UpdatesExistingWindows_AndNewWindowsInheritSnapshot(bool include)
    {
        var affinity = new Dictionary<nint, bool>();
        var policy = new CaptureVisibilityPolicy((hwnd, excluded) => affinity[hwnd] = excluded);
        policy.Register(1);
        Assert.True(affinity[1]);
        policy.BeginCapture(include, recordingActive: false);
        policy.Register(2);

        Assert.Equal(!include, affinity[1]);
        Assert.Equal(!include, affinity[2]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrivateOverlays_AreNeverIncludedOrSelectable(bool include)
    {
        var calls = new List<(nint Hwnd, bool Excluded)>();
        var policy = new CaptureVisibilityPolicy((hwnd, excluded) => calls.Add((hwnd, excluded)));
        policy.Register(1, alwaysExcluded: true);
        policy.BeginCapture(include, recordingActive: false);
        policy.BeginCapture(!include, recordingActive: false);

        Assert.All(calls, call => Assert.True(call.Excluded));
        Assert.False(policy.CanSelectWindow(isOwnProcess: true, 1, 99));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConcurrentCapture_DoesNotChangeRecordingPolicy(bool include)
    {
        var affinity = new Dictionary<nint, bool>();
        var policy = new CaptureVisibilityPolicy((hwnd, excluded) => affinity[hwnd] = excluded);
        policy.Register(1);
        policy.BeginCapture(include, recordingActive: false);
        policy.BeginCapture(!include, recordingActive: true);
        policy.Register(2);

        Assert.Equal(include, policy.IncludeTinyClips);
        Assert.Equal(!include, affinity[1]);
        Assert.Equal(!include, affinity[2]);

        policy.BeginCapture(!include, recordingActive: false);
        Assert.Equal(include, affinity[1]);
        Assert.Equal(include, affinity[2]);
    }

    [Fact]
    public void ClosedWindow_IsNotUpdated_AndReusedHandleHasNoPrivateClassification()
    {
        var calls = new List<nint>();
        var policy = new CaptureVisibilityPolicy((hwnd, _) => calls.Add(hwnd));
        policy.Register(1, alwaysExcluded: true);
        policy.Unregister(1);
        policy.BeginCapture(true, recordingActive: false);
        Assert.Empty(calls);

        policy.Register(1);
        Assert.True(policy.CanSelectWindow(isOwnProcess: true, 1, 99));
        Assert.Single(calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WindowSelection_FiltersOwnProcessButNeverForeignWindowsOrPicker(bool include)
    {
        var policy = new CaptureVisibilityPolicy((_, _) => { });
        policy.BeginCapture(include, recordingActive: false);

        Assert.Equal(include, policy.CanSelectWindow(isOwnProcess: true, 1, 99));
        Assert.True(policy.CanSelectWindow(isOwnProcess: false, 1, 99));
        Assert.False(policy.CanSelectWindow(isOwnProcess: true, 99, 99));
        Assert.False(policy.CanSelectWindow(isOwnProcess: false, 99, 99));
    }

    [Fact]
    public void UnchangedAffinity_IsNotReapplied()
    {
        var calls = new List<bool>();
        var policy = new CaptureVisibilityPolicy((_, excluded) => calls.Add(excluded));
        policy.Register(1);
        policy.BeginCapture(false, recordingActive: false);
        policy.BeginCapture(true, recordingActive: false);
        policy.BeginCapture(true, recordingActive: false);

        Assert.Equal([true, false], calls);
    }

    [Fact]
    public void NativeFailure_AbortsCapture_AndNextCaptureRetries()
    {
        var fail = false;
        var calls = new List<bool>();
        var policy = new CaptureVisibilityPolicy((_, excluded) =>
        {
            if (fail)
            {
                throw new InvalidOperationException("Synthetic affinity failure");
            }
            calls.Add(excluded);
        });
        policy.Register(1);
        fail = true;
        Assert.Throws<InvalidOperationException>(() => policy.BeginCapture(true, recordingActive: false));
        fail = false;
        policy.BeginCapture(false, recordingActive: false);
        Assert.Equal([true, true], calls);
    }

    [Fact]
    public void FailedWindowRegistration_RemainsTrackedUntilClosed()
    {
        var fail = true;
        var calls = new List<nint>();
        var policy = new CaptureVisibilityPolicy((hwnd, _) =>
        {
            if (fail)
            {
                throw new InvalidOperationException("Synthetic affinity failure");
            }
            calls.Add(hwnd);
        });
        Assert.Throws<InvalidOperationException>(() => policy.Register(1));
        policy.Unregister(1);
        fail = false;
        policy.BeginCapture(false, recordingActive: false);
        Assert.Empty(calls);
    }
}
