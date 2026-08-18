namespace TinyClips.Core.Services;

public interface IDisplaySleepAssertion
{
    void Acquire();

    void Release();
}
