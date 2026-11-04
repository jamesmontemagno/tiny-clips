using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
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
        timeout.CancelAfter(env.RequestTimeoutFor(command));

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
        var candidates = env.EndpointCandidates(out bool linuxDiscovery);
        foreach (var endpoint in candidates)
        {
            try
            {
                return await ConnectEndpointAsync(endpoint, linuxDiscovery, cancellationToken);
            }
            catch (Exception ex) when (ex is SocketException or IOException or TimeoutException or UnauthorizedAccessException)
            {
                lastFailure = ex;
            }
        }

        throw lastFailure ?? new IOException("No Tiny Clips automation endpoint was configured.");
    }

    private static async Task<Stream> ConnectEndpointAsync(string endpoint, bool linuxDiscovery, CancellationToken cancellationToken)
    {
        const string pipePrefix = @"\\.\pipe\";
        if (endpoint.StartsWith(pipePrefix, StringComparison.OrdinalIgnoreCase))
        {
            // WindowsIdentity.Owner can be a group SID; the app explicitly assigns the pipe
            // to WindowsIdentity.User, so verify that same account before writing a token.
            var options = PipeOptions.Asynchronous;
            if (!OperatingSystem.IsWindows()) options |= PipeOptions.CurrentUserOnly;
            var pipe = new NamedPipeClientStream(".", endpoint[pipePrefix.Length..], PipeDirection.InOut,
                options);
            try
            {
                await pipe.ConnectAsync(TimeSpan.FromSeconds(2), cancellationToken);
                if (OperatingSystem.IsWindows()) ValidatePipeOwner(pipe);
                return pipe;
            }
            catch
            {
                await pipe.DisposeAsync();
                throw;
            }
        }

        if (!File.Exists(endpoint)) throw new IOException("Socket not found.");
        if (linuxDiscovery && OperatingSystem.IsLinux()) ValidateLinuxEndpoint(endpoint);
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint), cancellationToken);
            // Check the actual peer after connect, before writing any pairing token.
            if (linuxDiscovery && OperatingSystem.IsLinux()) ValidateLinuxPeer(socket);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    [SupportedOSPlatform("linux")]
    private static void ValidateLinuxEndpoint(string path)
    {
        const UnixFileMode privateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        const UnixFileMode privateDirectory = privateFile | UnixFileMode.UserExecute;
        var file = new FileInfo(path);
        var app = file.Directory!;
        var runtime = app.Parent!.Parent!;
        if (file.UnixFileMode != privateFile || file.LinkTarget is not null
            || app.UnixFileMode != privateDirectory || runtime.UnixFileMode != privateDirectory)
            throw new UnauthorizedAccessException("Unsafe Linux automation endpoint permissions.");
        for (var dir = app; dir is not null; dir = dir.Parent)
            if (dir.LinkTarget is not null) throw new UnauthorizedAccessException("Unsafe Linux automation endpoint path.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxPeerCredentials { public int Pid; public uint Uid; public uint Gid; }

    [DllImport("libc", EntryPoint = "getsockopt")]
    private static extern int GetPeerCredentials(SafeSocketHandle fd, int level, int option,
        out LinuxPeerCredentials credentials, ref uint size);

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    [SupportedOSPlatform("linux")]
    private static void ValidateLinuxPeer(Socket socket)
    {
        const int solSocket = 1, soPeerCred = 17; // Linux socket ABI (x64/arm64).
        uint size = (uint)Marshal.SizeOf<LinuxPeerCredentials>();
        if (GetPeerCredentials(socket.SafeHandle, solSocket, soPeerCred, out var peer, ref size) != 0
            || size != Marshal.SizeOf<LinuxPeerCredentials>() || peer.Uid != GetEffectiveUserId())
            throw new UnauthorizedAccessException("The Tiny Clips socket peer is not the current user.");
    }

    [SupportedOSPlatform("windows")]
    private static void ValidatePipeOwner(NamedPipeClientStream pipe)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
        EnsurePipeOwner(owner, identity.User);
    }

    [SupportedOSPlatform("windows")]
    internal static void EnsurePipeOwner(IdentityReference? owner, SecurityIdentifier? user)
    {
        if (user is null || !user.Equals(owner))
        {
            throw new UnauthorizedAccessException("The Tiny Clips pipe is not owned by the current user.");
        }
    }
}
