using TinyClips.Core.Capture;

namespace TinyClips.Core.Tests;

public sealed class RecordingPerformanceMonitorTests
{
    [Fact]
    public void Complete_ReportsCountsAndStageStats()
    {
        var monitor = new RecordingPerformanceMonitor("gpu", 1920, 1080, 30) { EncoderPath = "test" };
        monitor.Start();

        for (var i = 0; i < 10; i++)
        {
            monitor.FrameEmitted();
            monitor.FrameEncoded();
        }

        monitor.FrameDropped();
        monitor.Record(RecordingStage.Composite, 1_000);
        monitor.Record(RecordingStage.Composite, 3_000);
        var begin = RecordingPerformanceMonitor.Begin();
        monitor.End(RecordingStage.EncoderWait, begin);

        var report = monitor.Complete();

        Assert.Equal("gpu", report.Pipeline);
        Assert.Equal("test", report.EncoderPath);
        Assert.Equal(1920, report.Width);
        Assert.Equal(10, report.FramesEmitted);
        Assert.Equal(10, report.FramesEncoded);
        Assert.Equal(1, report.FramesDropped);
        Assert.Equal(10.0, report.DropPercent, 6);

        var composite = Assert.Single(report.Stages, s => s.Stage == RecordingStage.Composite);
        Assert.Equal(2, composite.Count);
        Assert.True(composite.MaxMs >= composite.AverageMs);
        Assert.True(composite.P99Ms >= composite.AverageMs);
        Assert.Contains(report.Stages, s => s.Stage == RecordingStage.EncoderWait);
        Assert.DoesNotContain(report.Stages, s => s.Stage == RecordingStage.OverlayWebcam);
    }

    [Fact]
    public void Record_IgnoredBeforeStartAndNegativeDurations()
    {
        var monitor = new RecordingPerformanceMonitor("cpu", 1, 1, 30);
        monitor.Record(RecordingStage.Composite, 500);
        monitor.Start();
        monitor.Record(RecordingStage.Composite, -1);

        var report = monitor.Complete();

        Assert.Empty(report.Stages);
        Assert.False(monitor.IsRunning);
    }

    [Fact]
    public void SetDroppedFrames_OverridesCounter()
    {
        var monitor = new RecordingPerformanceMonitor("cpu", 1, 1, 30);
        monitor.Start();
        monitor.FrameDropped();
        monitor.SetDroppedFrames(7);

        Assert.Equal(7, monitor.Complete().FramesDropped);
    }

    [Fact]
    public void P99_UsesNearestRank()
    {
        var monitor = new RecordingPerformanceMonitor("cpu", 1, 1, 30);
        monitor.Start();
        for (var i = 1; i <= 100; i++)
        {
            monitor.Record(RecordingStage.Composite, i);
        }

        var stats = Assert.Single(monitor.Complete().Stages, s => s.Stage == RecordingStage.Composite);

        // Nearest-rank p99 of 1..100 is the 99th value, not the maximum.
        Assert.Equal(99 * 1000.0 / System.Diagnostics.Stopwatch.Frequency, stats.P99Ms, 9);
        Assert.Equal(100 * 1000.0 / System.Diagnostics.Stopwatch.Frequency, stats.MaxMs, 9);
    }

    [Fact]
    public void ToTable_ContainsSummaryAndStageRows()
    {
        var monitor = new RecordingPerformanceMonitor("gpu", 640, 480, 60);
        monitor.Start();
        monitor.Record(RecordingStage.FrameProduce, 10);
        var table = monitor.Complete().ToTable();

        Assert.Contains("pipeline=gpu", table);
        Assert.Contains("640x480@60", table);
        Assert.Contains("FrameProduce", table);
        Assert.Contains("gcPause=", table);
    }
}
