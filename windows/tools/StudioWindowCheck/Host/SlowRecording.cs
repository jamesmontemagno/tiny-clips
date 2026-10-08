using System.IO.Pipes;

namespace TinyClips.Tools.StudioWindowCheck.Host;

/// <summary>
/// A screen recording that takes as long to read as a check wants. It is a pipe: whoever opens
/// it as a file is handed the first half of a real recording's bytes, and the rest, and the end
/// of the file, only when the check says so. The store hands its path out in the place of a
/// project's screen recording (<see cref="GuardedStore.SendNextRecordingFrom"/>), so that the
/// copy the editor makes of the recording is under way for as long as the check needs to do
/// something in the middle of it. Nothing about the pipe is to be seen on the screen.
/// </summary>
internal sealed class SlowRecording : IDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly byte[] _bytes;
    private readonly Task _firstHalf;
    private int _finished;

    /// <param name="recordingPath">The recording whose bytes are handed out.</param>
    public SlowRecording(string recordingPath)
    {
        _bytes = File.ReadAllBytes(recordingPath);
        var name = $"StudioWindowCheck-{Environment.ProcessId}-{Guid.NewGuid():N}";
        Path = $@"\\.\pipe\{name}";

        // One reader, this user's only. Nothing is kept for the reader in advance, so a write
        // is over when the reader has taken what was written.
        _pipe = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        _firstHalf = HandOutFirstHalfAsync();
        Quietly(_firstHalf);
    }

    /// <summary>What to open to read the recording.</summary>
    public string Path { get; }

    /// <summary>How many bytes the recording has.</summary>
    public int Length => _bytes.Length;

    /// <summary>
    /// Waits until a reader has opened the recording and taken the first half of it. From then
    /// on the reader waits for the rest. False when no reader did that in time.
    /// </summary>
    public bool WaitUntilHalfIsRead(double seconds) => Wait(_firstHalf, seconds);

    /// <summary>
    /// Hands out the rest of the recording, and then its end. False when the reader did not
    /// take all of it in time.
    /// </summary>
    public bool Finish(double seconds)
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0)
        {
            return false;
        }

        var rest = Task.Run(async () =>
        {
            await _firstHalf.ConfigureAwait(false);
            await _pipe.WriteAsync(_bytes.AsMemory(_bytes.Length / 2)).ConfigureAwait(false);

            // Until the reader has taken everything: closing the pipe before that could lose the last of it.
            _pipe.WaitForPipeDrain();
        });
        Quietly(rest);
        var taken = Wait(rest, seconds);

        // The end of the file, for the reader.
        _pipe.Dispose();
        return taken;
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _finished, 1);
        _pipe.Dispose();
    }

    private async Task HandOutFirstHalfAsync()
    {
        await _pipe.WaitForConnectionAsync().ConfigureAwait(false);
        await _pipe.WriteAsync(_bytes.AsMemory(0, _bytes.Length / 2)).ConfigureAwait(false);
    }

    private static bool Wait(Task task, double seconds)
    {
        try
        {
            return task.Wait(TimeSpan.FromSeconds(seconds)) && task.IsCompletedSuccessfully;
        }
        catch (AggregateException)
        {
            return false;
        }
    }

    // A pipe that is closed while something waits on it ends that with an exception, which is nobody's to see.
    private static void Quietly(Task task) => task.ContinueWith(
        static failed => { _ = failed.Exception; },
        CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);
}
