using System.Collections.Immutable;
using System.Numerics;
using TinyClips.Core.Editing;
using Windows.Foundation;
using Windows.UI;

namespace TinyClips.Core.Tests;

public sealed class ScreenshotExportTests
{
    [Theory]
    [InlineData(1920, 1080, 100, 1920, 1080)]
    [InlineData(1920, 1080, 50, 960, 540)]
    [InlineData(101, 99, 50, 50, 50)]
    [InlineData(101.9, 99.9, 100, 101, 99)]
    [InlineData(101.9, 99.9, 10, 10, 10)]
    [InlineData(1, 1, 10, 1, 1)]
    public void OutputSize_UsesEstablishedTruncationAndScaleRounding(
        double width, double height, int scale, int expectedWidth, int expectedHeight)
    {
        var size = ScreenshotExportSize.Create(new Size(width, height), scale);

        Assert.Equal(expectedWidth, size.Width);
        Assert.Equal(expectedHeight, size.Height);
        Assert.Equal((int)(float)width, size.RenderWidth);
        Assert.Equal((int)(float)height, size.RenderHeight);
    }

    [Theory]
    [InlineData(ExportFramePreset.Original, 260, 160)]
    [InlineData(ExportFramePreset.Square, 260, 260)]
    [InlineData(ExportFramePreset.LandscapeFourByThree, 260, 195)]
    [InlineData(ExportFramePreset.LandscapeSixteenByNine, 285, 160)]
    [InlineData(ExportFramePreset.PortraitThreeByFour, 260, 347)]
    [InlineData(ExportFramePreset.PortraitNineBySixteen, 260, 463)]
    public void FramePresets_PreservePaddingAndFinalDimensions(
        ExportFramePreset preset, int width, int height)
    {
        var frame = ExportFrameLayout.Create(200, 100, 30, preset,
            ExportHorizontalAlignment.Right, ExportVerticalAlignment.Bottom);
        var size = ScreenshotExportSize.Create(frame.FrameSize, 50);

        Assert.Equal(new Size(width, height), frame.FrameSize);
        Assert.Equal(width - 30 - 200, frame.ImageBounds.X);
        Assert.Equal(height - 30 - 100, frame.ImageBounds.Y);
        Assert.Equal(200, frame.ImageBounds.Width);
        Assert.Equal(100, frame.ImageBounds.Height);
        Assert.Equal((int)Math.Round(width * 0.5), size.Width);
        Assert.Equal((int)Math.Round(height * 0.5), size.Height);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public void OutputSize_RejectsUnsupportedScale(int scale) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ScreenshotExportSize.Create(new Size(10, 10), scale));

    [Theory]
    [InlineData(0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void OutputSize_RejectsInvalidFrame(double width) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ScreenshotExportSize.Create(new Size(width, 10), 100));

    [Fact]
    public void CaptureSnapshot_DeepCopiesAllAnnotationData()
    {
        var annotation = new ScreenshotAnnotation
        {
            Tool = EditTool.Text,
            Bounds = new Rect(10, 20, 30, 40),
            Color = Color.FromArgb(255, 1, 2, 3),
            FillColor = Color.FromArgb(96, 4, 5, 6),
            Thickness = 7,
            Text = "synthetic text",
            Number = 8,
            SizeScale = 1.5,
            Redaction = RedactionLevel.Heavy,
            RedactStyle = RedactionStyle.Pixelate,
            ArrowStyle = ArrowStyle.Curved2,
            TextColor = Color.FromArgb(255, 9, 10, 11),
            FontSize = 42,
            FontFamily = "Arial",
            Bold = true,
            Italic = true,
            Underline = true,
            Strikethrough = true,
            Rotation = 45,
        };
        annotation.Points.Add(new Vector2(12, 34));
        var snapshot = annotation.CaptureSnapshot();
        var expected = new AnnotationSnapshot(EditTool.Text, new Rect(10, 20, 30, 40),
            Color.FromArgb(255, 1, 2, 3), Color.FromArgb(96, 4, 5, 6), 7, "synthetic text",
            8, 1.5, RedactionLevel.Heavy, RedactionStyle.Pixelate, ArrowStyle.Curved2,
            snapshot.Points, Color.FromArgb(255, 9, 10, 11), 42, "Arial", true, true, true, true, 45);
        Assert.Equal(expected, snapshot);

        annotation.Points[0] = Vector2.Zero;
        annotation.Points.Clear();
        annotation.Text = "changed";
        annotation.Bounds = new Rect(0, 0, 1, 1);
        annotation.Rotation = 0;
        annotation.Redaction = RedactionLevel.Light;
        annotation.FillColor = default;

        Assert.Equal(expected, snapshot);
        Assert.Equal(new Vector2(12, 34), Assert.Single(snapshot.Points));
        Assert.True(snapshot.IsRotated);
    }

    [Fact]
    public void RenderState_PreservesOrderingStylingAndCropDimensions()
    {
        var annotations = new List<ScreenshotAnnotation>
        {
            new() { Tool = EditTool.Rectangle },
            new() { Tool = EditTool.Redact },
            new() { Tool = EditTool.Emoji, Rotation = 90, Text = "synthetic sticker" },
        };
        var frame = ExportFrameLayout.Create(320, 180, 40, ExportFramePreset.Square,
            ExportHorizontalAlignment.Left, ExportVerticalAlignment.Bottom);
        var state = new ScreenshotRenderState(annotations.Select(a => a.CaptureSnapshot()).ToImmutableArray(),
            frame, ExportBackgroundStyle.Gradient, Color.FromArgb(255, 1, 2, 3),
            Color.FromArgb(255, 4, 5, 6), 20, 16, true, ScreenshotExportSize.Create(frame.FrameSize, 50));
        annotations.Reverse();
        annotations.Clear();

        Assert.Equal(new[] { EditTool.Rectangle, EditTool.Redact, EditTool.Emoji }, state.Annotations.Select(a => a.Tool));
        Assert.Equal(ExportBackgroundStyle.Gradient, state.BackgroundStyle);
        Assert.Equal(Color.FromArgb(255, 1, 2, 3), state.BackgroundColor);
        Assert.Equal(Color.FromArgb(255, 4, 5, 6), state.BackgroundColor2);
        Assert.Equal(20, state.CornerRadius);
        Assert.Equal(16, state.Shadow);
        Assert.Equal(200, state.OutputSize.Width);
        Assert.Equal(200, state.OutputSize.Height);
        var cropFrame = ExportFrameLayout.Create(320, 180, 0, ExportFramePreset.Original,
            ExportHorizontalAlignment.Center, ExportVerticalAlignment.Center);
        var cropSize = ScreenshotExportSize.Create(cropFrame.FrameSize, 100);
        Assert.Equal(320, cropSize.Width);
        Assert.Equal(180, cropSize.Height);
    }

    [Fact]
    public void SavingOlderRevision_DoesNotClearNewerEdits()
    {
        var document = new ScreenshotDocumentRevision();
        document.ReplaceDocument();
        document.MarkEdited();
        var savedRevision = document.Current;
        document.MarkEdited();

        Assert.False(document.IsCurrent(savedRevision, TestContext.Current.CancellationToken));
        Assert.True(document.IsSameDocument(savedRevision));
        Assert.False(document.MarkSaved(savedRevision));
        Assert.True(document.IsDirty);
        Assert.True(document.MarkSaved(document.Current));
        Assert.False(document.IsDirty);
    }

    [Fact]
    public void EditUndoAndReplacement_NeverReviveAnOldRevision()
    {
        var document = new ScreenshotDocumentRevision();
        document.ReplaceDocument();
        var original = document.Current;
        document.MarkEdited();
        var edited = document.Current;
        document.MarkEdited(); // Undo is itself a committed mutation.

        Assert.False(document.IsCurrent(original, TestContext.Current.CancellationToken));
        Assert.False(document.IsCurrent(edited, TestContext.Current.CancellationToken));
        Assert.False(document.MarkSaved(original));
        Assert.True(document.IsDirty);

        document.ReplaceDocument();
        Assert.False(document.IsSameDocument(edited));
        Assert.False(document.MarkSaved(edited));
        Assert.False(document.IsDirty);
        var replacement = document.Current;
        document.Close();
        Assert.False(document.IsCurrent(replacement, TestContext.Current.CancellationToken));
        Assert.False(document.IsSameDocument(replacement));
        Assert.False(document.MarkSaved(replacement));
        Assert.Throws<ObjectDisposedException>(() => document.MarkEdited());
    }

    [Fact]
    public void CanceledRequestAtCurrentRevision_CannotApplyItsSnapshot()
    {
        var document = new ScreenshotDocumentRevision();
        document.ReplaceDocument();
        document.MarkEdited();
        var revision = document.Current;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.True(document.IsCurrent(revision, TestContext.Current.CancellationToken));
        Assert.False(document.IsCurrent(revision, cancellation.Token));
        Assert.True(document.IsDirty);
    }

    [Fact]
    public void NewerRequestAtSameRevision_DoesNotReviveCanceledWork()
    {
        var document = new ScreenshotDocumentRevision();
        document.ReplaceDocument();
        var revision = document.Current;
        using var olderRequest = new CancellationTokenSource();
        using var newerRequest = new CancellationTokenSource();
        olderRequest.Cancel();

        Assert.False(document.IsCurrent(revision, olderRequest.Token));
        Assert.True(document.IsCurrent(revision, newerRequest.Token));
    }

    [Fact]
    public void SourceOwnership_DisposesExactlyOnceAfterLastLease()
    {
        var resource = new TrackedResource();
        var owner = new SharedResource<TrackedResource>(resource);
        var first = owner.Acquire();
        var second = owner.Acquire();

        owner.Dispose();
        owner.Dispose();
        Assert.Throws<ObjectDisposedException>(() => owner.Acquire());
        Assert.Same(resource, first.Value);
        Assert.Equal(0, resource.DisposeCount);
        first.Dispose();
        first.Dispose();
        Assert.Throws<ObjectDisposedException>(() => first.Value);
        Assert.Equal(0, resource.DisposeCount);
        second.Dispose();
        second.Dispose();
        Assert.Equal(1, resource.DisposeCount);
    }

    [Fact]
    public async Task LateWorker_AfterReplacementOrClosureKeepsItsLeaseButCannotPublish()
    {
        var document = new ScreenshotDocumentRevision();
        document.ReplaceDocument();
        var revision = document.Current;
        var resource = new TrackedResource();
        var owner = new SharedResource<TrackedResource>(resource);
        var lease = owner.Acquire();
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = Task.Run(async () =>
        {
            using (lease)
            {
                await resume.Task;
                Assert.Same(resource, lease.Value);
                Assert.Equal(0, resource.DisposeCount);
            }
        }, TestContext.Current.CancellationToken);

        document.ReplaceDocument();
        document.Close();
        owner.Dispose();
        Assert.False(document.IsCurrent(revision, TestContext.Current.CancellationToken));
        resume.SetResult();
        await worker;
        Assert.Equal(1, resource.DisposeCount);
    }

    private sealed class TrackedResource : IDisposable
    {
        private int _disposeCount;
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }
}
