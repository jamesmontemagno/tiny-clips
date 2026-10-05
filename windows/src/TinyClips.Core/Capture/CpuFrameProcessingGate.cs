namespace TinyClips.Core.Capture;

/// <summary>Serializes synchronous borrowed-frame consumers and waits for their return at stop.</summary>
internal sealed class CpuFrameProcessingGate
{
    private readonly object _sync = new();
    private volatile bool _running;

    public void Start()
    {
        lock (_sync)
        {
            _running = true;
        }
    }

    public bool TryProcess(Action process)
    {
        if (!Monitor.TryEnter(_sync))
        {
            return false;
        }

        try
        {
            if (!_running)
            {
                return false;
            }

            process();
            return true;
        }
        finally
        {
            Monitor.Exit(_sync);
        }
    }

    public void Stop()
    {
        _running = false;
        lock (_sync)
        {
            // The borrowed frame cannot be overwritten or cleared until its consumer returns.
        }
    }
}
