using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using TinyClips.Core.Automation;

namespace TinyClips.App.ViewModels;

/// <summary>
/// Settings → Agent control: the opt-in switch, the pipe endpoint, paired clients, and the audit log.
/// Changes apply immediately through <see cref="AgentControlService"/>.
/// </summary>
public sealed partial class AgentControlSettingsViewModel : ObservableObject
{
    private readonly AgentControlService _service;
    private readonly DispatcherQueue _dispatcher;
    private bool _loading = true;

    internal AgentControlSettingsViewModel(AgentControlService service)
    {
        _service = service;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        Endpoint = service.Endpoint;
        IsEnabled = service.IsEnabled;
        _loading = false;
        RefreshState();
        RefreshClients();
        _service.StateChanged += OnServiceStateChanged;
        _service.ClientsChanged += OnServiceClientsChanged;
    }

    public string Endpoint { get; }

    public string InstallCommand => "dotnet tool install -g TinyClips";

    public string PairCommand => "tinyclips pair --name \"My agent\"";

    public string StatusCommand => "tinyclips status";

    public string McpConfiguration => """{"mcpServers":{"tinyclips":{"command":"dotnet","args":["dnx","TinyClips","--verbosity","quiet","--yes","--","mcp"]}}}""";

    [RelayCommand]
    private async Task CopySetupTextAsync(string text)
    {
        try
        {
            await ClipboardService.CopyTextAsync(text);
            SetupCopyStatus = "Copied to clipboard.";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Copying agent setup text failed: {ex}");
            SetupCopyStatus = "Could not copy the setup text. Select the text and copy it manually.";
        }
    }

    public ObservableCollection<AgentClientItem> Clients { get; } = [];

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _errorText = string.Empty;

    [ObservableProperty]
    private Visibility _errorVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private Visibility _clientsVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private Visibility _noClientsVisibility = Visibility.Visible;

    [ObservableProperty]
    private bool _hasClients;

    [ObservableProperty]
    private string _setupCopyStatus = string.Empty;

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_loading)
        {
            _ = ApplyEnabledAsync(value);
        }
    }

    /// <summary>Stops listening to the service once the Settings window closes.</summary>
    public void Detach()
    {
        _service.StateChanged -= OnServiceStateChanged;
        _service.ClientsChanged -= OnServiceClientsChanged;
    }

    [RelayCommand]
    private void RevokeAll() => _service.Clients.RevokeAll();

    [RelayCommand]
    private async Task CopyEndpointAsync()
    {
        try
        {
            await ClipboardService.CopyTextAsync(Endpoint);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Copying the agent endpoint failed: {ex}");
        }
    }

    [RelayCommand]
    private void OpenAuditLog()
    {
        var path = _service.AuditLog.FilePath;
        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            else
            {
                OpenFolder(Path.GetDirectoryName(path));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Opening the agent audit log failed: {ex}");
        }
    }

    [RelayCommand]
    private void OpenDataFolder() => OpenFolder(Path.GetDirectoryName(_service.AuditLog.FilePath));

    private static void OpenFolder(string? folder)
    {
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Opening the agent data folder failed: {ex}");
        }
    }

    private async Task ApplyEnabledAsync(bool enabled)
    {
        try
        {
            await _service.SetEnabledAsync(enabled);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Changing agent control failed: {ex}");
        }

        RefreshState();
    }

    private void OnServiceStateChanged(object? sender, EventArgs e) => _dispatcher.TryEnqueue(() =>
    {
        _loading = true;
        IsEnabled = _service.IsEnabled;
        _loading = false;
        RefreshState();
    });

    private void OnServiceClientsChanged(object? sender, EventArgs e) => _dispatcher.TryEnqueue(RefreshClients);

    private void RefreshState()
    {
        StatusText = !_service.IsEnabled
            ? "Off. Agents cannot connect."
            : _service.IsRunning
                ? "On. Paired agents can control Tiny Clips."
                : "On, but not listening.";
        ErrorText = _service.IsEnabled ? _service.LastError ?? string.Empty : string.Empty;
        ErrorVisibility = string.IsNullOrEmpty(ErrorText) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshClients()
    {
        Clients.Clear();
        foreach (var client in _service.Clients.List().OrderByDescending(c => c.LastUsedAt ?? c.CreatedAt))
        {
            Clients.Add(new AgentClientItem(client, _service.Clients));
        }

        HasClients = Clients.Count > 0;
        ClientsVisibility = HasClients ? Visibility.Visible : Visibility.Collapsed;
        NoClientsVisibility = HasClients ? Visibility.Collapsed : Visibility.Visible;
    }
}

/// <summary>A paired agent, as listed in Settings → Agent control.</summary>
public sealed partial class AgentClientItem
{
    private readonly AutomationClientStore _store;

    internal AgentClientItem(AutomationClient client, AutomationClientStore store)
    {
        _store = store;
        Id = client.Id;
        Name = client.Name;
        ScopesText = client.Scopes.Count == 0 ? "No permissions" : "Can: " + string.Join(", ", client.Scopes);
        var paired = $"Paired {client.CreatedAt.ToLocalTime():d}";
        UsageText = client.LastUsedAt is { } lastUsed
            ? $"{paired} · Last used {lastUsed.ToLocalTime():g}"
            : $"{paired} · Never used";
        RevokeName = $"Revoke {client.Name}";
        RevokeCommand = new RelayCommand(() => _store.Revoke(Id));
    }

    public string Id { get; }

    public string Name { get; }

    public string ScopesText { get; }

    public string UsageText { get; }

    public string RevokeName { get; }

    public IRelayCommand RevokeCommand { get; }
}
