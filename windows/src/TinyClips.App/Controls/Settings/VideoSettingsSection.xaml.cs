using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TinyClips.Core.Services;

namespace TinyClips.App.Settings.Sections;

/// <summary>
/// Video quality, audio/microphone, webcam overlay, and recording/output settings. Owns
/// microphone/webcam permission requests directly (no HWND is needed for
/// <see cref="IMediaDevicePermissionService"/>) and defers device enumeration until this section
/// is first realized.
/// </summary>
public sealed partial class VideoSettingsSection : UserControl, ISettingsSectionLifecycle
{
    private readonly IDisposable _realizationScope;
    private readonly IMediaDevicePermissionService _mediaPermissions;
    private bool _suppressMediaToggleEvents;
    private bool _closed;

    // Retained so the (internally try/catch-wrapped, never-faulting) device enumeration task is
    // observed rather than fire-and-forget, satisfying the "no unobserved task" requirement even
    // though nothing here needs to await completion.
    private readonly System.Threading.Tasks.Task _mediaDevicesInitialization;

    public SettingsViewModel ViewModel { get; }

    /// <summary>The (cached, never-faulting) microphone/webcam enumeration task kicked off when
    /// this section was first constructed. Exposed for tests and diagnostics.</summary>
    public System.Threading.Tasks.Task MediaDevicesInitialization => _mediaDevicesInitialization;

    public VideoSettingsSection(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        _mediaPermissions = App.Services.GetRequiredService<IMediaDevicePermissionService>();
        _realizationScope = viewModel.BeginSectionRealization();
        InitializeComponent();
        SectionLifecycle.HookFirstLoad(this, viewModel, _realizationScope);

        _mediaDevicesInitialization = viewModel.EnsureMediaDevicesInitializedAsync();
    }

    /// <summary>Used by the InfoBar's x:Bind to show/hide based on whether an error message is set.</summary>
    public static bool HasError(string? message) => !string.IsNullOrEmpty(message);

    public void NotifyWindowClosed() => _closed = true;

    private async void OnRecordMicrophoneToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressMediaToggleEvents || sender is not ToggleSwitch toggle)
        {
            return;
        }

        if (!toggle.IsOn)
        {
            ViewModel.RecordMicrophone = false;
            return;
        }

        if (ViewModel.RecordMicrophone)
        {
            return;
        }

        toggle.IsEnabled = false;
        var isAllowed = await _mediaPermissions.RequestMicrophoneAccessAsync();
        if (_closed)
        {
            return;
        }

        toggle.IsEnabled = true;

        SetMediaToggleState(toggle, isAllowed);
        ViewModel.RecordMicrophone = isAllowed;
    }

    private async void OnEnableWebcamToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressMediaToggleEvents || sender is not ToggleSwitch toggle)
        {
            return;
        }

        if (!toggle.IsOn)
        {
            ViewModel.WebcamEnabled = false;
            return;
        }

        if (ViewModel.WebcamEnabled)
        {
            return;
        }

        toggle.IsEnabled = false;
        var isAllowed = await _mediaPermissions.RequestCameraAccessAsync();
        if (_closed)
        {
            return;
        }

        toggle.IsEnabled = true;

        SetMediaToggleState(toggle, isAllowed);
        ViewModel.WebcamEnabled = isAllowed;
    }

    private void SetMediaToggleState(ToggleSwitch toggle, bool isOn)
    {
        _suppressMediaToggleEvents = true;
        try
        {
            toggle.IsOn = isOn;
        }
        finally
        {
            _suppressMediaToggleEvents = false;
        }
    }
}
