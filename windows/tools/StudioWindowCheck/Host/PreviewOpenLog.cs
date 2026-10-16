using System.Diagnostics;
using System.Globalization;
using System.Text;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Preview;

namespace TinyClips.Tools.StudioWindowCheck.Host;

/// <summary>
/// What the preview engines of the tool's windows said while they worked, and what every open
/// that failed failed with. A window that cannot open its project shows one sentence; this keeps
/// what is behind it: the exceptions with their error codes, and the engine's own trace of the
/// seconds before. Any thread.
/// </summary>
internal sealed class PreviewOpenLog
{
    // About a minute of two previews playing, and far more of anything else.
    private const int Capacity = 6000;

    private readonly object _gate = new();
    private readonly Queue<(double At, string Text)> _lines = new();
    private readonly List<(double At, string Text)> _failures = [];
    private readonly long _startedAt = Stopwatch.GetTimestamp();

    /// <summary>How many opens have failed so far.</summary>
    public int FailureCount
    {
        get
        {
            lock (_gate)
            {
                return _failures.Count;
            }
        }
    }

    /// <summary>One line of an engine's trace. The engines of all windows write here, each with its own clock in front.</summary>
    public void Trace(string line)
    {
        var at = Stopwatch.GetElapsedTime(_startedAt).TotalSeconds;
        lock (_gate)
        {
            if (_lines.Count >= Capacity)
            {
                _lines.Dequeue();
            }

            _lines.Enqueue((at, line));
        }
    }

    /// <summary>An open failed. Keeps the exception and what caused it, each with its error code.</summary>
    public void Failed(Exception failure)
    {
        var at = Stopwatch.GetElapsedTime(_startedAt).TotalSeconds;
        var text = Describe(failure);
        lock (_gate)
        {
            _failures.Add((at, text));
        }
    }

    /// <summary>The exception of the open that failed last and what caused it, in one line. Empty when none has failed.</summary>
    public string LastFailure()
    {
        lock (_gate)
        {
            return _failures.Count == 0 ? string.Empty : _failures[^1].Text;
        }
    }

    /// <summary>
    /// Writes the open that failed last, and the trace of the <paramref name="seconds"/> before
    /// now, to a file. Returns its path, or null when no open has failed.
    /// </summary>
    public string? Write(string directory, string stamp, double seconds = 8)
    {
        (double At, string Text)[] lines;
        (double At, string Text)[] failures;
        lock (_gate)
        {
            if (_failures.Count == 0)
            {
                return null;
            }

            lines = [.. _lines];
            failures = [.. _failures];
        }

        var now = Stopwatch.GetElapsedTime(_startedAt).TotalSeconds;
        var text = new StringBuilder();
        text.AppendLine("Opens of a preview that failed, in seconds since the tool's windows could first be opened:");
        foreach (var (at, what) in failures)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"{at,9:0.000}  {what}");
        }

        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"The engines' trace of the {seconds:0} s before {now:0.000}. The second number is each engine's own clock, in ms since it was created:");
        foreach (var (at, what) in lines)
        {
            if (at >= now - seconds)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"{at,9:0.000}  {what}");
            }
        }

        var path = Path.Combine(directory, string.Create(CultureInfo.InvariantCulture, $"open-failure-{stamp}-{failures.Length}.txt"));
        File.WriteAllText(path, text.ToString());
        return path;
    }

    private static string Describe(Exception failure)
    {
        var text = new StringBuilder();
        for (var current = failure; current is not null; current = current.InnerException)
        {
            if (text.Length > 0)
            {
                text.Append(" <- ");
            }

            text.Append(CultureInfo.InvariantCulture, $"{current.GetType().Name} 0x{current.HResult:X8}: {current.Message.ReplaceLineEndings(" ")}");
        }

        return text.ToString();
    }
}

/// <summary>
/// The app's preview factory, with every open that fails written down before the window is told.
/// What it returns is what the app's factory returned, except for a project a check has asked to
/// have watched: its preview comes back inside a <see cref="WatchedPreview"/>.
/// </summary>
internal sealed class RecordingPreviewFactory(IStudioPreviewFactory inner, PreviewOpenLog log, PlaybackRateLog rates) : IStudioPreviewFactory
{
    public async Task<IStudioPreview> OpenAsync(
        StudioProject project,
        StudioEvents events,
        StudioProjectPaths paths,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var preview = await inner.OpenAsync(project, events, paths, cancellationToken).ConfigureAwait(false);
            return rates.IsWatched(paths.ProjectId) ? new WatchedPreview(preview, rates, paths.ProjectId) : preview;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Failed(ex);
            throw;
        }
    }
}
