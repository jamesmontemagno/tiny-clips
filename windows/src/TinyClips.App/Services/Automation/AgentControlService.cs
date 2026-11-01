using System.Diagnostics;
using System.IO;
using TinyClips.Core.Automation;
using TinyClips.Core.Services;
using TinyClips.Core.Services.ClipsLibrary;

namespace TinyClips.App;

/// <summary>
/// Owns agent control: the opt-in setting, the paired clients, the audit log, and the named pipe
/// that agents (the <c>tinyclips</c> CLI, MCP server, and skills) connect to. The pipe only
/// listens while agent control is on. See docs/agent-integration.md.
/// </summary>
internal sealed class AgentControlService
{
    internal const string EnabledSettingKey = "AutomationEnabled";

    private readonly ISettingsService _settings;
    private readonly AutomationHost _host;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AutomationPipeServer? _server;
    private volatile bool _enabled;

    public AgentControlService(
        ISettingsService settings,
        IAutomationCommandHandler handler,
        IAutomationApprovalPrompt approvals)
    {
        _settings = settings;
        _enabled = settings.Get(EnabledSettingKey, false);

        var localData = ClipsLibraryPaths.LocalDataDirectory();
        Clients = new AutomationClientStore(AutomationClientStore.DefaultFilePath(localData));
        AuditLog = new AutomationAuditLog(AutomationAuditLog.DefaultFilePath(localData));
        Clients.Changed += (_, _) => ClientsChanged?.Invoke(this, EventArgs.Empty);
        _host = new AutomationHost(AutomationContract.Default, Clients, AuditLog, handler, approvals, () => _enabled);
    }

    /// <summary>Raised when agent control is turned on or off, or the pipe starts, stops, or fails.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Raised when a client is paired or revoked. May be raised on any thread.</summary>
    public event EventHandler? ClientsChanged;

    public bool IsEnabled => _enabled;

    public bool IsRunning => _server?.IsRunning == true;

    /// <summary>Why the pipe could not be started, or null.</summary>
    public string? LastError { get; private set; }

    public string Endpoint => @"\\.\pipe\" + AutomationPipeServer.DefaultPipeName;

    public AutomationClientStore Clients { get; }

    public AutomationAuditLog AuditLog { get; }

    public IReadOnlyList<string> SupportedCommands => _host.SupportedCommands;

    /// <summary>Starts the pipe at launch when agent control was left on.</summary>
    public Task StartIfEnabledAsync() => _enabled ? ApplyAsync(true) : Task.CompletedTask;

    public async Task SetEnabledAsync(bool enabled)
    {
        if (_enabled != enabled)
        {
            _enabled = enabled;
            _settings.Set(EnabledSettingKey, enabled);
        }

        await ApplyAsync(enabled);
    }

    /// <summary>Stops listening without changing the setting, e.g. when the app exits.</summary>
    public async Task ShutdownAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await StopServerAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ApplyAsync(bool enabled)
    {
        await _gate.WaitAsync();
        try
        {
            if (enabled)
            {
                StartServer();
            }
            else
            {
                _host.CancelPendingPairings();
                await StopServerAsync();
                LastError = null;
            }
        }
        finally
        {
            _gate.Release();
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void StartServer()
    {
        if (_server?.IsRunning == true)
        {
            return;
        }

        var server = new AutomationPipeServer(_host);
        server.Faulted += OnServerFaulted;
        try
        {
            server.Start();
            _server = server;
            LastError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            server.Faulted -= OnServerFaulted;
            Debug.WriteLine($"Agent control pipe could not start: {ex}");
            LastError = "Tiny Clips could not open its agent connection. Another copy of Tiny Clips may already be using it.";
        }
    }

    private async Task StopServerAsync()
    {
        if (_server is not { } server)
        {
            return;
        }

        _server = null;
        server.Faulted -= OnServerFaulted;
        try
        {
            await server.StopAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Agent control pipe did not stop cleanly: {ex}");
        }
    }

    private void OnServerFaulted(object? sender, Exception ex)
    {
        Debug.WriteLine($"Agent control pipe failed: {ex}");
        LastError = "The agent connection stopped unexpectedly. Turn agent control off and on to try again.";
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
