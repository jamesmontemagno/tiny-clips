using System.Text.Json;
using TinyClips.Core.Capture;
using TinyClips.Core.Services;

namespace TinyClips.Core.Tests;

public sealed class RecordingDiagnosticAccountingTests
{
    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(int milliseconds) => _ticks += TimeSpan.FromMilliseconds(milliseconds).Ticks;
    }

    [Fact]
    public void Phases_SeparatePreparationWaitActivePauseAndDrain()
    {
        var clock = new Clock();
        var monitor = new RecordingPerformanceMonitor("cpu", 640, 480, 30, clock);
        monitor.BeginPreparation();
        clock.Advance(200);
        monitor.Prepared();
        clock.Advance(300);
        monitor.Start();
        clock.Advance(50);
        monitor.FrameEmitted(1, TimeSpan.FromMilliseconds(50));
        monitor.SubmissionAttempt();
        monitor.FrameEncoded();
        clock.Advance(150);
        monitor.Pause();
        clock.Advance(10_000);
        monitor.CpuSkippedTick();
        monitor.GpuPacingOverrun(100);
        monitor.Resume();
        clock.Advance(100);
        monitor.FrameEmitted(1, TimeSpan.FromMilliseconds(300));
        monitor.BeginFinalization();
        clock.Advance(400);
        monitor.SubmissionAttempt();
        monitor.FrameEncoded();
        monitor.FinalizationSucceeded = true;

        var report = monitor.Complete();
        Assert.Equal(new RecordingPhaseTimes(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(300),
            TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(400),
            TimeSpan.FromMilliseconds(50)), report.Phases);
        Assert.Equal(0, report.CpuSkippedTickEvents);
        Assert.Equal(0, report.GpuPacingMissedSlots);
        Assert.Equal(1, report.RepeatedSourceFrames);
        Assert.Equal(TimeSpan.Zero, report.MaxEmittedFrameGap);
        Assert.Equal(2 / 0.3, report.ActiveFps, 9);
        Assert.True(report.FinalizationSucceeded);
    }

    [Fact]
    public async Task BorrowedFrameGate_CountsActiveContentionButNotPauseOrStop()
    {
        var monitor = new RecordingPerformanceMonitor("cpu", 4, 4, 30);
        monitor.Start();
        var gate = new CpuFrameProcessingGate();
        gate.Start();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var processing = Task.Run(() => gate.TryProcess(() =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        }, monitor), TestContext.Current.CancellationToken);

        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.False(gate.TryProcess(() => throw new InvalidOperationException("Overlapping consumer ran"), monitor));
            monitor.Pause();
            Assert.False(gate.TryProcess(() => throw new InvalidOperationException("Paused consumer ran"), monitor));
        }
        finally
        {
            release.Set();
            await processing.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            gate.Stop();
        }

        monitor.Resume();
        Assert.False(gate.TryProcess(() => throw new InvalidOperationException("Stopped consumer ran"), monitor));
        var report = monitor.Complete();
        Assert.Equal(1, report.CpuSkippedTickEvents);
        Assert.Equal(0, report.FramesDropped);
        Assert.Equal(0, report.FramesEmitted);
        Assert.Equal(0, report.SubmissionAttempts);
    }

    [Fact]
    public void SinkWriter_BorrowedCpuAndGpuOverloadsRetainActualAcceptanceContract()
    {
        foreach (var signature in new[]
        {
            new[] { typeof(CapturedFrame), typeof(TimeSpan), typeof(TimeSpan) },
            new[] { typeof(GpuFrame), typeof(TimeSpan) },
        })
        {
            var method = typeof(MfSinkWriterEncoder).GetMethod(nameof(MfSinkWriterEncoder.WriteVideo), signature);
            Assert.NotNull(method);
            Assert.Equal(typeof(bool), method.ReturnType);
        }
    }

    [Fact]
    public void Counters_DoNotAddPacingEventsToQueuePoolProductionDrops()
    {
        var monitor = new RecordingPerformanceMonitor("gpu", 640, 480, 30);
        monitor.Start();
        Parallel.For(0, 100, _ =>
        {
            monitor.CpuSkippedTick();
            monitor.GpuPacingOverrun(3);
            monitor.QueueDropped();
            monitor.PoolExhausted();
            monitor.ProductionFailed();
            monitor.ReadbackFailed();
            monitor.NoSourceFrameTick();
            monitor.SubmissionAttempt();
            monitor.SubmissionFailed();
            monitor.FrameNotSubmitted();
        });
        var report = monitor.Complete();
        Assert.Equal(100, report.CpuSkippedTickEvents);
        Assert.Equal(100, report.GpuPacingOverrunEvents);
        Assert.Equal(300, report.GpuPacingMissedSlots);
        Assert.Equal(300, report.FramesDropped);
        Assert.Equal(report.FramesDropped, report.QueueDrops + report.PoolExhaustionEvents + report.ProductionFailures);
        Assert.Equal(100, report.CaptureReadbackFailures);
        Assert.Equal(100, report.NoSourceFrameTicks);
        Assert.Equal(100, report.SubmissionAttempts);
        Assert.Equal(100, report.SubmissionFailures);
        Assert.Equal(100, report.FramesNotSubmitted);
        Assert.Equal(0, report.FramesSubmitted);
    }

    [Fact]
    public void PacingOverrun_FromAnEarlierPauseEpochIsExcludedAfterResume()
    {
        var monitor = new RecordingPerformanceMonitor("gpu", 640, 480, 30);
        monitor.Start();
        var epoch = monitor.CadenceEpoch;
        monitor.Pause();
        monitor.Resume();
        monitor.GpuPacingOverrun(100, epoch);
        monitor.GpuPacingOverrun(2, monitor.CadenceEpoch);
        var report = monitor.Complete();
        Assert.Equal(1, report.GpuPacingOverrunEvents);
        Assert.Equal(2, report.GpuPacingMissedSlots);
    }

    [Fact]
    public void MissedCadenceIsReportedWhenQueueDropsAreZero()
    {
        var monitor = new RecordingPerformanceMonitor("gpu", 640, 480, 30);
        monitor.Start();
        monitor.GpuPacingOverrun(FramePacer.MissedSlots(350, 100, 100));
        monitor.FrameEmitted(1, TimeSpan.FromMilliseconds(100));
        monitor.FrameEmitted(1, TimeSpan.FromMilliseconds(450));
        monitor.FrameEmitted(2, TimeSpan.FromMilliseconds(500));
        var report = monitor.Complete();
        Assert.Equal(0, report.FramesDropped);
        Assert.Equal(1, report.GpuPacingOverrunEvents);
        Assert.Equal(3, report.GpuPacingMissedSlots);
        Assert.Equal(TimeSpan.FromMilliseconds(350), report.MaxEmittedFrameGap);
        Assert.Equal(1, report.RepeatedSourceFrames);
    }

    [Fact]
    public void SubmissionBudget_ExcludesSourceLossAndLeavesPendingFramesExplicit()
    {
        var monitor = new RecordingPerformanceMonitor("cpu", 640, 480, 30);
        monitor.Start();
        for (var i = 0; i < 10; i++) { monitor.FrameEmitted(); }
        for (var i = 0; i < 4; i++) { monitor.SubmissionAttempt(); monitor.FrameEncoded(); }
        for (var i = 0; i < 2; i++) { monitor.SubmissionAttempt(); monitor.SubmissionFailed(); monitor.QueueDropped(); }
        monitor.FrameNotSubmitted();
        monitor.PoolExhausted();
        monitor.ProductionFailed();
        var report = monitor.Complete();
        Assert.Equal(1, report.FramesPendingSubmission);
        Assert.Equal(10, report.FramesSubmitted + report.QueueDrops + report.SubmissionFailures
            + report.FramesNotSubmitted + report.FramesPendingSubmission);
        Assert.Equal(4, report.FramesDropped);
        Assert.Null(report.VerifiedOutputFrames);
    }

    [Theory]
    [InlineData(99, 100, 100, 0)]
    [InlineData(100, 100, 100, 1)]
    [InlineData(199, 100, 100, 1)]
    [InlineData(200, 100, 100, 2)]
    [InlineData(450, 100, 100, 4)]
    public void PacerSlots_UseNextStrictlyFutureSlot(long now, long due, long interval, long expected) =>
        Assert.Equal(expected, FramePacer.MissedSlots(now, due, interval));

    [Fact]
    public void EmptyAndStoppedWhilePaused_HaveNoFirstFrameOrFalseCadenceLoss()
    {
        var clock = new Clock();
        var monitor = new RecordingPerformanceMonitor("cpu", 640, 480, 30, clock);
        monitor.Start();
        clock.Advance(100);
        monitor.Pause();
        clock.Advance(1000);
        monitor.BeginFinalization();
        clock.Advance(200);
        var report = monitor.Complete();
        Assert.Null(report.Phases!.FirstFrameLatency);
        Assert.Equal(TimeSpan.FromMilliseconds(100), report.Phases.ActiveRecording);
        Assert.Equal(TimeSpan.FromSeconds(1), report.Phases.Paused);
        Assert.Equal(TimeSpan.FromMilliseconds(200), report.Phases.Finalization);
        Assert.Null(report.VerifiedOutputFrames);
        Assert.Equal(0, report.ActiveFps);
    }

    [Fact]
    public void DiagnosticHotPath_UsesBoundedStorageWithoutPerFrameAllocation()
    {
        var monitor = new RecordingPerformanceMonitor("gpu", 640, 480, 30, new Clock());
        monitor.Start();
        for (var i = 0; i < 5000; i++)
        {
            monitor.FrameEmitted(1, TimeSpan.FromTicks(i));
            monitor.Record(RecordingStage.Composite, 1);
            monitor.CpuSkippedTick();
            monitor.GpuPacingOverrun(2);
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 5000; i < 10_000; i++)
        {
            monitor.FrameEmitted(1, TimeSpan.FromTicks(i));
            monitor.Record(RecordingStage.Composite, 1);
            monitor.CpuSkippedTick();
            monitor.GpuPacingOverrun(2);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        var report = monitor.Complete();
        Assert.Equal(10_000, Assert.Single(report.Stages).Count);
    }

    [Fact]
    public void JsonV2_PreservesLegacyFieldsAndDoesNotClaimHardwareOrDecodedFrames()
    {
        var monitor = new RecordingPerformanceMonitor("cpu", 640, 480, 30)
        {
            RequestedPipeline = "gpu", RequestedEncoderBackend = "SinkWriter",
            EncoderBackend = "Transcoder", D3DDriver = "warp", HardwareEncodingRequested = true,
            Geometry = CaptureOutputGeometry.Calculate(641, 481, null),
        };
        monitor.Start();
        monitor.FrameEmitted();
        monitor.SubmissionAttempt();
        monitor.FrameEncoded();
        var report = monitor.Complete();
        var json = JsonSerializer.Serialize(report);
        using var doc = JsonDocument.Parse(json);
        foreach (var field in new[] { "Pipeline", "EncoderPath", "Width", "Height", "TargetFps", "WallClock",
                     "FramesEmitted", "FramesEncoded", "FramesDropped", "ProcessCpuPercent", "ProcessCpuCores",
                     "ManagedAllocatedBytes", "Gen0Collections", "Gen1Collections", "Gen2Collections", "GcPauseTotal",
                     "PeakWorkingSetBytes", "Stages", "EffectiveFps", "DropPercent", "GcPausePercent", "AllocationMbPerSecond" })
        {
            Assert.True(doc.RootElement.TryGetProperty(field, out _), field);
        }
        var restored = JsonSerializer.Deserialize<RecordingPerformanceReport>(json)!;
        Assert.Equal(2, restored.SchemaVersion);
        Assert.Equal("gpu", restored.RequestedPipeline);
        Assert.Equal("cpu", restored.Pipeline);
        Assert.Equal("warp", restored.D3DDriver);
        Assert.Equal("unknown", restored.HardwareEncoding);
        Assert.Null(restored.VerifiedOutputFrames);
        Assert.Equal(restored.FramesEncoded, restored.FramesSubmitted);
        Assert.Equal(report.Geometry, restored.Geometry);
        Assert.Contains("hardwareVerified=unknown", restored.ToTable());
        Assert.Contains("submitted=1", restored.ToTable());
    }

    [Theory]
    [InlineData(0xAA64, 0, 0xAA64, "native")]
    [InlineData(0x8664, 0, 0x8664, "native")]
    [InlineData(0xAA64, 0x8664, 0x8664, "emulated-or-hybrid")]
    [InlineData(0xAA64, 0, 0x8664, "emulated-or-hybrid")]
    [InlineData(0xAA64, 0x014c, 0x014c, "emulated")]
    [InlineData(0x8664, 0x014c, 0x014c, "wow64")]
    [InlineData(0xAA64, 0, 0xA641, "hybrid")]
    [InlineData(0xAA64, 0x8664, 0xAA64, "ambiguous")]
    [InlineData(0, 0, 0x8664, "unknown")]
    [InlineData(0xAA64, 0, 0xffff, "unknown")]
    public void Architecture_IsEvidenceBased(int os, int wow, int target, string expected) =>
        Assert.Equal(expected, ProcessArchitectureClassifier.Classify((ushort)os, (ushort)wow, (ushort)target));

    [Fact]
    public void Architecture_UnspecifiedWowMachineIsNotProofOfNativeRuntime()
    {
        Assert.Equal("unknown", ProcessArchitectureClassifier.Classify(0xAA64, 0, null));
        Assert.Equal("unknown", ProcessArchitectureClassifier.Classify(0x8664, 0, null));
        Assert.Equal("emulated-or-hybrid", ProcessArchitectureClassifier.Classify(0xAA64, 0x8664, null));
    }
}
