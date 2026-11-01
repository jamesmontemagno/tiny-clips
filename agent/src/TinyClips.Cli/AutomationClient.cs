using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace TinyClips.Cli;

internal sealed class AutomationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Sends one newline-delimited JSON request to the running app (docs/agent-integration.md).</summary>
internal static class AutomationClient
{
    private static int _nextId;

    public static async Task<JsonNode> SendAsync(CliEnvironment env, string command, JsonObject? parameters = null, bool useToken = true, CancellationToken cancellationToken = default)
    {
        var request = new JsonObject
        {
            ["id"] = Interlocked.Increment(ref _nextId).ToString(),
            ["command"] = command,
            ["params"] = parameters?.DeepClone() ?? new JsonObject(),
        };
        if (useToken && AgentConfig.Token(env) is { } token) request["token"] = token;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(env.RequestTimeout);

        string? line;
        try
        {
            await using var stream = await ConnectAsync(env, timeout.Token);
            var bytes = Encoding.UTF8.GetBytes(request.ToJsonString() + "\n");
            await stream.WriteAsync(bytes, timeout.Token);
            await stream.FlushAsync(timeout.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            line = await reader.ReadLineAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AutomationException("timeout", $"Tiny Clips did not answer \"{command}\" in time.");
        }
        catch (Exception ex) when (ex is SocketException or IOException or TimeoutException or UnauthorizedAccessException)
        {
            throw new AutomationException("not_running", "Tiny Clips is not running, or agent control is turned off (Settings → Agent control).");
        }

        if (line is null) throw new AutomationException("connection", "Tiny Clips closed the connection.");

        JsonObject response;
        try
        {
            response = JsonNode.Parse(line)?.AsObject() ?? throw new FormatException();
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException or InvalidOperationException)
        {
            throw new AutomationException("protocol", "Tiny Clips sent an invalid response.");
        }

        if (response["ok"]?.GetValue<bool>() == true) return response["result"]?.DeepClone() ?? new JsonObject();
        var error = response["error"];
        throw new AutomationException(
            error?["code"]?.GetValue<string>() ?? "internal",
            error?["message"]?.GetValue<string>() ?? "Command failed.");
    }

    private static async Task<Stream> ConnectAsync(CliEnvironment env, CancellationToken cancellationToken)
    {
        Exception? lastFailure = null;
        foreach (var endpoint in env.EndpointCandidates())
        {
            try
            {
                return await ConnectEndpointAsync(endpoint, cancellationToken);
            }
            catch (Exception ex) when (ex is SocketException or IOException or TimeoutException or UnauthorizedAccessException)
            {
                lastFailure = ex;
            }
        }

        throw lastFailure ?? new IOException("No Tiny Clips automation endpoint was configured.");
    }

    private static async Task<Stream> ConnectEndpointAsync(string endpoint, CancellationToken cancellationToken)
    {
        const string pipePrefix = @"\\.\pipe\";
        if (endpoint.StartsWith(pipePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var pipe = new NamedPipeClientStream(".", endpoint[pipePrefix.Length..], PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(TimeSpan.FromSeconds(2), cancellationToken);
                return pipe;
            }
            catch
            {
                await pipe.DisposeAsync();
                throw;
            }
        }

        if (!File.Exists(endpoint)) throw new IOException("Socket not found.");
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint), cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
