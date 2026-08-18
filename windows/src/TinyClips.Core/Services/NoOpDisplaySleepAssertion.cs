namespace TinyClips.Core.Services;

public sealed class NoOpDisplaySleepAssertion : IDisplaySleepAssertion
{
    public void Acquire()
    {
    }

    public void Release()
    {
    }
}
