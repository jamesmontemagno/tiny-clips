using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using TinyClips.Core.Services;

namespace TinyClips.App;

/// <summary>
/// The welcome wizard's launch-at-login choice. Windows owns the real state, so the toggle starts
/// locked until it has been read, and every result from the OS replaces what the toggle shows.
/// </summary>
public sealed class OnboardingViewModel : ObservableObject
{
    private const string UnconfirmedNote = "Couldn't confirm launch at login. You can change it later in Settings \u2192 General.";

    private readonly ILaunchAtLoginService _launchAtLoginService;
    private readonly ICaptureSettings _settings;
    private Task _pending = Task.CompletedTask;
    private int _requestVersion;
    private bool _confirmedLaunchAtLogin;
    private bool _launchAtLogin;
    private bool _canChangeLaunchAtLogin;
    private string _launchAtLoginNote = string.Empty;

    public OnboardingViewModel(ILaunchAtLoginService launchAtLoginService, ICaptureSettings settings)
    {
        _launchAtLoginService = launchAtLoginService;
        _settings = settings;
    }

    public bool LaunchAtLogin
    {
        get => _launchAtLogin;
        set
        {
            if (SetProperty(ref _launchAtLogin, value))
            {
                _ = Enqueue(() => _launchAtLoginService.SetEnabledAsync(value));
            }
        }
    }

    public bool CanChangeLaunchAtLogin
    {
        get => _canChangeLaunchAtLogin;
        private set => SetProperty(ref _canChangeLaunchAtLogin, value);
    }

    public string LaunchAtLoginNote
    {
        get => _launchAtLoginNote;
        private set
        {
            if (SetProperty(ref _launchAtLoginNote, value))
            {
                OnPropertyChanged(nameof(HasLaunchAtLoginNote));
            }
        }
    }

    public bool HasLaunchAtLoginNote => !string.IsNullOrEmpty(LaunchAtLoginNote);

    /// <summary>Settles once every requested read and change has reached the OS.</summary>
    internal Task Pending => _pending;

    /// <summary>Re-reads the OS state, after any change still in progress.</summary>
    public Task RefreshAsync() => Enqueue(_launchAtLoginService.GetStateAsync);

    // Changes run one at a time so the last choice is applied last; only the newest result is shown.
    private Task Enqueue(Func<Task<LaunchAtLoginState>> request)
    {
        var version = ++_requestVersion;
        return _pending = RunAfterAsync(_pending, request, version);
    }

    private async Task RunAfterAsync(Task previous, Func<Task<LaunchAtLoginState>> request, int version)
    {
        await previous;

        LaunchAtLoginState state;
        try
        {
            state = await request();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Unable to reconcile launch at login during onboarding ({ex.GetType().Name}).");
            if (version == _requestVersion)
            {
                ShowConfirmedState(_confirmedLaunchAtLogin);
                LaunchAtLoginNote = UnconfirmedNote;
            }

            return;
        }

        if (version == _requestVersion)
        {
            Apply(state);
        }
    }

    private void Apply(LaunchAtLoginState state)
    {
        var enabled = state.IsOn();
        _confirmedLaunchAtLogin = enabled;
        _settings.LaunchAtLogin = enabled;
        ShowConfirmedState(enabled);
        CanChangeLaunchAtLogin = state.CanChange();
        LaunchAtLoginNote = state.Describe();
    }

    // Writes the field directly: this reports the OS state and must not request a change.
    private void ShowConfirmedState(bool enabled)
    {
        if (_launchAtLogin != enabled)
        {
            _launchAtLogin = enabled;
            OnPropertyChanged(nameof(LaunchAtLogin));
        }
    }
}
