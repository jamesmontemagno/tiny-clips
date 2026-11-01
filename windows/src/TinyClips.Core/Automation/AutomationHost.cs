using System.Text.Json;
using System.Text.Json.Nodes;

namespace TinyClips.Core.Automation;

/// <summary>
/// Turns protocol requests into responses: session commands, pairing, the policy, parameter
/// validation, in-app approval, and the audit log. Transport-independent, so it can be tested
/// without a named pipe.
/// </summary>
public sealed class AutomationHost
{
    public const string AppName = "Tiny Clips";
    public const string Platform = "windows";
    public const int MaxPendingPairings = 4;

    /// <summary>How long an approval prompt waits before it counts as denied.</summary>
    public static readonly TimeSpan DefaultApprovalTimeout = TimeSpan.FromSeconds(120);

    // An approved token waits this long for session.pairStatus before it is dropped.
    private static readonly TimeSpan PairingRetention = TimeSpan.FromMinutes(10);

    private readonly AutomationContract _contract;
    private readonly AutomationPolicy _policy;
    private readonly AutomationClientStore _clients;
    private readonly AutomationAuditLog? _audit;
    private readonly IAutomationCommandHandler _handler;
    private readonly IAutomationApprovalPrompt _approvals;
    private readonly Func<bool> _isEnabled;
    private readonly TimeProvider _time;
    private readonly TimeSpan _approvalTimeout;
    private readonly object _pairingGate = new();
    private readonly Dictionary<string, PendingPairing> _pairings = new(StringComparer.Ordinal);
    private CancellationTokenSource _pairingCancellation = new();

    public AutomationHost(
        AutomationContract contract,
        AutomationClientStore clients,
        AutomationAuditLog? audit,
        IAutomationCommandHandler handler,
        IAutomationApprovalPrompt approvals,
        Func<bool> isEnabled,
        TimeProvider? timeProvider = null,
        TimeSpan? approvalTimeout = null)
    {
        _contract = contract ?? throw new ArgumentNullException(nameof(contract));
        _policy = new AutomationPolicy(contract);
        _clients = clients ?? throw new ArgumentNullException(nameof(clients));
        _audit = audit;
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _approvals = approvals ?? throw new ArgumentNullException(nameof(approvals));
        _isEnabled = isEnabled ?? throw new ArgumentNullException(nameof(isEnabled));
        _time = timeProvider ?? TimeProvider.System;
        _approvalTimeout = approvalTimeout ?? DefaultApprovalTimeout;
    }

    public AutomationContract Contract => _contract;

    /// <summary>The contract commands this build runs, in contract order.</summary>
    public IReadOnlyList<string> SupportedCommands =>
        _contract.Commands.Select(c => c.Name).Where(_handler.SupportedCommands.Contains).ToArray();

    /// <summary>Denies every pairing that is still waiting, e.g. when agent control is turned off.</summary>
    public void CancelPendingPairings()
    {
        CancellationTokenSource previous;
        lock (_pairingGate)
        {
            previous = _pairingCancellation;
            _pairingCancellation = new CancellationTokenSource();
            foreach (var pairing in _pairings.Values.Where(p => p.Status == PairingStatus.Pending))
            {
                pairing.Status = PairingStatus.Denied;
            }
        }

        previous.Cancel();
        previous.Dispose();
    }

    /// <summary>Handles one request line and returns the response line (without a newline).</summary>
    public async Task<string> HandleLineAsync(string line, CancellationToken cancellationToken)
    {
        AutomationRequest request;
        try
        {
            request = AutomationProtocol.ParseRequest(line);
        }
        catch (JsonException ex)
        {
            Audit("(invalid)", "error", null, AutomationErrorCodes.InvalidParams);
            return AutomationProtocol.SerializeResponse(
                AutomationResponse.Failure(null, AutomationErrorCodes.InvalidParams, $"The request is not valid JSON: {ex.Message}"));
        }

        var response = await HandleAsync(request, cancellationToken).ConfigureAwait(false);
        return AutomationProtocol.SerializeResponse(response);
    }

    public async Task<AutomationResponse> HandleAsync(AutomationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var id = request.Id;
        var command = request.Command;
        if (string.IsNullOrWhiteSpace(command))
        {
            Audit("(missing)", "error", null, AutomationErrorCodes.InvalidParams);
            return AutomationResponse.Failure(id, AutomationErrorCodes.InvalidParams, "The request has no command.");
        }

        var enabled = _isEnabled();
        AutomationClient? client = null;
        if (enabled && !_contract.IsSessionCommand(command))
        {
            client = _clients.FindByToken(request.Token);
        }

        var decision = _policy.Decide(enabled, client?.Scopes, command);
        if (decision.Kind == AutomationDecisionKind.Deny)
        {
            return Fail(id, command, client, decision.ErrorCode!, MessageFor(decision.ErrorCode!, command, client));
        }

        switch (command)
        {
            case AutomationSessionCommands.Hello:
                Audit(command, "ok", null, null);
                return AutomationResponse.Success(id, Hello(enabled));
            case AutomationSessionCommands.Pair:
                return Pair(id, request.Params);
            case AutomationSessionCommands.PairStatus:
                return PairStatus(id, request.Params);
        }

        if (client is null || !_contract.TryGetCommand(command, out var definition))
        {
            return Fail(id, command, client, AutomationErrorCodes.Internal, "The command could not be resolved.");
        }

        _clients.TouchLastUsed(client.Id);

        if (!_handler.SupportedCommands.Contains(command))
        {
            return Fail(id, command, client, AutomationErrorCodes.NotSupported, $"{command} is not available in Tiny Clips for Windows.");
        }

        if (!TryValidateParams(definition, request.Params, out var parameters, out var paramError))
        {
            return Fail(id, command, client, AutomationErrorCodes.InvalidParams, paramError);
        }

        var context = new AutomationCommandContext(definition, parameters, client);
        bool? approved = null;
        if (decision.Kind == AutomationDecisionKind.Approve)
        {
            approved = await AskAsync(ct => _approvals.RequestCommandApprovalAsync(context, ct), cancellationToken).ConfigureAwait(false);
            if (approved != true)
            {
                return Fail(id, command, client, AutomationErrorCodes.ApprovalDenied, "The request was declined or not answered in Tiny Clips.", approved);
            }
        }

        try
        {
            var result = await _handler.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
            Audit(command, "ok", client, null, approved);
            return AutomationResponse.Success(id, result);
        }
        catch (AutomationException ex)
        {
            return Fail(id, command, client, ex.Code, ex.Message, approved);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AutomationHost: {command} failed: {ex}");
            return Fail(id, command, client, AutomationErrorCodes.Internal, "The command failed unexpectedly.", approved);
        }
    }

    private JsonObject Hello(bool enabled)
    {
        var commands = new JsonArray();
        foreach (var name in SupportedCommands)
        {
            commands.Add(name);
        }

        return new JsonObject
        {
            ["app"] = AppName,
            ["version"] = _handler.AppVersion,
            ["platform"] = Platform,
            ["build"] = _handler.BuildFlavor,
            ["protocolVersion"] = _contract.ProtocolVersion,
            ["automationEnabled"] = enabled,
            ["commands"] = commands,
        };
    }

    private AutomationResponse Pair(JsonNode? id, JsonObject? parameters)
    {
        const string command = AutomationSessionCommands.Pair;
        var clientName = parameters?["clientName"] is JsonValue n && n.TryGetValue<string>(out var s) ? s.Trim() : null;
        if (string.IsNullOrEmpty(clientName) || clientName.Length > 100)
        {
            return Fail(id, command, null, AutomationErrorCodes.InvalidParams, "clientName must be a non-empty string of at most 100 characters.");
        }

        if (parameters?["scopes"] is not JsonArray scopeArray || scopeArray.Count == 0)
        {
            return Fail(id, command, null, AutomationErrorCodes.InvalidParams, "scopes must be a non-empty array of scope names.");
        }

        var scopes = new List<string>();
        foreach (var item in scopeArray)
        {
            if (item is not JsonValue v || !v.TryGetValue<string>(out var scope) || !_contract.Scopes.ContainsKey(scope))
            {
                return Fail(id, command, null, AutomationErrorCodes.InvalidParams,
                    $"Unknown scope. Valid scopes: {string.Join(", ", _contract.Scopes.Keys)}.");
            }

            if (!scopes.Contains(scope, StringComparer.Ordinal))
            {
                scopes.Add(scope);
            }
        }

        PendingPairing pairing;
        CancellationToken pairingToken;
        lock (_pairingGate)
        {
            PrunePairings();
            if (_pairings.Values.Count(p => p.Status == PairingStatus.Pending) >= MaxPendingPairings)
            {
                return Fail(id, command, null, AutomationErrorCodes.Busy, "Too many pairing requests are waiting for approval.");
            }

            pairing = new PendingPairing(Guid.NewGuid().ToString("N"), clientName, scopes, _time.GetUtcNow());
            _pairings[pairing.Id] = pairing;
            pairingToken = _pairingCancellation.Token;
        }

        _ = CompletePairingAsync(pairing, pairingToken);
        Audit(command, "ok", null, null, clientName: clientName);
        return AutomationResponse.Success(id, new JsonObject { ["pairingId"] = pairing.Id });
    }

    private async Task CompletePairingAsync(PendingPairing pairing, CancellationToken cancellationToken)
    {
        bool approved;
        try
        {
            approved = await AskAsync(
                ct => _approvals.RequestPairingApprovalAsync(new AutomationPairingRequest(pairing.Id, pairing.ClientName, pairing.Scopes), ct),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AutomationHost: pairing prompt failed: {ex}");
            approved = false;
        }

        lock (_pairingGate)
        {
            if (pairing.Status != PairingStatus.Pending || !_isEnabled())
            {
                pairing.Status = PairingStatus.Denied;
                approved = false;
            }
            else if (approved)
            {
                var (client, token) = _clients.Add(pairing.ClientName, pairing.Scopes);
                pairing.ClientId = client.Id;
                pairing.Token = token;
                pairing.Status = PairingStatus.Approved;
            }
            else
            {
                pairing.Status = PairingStatus.Denied;
            }
        }

        Audit("session.pairApproval", approved ? "ok" : "denied", null, approved ? null : AutomationErrorCodes.ApprovalDenied,
            approved, pairing.ClientName, pairing.ClientId);
    }

    private AutomationResponse PairStatus(JsonNode? id, JsonObject? parameters)
    {
        const string command = AutomationSessionCommands.PairStatus;
        var pairingId = parameters?["pairingId"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        if (string.IsNullOrEmpty(pairingId))
        {
            return Fail(id, command, null, AutomationErrorCodes.InvalidParams, "pairingId is required.");
        }

        JsonObject result;
        lock (_pairingGate)
        {
            PrunePairings();
            if (!_pairings.TryGetValue(pairingId, out var pairing))
            {
                return Fail(id, command, null, AutomationErrorCodes.NotFound, "No pairing request with that id is waiting.");
            }

            result = new JsonObject { ["status"] = pairing.Status switch
            {
                PairingStatus.Approved => "approved",
                PairingStatus.Denied => "denied",
                _ => "pending",
            } };

            if (pairing.Status == PairingStatus.Approved)
            {
                if (pairing.Token is { } token)
                {
                    result["token"] = token;
                    var scopes = new JsonArray();
                    foreach (var scope in pairing.Scopes)
                    {
                        scopes.Add(scope);
                    }

                    result["scopes"] = scopes;
                    pairing.Token = null;
                }
            }

            if (pairing.Status != PairingStatus.Pending)
            {
                _pairings.Remove(pairingId);
            }
        }

        Audit(command, "ok", null, null);
        return AutomationResponse.Success(id, result);
    }

    private void PrunePairings()
    {
        var cutoff = _time.GetUtcNow() - PairingRetention;
        foreach (var stale in _pairings.Values.Where(p => p.Status != PairingStatus.Pending && p.CreatedAt < cutoff).ToArray())
        {
            _pairings.Remove(stale.Id);
        }
    }

    private async Task<bool> AskAsync(Func<CancellationToken, Task<bool>> prompt, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_approvalTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        try
        {
            return await prompt(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Validates params against the contract and fills in defaults.</summary>
    public static bool TryValidateParams(
        AutomationCommandDefinition definition,
        JsonObject? supplied,
        out JsonObject parameters,
        out string error)
    {
        parameters = new JsonObject();
        error = string.Empty;
        foreach (var (name, spec) in definition.Parameters)
        {
            var value = supplied?[name];
            if (value is null)
            {
                if (spec.Required)
                {
                    error = $"{name} is required.";
                    return false;
                }

                if (spec.DefaultValue is { } defaultValue)
                {
                    parameters[name] = JsonNode.Parse(defaultValue.GetRawText());
                }

                continue;
            }

            var kind = value.GetValueKind();
            var valid = spec.Type switch
            {
                "string" => kind == JsonValueKind.String,
                "boolean" => kind is JsonValueKind.True or JsonValueKind.False,
                "integer" => kind == JsonValueKind.Number && value is JsonValue n && n.TryGetValue<double>(out var d) && Math.Floor(d) == d,
                "number" => kind == JsonValueKind.Number,
                "object" => kind == JsonValueKind.Object,
                "array" => kind == JsonValueKind.Array,
                _ => true,
            };
            if (!valid)
            {
                error = $"{name} must be a {spec.Type}.";
                return false;
            }

            if (spec.AllowedValues is { } allowed && !allowed.Contains(value.GetValue<string>(), StringComparer.Ordinal))
            {
                error = $"{name} must be one of: {string.Join(", ", allowed)}.";
                return false;
            }

            parameters[name] = value.DeepClone();
        }

        return true;
    }

    private string MessageFor(string code, string command, AutomationClient? client) => code switch
    {
        AutomationErrorCodes.AutomationDisabled => "Agent control is turned off. Turn it on in Tiny Clips Settings → Agent control.",
        AutomationErrorCodes.Unauthorized => "Missing, unknown, or revoked client token. Pair again with session.pair.",
        AutomationErrorCodes.UnknownCommand => $"{command} is not a Tiny Clips command.",
        AutomationErrorCodes.MissingScope when _contract.TryGetCommand(command, out var d) =>
            $"This client was not granted the {d.Scope} scope.",
        _ => _contract.Errors.TryGetValue(code, out var description) ? description : code,
    };

    private AutomationResponse Fail(JsonNode? id, string command, AutomationClient? client, string code, string message, bool? approved = null)
    {
        Audit(command, code is AutomationErrorCodes.ApprovalDenied or AutomationErrorCodes.MissingScope or AutomationErrorCodes.Unauthorized
            or AutomationErrorCodes.AutomationDisabled ? "denied" : "error", client, code, approved);
        return AutomationResponse.Failure(id, code, message);
    }

    private void Audit(string command, string outcome, AutomationClient? client, string? errorCode, bool? approved = null,
        string? clientName = null, string? clientId = null)
    {
        _audit?.Write(new AutomationAuditEntry
        {
            Timestamp = _time.GetUtcNow(),
            Command = command.Length > 100 ? command[..100] : command,
            Outcome = outcome,
            ClientId = client?.Id ?? clientId,
            ClientName = client?.Name ?? clientName,
            ErrorCode = errorCode,
            Approved = approved,
        });
    }

    private enum PairingStatus
    {
        Pending,
        Approved,
        Denied,
    }

    private sealed class PendingPairing(string id, string clientName, IReadOnlyList<string> scopes, DateTimeOffset createdAt)
    {
        public string Id { get; } = id;

        public string ClientName { get; } = clientName;

        public IReadOnlyList<string> Scopes { get; } = scopes;

        public DateTimeOffset CreatedAt { get; } = createdAt;

        public PairingStatus Status { get; set; } = PairingStatus.Pending;

        public string? Token { get; set; }

        public string? ClientId { get; set; }
    }
}
