using TinyClips.Core.Capture;

namespace TinyClips.Core.Tests;

public sealed class TeleprompterPlacementTests
{
    [Fact]
    public void Calculate_UsesTargetMonitorScaleForSizeAndRelativePosition()
    {
        var monitor = CreateMonitor(
            deviceName: @"\\.\DISPLAY2",
            workAreaX: 1920,
            workAreaY: 40,
            workAreaWidth: 2560,
            workAreaHeight: 1400,
            dpi: 144);

        var placement = TeleprompterPlacement.Calculate(
            monitor,
            widthDip: 600,
            heightDip: 140,
            topOffsetDip: 24,
            savedXDip: 100,
            savedYDip: 50,
            savedPositionIsMonitorRelative: true);

        Assert.Equal(new PixelRect(2070, 115, 900, 210), placement);
    }

    [Fact]
    public void Calculate_ClampsSavedPositionToCurrentWorkArea()
    {
        var monitor = CreateMonitor(
            deviceName: @"\\.\DISPLAY1",
            workAreaX: -1920,
            workAreaY: 0,
            workAreaWidth: 1920,
            workAreaHeight: 1040,
            dpi: 96);

        var placement = TeleprompterPlacement.Calculate(
            monitor,
            widthDip: 600,
            heightDip: 140,
            topOffsetDip: 24,
            savedXDip: 5000,
            savedYDip: 5000,
            savedPositionIsMonitorRelative: true);

        Assert.Equal(new PixelRect(-600, 900, 600, 140), placement);
    }

    [Fact]
    public void Calculate_CapsPanelSizeToNarrowWorkArea()
    {
        var monitor = CreateMonitor(
            deviceName: @"\\.\DISPLAY1",
            workAreaX: 0,
            workAreaY: 0,
            workAreaWidth: 800,
            workAreaHeight: 600,
            dpi: 144);

        var placement = TeleprompterPlacement.Calculate(
            monitor,
            widthDip: 600,
            heightDip: 500,
            topOffsetDip: 24,
            savedXDip: -1,
            savedYDip: -1,
            savedPositionIsMonitorRelative: false);

        Assert.Equal(new PixelRect(0, 0, 800, 600), placement);
    }

    [Fact]
    public void RelativePosition_RoundTripsAcrossDifferentMonitorScale()
    {
        var oldMonitor = CreateMonitor(@"\\.\DISPLAY2", 1920, 40, 2560, 1400, 144);
        var relative = TeleprompterPlacement.ToMonitorRelativeDips(oldMonitor, 2220, 190);
        var replacementMonitor = CreateMonitor(@"\\.\DISPLAY3", -1920, 0, 1920, 1040, 96);

        var placement = TeleprompterPlacement.Calculate(
            replacementMonitor,
            widthDip: 600,
            heightDip: 140,
            topOffsetDip: 24,
            savedXDip: relative.X,
            savedYDip: relative.Y,
            savedPositionIsMonitorRelative: true);

        Assert.Equal(new PixelRect(-1720, 100, 600, 140), placement);
    }

    private static MonitorInfo CreateMonitor(
        string deviceName,
        int workAreaX,
        int workAreaY,
        int workAreaWidth,
        int workAreaHeight,
        int dpi) =>
        new()
        {
            DeviceName = deviceName,
            X = workAreaX,
            Y = workAreaY,
            Width = workAreaWidth,
            Height = workAreaHeight,
            WorkAreaX = workAreaX,
            WorkAreaY = workAreaY,
            WorkAreaWidth = workAreaWidth,
            WorkAreaHeight = workAreaHeight,
            DpiX = dpi,
            DpiY = dpi,
            IsPrimary = workAreaX == 0,
            HMonitor = nint.Zero,
        };
}
