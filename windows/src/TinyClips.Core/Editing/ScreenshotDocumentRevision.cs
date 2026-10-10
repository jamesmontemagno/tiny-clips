namespace TinyClips.Core.Editing;

public readonly record struct DocumentRevision(long Document, long Edit);

/// <summary>Owning-thread revision tracking for asynchronous editor work.</summary>
public sealed class ScreenshotDocumentRevision
{
    public DocumentRevision Current { get; private set; }
    public bool IsDirty { get; private set; }
    public bool IsClosed { get; private set; }

    public void MarkEdited()
    {
        ObjectDisposedException.ThrowIf(IsClosed, this);
        Current = Current with { Edit = checked(Current.Edit + 1) };
        IsDirty = true;
    }

    public void ReplaceDocument()
    {
        ObjectDisposedException.ThrowIf(IsClosed, this);
        Current = new DocumentRevision(checked(Current.Document + 1), checked(Current.Edit + 1));
        IsDirty = false;
    }

    public bool IsCurrent(DocumentRevision revision, CancellationToken cancellationToken = default) =>
        !IsClosed && !cancellationToken.IsCancellationRequested && Current == revision;

    public bool IsSameDocument(DocumentRevision revision) => !IsClosed && Current.Document == revision.Document;

    public bool MarkSaved(DocumentRevision revision)
    {
        if (!IsCurrent(revision)) return false;
        IsDirty = false;
        return true;
    }

    public void Close() => IsClosed = true;
}
