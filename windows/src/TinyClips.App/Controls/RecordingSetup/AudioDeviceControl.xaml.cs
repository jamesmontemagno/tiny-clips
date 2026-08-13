using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TinyClips.Core.Services;

namespace TinyClips.App.RecordingSetup;

/// <summary>
/// System audio + microphone toggles and the microphone device picker for
/// <see cref="TinyClips.App.RecordingSetupWindow"/>. Device selection, on/off state, and the
/// audio/mic permission decision (which requires the host window's close guard and
/// <c>IMediaDevicePermissionService</c>) stay with the window; this control owns only the visuals
/// and the microphone flyout's cached menu items.
/// </summary>
/// <remarks>
/// The microphone <see cref="MenuFlyoutItem"/>s are built once per distinct device collection and
/// reused afterwards — an ordinary selection change (<see cref="OnMicrophoneItemClick"/>) only
/// flips <c>IsChecked</c> on the cached items, it never clears/rebuilds
/// <see cref="MicrophoneFlyout"/>. The flyout's <c>Items</c> collection is only repopulated when
/// the enumerated device list actually changes or loading starts/ends (see
/// <see cref="SetMicrophones"/> and <see cref="SetMicrophoneLoading"/>).
/// </remarks>
public sealed partial class AudioDeviceControl : UserControl
{
    private readonly List<AudioInputDevice> _microphones = new();
    private readonly List<ToggleMenuFlyoutItem> _microphoneItems = new();

    private MenuFlyoutItem? _loadingMicrophoneItem;
    private bool _suppressEvents;
    private bool _microphonesLoading;
    private bool _microphonesEnumerated;
    private bool _recordSystemAudio;
    private bool _recordMicrophone;
    private string _selectedMicrophoneId = string.Empty;

    public AudioDeviceControl()
    {
        InitializeComponent();

        _microphones.Add(new AudioInputDevice(string.Empty, "System default"));
        RebuildMicrophoneItems();
        ShowMicrophoneItems();
    }

    /// <summary>Raised when the user checks the microphone toggle while it was previously off,
    /// meaning the host must request microphone access before the state can change. The host calls
    /// <see cref="SetMicrophoneAllowed"/> with the resolved value once the request completes.</summary>
    public event EventHandler? MicrophoneToggleRequested;

    /// <summary>Raised whenever something that can change <see cref="IsMicrophoneSelectionReady"/>
    /// happens: the microphone toggle, loading state, a permission outcome, or device-list
    /// reconciliation. The host uses this to keep its Start button's enabled state current.</summary>
    public event EventHandler? ReadinessChanged;

    public bool RecordSystemAudio => _recordSystemAudio;

    public bool RecordMicrophone => _recordMicrophone;

    public string SelectedMicrophoneId => _selectedMicrophoneId;

    /// <summary>
    /// <see langword="true"/> when the microphone is not being recorded (nothing to resolve), or
    /// when it is being recorded and the device list has finished at least one enumeration pass
    /// with no load in progress — i.e. <see cref="SelectedMicrophoneId"/> reflects a reconciled
    /// choice rather than a persisted id that hasn't been checked against the real device list yet.
    /// </summary>
    public bool IsMicrophoneSelectionReady => !_recordMicrophone || (_microphonesEnumerated && !_microphonesLoading);

    public bool IsMicrophoneToggleEnabled
    {
        get => MicrophoneToggle.IsEnabled;
        set => MicrophoneToggle.IsEnabled = value;
    }

    public void SetVisibleForVideo(bool isVideo)
    {
        var visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;
        SystemAudioToggle.Visibility = visibility;
        MicrophoneToggle.Visibility = visibility;
        MicrophoneDeviceButton.Visibility = visibility;
    }

    /// <summary>Seeds the initial state from persisted settings. Must be called once, before the
    /// window is shown.</summary>
    public void Initialize(bool recordSystemAudio, bool recordMicrophone, string? selectedMicrophoneId)
    {
        _recordSystemAudio = recordSystemAudio;
        _recordMicrophone = recordMicrophone;
        _selectedMicrophoneId = selectedMicrophoneId ?? string.Empty;

        _suppressEvents = true;
        try
        {
            SystemAudioToggle.IsChecked = _recordSystemAudio;
            MicrophoneToggle.IsChecked = _recordMicrophone;
        }
        finally
        {
            _suppressEvents = false;
        }

        UpdateSystemAudioVisual();
        UpdateMicrophoneVisual();
        UpdateMicrophoneSelectionVisuals();
        UpdateMicrophonePickerEnabled();
        ReadinessChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Applies the outcome of a microphone permission request (or a webcam-driven
    /// auto-enable) without re-raising <see cref="MicrophoneToggleRequested"/>.</summary>
    public void SetMicrophoneAllowed(bool allowed)
    {
        _recordMicrophone = allowed;
        _suppressEvents = true;
        try
        {
            MicrophoneToggle.IsChecked = allowed;
        }
        finally
        {
            _suppressEvents = false;
        }

        UpdateMicrophoneVisual();
        UpdateMicrophonePickerEnabled();
        ReadinessChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetMicrophoneLoading(bool loading)
    {
        _microphonesLoading = loading;
        if (loading)
        {
            ShowMicrophoneLoading();
        }
        else
        {
            ShowMicrophoneItems();
        }

        UpdateMicrophonePickerEnabled();
        ReadinessChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Applies the enumerated microphone list. Device menu items are only rebuilt when
    /// the collection actually changed; otherwise this just re-resolves the selected id. A
    /// persisted <see cref="SelectedMicrophoneId"/> that matches an enumerated device is kept
    /// as-is; it only falls back to system default when the id is absent from the list.</summary>
    public void SetMicrophones(IReadOnlyList<AudioInputDevice> microphones)
    {
        var incoming = microphones.Count == 0
            ? new List<AudioInputDevice> { new(string.Empty, "System default") }
            : new List<AudioInputDevice>(microphones);

        if (!DevicesEqual(_microphones, incoming))
        {
            _microphones.Clear();
            _microphones.AddRange(incoming);
            RebuildMicrophoneItems();
        }

        var selected = _microphones.FirstOrDefault(m => m.Id == _selectedMicrophoneId) ?? _microphones[0];
        _selectedMicrophoneId = selected.Id;
        _microphonesEnumerated = true;

        ShowMicrophoneItems();
        UpdateMicrophonePickerEnabled();
        ReadinessChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnSystemAudioToggleClicked(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        _recordSystemAudio = SystemAudioToggle.IsChecked == true;
        UpdateSystemAudioVisual();
    }

    private void OnMicrophoneToggleClicked(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        var isChecked = MicrophoneToggle.IsChecked == true;
        if (isChecked && !_recordMicrophone)
        {
            MicrophoneToggleRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        _recordMicrophone = isChecked;
        UpdateMicrophoneVisual();
        UpdateMicrophonePickerEnabled();
        ReadinessChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnMicrophoneItemClick(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        if (sender is ToggleMenuFlyoutItem { Tag: AudioInputDevice device })
        {
            _selectedMicrophoneId = device.Id;
            UpdateMicrophoneSelectionVisuals();
        }
    }

    private void RebuildMicrophoneItems()
    {
        foreach (var item in _microphoneItems)
        {
            item.Click -= OnMicrophoneItemClick;
        }

        _microphoneItems.Clear();

        foreach (var microphone in _microphones)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = microphone.Name,
                Tag = microphone,
                IsChecked = microphone.Id == _selectedMicrophoneId,
            };
            item.Click += OnMicrophoneItemClick;
            _microphoneItems.Add(item);
        }
    }

    private void ShowMicrophoneItems()
    {
        MicrophoneFlyout.Items.Clear();
        foreach (var item in _microphoneItems)
        {
            MicrophoneFlyout.Items.Add(item);
        }

        UpdateMicrophoneSelectionVisuals();
    }

    private void ShowMicrophoneLoading()
    {
        MicrophoneDeviceLabel.Text = "Loading...";
        MicrophoneFlyout.Items.Clear();
        _loadingMicrophoneItem ??= new MenuFlyoutItem { Text = "Loading microphones...", IsEnabled = false };
        MicrophoneFlyout.Items.Add(_loadingMicrophoneItem);
    }

    private void UpdateMicrophoneSelectionVisuals()
    {
        foreach (var item in _microphoneItems)
        {
            item.IsChecked = item.Tag is AudioInputDevice device && device.Id == _selectedMicrophoneId;
        }

        var selected = _microphones.FirstOrDefault(m => m.Id == _selectedMicrophoneId) ?? _microphones.FirstOrDefault();
        if (selected is not null)
        {
            MicrophoneDeviceLabel.Text = selected.Name;
            ToolTipService.SetToolTip(MicrophoneDeviceButton, $"Microphone device: {selected.Name}");
        }
    }

    private void UpdateSystemAudioVisual()
    {
        var state = _recordSystemAudio ? "On" : "Off";
        SystemAudioIcon.Glyph = _recordSystemAudio ? "\uE767" : "\uE74F";
        ToolTipService.SetToolTip(SystemAudioToggle, $"System audio: {state}");
        AutomationProperties.SetName(SystemAudioToggle, $"System audio {state}");
    }

    private void UpdateMicrophoneVisual()
    {
        MicrophoneSlash.Visibility = _recordMicrophone ? Visibility.Collapsed : Visibility.Visible;
        var state = _recordMicrophone ? "On" : "Off";
        ToolTipService.SetToolTip(MicrophoneToggle, $"Microphone: {state}");
        AutomationProperties.SetName(MicrophoneToggle, $"Microphone {state}");
    }

    private void UpdateMicrophonePickerEnabled()
    {
        MicrophoneDeviceButton.IsEnabled = _recordMicrophone && !_microphonesLoading && MicrophoneFlyout.Items.Count > 0;
    }

    private static bool DevicesEqual(IReadOnlyList<AudioInputDevice> a, IReadOnlyList<AudioInputDevice> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].Id != b[i].Id || a[i].Name != b[i].Name)
            {
                return false;
            }
        }

        return true;
    }
}
