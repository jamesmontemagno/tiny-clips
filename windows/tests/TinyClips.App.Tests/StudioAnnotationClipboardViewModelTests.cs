using TinyClips.App.Services.Studio;
using TinyClips.App.ViewModels.Studio;
using TinyClips.Core.Studio;

namespace TinyClips.App.Tests;

public sealed class StudioAnnotationClipboardViewModelTests : StudioViewModelTestBase
{
    private static byte[] Payload() => StudioAnnotationClipboard.Encode(new(
        new StudioAnnotation { Id = "source", Kind = "arrow", Start = 1, End = 3, X = 0.25, Y = 0.75 }, 2));

    [Fact]
    public async Task PasteWithoutSelection_AppendsSelectsAndAnnouncesOnlyAfterCommit()
    {
        var model = await OpenAsync(CreateProject(camera: false));
        var clipboard = new TestClipboard { Payload = Payload() };
        model.AttachAnnotationClipboard(clipboard);
        Assert.False(model.HasSelectedAnnotation);
        Assert.False(model.CanCopyAnnotation);
        Assert.True(model.CanPasteAnnotation);
        model.Scrub(4.015);
        await model.PasteAnnotationAsync();
        Assert.Equal((4d, 6d, 0.25, 0.75), (model.Annotations[0].Start, model.Annotations[0].End, model.Annotations[0].X, model.Annotations[0].Y));
        Assert.Equal(0, model.SelectedAnnotationIndex);
        Assert.Equal("Annotation pasted.", model.AnnotationClipboardStatus);
        Assert.Contains("Annotation pasted.", Announcements);
        Assert.True(model.CanCopyAnnotation);
        await model.CopyAnnotationAsync();
        Assert.NotNull(clipboard.Written);
        Assert.Equal("Annotation copied.", model.AnnotationClipboardStatus);
        var firstId = model.Annotations[0].Id;
        await model.PasteAnnotationAsync();
        Assert.Equal(2, model.Annotations.Count);
        Assert.NotEqual(firstId, model.Annotations[1].Id);
        model.Undo();
        Assert.Single(model.Annotations);
        model.Redo();
        Assert.Equal(2, model.Annotations.Count);
        await CloseAsync(model);
    }

    [Theory]
    [InlineData("playhead")]
    [InlineData("edit")]
    [InlineData("drag")]
    [InlineData("window")]
    [InlineData("closed")]
    public async Task ExternalAsyncReadCannotCommitToStaleDestination(string change)
    {
        var model = await OpenAsync(CreateProject(camera: false));
        var pending = new TaskCompletionSource<byte[]?>();
        var clipboard = new TestClipboard { Pending = pending };
        model.AttachAnnotationClipboard(clipboard);
        var paste = model.PasteAnnotationAsync();
        Assert.False(paste.IsCompleted);
        Assert.False(model.CanPasteAnnotation);
        switch (change)
        {
            case "playhead": model.Scrub(3); model.Scrub(0); break;
            case "edit": model.AddArrowAtPlayhead(); model.Undo(); break;
            case "drag": model.BeginGesture(); break;
            case "window": model.CanUseWindowClipboard = () => false; break;
            case "closed": await CloseAsync(model); break;
        }
        pending.SetResult(Payload());
        await paste;
        Assert.Empty(model.Annotations);
        Assert.DoesNotContain(Announcements, message => message.StartsWith("Annotation pasted", StringComparison.Ordinal));
        if (change != "closed")
        {
            Assert.Contains("destination changed", model.AnnotationClipboardStatus);
            if (change == "drag") model.EndGesture();
            await CloseAsync(model);
        }
    }

    [Fact]
    public async Task ClipboardReadWriteFailuresAndInvalidDataNeverAnnounceSuccess()
    {
        var model = await OpenAsync(CreateProject(camera: false));
        var clipboard = new TestClipboard { ReadFailure = new IOException("Clipboard is locked.") };
        model.AttachAnnotationClipboard(clipboard);
        await model.PasteAnnotationAsync();
        Assert.Contains("Clipboard is locked", model.ErrorMessage);
        Assert.Empty(model.Annotations);
        clipboard.ReadFailure = null;
        clipboard.Payload = [0xff];
        await model.PasteAnnotationAsync();
        Assert.Contains("Could not paste", model.ErrorMessage);
        Assert.Empty(model.Annotations);
        model.AddArrowAtPlayhead();
        clipboard.WriteFailure = new IOException("Clipboard is locked.");
        await model.CopyAnnotationAsync();
        Assert.Contains("Could not copy", model.ErrorMessage);
        Assert.DoesNotContain(Announcements, message => message is "Annotation copied." or "Annotation pasted.");
        await CloseAsync(model);
    }

    [Fact]
    public async Task NoRoomDoesNotClaimACommittedPaste()
    {
        var model = await OpenAsync(CreateProject(camera: false));
        model.AttachAnnotationClipboard(new TestClipboard { Payload = Payload() });
        model.Scrub(10);
        await model.PasteAnnotationAsync();
        Assert.Empty(model.Annotations);
        Assert.Equal("There is no room for an annotation here.", model.AnnotationClipboardStatus);
        Assert.DoesNotContain("Annotation pasted.", Announcements);
        await CloseAsync(model);
    }

    [Fact]
    public async Task ScreenAttachmentIsPreservedOnCameraOnlyDestinationWithExistingVisibilityFeedback()
    {
        var model = await OpenAsync(CreateProject(project => project with
        {
            Scenes = [new StudioScene { Start = 0, Layout = StudioLayout.Camera }],
        }));
        model.AttachAnnotationClipboard(new TestClipboard { Payload = Payload() });
        await model.PasteAnnotationAsync();
        Assert.Equal(StudioAnnotationAttach.Screen, Assert.Single(model.Annotations).Attach);
        Assert.True(model.IsAnnotationNotDrawn);
        Assert.Contains("not drawn", model.AnnotationClipboardStatus);
        await CloseAsync(model);
    }

    [Fact]
    public async Task AvailabilityTracksContentChangesAndDisposesObservers()
    {
        var model = await OpenAsync(CreateProject(camera: false));
        var clipboard = new TestClipboard { HasAnnotation = false };
        model.AttachAnnotationClipboard(clipboard);
        Assert.False(model.CanPasteAnnotation);
        clipboard.HasAnnotation = true;
        clipboard.Notify();
        Pump();
        Assert.True(model.CanPasteAnnotation);
        clipboard.HasAnnotation = false;
        clipboard.Notify();
        Pump();
        Assert.False(model.CanPasteAnnotation);
        await CloseAsync(model);
        Assert.True(clipboard.IsDisposed);
        Assert.Equal(0, clipboard.SubscriberCount);
        clipboard.Notify();
        Pump();
        Assert.False(model.CanPasteAnnotation);
    }

    private sealed class TestClipboard : IStudioAnnotationClipboardService
    {
        public event EventHandler? ContentChanged;
        public bool HasAnnotation { get; set; } = true;
        public bool IsDisposed { get; private set; }
        public int SubscriberCount => ContentChanged?.GetInvocationList().Length ?? 0;
        public byte[]? Payload { get; set; }
        public byte[]? Written { get; private set; }
        public TaskCompletionSource<byte[]?>? Pending { get; init; }
        public Exception? ReadFailure { get; set; }
        public Exception? WriteFailure { get; set; }

        public void Notify() => ContentChanged?.Invoke(this, EventArgs.Empty);
        public Task<byte[]?> ReadAsync() => ReadFailure is { } failure ? Task.FromException<byte[]?>(failure)
            : Pending?.Task ?? Task.FromResult(Payload);
        public Task WriteAsync(byte[] payload)
        {
            if (WriteFailure is { } failure) return Task.FromException(failure);
            Written = payload;
            return Task.CompletedTask;
        }
        public void Dispose() => IsDisposed = true;
    }
}
