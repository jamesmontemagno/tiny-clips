namespace TinyClips.App;

/// <summary>
/// Owner-thread refresh with one background reader and one pending request. A newer request
/// invalidates an in-flight result; disposal also suppresses late publication.
/// </summary>
internal sealed class CoalescedRefresh<T>(Func<T> read, Action<T> publish, Action<Exception> failed) : IDisposable
{
    private bool _isReading;
    private bool _isPending;
    private bool _isDisposed;

    public void Request(bool invalidate = true)
    {
        if (_isDisposed)
        {
            return;
        }

        // A periodic probe does not make an already-running read stale. Otherwise a slow
        // external drive taking longer than the polling interval could starve publication.
        if (_isReading && !invalidate)
        {
            return;
        }

        _isPending = true;
        if (!_isReading)
        {
            _ = RunAsync();
        }
    }

    private async Task RunAsync()
    {
        _isReading = true;
        try
        {
            while (_isPending && !_isDisposed)
            {
                _isPending = false;
                try
                {
                    var result = await Task.Run(read);
                    if (!_isDisposed && !_isPending)
                    {
                        publish(result);
                    }
                }
                catch (Exception ex)
                {
                    if (!_isDisposed)
                    {
                        failed(ex);
                    }
                }
            }
        }
        finally
        {
            _isReading = false;
        }
    }

    public void Dispose()
    {
        _isDisposed = true;
        _isPending = false;
    }
}
