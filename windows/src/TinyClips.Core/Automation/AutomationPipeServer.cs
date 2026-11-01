using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace TinyClips.Core.Automation;

/// <summary>
/// Serves the automation protocol on <c>\\.\pipe\TinyClips.Automation.&lt;UserName&gt;</c>, readable
/// and writable by the current user only. Each connection carries newline-delimited JSON requests
/// and gets responses back in order; several connections are served at once.
/// </summary>
public sealed class AutomationPipeServer : IAsyncDisposable
{
    public const string PipeNamePrefix = "TinyClips.Automation.";

    /// <summary>A request line longer than this closes the connection.</summary>
    public const int MaxLineBytes = 1024 * 1024;

    private const int MaxConnections = 16;

    private readonly Func<string, CancellationToken, Task<string>> _handleLine;
    private readonly object _gate = new();
    private readonly HashSet<Task> _connections = [];
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public AutomationPipeServer(AutomationHost host, string? pipeName = null)
        : this(host.HandleLineAsync, pipeName)
    {
    }

    public AutomationPipeServer(Func<string, CancellationToken, Task<string>> handleLine, string? pipeName = null)
    {
        _handleLine = handleLine ?? throw new ArgumentNullException(nameof(handleLine));
        PipeName = pipeName ?? DefaultPipeName;
    }

    public static string DefaultPipeName => PipeNamePrefix + Environment.UserName;

    public string PipeName { get; }

    /// <summary>The endpoint as users and the CLI write it.</summary>
    public string Endpoint => @"\\.\pipe\" + PipeName;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _acceptLoop is { IsCompleted: false };
            }
        }
    }

    /// <summary>Raised (on a worker thread) when the server stops because the pipe could not be created.</summary>
    public event EventHandler<Exception>? Faulted;

    /// <summary>
    /// Creates the first pipe instance synchronously, so a name that is already taken (another
    /// Tiny Clips, or another process squatting on it) fails here rather than silently.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_acceptLoop is { IsCompleted: false })
            {
                return;
            }

            var first = CreatePipe(firstInstance: true);
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _acceptLoop = Task.Run(() => AcceptLoopAsync(first, token), CancellationToken.None);
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? loop;
        Task[] connections;
        lock (_gate)
        {
            cts = _cts;
            loop = _acceptLoop;
            connections = _connections.ToArray();
            _cts = null;
            _acceptLoop = null;
        }

        if (cts is null)
        {
            return;
        }

        await cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(connections.Append(loop ?? Task.CompletedTask)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
        finally
        {
            cts.Dispose();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private NamedPipeServerStream CreatePipe(bool firstInstance)
    {
        var options = PipeOptions.Asynchronous | (firstInstance ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        return NamedPipeServerStreamAcl.Create(
            PipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            options,
            inBufferSize: 0,
            outBufferSize: 0,
            CreateSecurity());
    }

    private static PipeSecurity CreateSecurity()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("The current user has no SID.");
        var security = new PipeSecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    private async Task AcceptLoopAsync(NamedPipeServerStream first, CancellationToken cancellationToken)
    {
        NamedPipeServerStream? pipe = first;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var connected = pipe;
                pipe = null;

                Task connection;
                lock (_gate)
                {
                    if (_connections.Count >= MaxConnections)
                    {
                        connected.Dispose();
                        connection = Task.CompletedTask;
                    }
                    else
                    {
                        connection = ServeAsync(connected, cancellationToken);
                        _connections.Add(connection);
                    }
                }

                _ = connection.ContinueWith(
                    t =>
                    {
                        lock (_gate)
                        {
                            _connections.Remove(t);
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);

                pipe = CreatePipe(firstInstance: false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AutomationPipeServer: accept loop stopped: {ex}");
            Faulted?.Invoke(this, ex);
        }
        finally
        {
            pipe?.Dispose();
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await Task.Yield();
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        try
        {
            await using (pipe.ConfigureAwait(false))
            {
                var reader = new LineReader(pipe, MaxLineBytes);
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line is null)
                    {
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    var response = await _handleLine(line, cancellationToken).ConfigureAwait(false);
                    var bytes = encoding.GetBytes(response + "\n");
                    await pipe.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or InvalidDataException)
        {
            // The client went away, the server is stopping, or the client sent an oversized line.
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AutomationPipeServer: connection failed: {ex}");
        }
    }

    /// <summary>Reads UTF-8 lines terminated by <c>\n</c> (a trailing <c>\r</c> is dropped), with a length cap.</summary>
    internal sealed class LineReader(Stream stream, int maxLineBytes)
    {
        private readonly byte[] _buffer = new byte[8192];
        private readonly MemoryStream _line = new();
        private int _start;
        private int _end;

        public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                var newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
                if (newline >= 0)
                {
                    _line.Write(_buffer, _start, newline - _start);
                    _start = newline + 1;
                    return TakeLine();
                }

                _line.Write(_buffer, _start, _end - _start);
                _start = _end = 0;
                if (_line.Length > maxLineBytes)
                {
                    throw new InvalidDataException("The request line is too long.");
                }

                var read = await stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return _line.Length > 0 ? TakeLine() : null;
                }

                _end = read;
            }
        }

        private string TakeLine()
        {
            if (_line.Length > maxLineBytes)
            {
                throw new InvalidDataException("The request line is too long.");
            }

            var text = Encoding.UTF8.GetString(_line.GetBuffer(), 0, (int)_line.Length);
            _line.SetLength(0);
            return text.EndsWith('\r') ? text[..^1] : text;
        }
    }
}
