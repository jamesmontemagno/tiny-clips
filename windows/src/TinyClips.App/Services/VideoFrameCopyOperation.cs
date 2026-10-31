namespace TinyClips.App;

/// <summary>UI-thread-owned copy lifetime, independent of the native frame and clipboard adapters.</summary>
internal sealed class VideoFrameCopyOperation
{
    private CancellationTokenSource? _cancellation;
    private bool _closed;

    public bool IsBusy => _cancellation is not null;
    public Task WhenIdle { get; private set; } = Task.CompletedTask;

    public async Task<bool> CopyAsync<TFrame, TImage>(
        TimeSpan position,
        Func<TimeSpan, Task<TFrame>> capture,
        Func<TFrame, CancellationToken, Task<TImage>> encode,
        Action<TImage> publish)
        where TFrame : IDisposable
        where TImage : IDisposable
    {
        if (_closed || IsBusy)
        {
            return false;
        }

        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        WhenIdle = idle.Task;
        var token = cancellation.Token;
        try
        {
            using var frame = await capture(position);
            token.ThrowIfCancellationRequested();
            using var image = await encode(frame, token);
            // Native extraction may not be cancellable. Never publish after close or deletion,
            // even when an adapter completes successfully after cancellation.
            token.ThrowIfCancellationRequested();
            publish(image);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            _cancellation = null;
            idle.SetResult();
        }
    }

    public void Cancel() => _cancellation?.Cancel();

    public void Close()
    {
        _closed = true;
        Cancel();
    }
}
