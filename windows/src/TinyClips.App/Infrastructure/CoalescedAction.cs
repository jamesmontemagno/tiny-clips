namespace TinyClips.App;

/// <summary>Coalesces owner-thread notifications; disposal suppresses queued callbacks.</summary>
internal sealed class CoalescedAction(Func<Action, bool> enqueue, Action action) : IDisposable
{
    private Action? _action = action;
    private bool _queued;

    public bool Request()
    {
        if (_action is null || _queued)
        {
            return true;
        }

        _queued = true;
        if (enqueue(Run))
        {
            return true;
        }

        _queued = false;
        return false;
    }

    private void Run()
    {
        _queued = false;
        _action?.Invoke();
    }

    public void Dispose() => _action = null;
}
