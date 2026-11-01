using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace TinyClips.Cli.Tests;

/// <summary>Stands in for the app: implements the protocol and the policy order from docs/agent-integration.md.</summary>
internal sealed class FakeApp : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Socket? _socket;
    private readonly Task _loop;
    private readonly Dictionary<string, (string[] Scopes, int Polls)> _pairings = [];
    private readonly string _directory = Directory.CreateTempSubdirectory("tc-").FullName;

    public Dictionary<string, string[]> Clients { get; } = [];
    public List<JsonObject> Received { get; } = [];
    public bool Enabled { get; init; } = true;
    public bool Approve { get; init; } = true;
    public string Endpoint { get; }

    public FakeApp()
    {
        if (OperatingSystem.IsWindows())
        {
            Endpoint = $@"\\.\pipe\tc-test-{Guid.NewGuid():N}";
            _loop = Task.Run(ServePipesAsync);
        }
        else
        {
            Endpoint = Path.Combine(_directory, "a.sock");
            _socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _socket.Bind(new UnixDomainSocketEndPoint(Endpoint));
            _socket.Listen();
            _loop = Task.Run(ServeSocketAsync);
        }
    }

    public CliEnvironment Environment(StringWriter output, StringWriter error, string? token = null) => new()
    {
        GetVariable = name => name switch
        {
            "TINYCLIPS_AUTOMATION_ENDPOINT" => Endpoint,
            "TINYCLIPS_AGENT_CONFIG" => ConfigPath,
            "TINYCLIPS_TOKEN" => token,
            _ => null,
        },
        Out = output,
        Error = error,
        PollInterval = TimeSpan.FromMilliseconds(5),
        RequestTimeout = TimeSpan.FromSeconds(10),
    };

    public string ConfigPath => Path.Combine(_directory, "agent.json");

    public static string Decide(Contract contract, bool enabled, string[]? scopes, string command)
    {
        if (command == "session.hello") return "allow";
        if (!enabled) return "automation_disabled";
        if (command is "session.pair" or "session.pairStatus") return "allow";
        if (scopes is null) return "unauthorized";
        var entry = contract.Find(command);
        if (entry is null) return "unknown_command";
        if (!scopes.Contains(entry.Scope)) return "missing_scope";
        return contract.IsSensitive(entry) ? "approve" : "allow";
    }

    private JsonObject Handle(JsonObject request)
    {
        lock (Received) Received.Add(request);
        var id = request["id"]?.DeepClone();
        var command = request["command"]!.GetValue<string>();
        var parameters = request["params"] as JsonObject ?? [];
        string[]? scopes = null;
        if (request["token"]?.GetValue<string>() is { } token) Clients.TryGetValue(token, out scopes);

        JsonObject Ok(JsonObject result) => new() { ["id"] = id, ["ok"] = true, ["result"] = result };
        JsonObject Fail(string code) => new() { ["id"] = id, ["ok"] = false, ["error"] = new JsonObject { ["code"] = code, ["message"] = code } };

        var decision = Decide(Contract.Current, Enabled, scopes, command);
        if (decision is not ("allow" or "approve")) return Fail(decision);
        if (decision == "approve" && !Approve) return Fail("approval_denied");

        switch (command)
        {
            case "session.hello":
                return Ok(new JsonObject { ["app"] = "TinyClips", ["automationEnabled"] = Enabled });
            case "session.pair":
            {
                var pairingId = $"p{_pairings.Count + 1}";
                _pairings[pairingId] = (parameters["scopes"]!.AsArray().Select(s => s!.GetValue<string>()).ToArray(), 0);
                return Ok(new JsonObject { ["pairingId"] = pairingId });
            }
            case "session.pairStatus":
            {
                var pairingId = parameters["pairingId"]!.GetValue<string>();
                var pairing = _pairings[pairingId];
                _pairings[pairingId] = pairing with { Polls = pairing.Polls + 1 };
                if (pairing.Polls < 1) return Ok(new JsonObject { ["status"] = "pending" });
                if (!Approve) return Ok(new JsonObject { ["status"] = "denied" });
                var issued = $"tok-{pairingId}";
                Clients[issued] = pairing.Scopes;
                return Ok(new JsonObject
                {
                    ["status"] = "approved",
                    ["token"] = issued,
                    ["scopes"] = new JsonArray(pairing.Scopes.Select(s => (JsonNode)s).ToArray()),
                });
            }
            case "capture.screenshot":
                return Ok(new JsonObject { ["path"] = $"/tmp/shot-{parameters["mode"]?.GetValue<string>() ?? "screen"}.png" });
            default:
                return Ok(new JsonObject { ["command"] = command, ["params"] = parameters.DeepClone() });
        }
    }

    private async Task ServeSocketAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket client;
            try { client = await _socket!.AcceptAsync(_stop.Token); }
            catch (Exception) { return; }
            _ = ServeStreamAsync(new NetworkStream(client, ownsSocket: true));
        }
    }

    private async Task ServePipesAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(Endpoint[@"\\.\pipe\".Length..], PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            try { await pipe.WaitForConnectionAsync(_stop.Token); }
            catch (Exception) { await pipe.DisposeAsync(); return; }
            _ = ServeStreamAsync(pipe);
        }
    }

    private async Task ServeStreamAsync(Stream stream)
    {
        await using (stream)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            try
            {
                while (await reader.ReadLineAsync(_stop.Token) is { } line)
                {
                    var response = Handle(JsonNode.Parse(line)!.AsObject());
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(response.ToJsonString() + "\n"), _stop.Token);
                    await stream.FlushAsync(_stop.Token);
                }
            }
            catch (Exception) when (_stop.IsCancellationRequested) { }
            catch (IOException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _socket?.Dispose();
        try { await _loop; } catch { }
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }
}
