namespace TinyClips.Core.Editing;

/// <summary>
/// Retains an immutable disposable resource across owner replacement/closure. The owner and
/// each lease release one reference; the last release disposes the resource exactly once.
/// </summary>
public sealed class SharedResource<T> : IDisposable where T : class, IDisposable
{
    private readonly object _sync = new();
    private readonly T _value;
    private int _references = 1;
    private bool _ownerReleased;

    public SharedResource(T value) => _value = value ?? throw new ArgumentNullException(nameof(value));

    public Lease Acquire()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_ownerReleased, this);
            _references = checked(_references + 1);
            return new Lease(this, _value);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_ownerReleased) return;
            _ownerReleased = true;
        }
        Release();
    }

    private void Release()
    {
        bool dispose;
        lock (_sync)
        {
            dispose = --_references == 0;
        }
        if (dispose) _value.Dispose();
    }

    public sealed class Lease : IDisposable
    {
        private SharedResource<T>? _owner;
        private readonly T _value;

        internal Lease(SharedResource<T> owner, T value)
        {
            _owner = owner;
            _value = value;
        }

        public T Value
        {
            get
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _owner) is null, this);
                return _value;
            }
        }

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
