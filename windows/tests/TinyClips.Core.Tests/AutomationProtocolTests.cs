using System.Text.Json;
using System.Text.Json.Nodes;
using TinyClips.Core.Automation;

namespace TinyClips.Core.Tests;

public sealed class AutomationProtocolTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TinyClipsAutomationTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Request_RoundTrips()
    {
        var line = """{"id":"1","token":"abc","command":"capture.screenshot","params":{"mode":"screen"}}""";
        var request = AutomationProtocol.ParseRequest(line);
        Assert.Equal("1", request.Id!.GetValue<string>());
        Assert.Equal("abc", request.Token);
        Assert.Equal("capture.screenshot", request.Command);
        Assert.Equal("screen", request.Params!["mode"]!.GetValue<string>());

        var again = AutomationProtocol.ParseRequest(AutomationProtocol.SerializeRequest(request));
        Assert.Equal(request.Command, again.Command);
        Assert.Equal(request.Token, again.Token);
        Assert.Equal("screen", again.Params!["mode"]!.GetValue<string>());
    }

    [Fact]
    public void Request_KeepsNumericIds_AndRejectsNonObjects()
    {
        Assert.Equal(7, AutomationProtocol.ParseRequest("""{"id":7,"command":"session.hello"}""").Id!.GetValue<int>());
        Assert.Throws<JsonException>(() => AutomationProtocol.ParseRequest("[]"));
        Assert.Throws<JsonException>(() => AutomationProtocol.ParseRequest("""{"command":"x","params":[1]}"""));
    }

    [Fact]
    public void SuccessResponse_HasWireShape()
    {
        var line = AutomationProtocol.SerializeResponse(
            AutomationResponse.Success(JsonValue.Create("1"), new JsonObject { ["path"] = @"C:\clip.png" }));
        Assert.Equal("""{"id":"1","ok":true,"result":{"path":"C:\\clip.png"}}""", line);
        Assert.DoesNotContain('\n', line);

        var parsed = AutomationProtocol.ParseResponse(line);
        Assert.True(parsed.Ok);
        Assert.Equal(@"C:\clip.png", parsed.Result!["path"]!.GetValue<string>());
        Assert.Null(parsed.Error);
    }

    [Fact]
    public void FailureResponse_HasWireShape()
    {
        var line = AutomationProtocol.SerializeResponse(
            AutomationResponse.Failure(JsonValue.Create("2"), AutomationErrorCodes.MissingScope, "No record scope."));
        Assert.Equal("""{"id":"2","ok":false,"error":{"code":"missing_scope","message":"No record scope."}}""", line);

        var parsed = AutomationProtocol.ParseResponse(line);
        Assert.False(parsed.Ok);
        Assert.Equal(AutomationErrorCodes.MissingScope, parsed.Error!.Code);
    }

    [Fact]
    public void SuccessWithoutResult_SendsEmptyObject()
    {
        var line = AutomationProtocol.SerializeResponse(AutomationResponse.Success(null, null));
        Assert.Equal("""{"id":null,"ok":true,"result":{}}""", line);
    }

    [Fact]
    public async Task Host_HelloWorksWhileDisabled_AndOtherCommandsAreRefused()
    {
        var enabled = false;
        var host = CreateHost(new FakeHandler(), new FakeApprovals(true), () => enabled);

        var hello = await HandleAsync(host, """{"id":"1","command":"session.hello"}""");
        Assert.True(hello.Ok);
        Assert.Equal("windows", hello.Result!["platform"]!.GetValue<string>());
        Assert.False(hello.Result!["automationEnabled"]!.GetValue<bool>());
        Assert.Equal(["app.status", "library.delete"], hello.Result!["commands"]!.AsArray().Select(n => n!.GetValue<string>()));

        var pair = await HandleAsync(host, """{"id":"2","command":"session.pair","params":{"clientName":"x","scopes":["read"]}}""");
        Assert.Equal(AutomationErrorCodes.AutomationDisabled, pair.Error!.Code);
    }

    [Fact]
    public async Task Host_PairsRunsAndGatesSensitiveCommands()
    {
        var approvals = new FakeApprovals(true);
        var handler = new FakeHandler();
        var host = CreateHost(handler, approvals, () => true);

        var unauthorized = await HandleAsync(host, """{"id":"0","command":"app.status"}""");
        Assert.Equal(AutomationErrorCodes.Unauthorized, unauthorized.Error!.Code);

        var pair = await HandleAsync(host, """{"id":"1","command":"session.pair","params":{"clientName":"Test agent","scopes":["read","delete"]}}""");
        var pairingId = pair.Result!["pairingId"]!.GetValue<string>();
        var token = await WaitForTokenAsync(host, pairingId);

        var status = await HandleAsync(host, $$$"""{"id":"3","token":"{{{token}}}","command":"app.status"}""");
        Assert.True(status.Ok);
        Assert.Equal("idle", status.Result!["state"]!.GetValue<string>());

        var record = await HandleAsync(host, $$$"""{"id":"4","token":"{{{token}}}","command":"recording.startGif"}""");
        Assert.Equal(AutomationErrorCodes.MissingScope, record.Error!.Code);

        var delete = await HandleAsync(host, $$$"""{"id":"5","token":"{{{token}}}","command":"library.delete","params":{"path":"a.png"}}""");
        Assert.True(delete.Ok);
        Assert.Equal(1, approvals.CommandPrompts);

        approvals.Answer = false;
        var declined = await HandleAsync(host, $$$"""{"id":"6","token":"{{{token}}}","command":"library.delete","params":{"path":"a.png"}}""");
        Assert.Equal(AutomationErrorCodes.ApprovalDenied, declined.Error!.Code);
        Assert.Equal(1, handler.Deletes);

        var missing = await HandleAsync(host, $$$"""{"id":"7","token":"{{{token}}}","command":"library.delete"}""");
        Assert.Equal(AutomationErrorCodes.InvalidParams, missing.Error!.Code);
    }

    [Fact]
    public async Task Host_UnansweredApprovalTimesOutAsDenied()
    {
        var approvals = new FakeApprovals(true) { Hang = true };
        var host = CreateHost(new FakeHandler(), approvals, () => true, TimeSpan.FromMilliseconds(50));
        var pair = await HandleAsync(host, """{"id":"1","command":"session.pair","params":{"clientName":"x","scopes":["delete"]}}""");
        var pairingId = pair.Result!["pairingId"]!.GetValue<string>();

        AutomationResponse status;
        do
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
            status = await HandleAsync(host, $$$"""{"id":"2","command":"session.pairStatus","params":{"pairingId":"{{{pairingId}}}"}}""");
        }
        while (status.Result!["status"]!.GetValue<string>() == "pending");

        Assert.Equal("denied", status.Result!["status"]!.GetValue<string>());
        Assert.Empty(new AutomationClientStore(Path.Combine(_directory, "clients.json")).List());
    }

    [Fact]
    public async Task Host_DoesNotExecuteIfCancellationRacesWithApproval()
    {
        var clients = new AutomationClientStore(Path.Combine(_directory, "clients.json"));
        var (_, token) = clients.Add("Agent", ["delete"]);
        var handler = new FakeHandler();
        var approvals = new DelayedApproval();
        var host = new AutomationHost(
            AutomationContract.Default,
            clients,
            new AutomationAuditLog(Path.Combine(_directory, AutomationAuditLog.FileName)),
            handler,
            approvals,
            () => true);
        using var cancellation = new CancellationTokenSource();
        var pending = host.HandleLineAsync(
            $$$"""{"id":"1","token":"{{{token}}}","command":"library.delete","params":{"path":"a.png"}}""",
            cancellation.Token);
        await approvals.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        cancellation.Cancel();
        approvals.Result.TrySetResult(true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(0, handler.Deletes);
    }

    [Fact]
    public async Task Host_UnknownCommandAndBadJson()
    {
        var host = CreateHost(new FakeHandler(), new FakeApprovals(true), () => true);
        var token = await WaitForTokenAsync(host, (await HandleAsync(host,
            """{"id":"1","command":"session.pair","params":{"clientName":"x","scopes":["read"]}}""")).Result!["pairingId"]!.GetValue<string>());

        var unknown = await HandleAsync(host, $$$"""{"id":"2","token":"{{{token}}}","command":"app.explode"}""");
        Assert.Equal(AutomationErrorCodes.UnknownCommand, unknown.Error!.Code);

        var unsupported = await HandleAsync(host, $$$"""{"id":"3","token":"{{{token}}}","command":"recording.status"}""");
        Assert.Equal(AutomationErrorCodes.NotSupported, unsupported.Error!.Code);

        var bad = AutomationProtocol.ParseResponse(await host.HandleLineAsync("{nope", CancellationToken.None));
        Assert.Equal(AutomationErrorCodes.InvalidParams, bad.Error!.Code);
    }

    [Fact]
    public async Task LineReader_SplitsLines_AndCapsLength()
    {
        using var stream = new MemoryStream("one\r\ntwo\nthree"u8.ToArray());
        var reader = new AutomationPipeServer.LineReader(stream, 16);
        Assert.Equal("one", await reader.ReadLineAsync(CancellationToken.None));
        Assert.Equal("two", await reader.ReadLineAsync(CancellationToken.None));
        Assert.Equal("three", await reader.ReadLineAsync(CancellationToken.None));
        Assert.Null(await reader.ReadLineAsync(CancellationToken.None));

        using var tooLong = new MemoryStream(new byte[64]);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new AutomationPipeServer.LineReader(tooLong, 16).ReadLineAsync(CancellationToken.None));
    }

    private AutomationHost CreateHost(FakeHandler handler, FakeApprovals approvals, Func<bool> enabled, TimeSpan? timeout = null) =>
        new(
            AutomationContract.Default,
            new AutomationClientStore(Path.Combine(_directory, "clients.json")),
            new AutomationAuditLog(Path.Combine(_directory, AutomationAuditLog.FileName)),
            handler,
            approvals,
            enabled,
            approvalTimeout: timeout);

    private static async Task<AutomationResponse> HandleAsync(AutomationHost host, string line) =>
        AutomationProtocol.ParseResponse(await host.HandleLineAsync(line, CancellationToken.None));

    private static async Task<string> WaitForTokenAsync(AutomationHost host, string pairingId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var status = await HandleAsync(host, $$$"""{"id":"p","command":"session.pairStatus","params":{"pairingId":"{{{pairingId}}}"}}""");
            if (status.Result!["status"]!.GetValue<string>() == "approved")
            {
                return status.Result!["token"]!.GetValue<string>();
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Pairing was not approved.");
    }

    private sealed class FakeHandler : IAutomationCommandHandler
    {
        public int Deletes { get; private set; }

        public string AppVersion => "9.9.9";

        public string BuildFlavor => "direct";

        public IReadOnlyCollection<string> SupportedCommands { get; } = ["app.status", "library.delete"];

        public Task<JsonNode?> ExecuteAsync(AutomationCommandContext context, CancellationToken cancellationToken)
        {
            if (context.Command.Name == "library.delete")
            {
                Deletes++;
                return Task.FromResult<JsonNode?>(new JsonObject { ["deleted"] = true });
            }

            return Task.FromResult<JsonNode?>(new JsonObject { ["state"] = "idle" });
        }
    }

    private sealed class FakeApprovals(bool answer) : IAutomationApprovalPrompt
    {
        public bool Answer { get; set; } = answer;

        public bool Hang { get; set; }

        public int CommandPrompts { get; private set; }

        public async Task<bool> RequestPairingApprovalAsync(AutomationPairingRequest request, CancellationToken cancellationToken)
        {
            if (Hang)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return Answer;
        }

        public Task<bool> RequestCommandApprovalAsync(AutomationCommandContext context, CancellationToken cancellationToken)
        {
            CommandPrompts++;
            return Task.FromResult(Answer);
        }
    }

    private sealed class DelayedApproval : IAutomationApprovalPrompt
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> RequestPairingApprovalAsync(AutomationPairingRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<bool> RequestCommandApprovalAsync(AutomationCommandContext context, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            return Result.Task;
        }
    }
}
