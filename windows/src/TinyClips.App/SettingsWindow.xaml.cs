using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TinyClips.Core.Models;
using TinyClips.Core.Services;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;

namespace TinyClips.App;

public sealed partial class SettingsWindow : Window
{
    public SettingsViewModel ViewModel { get; }
    public bool IsStoreBuild => BuildFlavor.IsStoreBuild;
    public bool IsDirectBuild => BuildFlavor.IsDirectBuild;

    public SettingsWindow()
    {
        ViewModel = new SettingsViewModel(
            App.Services.GetRequiredService<ICaptureSettings>(),
            App.Services.GetRequiredService<IHotKeyService>(),
            App.Services.GetRequiredService<ILaunchAtLoginService>(),
            App.Services.GetRequiredService<IAudioDeviceService>(),
            App.Services.GetRequiredService<IClipStorageService>());

        InitializeComponent();
        ApplyBuildFlavorVisibility();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        SettingsNavigation.SelectedItem = GeneralNavigationItem;
        ShowSettingsSection("General");

        AppWindow.Resize(new SizeInt32(1040, 820));

        ApplyTheme();
        UpdateMouseClickPreview();
        UpdateGifMouseClickPreview();
        UpdateAboutInfo();
        ViewModel.ThemeChanged += ApplyTheme;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        RootGrid.Loaded += OnRootGridLoaded;
        Closed += OnClosed;
    }

    // Runs after the first layout pass, once the controls' initial TwoWay binding
    // write-backs have settled. Re-syncs the view model from persisted storage so the
    // fields show real values, then re-applies anything that depends on them.
    private void OnRootGridLoaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= OnRootGridLoaded;
        ViewModel.CompleteInitialization();
        ApplyTheme();
        UpdateMouseClickPreview();
        UpdateGifMouseClickPreview();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        ViewModel.ThemeChanged -= ApplyTheme;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        RootGrid.Loaded -= OnRootGridLoaded;
        Closed -= OnClosed;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.MouseClickPreviewColorHex) ||
            e.PropertyName == nameof(SettingsViewModel.VideoMouseClickColorHex))
        {
            UpdateMouseClickPreview();
        }

        if (e.PropertyName == nameof(SettingsViewModel.GifMouseClickPreviewColorHex) ||
            e.PropertyName == nameof(SettingsViewModel.GifMouseClickColorHex) ||
            e.PropertyName == nameof(SettingsViewModel.GifMouseClicksUseVideoSettings) ||
            e.PropertyName == nameof(SettingsViewModel.VideoMouseClickColorHex))
        {
            UpdateGifMouseClickPreview();
        }
    }

    private void UpdateMouseClickPreview()
    {
        MouseClickPreviewRing.Stroke = new SolidColorBrush(ParseHexColor(ViewModel.MouseClickPreviewColorHex));
    }

    private void UpdateGifMouseClickPreview()
    {
        GifMouseClickPreviewRing.Stroke = new SolidColorBrush(ParseHexColor(ViewModel.GifMouseClickPreviewColorHex));
    }

    private static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private void OnVideoColorFlyoutOpening(object? sender, object e)
    {
        VideoColorPicker.Color = ParseHexColor(ViewModel.VideoMouseClickColorHex);
    }

    private void OnVideoColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        ViewModel.VideoMouseClickColorHex = ToHex(args.NewColor);
    }

    private void OnGifColorFlyoutOpening(object? sender, object e)
    {
        GifColorPicker.Color = ParseHexColor(ViewModel.GifMouseClickColorHex);
    }

    private void OnGifColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        ViewModel.GifMouseClickColorHex = ToHex(args.NewColor);
    }

    private static CaptureType TypeFromTag(object? tag) => (tag as string) switch
    {
        "Video" => CaptureType.Video,
        "Gif" => CaptureType.Gif,
        _ => CaptureType.Screenshot,
    };

    private async void OnEditHotKey(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            await RecordShortcutAsync(TypeFromTag(element.Tag));
        }
    }

    private void OnResetHotKey(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element)
        {
            return;
        }

        ViewModel.ResetHotKey(TypeFromTag(element.Tag));
        (App.Current as App)?.ReapplyGlobalHotKeys();
    }

    private async Task RecordShortcutAsync(CaptureType type)
    {
        var prompt = new TextBlock
        {
            Text = "Press the key combination you want (include Ctrl, Alt, Shift, or Win).",
            TextWrapping = TextWrapping.Wrap,
        };

        var dialog = new ContentDialog
        {
            Title = "Set shortcut",
            Content = prompt,
            CloseButtonText = "Cancel",
            XamlRoot = Content.XamlRoot,
        };

        HotKeyModifiers chosenModifiers = 0;
        uint chosenKey = 0;

        void OnKey(object s, KeyRoutedEventArgs args)
        {
            args.Handled = true;

            if (IsModifierKey(args.Key))
            {
                return;
            }

            var modifiers = CurrentModifiers();
            if (modifiers == 0)
            {
                prompt.Text = "Please include at least one modifier (Ctrl, Alt, Shift, or Win).";
                return;
            }

            chosenModifiers = modifiers;
            chosenKey = (uint)args.Key;
            dialog.Hide();
        }

        dialog.KeyDown += OnKey;
        await dialog.ShowAsync();
        dialog.KeyDown -= OnKey;

        if (chosenKey != 0)
        {
            ViewModel.SetHotKey(type, chosenModifiers, chosenKey);
            (App.Current as App)?.ReapplyGlobalHotKeys();
        }
    }

    private static bool IsModifierKey(VirtualKey key) => key is
        VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl or
        VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift or
        VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu or
        VirtualKey.LeftWindows or VirtualKey.RightWindows;

    private static HotKeyModifiers CurrentModifiers()
    {
        HotKeyModifiers modifiers = 0;
        if (IsKeyDown(VirtualKey.Control))
        {
            modifiers |= HotKeyModifiers.Control;
        }

        if (IsKeyDown(VirtualKey.Shift))
        {
            modifiers |= HotKeyModifiers.Shift;
        }

        if (IsKeyDown(VirtualKey.Menu))
        {
            modifiers |= HotKeyModifiers.Alt;
        }

        if (IsKeyDown(VirtualKey.LeftWindows) || IsKeyDown(VirtualKey.RightWindows))
        {
            modifiers |= HotKeyModifiers.Win;
        }

        return modifiers;
    }

    private static bool IsKeyDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private static Color ParseHexColor(string? hex)
    {
        var s = (hex ?? string.Empty).Trim().TrimStart('#');
        if (s.Length == 8)
        {
            s = s[2..];
        }

        if (s.Length == 6 &&
            byte.TryParse(s.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) &&
            byte.TryParse(s.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) &&
            byte.TryParse(s.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
        {
            return Color.FromArgb(255, r, g, b);
        }

        return Color.FromArgb(255, 255, 214, 10);
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
        if (args.SelectedItem is NavigationViewItem { Tag: string sectionTag })
        {
            ShowSettingsSection(sectionTag);
        }
    }

    private void UpdateAboutInfo()
    {
        var version = "1.0.0";
        try
        {
            var v = Windows.ApplicationModel.Package.Current.Id.Version;
            version = $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
        }
        catch
        {
            // Unpackaged runs can't query the package version; fall back to the assembly version.
            var asmVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            if (asmVersion is not null)
            {
                version = asmVersion.ToString();
            }
        }

        AboutVersionText.Text = $"Version {version}";
        AboutIssueLink.NavigateUri = BuildIssueRequestUri(version);
        AboutCopyrightText.Text = $"© {DateTime.Now.Year} Refractored LLC";
    }

    private static Uri BuildIssueRequestUri(string version)
    {
        const string repositoryIssuesNewUrl = "https://github.com/jamesmontemagno/tiny-clips/issues/new";
        var runtime = RuntimeInformation.OSDescription;
        var body =
            "### Details" + "\n" +
            $"- App: Tiny Clips for Windows" + "\n" +
            $"- Version: {version}" + "\n" +
            $"- OS: {runtime}" + "\n\n" +
            "### Describe your issue or feature request" + "\n" +
            "<!-- Tell us what happened or what you'd like to see -->";

        var title = "[Issue/Feature]: ";
        var query = $"title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(body)}";
        return new Uri($"{repositoryIssuesNewUrl}?{query}");
    }

    private void ShowSettingsSection(string sectionTag)
    {
        GeneralSection.Visibility = sectionTag == "General" ? Visibility.Visible : Visibility.Collapsed;
        ScreenshotSection.Visibility = sectionTag == "Screenshot" ? Visibility.Visible : Visibility.Collapsed;
        VideoSection.Visibility = sectionTag == "Video" ? Visibility.Visible : Visibility.Collapsed;
        GifSection.Visibility = sectionTag == "Gif" ? Visibility.Visible : Visibility.Collapsed;
        MouseClicksSection.Visibility = sectionTag == "MouseClicks" ? Visibility.Visible : Visibility.Collapsed;
        BrandingSection.Visibility = sectionTag == "Branding" ? Visibility.Visible : Visibility.Collapsed;
        HotkeysSection.Visibility = sectionTag == "Hotkeys" ? Visibility.Visible : Visibility.Collapsed;
        AboutSection.Visibility = sectionTag == "About" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyBuildFlavorVisibility()
    {
        DirectBuildUpdatesCard.Visibility = IsDirectBuild ? Visibility.Visible : Visibility.Collapsed;
        StoreBuildUpdatesCard.Visibility = IsStoreBuild ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnBrowseSaveDirectory(object sender, RoutedEventArgs e)
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
