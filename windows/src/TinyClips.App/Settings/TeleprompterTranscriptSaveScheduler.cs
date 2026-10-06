using Microsoft.UI.Dispatching;

namespace TinyClips.App.Settings;

internal interface ITeleprompterTranscriptSaveScheduler
{
    void Restart(Action save);
    void Stop();
}

internal sealed class DispatcherTranscriptSaveScheduler : ITeleprompterTranscriptSaveScheduler
{
    private readonly DispatcherQueueTimer _timer;
    private Action? _save;

    public DispatcherTranscriptSaveScheduler(DispatcherQueue dispatcher)
    {
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(500);
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) =>
        {
            var save = _save;
            _save = null;
            save?.Invoke();
        };
    }

    public void Restart(Action save)
    {
        _timer.Stop();
        _save = save;
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
        _save = null;
    }
}
