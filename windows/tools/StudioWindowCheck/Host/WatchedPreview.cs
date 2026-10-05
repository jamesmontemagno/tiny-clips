using System.Diagnostics;
using Microsoft.UI.Xaml;
using TinyClips.App.Services.Studio;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Preview;

namespace TinyClips.Tools.StudioWindowCheck.Host;

/// <summary>One time an editor told its preview how fast to play.</summary>
/// <param name="At">A Stopwatch timestamp.</param>
/// <param name="Rate">The rate that was asked for: seconds of the recording in one second.</param>
/// <param name="Position">Where the preview said it was when it was asked, in source time.</param>
/// <param name="WasPlaying">Whether the preview was playing when it was asked.</param>
internal readonly record struct PlaybackRateRequest(long At, double Rate, double Position, bool WasPlaying);

/// <summary>
/// What the editors of the projects a check asked to have watched told their previews about the
/// speed of playback. A check names a project before it opens it; nothing of a window or of a
/// preview is kept here, only numbers by the project's id. Any thread.
/// </summary>
internal sealed class PlaybackRateLog
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<PlaybackRateRequest>> _requests = new(StringComparer.Ordinal);

    /// <summary>Has the preview of a project watched, from the next time the project is opened.</summary>
    public void Watch(string projectId)
    {
        lock (_gate)
        {
            _requests.TryAdd(projectId, []);
        }
    }

    public bool IsWatched(string projectId)
    {
        lock (_gate)
        {
            return _requests.ContainsKey(projectId);
        }
    }

    /// <summary>How many requests a project's preview has been sent so far: a mark to read on from.</summary>
    public int Count(string projectId)
    {
        lock (_gate)
        {
            return _requests.TryGetValue(projectId, out var list) ? list.Count : 0;
        }
    }

    /// <summary>The requests a project's preview was sent after a mark, in order.</summary>
    public PlaybackRateRequest[] Since(string projectId, int mark = 0)
    {
        lock (_gate)
        {
            return _requests.TryGetValue(projectId, out var list) && list.Count > mark ? [.. list.Skip(mark)] : [];
        }
    }

    public void Add(string projectId, PlaybackRateRequest request)
    {
        lock (_gate)
        {
            if (_requests.TryGetValue(projectId, out var list))
            {
                list.Add(request);
            }
        }
    }
}

/// <summary>
/// A preview as the editor sees it, with every request for a playback rate written down before
/// it is passed on. Everything else goes straight to the preview it stands in front of, and the
/// events are that preview's own.
/// </summary>
/// <remarks>
/// The preview engine does not play a rate yet, and says nothing of one in its trace, so what
/// the editor asks for can only be seen from in front of it. Only the projects a check names are
/// watched: every other window of the tool has the engine itself, as the app has.
/// </remarks>
internal sealed class WatchedPreview(IStudioPreview inner, PlaybackRateLog log, string projectId) : IStudioPreview
{
    public event EventHandler? PositionChanged
    {
        add => inner.PositionChanged += value;
        remove => inner.PositionChanged -= value;
    }

    public event EventHandler? IsPlayingChanged
    {
        add => inner.IsPlayingChanged += value;
        remove => inner.IsPlayingChanged -= value;
    }

    public event EventHandler<StudioPreviewFailedEventArgs>? Failed
    {
        add => inner.Failed += value;
        remove => inner.Failed -= value;
    }

    /// <summary>The preview the app's factory opened.</summary>
    public IStudioPreview Inner => inner;

    public double Position => inner.Position;

    public bool IsPlaying => inner.IsPlaying;

    public void UpdateProject(StudioProject project) => inner.UpdateProject(project);

    public void Play() => inner.Play();

    public void Pause() => inner.Pause();

    public void Seek(double sourceTime) => inner.Seek(sourceTime);

    public void SetPlaybackRate(double rate)
    {
        log.Add(projectId, new PlaybackRateRequest(Stopwatch.GetTimestamp(), rate, inner.Position, inner.IsPlaying));
        inner.SetPlaybackRate(rate);
    }

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}

/// <summary>
/// The app's preview view factory, handed the engine itself where the tool stands in front of
/// it: the app's factory shows a preview engine and nothing else.
/// </summary>
internal sealed class WatchedPreviewViewFactory(IStudioPreviewViewFactory inner) : IStudioPreviewViewFactory
{
    public FrameworkElement Create(IStudioPreview preview) =>
        inner.Create(preview is WatchedPreview watched ? watched.Inner : preview);
}
