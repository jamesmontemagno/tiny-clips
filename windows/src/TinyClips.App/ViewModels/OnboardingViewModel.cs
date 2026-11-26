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
    private bool _saveScreenshotToDisk;
    private bool _copyScreenshotToClipboard;
    private bool _showScreenshotEditor;
    private bool _hasScreenshotOutputWarning;
    private bool _refreshingScreenshotOutputs;
    private bool _showSaveNotifications;
    private bool? _recordingCountdown;

    public OnboardingViewModel(ILaunchAtLoginService launchAtLoginService, ICaptureSettings settings)
    {
        _launchAtLoginService = launchAtLoginService;
        _settings = settings;
        RefreshPreferences();
    }

    public bool SaveScreenshotToDisk
    {
        get => _saveScreenshotToDisk;
        set => SetScreenshotOutput(ref _saveScreenshotToDisk, value, ScreenshotOutput.SaveToDisk, nameof(SaveScreenshotToDisk));
    }

    public bool CopyScreenshotToClipboard
    {
        get => _copyScreenshotToClipboard;
        set => SetScreenshotOutput(ref _copyScreenshotToClipboard, value, ScreenshotOutput.CopyToClipboard, nameof(CopyScreenshotToClipboard));
    }

    public bool ShowScreenshotEditor
    {
        get => _showScreenshotEditor;
        set => SetScreenshotOutput(ref _showScreenshotEditor, value, ScreenshotOutput.OpenEditor, nameof(ShowScreenshotEditor));
    }

    public bool HasScreenshotOutputWarning
    {
        get => _hasScreenshotOutputWarning;
        private set => SetProperty(ref _hasScreenshotOutputWarning, value);
    }

    public string ScreenshotOutputExplanation => ScreenshotOutputPolicy.LastOutputExplanation;

    public bool ShowSaveNotifications
    {
        get => _showSaveNotifications;
        set
        {
            if (SetProperty(ref _showSaveNotifications, value))
            {
                _settings.ShowSaveNotifications = value;
            }
        }
    }

    public bool RecordingCountdown
    {
        get => _recordingCountdown == true;
        set
        {
            // A mixed saved state displays as Custom; initial binding write-backs must not normalize it.
            if (value != RecordingCountdown)
            {
                _recordingCountdown = value;
                _settings.VideoCountdownEnabled = value;
                _settings.GifCountdownEnabled = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RecordingCountdownOffContent));
            }
        }
    }

    public string RecordingCountdownOffContent => _recordingCountdown is null ? "Custom" : "Off";

    public void RefreshPreferences()
    {
        RefreshScreenshotOutputs();
        HasScreenshotOutputWarning = false;
        SetProperty(ref _showSaveNotifications, _settings.ShowSaveNotifications, nameof(ShowSaveNotifications));
        bool? countdown = _settings.VideoCountdownEnabled == _settings.GifCountdownEnabled
            ? _settings.VideoCountdownEnabled
            : null;
        if (SetProperty(ref _recordingCountdown, countdown, nameof(RecordingCountdown)))
        {
            OnPropertyChanged(nameof(RecordingCountdownOffContent));
        }
    }

    private void SetScreenshotOutput(ref bool current, bool value, ScreenshotOutput output, string propertyName)
    {
        if (_refreshingScreenshotOutputs)
        {
            return;
        }

        if (current == value)
        {
            RefreshScreenshotOutputs();
            return;
        }

        var accepted = ScreenshotOutputPolicy.TrySet(_settings, output, value);
        HasScreenshotOutputWarning = !accepted;
        RefreshScreenshotOutputs();
        if (!accepted)
        {
            OnPropertyChanged(propertyName);
        }
    }

    private void RefreshScreenshotOutputs()
    {
        var save = _settings.SaveScreenshotToDisk;
        var copy = _settings.CopyScreenshotToClipboard;
        var editor = _settings.ShowScreenshotEditor;
        _refreshingScreenshotOutputs = true;
        try
        {
            SetProperty(ref _saveScreenshotToDisk, save, nameof(SaveScreenshotToDisk));
            SetProperty(ref _copyScreenshotToClipboard, copy, nameof(CopyScreenshotToClipboard));
            SetProperty(ref _showScreenshotEditor, editor, nameof(ShowScreenshotEditor));
        }
        finally
        {
            _refreshingScreenshotOutputs = false;
        }
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
