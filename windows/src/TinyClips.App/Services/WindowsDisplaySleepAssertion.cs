using System.Diagnostics;
using TinyClips.Core.Services;
using Windows.System.Display;

namespace TinyClips.App;

public sealed class WindowsDisplaySleepAssertion : IDisplaySleepAssertion
{
    private readonly DisplayRequest _displayRequest = new();
    private bool _active;

    public void Acquire()
    {
        if (_active)
        {
            return;
        }

        _displayRequest.RequestActive();
        _active = true;
    }

    public void Release()
    {
        if (!_active)
        {
            return;
        }

        try
        {
            _displayRequest.RequestRelease();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to release display sleep assertion: {ex}");
        }
        finally
        {
            _active = false;
        }
    }
}
