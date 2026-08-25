using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TinyClips.App.Settings;
using TinyClips.App.Settings.Sections;
using TinyClips.Core.Models;
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
    private const int MinimumWidthDip = 480;
    private const int MinimumHeightDip = 640;
    private const ulong MaximumTranscriptSizeInBytes = 1_000_000;

    private readonly Dictionary<SettingsSectionKind, UserControl> _sectionCache = new();
    private XamlRoot? _xamlRoot;

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

        Activated += OnActivatedSetIcon;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // Triggers OnSettingsNavigationSelectionChanged, which lazily constructs and shows the
        // General section — the only section realized at startup.
        SettingsNavigation.SelectedItem = GeneralNavigationItem;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        UpdatePreferredMinimumSize(AppWindowPlacement.GetScaleForWindow(hwnd));

        AppWindowPlacement.CenterInCurrentWorkAreaAtDipSize(AppWindow, hwnd, 1200, 860);

        RootGrid.Loaded += OnRootGridLoaded;
        ApplyTheme();
        ViewModel.ThemeChanged += ApplyTheme;
        Closed += OnClosed;
    }

    private void OnActivatedSetIcon(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnActivatedSetIcon;
        WindowIcon.Apply(AppWindow);
    }

    private void OnRootGridLoaded(object sender, RoutedEventArgs args)
    {
        RootGrid.Loaded -= OnRootGridLoaded;
        _xamlRoot = RootGrid.XamlRoot;
        if (_xamlRoot is null)
        {
            return;
        }

        _xamlRoot.Changed += OnXamlRootChanged;
        UpdatePreferredMinimumSize(_xamlRoot.RasterizationScale);
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        UpdatePreferredMinimumSize(sender.RasterizationScale);
    }

    private void UpdatePreferredMinimumSize(double scale)
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = AppWindowPlacement.DipToPixels(MinimumWidthDip, scale);
            presenter.PreferredMinimumHeight = AppWindowPlacement.DipToPixels(MinimumHeightDip, scale);
        }
    }

    private void OnSettingsNavigationDisplayModeChanged(
        NavigationView sender,
        NavigationViewDisplayModeChangedEventArgs args)
    {
        AppTitleBar.IsPaneToggleButtonVisible =
            args.DisplayMode is NavigationViewDisplayMode.Compact or NavigationViewDisplayMode.Minimal;
    }

    private void OnTitleBarPaneToggleRequested(TitleBar sender, object args)
    {
        SettingsNavigation.IsPaneOpen = !SettingsNavigation.IsPaneOpen;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        RootGrid.Loaded -= OnRootGridLoaded;
        if (_xamlRoot is not null)
        {
            _xamlRoot.Changed -= OnXamlRootChanged;
            _xamlRoot = null;
        }

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
            SettingsSectionKind.Teleprompter => CreateTeleprompterSection(),
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

    private TeleprompterSettingsSection CreateTeleprompterSection()
    {
        var section = new TeleprompterSettingsSection(ViewModel);
        section.LoadTranscriptRequested += OnLoadTranscriptRequested;
        return section;
    }

    // The folder picker must be owned by this window (it needs an HWND via
    // WinRT.Interop.WindowNative), so GeneralSettingsSection only raises a request event and this
    // shell shows the picker on its behalf.
    private async void OnBrowseSaveDirectoryRequested(CaptureType type)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = type == CaptureType.Screenshot
                ? PickerLocationId.PicturesLibrary
                : PickerLocationId.VideosLibrary,
        };
        picker.FileTypeFilter.Add("*");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        StorageFolder? folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            switch (type)
            {
                case CaptureType.Screenshot:
                    ViewModel.ScreenshotSaveDirectory = folder.Path;
                    break;
                case CaptureType.Video:
                    ViewModel.VideoSaveDirectory = folder.Path;
                    break;
                case CaptureType.Gif:
                    ViewModel.GifSaveDirectory = folder.Path;
                    break;
            }
        }
    }

    private async void OnLoadTranscriptRequested(object? sender, EventArgs e)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeFilter.Add(".txt");
        picker.FileTypeFilter.Add(".md");
        picker.FileTypeFilter.Add(".csv");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        try
        {
            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            var properties = await file.GetBasicPropertiesAsync();
            if (properties.Size > MaximumTranscriptSizeInBytes)
            {
                await ShowTranscriptLoadErrorAsync("The selected transcript is larger than 1 MB.");
                return;
            }

            ViewModel.TeleprompterTranscript = await FileIO.ReadTextAsync(file);
        }
        catch (Exception)
        {
            await ShowTranscriptLoadErrorAsync(
                "The selected file could not be loaded. Choose a plain-text file that is 1 MB or smaller.");
        }
    }

    private async Task ShowTranscriptLoadErrorAsync(string message)
    {
        var dialog = new ContentDialog
        {
            CloseButtonText = "Close",
            Content = message,
            DefaultButton = ContentDialogButton.Close,
            Title = "Unable to load transcript",
            XamlRoot = RootGrid.XamlRoot,
        };

        await dialog.ShowAsync();
    }
}
