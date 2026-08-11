using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TinyClips.App.Settings;
using TinyClips.App.Settings.Sections;
using TinyClips.Core.Services;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace TinyClips.App;

/// <summary>
/// The Settings window shell: title bar, shared <see cref="SettingsViewModel"/>, the
/// <see cref="NavigationView"/>, and a single content host. Each section is a focused
/// <see cref="UserControl"/> under <c>Settings/Sections</c> that is constructed lazily on its
/// first navigation and cached afterwards, so only the section the user is looking at (starting
/// with General) is ever realized, and Analytics/Video-only service calls (capture history
/// refresh, microphone/webcam enumeration) only run once their section is first shown.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private readonly Dictionary<SettingsSectionKind, UserControl> _sectionCache = new();

    public SettingsViewModel ViewModel { get; }

    public SettingsWindow()
    {
        ViewModel = new SettingsViewModel(
            App.Services.GetRequiredService<ICaptureSettings>(),
            App.Services.GetRequiredService<IHotKeyService>(),
            App.Services.GetRequiredService<ILaunchAtLoginService>(),
            App.Services.GetRequiredService<IAudioDeviceService>(),
            App.Services.GetRequiredService<IWebcamDeviceEnumerator>(),
            App.Services.GetRequiredService<IClipStorageService>(),
            App.Services.GetRequiredService<IClipAnalyticsService>(),
            App.Services.GetRequiredService<IUploadcareCredentialStore>());

        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // Triggers OnSettingsNavigationSelectionChanged, which lazily constructs and shows the
        // General section — the only section realized at startup.
        SettingsNavigation.SelectedItem = GeneralNavigationItem;

        AppWindow.Resize(new SizeInt32(1200, 820));

        ApplyTheme();
        ViewModel.ThemeChanged += ApplyTheme;
        Closed += OnClosed;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        ViewModel.ThemeChanged -= ApplyTheme;
        Closed -= OnClosed;

        foreach (var section in _sectionCache.Values)
        {
            if (section is ISettingsSectionLifecycle lifecycle)
            {
                lifecycle.NotifyWindowClosed();
            }
        }

        ViewModel.NotifyClosed();
    }

    private void ApplyTheme()
    {
        RootGrid.RequestedTheme = ViewModel.ThemeIndex switch
        {
            1 => ElementTheme.Light,
            2 => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }

    private void OnSettingsNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string sectionTag } &&
            Enum.TryParse<SettingsSectionKind>(sectionTag, out var kind))
        {
            SectionHost.Content = GetOrCreateSection(kind);
        }
    }

    private UserControl GetOrCreateSection(SettingsSectionKind kind)
    {
        if (_sectionCache.TryGetValue(kind, out var existing))
        {
            return existing;
        }

        UserControl section = kind switch
        {
            SettingsSectionKind.General => CreateGeneralSection(),
            SettingsSectionKind.Uploadcare => new UploadcareSettingsSection(ViewModel),
            SettingsSectionKind.Analytics => new AnalyticsSettingsSection(
                ViewModel,
                WinRT.Interop.WindowNative.GetWindowHandle(this)),
            SettingsSectionKind.Screenshot => new ScreenshotSettingsSection(ViewModel),
            SettingsSectionKind.Video => new VideoSettingsSection(ViewModel),
            SettingsSectionKind.Gif => new GifSettingsSection(ViewModel),
            SettingsSectionKind.MouseClicks => new MouseClicksSettingsSection(ViewModel),
            SettingsSectionKind.Branding => new BrandingSettingsSection(ViewModel),
            SettingsSectionKind.Teleprompter => new TeleprompterSettingsSection(ViewModel),
            SettingsSectionKind.Hotkeys => new HotkeysSettingsSection(ViewModel),
            SettingsSectionKind.About => new AboutSettingsSection(ViewModel),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, message: null),
        };

        _sectionCache[kind] = section;
        return section;
    }

    private GeneralSettingsSection CreateGeneralSection()
    {
        var section = new GeneralSettingsSection(ViewModel);
        section.BrowseSaveDirectoryRequested += OnBrowseSaveDirectoryRequested;
        return section;
    }

    // The folder picker must be owned by this window (it needs an HWND via
    // WinRT.Interop.WindowNative), so GeneralSettingsSection only raises a request event and this
    // shell shows the picker on its behalf.
    private async void OnBrowseSaveDirectoryRequested(object? sender, EventArgs e)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
        };
        picker.FileTypeFilter.Add("*");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        StorageFolder? folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            ViewModel.SaveDirectory = folder.Path;
        }
    }
}
