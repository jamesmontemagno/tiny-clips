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

    public string PluginRepositoryUrl => "https://github.com/jamesmontemagno/tiny-clips.git";

    public string PluginInstallCommand => "copilot plugin install jamesmontemagno/tiny-clips";

    public string InstallCommand => "dotnet tool install -g TinyClips";

    public string PairCommand => "dnx TinyClips --yes -- pair --name \"My agent\"";

    public string StatusCommand => "dnx TinyClips --yes -- status";

    public string GlobalPairCommand => "tinyclips pair --name \"My agent\"";

    public string GlobalStatusCommand => "tinyclips status";

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
        var clients = _service.Clients.List().OrderByDescending(c => c.LastUsedAt ?? c.CreatedAt).ToArray();
        if (clients.Select(c => c.Id).SequenceEqual(Clients.Select(c => c.Id), StringComparer.Ordinal))
        {
            // Update rows in place so an open permissions flyout is not torn down.
            for (var i = 0; i < clients.Length; i++)
            {
                Clients[i].Update(clients[i]);
            }
        }
        else
        {
            Clients.Clear();
            foreach (var client in clients)
            {
                Clients.Add(new AgentClientItem(client, _service.Clients));
            }
        }

        HasClients = Clients.Count > 0;
        ClientsVisibility = HasClients ? Visibility.Visible : Visibility.Collapsed;
        NoClientsVisibility = HasClients ? Visibility.Collapsed : Visibility.Visible;
    }
}

/// <summary>A paired agent, as listed in Settings → Agent control.</summary>
public sealed partial class AgentClientItem : ObservableObject
{
    private readonly AutomationClientStore _store;
    private bool _updating;

    internal AgentClientItem(AutomationClient client, AutomationClientStore store)
    {
        _store = store;
        Id = client.Id;
        Name = client.Name;
        RevokeName = $"Revoke {client.Name}";
        PermissionsName = $"Change permissions for {client.Name}";
        RevokeCommand = new RelayCommand(() => _store.Revoke(Id));
        var contract = AutomationContract.Default;
        ScopeOptions = contract.Scopes
            .Select(s => new AgentScopeOption(s.Key, s.Value, contract.IsSensitiveScope(s.Key), OnScopeToggled))
            .ToArray();
        Update(client);
    }

    public string Id { get; }

    public string Name { get; }

    [ObservableProperty]
    private string _scopesText = string.Empty;

    [ObservableProperty]
    private string _usageText = string.Empty;

    [ObservableProperty]
    private string _permissionErrorText = string.Empty;

    public string RevokeName { get; }

    public string PermissionsName { get; }

    public IRelayCommand RevokeCommand { get; }

    /// <summary>Every contract scope, checked when the agent has it. Changes apply immediately.</summary>
    public IReadOnlyList<AgentScopeOption> ScopeOptions { get; }

    internal void Update(AutomationClient client)
    {
        ScopesText = client.Scopes.Count == 0 ? "No permissions" : "Can: " + string.Join(", ", client.Scopes);
        var paired = $"Paired {client.CreatedAt.ToLocalTime():d}";
        UsageText = client.LastUsedAt is { } lastUsed
            ? $"{paired} · Last used {lastUsed.ToLocalTime():g}"
            : $"{paired} · Never used";

        _updating = true;
        try
        {
            foreach (var option in ScopeOptions)
            {
                option.IsSelected = client.Scopes.Contains(option.Name, StringComparer.Ordinal);
            }
        }
        finally
        {
            _updating = false;
        }

        UpdateOptionAvailability();
    }

    private void OnScopeToggled()
    {
        if (_updating)
        {
            return;
        }

        var selected = ScopeOptions.Where(o => o.IsSelected).Select(o => o.Name).ToArray();
        UpdateOptionAvailability();
        if (selected.Length == 0)
        {
            return;
        }

        try
        {
            if (!_store.UpdateScopes(Id, selected))
            {
                RestoreSavedScopes();
                PermissionErrorText = "This agent is no longer paired. Its permissions were not changed.";
                return;
            }

            PermissionErrorText = string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Updating agent permissions failed: {ex}");
            RestoreSavedScopes();
            PermissionErrorText = "Could not save permissions. The change was not applied. Check disk space and access, then try again.";
        }
    }

    private void RestoreSavedScopes()
    {
        var savedClient = _store.List().FirstOrDefault(client => client.Id == Id);
        if (savedClient is not null)
        {
            Update(savedClient);
        }
    }

    // An agent needs at least one scope; the last checked box is locked. Revoke removes the agent instead.
    private void UpdateOptionAvailability()
    {
        var onlyOne = ScopeOptions.Count(o => o.IsSelected) == 1;
        foreach (var option in ScopeOptions)
        {
            option.IsEnabled = !(onlyOne && option.IsSelected);
        }
    }
}

/// <summary>One permission checkbox in a paired agent's permissions flyout.</summary>
public sealed partial class AgentScopeOption : ObservableObject
{
    private readonly Action _toggled;

    internal AgentScopeOption(string name, string description, bool isSensitive, Action toggled)
    {
        Name = name;
        Description = description;
        Label = isSensitive ? $"{name} (asks every time)" : name;
        AccessibleName = $"{name}: {description}";
        _toggled = toggled;
    }

    public string Name { get; }

    public string Label { get; }

    public string Description { get; }

    public string AccessibleName { get; }

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isEnabled = true;

    partial void OnIsSelectedChanged(bool value) => _toggled();
}
