using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using TinyClips.Core.Capture;
using TinyClips.Core.Models;
using TinyClips.Core.Services;
using Windows.Storage;

namespace TinyClips.App;

public partial class App : Application
{
    private static readonly FontFamily FluentIconFont = new("Segoe Fluent Icons");
    private static readonly object NotificationRegistrationGate = new();

    // Segoe Fluent Icons glyphs.
    private const string GlyphScreenshot = "\uE722";
    private const string GlyphVideo = "\uE714";
    private const string GlyphGif = "\uE8B9";
    private const string GlyphStop = "\uE71A";
    private const string GlyphCheckForUpdates = "\uE895";
    private const string GlyphFolder = "\uE8B7";
    private const string GlyphHistory = "\uE81C";
    private const uint MonitorDefaultToNearest = 2;

    private TaskbarIcon? _taskbarIcon;
    private SettingsWindow? _settingsWindow;
    private GuideWindow? _guideWindow;
    private OnboardingWindow? _onboardingWindow;
    private ScreenshotEditorWindow? _editorWindow;
    private Window? _trimmerWindow;
    private string? _lastTrimmerSourcePath;
    private RecordingIndicatorWindow? _recordingIndicator;
    private ProcessingIndicatorWindow? _processingIndicator;
    private RegionIndicatorWindow? _recordingRegionIndicator;
    private CancellationTokenSource? _captureFlowCts;
    private DispatcherTimer? _recordingTimer;
    private DateTime _recordingStartedUtc;
    private TimeSpan _recordingElapsedBeforePause;
    private TargetSelection? _activeRecordingSelection;
    private CaptureType? _activeRecordingType;
    private bool _activeRecordingWasPickerInitiated;
    private CaptureTile? _videoTile;
    private CaptureTile? _gifTile;
    private TrayPopupWindow? _trayPopup;
    private const double TrayPopupWidth = 288;
    private const double TrayPopupHeight = 242;
    private GlobalHotKeyManager? _hotKeyManager;
    private DispatcherQueue? _dispatcher;
    private bool _isExiting;
    private static bool _notificationsRegistered;

    public static IServiceProvider Services { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
        Services = new ServiceCollection()
            .AddTinyClipsCore()
            .AddSingleton<IMediaDevicePermissionService, MediaDevicePermissionService>()
            .BuildServiceProvider();

        ApplyTheme();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        WireRecordingEvents();
        CreateTrayIcon();
        RegisterGlobalHotKeys();
        ShowOnboardingIfNeeded();
        HandleFileActivation();
#if !TINYCLIPS_STORE_BUILD
        _ = RunStartupUpdateCheckAsync();
#endif
    }

    /// <summary>
    /// If the app was launched via "Open with → Tiny Clips" on an image file, open that image
    /// in the screenshot editor. Mirrors the macOS open-in-editor file-activation behaviour.
    /// </summary>
    private void HandleFileActivation()
    {
        try
        {
            var activation = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activation?.Kind != Microsoft.Windows.AppLifecycle.ExtendedActivationKind.File)
            {
                return;
            }

            if (activation.Data is not Windows.ApplicationModel.Activation.IFileActivatedEventArgs fileArgs)
            {
                return;
            }

            foreach (var item in fileArgs.Files)
            {
                if (item is StorageFile file && IsSupportedImage(file.Path))
                {
                    var path = file.Path;
                    _dispatcher?.TryEnqueue(() => OpenScreenshotEditor(path));
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"File activation handling failed: {ex}");
        }
    }

    private static bool IsSupportedImage(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    private void CreateTrayIcon()
    {
        if (_taskbarIcon is not null)
        {
            return;
        }

        var hotKeys = Services.GetRequiredService<IHotKeyService>();

        _trayPopup = new TrayPopupWindow(BuildTrayPopupContent(hotKeys));

        _taskbarIcon = new TaskbarIcon
        {
            ToolTipText = "Tiny Clips",
            IconSource = new BitmapImage(new Uri("ms-appx:///Assets/TrayIcon.ico")),
            NoLeftClickDelay = true,
        };

        // We host our own PowerToys-style popup window rather than a context flyout, so
        // both mouse buttons just open it next to the tray icon.
        var showPopup = new RelayCommand(ShowTrayPopup);
        _taskbarIcon.LeftClickCommand = showPopup;
        _taskbarIcon.RightClickCommand = showPopup;

        _taskbarIcon.ForceCreate();

        UpdateRecordingState();
    }

    private void ShowTrayPopup()
    {
        if (_trayPopup is null)
        {
            return;
        }

        _trayPopup.Content = BuildTrayPopupContent(Services.GetRequiredService<IHotKeyService>());
        UpdateRecordingState();
        _trayPopup.ShowNearCursor(TrayPopupWidth, TrayPopupHeight);
    }

    // PowerToys-style "quick access" popup: three large capture tiles across the top,
    // a divider, then a row of small icon buttons (Settings / Guide / Exit) at the bottom.
    private UIElement BuildTrayPopupContent(IHotKeyService hotKeys)
    {
        void Dismiss() => _trayPopup?.Hide();

        var root = new StackPanel
        {
            Width = TrayPopupWidth,
            Padding = new Thickness(12),
            Spacing = 10,
        };

        root.Children.Add(new TextBlock
        {
            Text = "Tiny Clips",
            Margin = new Thickness(4, 2, 0, 0),
            Style = TextStyle("BodyStrongTextBlockStyle"),
        });

        var tiles = new Grid { ColumnSpacing = 6 };
        for (var i = 0; i < 3; i++)
        {
            tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        var screenshot = CreateCaptureTile(
            "Screenshot",
            GlyphScreenshot,
            hotKeys.GetBinding(CaptureType.Screenshot).DisplayString,
            new AsyncRelayCommand(CaptureScreenshotAsync),
            Dismiss);
        Grid.SetColumn(screenshot.Button, 0);
        tiles.Children.Add(screenshot.Button);

        _videoTile = CreateCaptureTile(
            "Video",
            GlyphVideo,
            hotKeys.GetBinding(CaptureType.Video).DisplayString,
            new AsyncRelayCommand(ToggleVideoAsync),
            Dismiss);
        Grid.SetColumn(_videoTile.Button, 1);
        tiles.Children.Add(_videoTile.Button);

        _gifTile = CreateCaptureTile(
            "GIF",
            GlyphGif,
            hotKeys.GetBinding(CaptureType.Gif).DisplayString,
            new AsyncRelayCommand(ToggleGifAsync),
            Dismiss);
        Grid.SetColumn(_gifTile.Button, 2);
        tiles.Children.Add(_gifTile.Button);

        root.Children.Add(tiles);

        var quickAccess = new Grid { ColumnSpacing = 6 };
        quickAccess.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        quickAccess.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var folders = CreateFolderButton(Dismiss);
        Grid.SetColumn(folders, 0);
        quickAccess.Children.Add(folders);

        var recent = CreateRecentCapturesButton(Dismiss);
        Grid.SetColumn(recent, 1);
        quickAccess.Children.Add(recent);
        root.Children.Add(quickAccess);

        root.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(0, 2, 0, 2),
            Background = ThemeBrush("DividerStrokeColorDefaultBrush"),
        });

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 2,
        };
        footer.Children.Add(CreateFooterButton("\uE713", "Settings", new RelayCommand(OpenSettingsWindow), Dismiss));
        footer.Children.Add(CreateFooterButton("\uE897", "Guide", new RelayCommand(OpenGuideWindow), Dismiss));
#if !TINYCLIPS_STORE_BUILD
        footer.Children.Add(CreateFooterButton(GlyphCheckForUpdates, "Check for updates", new AsyncRelayCommand(CheckForUpdatesFromTrayAsync), Dismiss));
#endif
        footer.Children.Add(CreateFooterButton("\uEA39", "File a Bug", new AsyncRelayCommand(() => OpenQuickBugReportFromTrayAsync(root.XamlRoot)), Dismiss));
        footer.Children.Add(CreateFooterButton("\uE7E8", "Exit", new RelayCommand(() => _ = ExitApplicationAsync()), Dismiss));
        root.Children.Add(footer);

        return new Border
        {
            Child = root,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeBrush("SurfaceStrokeColorDefaultBrush"),
        };
    }

    private ButtonBase CreateFolderButton(Action dismiss)
    {
        var settings = Services.GetRequiredService<ICaptureSettings>();
        var storage = Services.GetRequiredService<IClipStorageService>();

        if (!string.IsNullOrWhiteSpace(settings.SaveDirectory))
        {
            return CreateQuickAccessButton(
                "Open Save Folder",
                GlyphFolder,
                new RelayCommand(() => OpenFolder(storage.OutputDirectory(CaptureType.Screenshot))),
                dismiss);
        }

        var flyout = new MenuFlyout();
        var button = new DropDownButton
        {
            Content = QuickAccessContent(GlyphFolder, "Open folders"),
            Flyout = flyout,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(8, 6, 8, 6),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, "Open capture folders");
        foreach (var type in Enum.GetValues<CaptureType>())
        {
            var capturedType = type;
            var item = new MenuFlyoutItem { Text = $"Open {CaptureTypeLabel(type)} Folder" };
            item.Click += (_, _) =>
            {
                dismiss();
                OpenFolder(storage.OutputDirectory(capturedType));
            };
            flyout.Items.Add(item);
        }
        return button;
    }

    private ButtonBase CreateRecentCapturesButton(Action dismiss)
    {
        var history = Services.GetRequiredService<IRecentCaptureService>();
        var captures = history.GetRecentCaptures();
        var flyout = new MenuFlyout();
        var button = new DropDownButton
        {
            Content = QuickAccessContent(GlyphHistory, captures.Count == 0 ? "No recent captures" : $"Recent ({captures.Count})"),
            Flyout = flyout,
            IsEnabled = captures.Count > 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(8, 6, 8, 6),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, "Recent captures");

        foreach (var capture in captures)
        {
            var capturedItem = capture;
            var item = new MenuFlyoutItem
            {
                Text = $"{Path.GetFileName(capture.Path)} — {CaptureTypeLabel(capture.Type)}, {capture.CapturedAt:g}",
            };
            item.Click += (_, _) =>
            {
                dismiss();
                OpenRecentCapture(capturedItem);
            };
            flyout.Items.Add(item);
        }
        return button;
    }

    private Button CreateQuickAccessButton(string text, string glyph, ICommand command, Action dismiss)
    {
        var button = new Button
        {
            Content = QuickAccessContent(glyph, text),
            Command = command,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(8, 6, 8, 6),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, text);
        button.Click += (_, _) => dismiss();
        return button;
    }

    private static StackPanel QuickAccessContent(string glyph, string text)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        panel.Children.Add(new FontIcon { Glyph = glyph, FontFamily = FluentIconFont, FontSize = 14 });
        panel.Children.Add(new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis });
        return panel;
    }

    private static string CaptureTypeLabel(CaptureType type) => type switch
    {
        CaptureType.Screenshot => "Screenshot",
        CaptureType.Video => "Video",
        CaptureType.Gif => "GIF",
        _ => type.ToString(),
    };

    private sealed class CaptureTile
    {
        public required Button Button { get; init; }
        public required FontIcon Icon { get; init; }
        public required TextBlock Label { get; init; }
    }

    private CaptureTile CreateCaptureTile(string text, string glyph, string? accelerator, ICommand command, Action dismiss)
    {
        var icon = new FontIcon
        {
            Glyph = glyph,
            FontFamily = FluentIconFont,
            FontSize = 22,
        };
        var label = new TextBlock
        {
            Text = text,
            FontSize = 12,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var panel = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(icon);
        panel.Children.Add(label);

        var button = new Button
        {
            Content = panel,
            Command = command,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(4, 14, 4, 14),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, text);
        ToolTipService.SetToolTip(button, string.IsNullOrEmpty(accelerator) ? text : $"{text} ({accelerator})");
        button.Click += (_, _) => dismiss();
        return new CaptureTile { Button = button, Icon = icon, Label = label };
    }

    private Button CreateFooterButton(string glyph, string tooltip, ICommand command, Action dismiss)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontFamily = FluentIconFont, FontSize = 16 },
            Width = 40,
            Height = 36,
            Padding = new Thickness(0),
            Command = command,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
        };
        ToolTipService.SetToolTip(button, tooltip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, tooltip);
        button.Click += (_, _) => dismiss();
        return button;
    }

    private static Style? TextStyle(string key)
        => Application.Current.Resources.TryGetValue(key, out var value) ? value as Style : null;

    private static Brush? ThemeBrush(string key)
        => Application.Current.Resources.TryGetValue(key, out var value) ? value as Brush : null;

    private Task CaptureScreenshotAsync() => BeginCaptureAsync(CaptureType.Screenshot);

    /// <summary>
    /// Shows the capture picker bar (Region / Screen / Window + countdown), resolves the
    /// chosen target, runs the countdown, then performs the capture or starts recording.
    /// </summary>
    private async Task BeginCaptureAsync(CaptureType type, bool abortIfRecording = false)
    {
        if (_captureFlowCts is not null)
        {
            return;
        }

        var captureFlowCts = new CancellationTokenSource();
        _captureFlowCts = captureFlowCts;
        try
        {
            // Give the tray menu a moment to dismiss so it isn't part of the capture.
            await Task.Delay(150);

            // For an auto-reopened picker, bail out if a recording started during the delay.
            if (abortIfRecording && (_isExiting || IsAnyRecordingActive()))
            {
                return;
            }

            var settings = Services.GetRequiredService<ICaptureSettings>();
            var (cdEnabled, cdDuration) = GetCountdown(settings, type);
            var wasPickerInitiated = settings.ShouldShowCapturePicker(type);

            var pick = wasPickerInitiated
                ? await CapturePickerWindow.RunAsync(type, cdEnabled, cdDuration, settings.VideoRecordingTimeLimitMinutes)
                : new CapturePickerResult(
                    CapturePickerMode.Region,
                    cdEnabled,
                    cdDuration,
                    settings.VideoRecordingTimeLimitMinutes);
            if (pick is null)
            {
                return;
            }

            var resolved = await ResolveTargetAsync(pick.Mode);
            if (resolved is not { } selection)
            {
                return;
            }

            RecordingSetupResult? recordingSetup = null;
            if (type is CaptureType.Video or CaptureType.Gif)
            {
                recordingSetup = await ShowRecordingSetupAsync(type, selection, settings);
                if (recordingSetup is null)
                {
                    CloseRecordingRegionIndicator();
                    return;
                }

                ApplyRecordingSetup(type, recordingSetup, settings);
            }

            var showDisabledStopDuringCountdown = type is CaptureType.Video or CaptureType.Gif
                && pick.CountdownEnabled
                && pick.CountdownDuration > 0;
            if (showDisabledStopDuringCountdown)
            {
                ShowRecordingIndicator(type, selection, stopEnabled: false, startTimer: false);
            }

            RegionIndicatorWindow? regionIndicator = null;
            if (pick.CountdownEnabled && pick.CountdownDuration > 0)
            {
                try
                {
                    var recordingRegionAlreadyShown = type is CaptureType.Video or CaptureType.Gif
                        && _recordingRegionIndicator is not null;
                    if (selection.Region is { } region && !recordingRegionAlreadyShown)
                    {
                        regionIndicator = new RegionIndicatorWindow();
                        regionIndicator.Show(ToVirtualDesktopRegion(selection.Target, region));
                    }

                    await CountdownWindow.RunAsync(pick.CountdownDuration, selection.Monitor, captureFlowCts.Token);
                }
                finally
                {
                    regionIndicator?.ClosePanel();
                }
            }

            switch (type)
            {
                case CaptureType.Screenshot:
                    captureFlowCts.Token.ThrowIfCancellationRequested();
                    var screenshots = Services.GetRequiredService<IScreenshotService>();
                    var path = await screenshots.CaptureTargetAsync(selection.Target, selection.Region);
                    Services.GetRequiredService<IRecentCaptureService>().Record(path, CaptureType.Screenshot);
                    await CopyToClipboardAsync(path, CaptureType.Screenshot);
                    if (settings.ShowScreenshotEditor)
                    {
                        OpenScreenshotEditor(path, reopenPickerAfterClose: wasPickerInitiated);
                    }
                    else
                    {
                        RevealInExplorer(path);
                        ShowSaveToast(path);
                        ReopenPickerAfterCaptureIfNeeded(CaptureType.Screenshot, wasPickerInitiated);
                    }
                    break;

                case CaptureType.Video:
                    captureFlowCts.Token.ThrowIfCancellationRequested();
                    settings.VideoRecordingTimeLimitMinutes = (int)Math.Round(Math.Max(0, pick.VideoTimeLimitMinutes));
                    _activeRecordingSelection = selection;
                    _activeRecordingType = CaptureType.Video;
                    _activeRecordingWasPickerInitiated = wasPickerInitiated;
                    ShowRecordingRegionIndicator(selection);
                    if (!showDisabledStopDuringCountdown)
                    {
                        ShowRecordingIndicator(CaptureType.Video, selection);
                    }
                    await Services.GetRequiredService<IVideoRecordingService>()
                        .StartAsync(selection.Target, selection.Region, pick.VideoTimeLimitMinutes, captureFlowCts.Token);
                    ActivateRecordingIndicatorForStartedCapture();
                    UpdateRecordingState();
                    break;

                case CaptureType.Gif:
                    captureFlowCts.Token.ThrowIfCancellationRequested();
                    _activeRecordingSelection = selection;
                    _activeRecordingType = CaptureType.Gif;
                    _activeRecordingWasPickerInitiated = wasPickerInitiated;
                    ShowRecordingRegionIndicator(selection);
                    if (!showDisabledStopDuringCountdown)
                    {
                        ShowRecordingIndicator(CaptureType.Gif, selection);
                    }
                    await Services.GetRequiredService<IGifRecordingService>()
                        .StartAsync(selection.Target, selection.Region, captureFlowCts.Token);
                    ActivateRecordingIndicatorForStartedCapture();
                    UpdateRecordingState();
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            CloseRecordingRegionIndicator();
            HideRecordingIndicatorIfNotRecording();
            _activeRecordingSelection = null;
            _activeRecordingType = null;
            _activeRecordingWasPickerInitiated = false;
            UpdateRecordingState();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Capture failed: {ex}");
            UpdateRecordingState();
            CloseRecordingRegionIndicator();
            _activeRecordingSelection = null;
            _activeRecordingType = null;
            _activeRecordingWasPickerInitiated = false;
            HideRecordingIndicatorIfNotRecording();
        }
        finally
        {
            if (ReferenceEquals(_captureFlowCts, captureFlowCts))
            {
                _captureFlowCts = null;
            }

            captureFlowCts.Dispose();
        }
    }

    private async Task<RecordingSetupResult?> ShowRecordingSetupAsync(CaptureType type, TargetSelection selection, ICaptureSettings settings)
    {
        PixelRect? region = null;
        if (selection.Region is { } selectedRegion)
        {
            region = ToVirtualDesktopRegion(selection.Target, selectedRegion);
            RegionIndicatorWindow? setupRegionIndicator = null;
            try
            {
                if (settings.ShowRegionIndicator)
                {
                    setupRegionIndicator = ShowRegionIndicator(selection);
                }

                var monitor = selection.Monitor ?? ResolveMonitorForTarget(selection.Target);
                var audioDevices = Services.GetRequiredService<IAudioDeviceService>();
                var webcamDevices = Services.GetRequiredService<IWebcamDeviceEnumerator>();
                var mediaPermissions = Services.GetRequiredService<IMediaDevicePermissionService>();
                return await RecordingSetupWindow.RunAsync(
                    type,
                    settings,
                    audioDevices,
                    webcamDevices,
                    mediaPermissions,
                    monitor,
                    region);
            }
            finally
            {
                setupRegionIndicator?.ClosePanel();
            }
        }

        var setupMonitor = selection.Monitor ?? ResolveMonitorForTarget(selection.Target);
        var setupAudioDevices = Services.GetRequiredService<IAudioDeviceService>();
        var setupWebcamDevices = Services.GetRequiredService<IWebcamDeviceEnumerator>();
        var setupMediaPermissions = Services.GetRequiredService<IMediaDevicePermissionService>();
        return await RecordingSetupWindow.RunAsync(
            type,
            settings,
            setupAudioDevices,
            setupWebcamDevices,
            setupMediaPermissions,
            setupMonitor,
            region);
    }

    private static void ApplyRecordingSetup(CaptureType type, RecordingSetupResult setup, ICaptureSettings settings)
    {
        settings.SetShowMouseClickVisuals(setup.ShowMouseClicks, type);

        if (type != CaptureType.Video)
        {
            return;
        }

        settings.RecordAudio = setup.RecordSystemAudio;
        settings.RecordMicrophone = setup.RecordMicrophone;
        settings.SelectedMicrophoneId = setup.SelectedMicrophoneId;
        settings.WebcamEnabled = setup.WebcamEnabled;
        settings.SelectedWebcamId = setup.SelectedWebcamId;
        settings.WebcamShape = setup.WebcamShape;
        settings.WebcamSizePreset = setup.WebcamSizePreset;
        settings.WebcamCornerPosition = setup.WebcamCornerPosition;
        settings.WebcamCornerRadius = setup.WebcamCornerRadius;
    }

    private async Task<TargetSelection?> ResolveTargetAsync(CapturePickerMode mode)
    {
        var monitors = Services.GetRequiredService<IMonitorService>();
        var settings = Services.GetRequiredService<ICaptureSettings>();

        switch (mode)
        {
            case CapturePickerMode.Region:
            {
                var all = monitors.GetMonitors();
                if (all.Count == 0)
                {
                    return null;
                }

                var preferredMonitor = ResolvePreferredMonitor(monitors, settings.MultiMonitorCaptureMode);
                if (preferredMonitor is { } single)
                {
                    var region = await RegionSelectWindow.RunAsync(single);
                    return region is { } singleRegion
                        ? new TargetSelection(CaptureTarget.Monitor(single.HMonitor), singleRegion, single)
                        : null;
                }

                var result = await RegionSelectController.RunAsync(all);
                if (result is not { } selection)
                {
                    return null;
                }

                var selectedMonitor = all.FirstOrDefault(m => m.HMonitor == selection.HMonitor)
                    ?? monitors.GetPrimaryMonitor();
                return new TargetSelection(CaptureTarget.Monitor(selection.HMonitor), selection.Region, selectedMonitor);
            }

            case CapturePickerMode.Screen:
            {
                var all = monitors.GetMonitors();
                if (all.Count == 0)
                {
                    return null;
                }

                var chosen = ResolvePreferredMonitor(monitors, settings.MultiMonitorCaptureMode);
                if (chosen is null)
                {
                    chosen = all.Count <= 1
                        ? all.FirstOrDefault() ?? monitors.GetPrimaryMonitor()
                        : await ScreenPickerWindow.RunAsync(all);
                }

                return chosen is { } monitor
                    ? new TargetSelection(CaptureTarget.Monitor(monitor.HMonitor), null, monitor)
                    : null;
            }

            case CapturePickerMode.Window:
            {
                var hwnd = await WindowPickerWindow.RunAsync();
                return hwnd is { } h
                    ? new TargetSelection(CaptureTarget.Window(h), null, null)
                    : null;
            }

            default:
                return null;
        }
    }

    private static (bool Enabled, int Duration) GetCountdown(ICaptureSettings settings, CaptureType type) => type switch
    {
        CaptureType.Video => (settings.VideoCountdownEnabled, settings.VideoCountdownDuration),
        CaptureType.Gif => (settings.GifCountdownEnabled, settings.GifCountdownDuration),
        _ => (settings.ScreenshotCountdownEnabled, settings.ScreenshotCountdownDuration),
    };

    private static PixelRect ToVirtualDesktopRegion(CaptureTarget target, PixelRect region)
    {
        if (target.HMonitor == 0)
        {
            return region;
        }

        var monitors = Services.GetRequiredService<IMonitorService>().GetMonitors();
        var monitor = monitors.FirstOrDefault(m => m.HMonitor == target.HMonitor);
        return monitor is null
            ? region
            : region with { X = monitor.X + region.X, Y = monitor.Y + region.Y };
    }

    private static MonitorInfo? ResolvePreferredMonitor(IMonitorService monitors, MultiMonitorCaptureMode mode) => mode switch
    {
        MultiMonitorCaptureMode.UnderCursor => monitors.GetMonitorUnderCursor() ?? monitors.GetPrimaryMonitor(),
        MultiMonitorCaptureMode.MainDisplay => monitors.GetPrimaryMonitor(),
        _ => null,
    };

    private static MonitorInfo? ResolveMonitorForTarget(CaptureTarget target)
    {
        var monitorService = Services.GetRequiredService<IMonitorService>();

        if (target.HMonitor != 0)
        {
            var monitors = monitorService.GetMonitors();
            return monitors.FirstOrDefault(m => m.HMonitor == target.HMonitor)
                ?? monitorService.GetPrimaryMonitor();
        }

        if (target.Hwnd != 0)
        {
            return ResolveMonitorForWindowTarget(target.Hwnd) ?? monitorService.GetPrimaryMonitor();
        }

        return monitorService.GetPrimaryMonitor();
    }

    private static MonitorInfo? ResolveMonitorForWindowTarget(nint hwnd)
    {
        var monitorService = Services.GetRequiredService<IMonitorService>();
        var hMonitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (hMonitor == nint.Zero)
        {
            return monitorService.GetPrimaryMonitor();
        }

        var monitors = monitorService.GetMonitors();
        return monitors.FirstOrDefault(m => m.HMonitor == hMonitor) ?? monitorService.GetPrimaryMonitor();
    }

    private readonly record struct TargetSelection(CaptureTarget Target, PixelRect? Region, MonitorInfo? Monitor);

    private async Task ToggleVideoAsync()
    {
        var video = Services.GetRequiredService<IVideoRecordingService>();
        var gif = Services.GetRequiredService<IGifRecordingService>();

        try
        {
            if (video.IsRecording)
            {
                await StopActiveRecordingAsync();
                return;
            }

            if (gif.IsRecording)
            {
                return;
            }

            await BeginCaptureAsync(CaptureType.Video);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Video recording toggle failed: {ex}");
            UpdateRecordingState();
        }
    }

    private async Task ToggleGifAsync()
    {
        var video = Services.GetRequiredService<IVideoRecordingService>();
        var gif = Services.GetRequiredService<IGifRecordingService>();

        try
        {
            if (gif.IsRecording)
            {
                await StopActiveRecordingAsync();
                return;
            }

            if (video.IsRecording)
            {
                return;
            }

            await BeginCaptureAsync(CaptureType.Gif);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"GIF recording toggle failed: {ex}");
            UpdateRecordingState();
            HideRecordingIndicatorIfNotRecording();
        }
    }

    private void WireRecordingEvents()
    {
        var video = Services.GetRequiredService<IVideoRecordingService>();
        var gif = Services.GetRequiredService<IGifRecordingService>();
        video.RecordingCompleted += OnRecordingCompleted;
        gif.RecordingCompleted += OnRecordingCompleted;
        video.WebcamCaptureFailed += OnWebcamCaptureFailed;
    }

    private void OnWebcamCaptureFailed(object? sender, string reason)
    {
        _dispatcher?.TryEnqueue(() => ShowWebcamFailureNotification(reason));
    }

    private static void ShowWebcamFailureNotification(string reason)
    {
        try
        {
            EnsureNotificationsRegistered();
            var notification = new AppNotificationBuilder()
                .AddText("Webcam unavailable")
                .AddText(reason)
                .BuildNotification();

            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to show webcam failure notification: {ex}");
        }
    }

    private async Task RunStartupUpdateCheckAsync()
    {
        var result = await CheckForUpdatesAsync(isManualCheck: false);
        if (result.Status == AppUpdateStatus.UpdateAvailable)
        {
            ShowUpdateCheckNotification(
                "Update available",
                $"Tiny Clips {result.LatestVersion} is available. Open Settings > About to update.");
        }
    }

    private async Task CheckForUpdatesFromTrayAsync()
    {
        var result = await CheckForUpdatesAsync(isManualCheck: true);
        switch (result.Status)
        {
            case AppUpdateStatus.UpToDate:
                ShowUpdateCheckNotification("You're up to date", $"Tiny Clips {result.CurrentVersion} is current.");
                break;
            case AppUpdateStatus.UpdateAvailable:
                ShowUpdateCheckNotification("Update available", $"Tiny Clips {result.LatestVersion} is available. Open Settings > About to update.");
                break;
            default:
                ShowUpdateCheckNotification("Couldn't check for updates", result.Message ?? "Please try again later.");
                break;
        }
    }

    private async Task<AppUpdateCheckResult> CheckForUpdatesAsync(bool isManualCheck)
    {
        var currentVersion = AppVersionInfo.GetCurrentVersion();
        try
        {
            var updateService = Services.GetRequiredService<IAppUpdateService>();
            var result = await updateService.CheckForUpdatesAsync(currentVersion);
            Debug.WriteLine($"Update check ({(isManualCheck ? "manual" : "startup")}): {result.Status}, current={result.CurrentVersion}, latest={result.LatestVersion}");
            return result;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Update check ({(isManualCheck ? "manual" : "startup")}) failed unexpectedly: {ex}");
            return AppUpdateCheckResult.Failed(currentVersion, "Unexpected error while checking for updates.");
        }
    }

    private void OnRecordingCompleted(object? sender, string? path)
    {
        _dispatcher?.TryEnqueue(async () =>
        {
            UpdateRecordingState();
            HideRecordingIndicator();
            HideProcessingIndicator();
            CloseRecordingRegionIndicator();
            _activeRecordingSelection = null;
            _activeRecordingType = null;
            var wasPickerInitiated = _activeRecordingWasPickerInitiated;
            _activeRecordingWasPickerInitiated = false;
            if (_isExiting)
            {
                return;
            }

            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            var type = Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase)
                ? CaptureType.Gif
                : CaptureType.Video;
            Services.GetRequiredService<IRecentCaptureService>().Record(path, type);

            var settings = Services.GetRequiredService<ICaptureSettings>();
            var showTrimmer = type == CaptureType.Gif ? settings.ShowGifTrimmer : settings.ShowTrimmer;
            if (showTrimmer)
            {
                OpenTrimmer(path, type, pickerInitiated: wasPickerInitiated);
            }
            else
            {
                await FinalizeClipAsync(path, type);
                ReopenPickerAfterCaptureIfNeeded(type, wasPickerInitiated);
            }
        });
    }

    private async Task StopActiveRecordingAsync()
    {
        try
        {
            var video = Services.GetRequiredService<IVideoRecordingService>();
            var gif = Services.GetRequiredService<IGifRecordingService>();

            if (video.IsRecording)
            {
                HideRecordingIndicator();
                ShowProcessingIndicator(CaptureType.Video, _activeRecordingSelection);
                await video.StopAsync();
            }
            else if (gif.IsRecording)
            {
                HideRecordingIndicator();
                ShowProcessingIndicator(CaptureType.Gif, _activeRecordingSelection);
                await gif.StopAsync();
            }
            else
            {
                CancelCaptureFlow();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to stop active recording: {ex}");
        }
        finally
        {
            HideProcessingIndicator();
            UpdateRecordingState();
            CloseRecordingRegionIndicatorIfNotRecording();
            HideRecordingIndicatorIfNotRecording();
            _activeRecordingSelection = null;
            _activeRecordingType = null;
            _activeRecordingWasPickerInitiated = false;
        }
    }

    private async Task PauseActiveRecordingAsync()
    {
        var video = Services.GetRequiredService<IVideoRecordingService>();
        var gif = Services.GetRequiredService<IGifRecordingService>();

        if (video.IsRecording)
        {
            if (video.IsPaused)
            {
                return;
            }

            await video.PauseAsync();
        }
        else if (gif.IsRecording)
        {
            if (gif.IsPaused)
            {
                return;
            }

            await gif.PauseAsync();
        }
        else
        {
            return;
        }

        _recordingElapsedBeforePause += DateTime.UtcNow - _recordingStartedUtc;
        StopRecordingTimer();
        _recordingIndicator?.UpdateElapsed(_recordingElapsedBeforePause);
        _recordingIndicator?.SetPaused(true);
    }

    private async Task ResumeActiveRecordingAsync()
    {
        var video = Services.GetRequiredService<IVideoRecordingService>();
        var gif = Services.GetRequiredService<IGifRecordingService>();

        if (video.IsRecording)
        {
            if (!video.IsPaused)
            {
                return;
            }

            await video.ResumeAsync();
        }
        else if (gif.IsRecording)
        {
            if (!gif.IsPaused)
            {
                return;
            }

            await gif.ResumeAsync();
        }
        else
        {
            return;
        }

        _recordingStartedUtc = DateTime.UtcNow;
        _recordingIndicator?.SetPaused(false);
        StartRecordingTimer();
    }

    private async Task RestartActiveRecordingAsync()
    {
        if (_activeRecordingSelection is not { } selection || _activeRecordingType is not { } type)
        {
            return;
        }

        await DiscardActiveRecordingAsync(clearActiveSelection: false);
        _activeRecordingSelection = selection;
        _activeRecordingType = type;
        ShowRecordingRegionIndicator(selection);
        ShowRecordingIndicator(type, selection, stopEnabled: false, startTimer: false);

        if (type == CaptureType.Video)
        {
            var settings = Services.GetRequiredService<ICaptureSettings>();
            await Services.GetRequiredService<IVideoRecordingService>()
                .StartAsync(selection.Target, selection.Region, settings.VideoRecordingTimeLimitMinutes);
        }
        else
        {
            await Services.GetRequiredService<IGifRecordingService>().StartAsync(selection.Target, selection.Region);
        }

        ActivateRecordingIndicatorForStartedCapture();
        UpdateRecordingState();
    }

    private Task DiscardActiveRecordingAsync() => DiscardActiveRecordingAsync(clearActiveSelection: true);

    private async Task DiscardActiveRecordingAsync(bool clearActiveSelection)
    {
        try
        {
            var video = Services.GetRequiredService<IVideoRecordingService>();
            var gif = Services.GetRequiredService<IGifRecordingService>();

            if (video.IsRecording)
            {
                await video.CancelAsync();
            }
            else if (gif.IsRecording)
            {
                await gif.CancelAsync();
            }
            else
            {
                CancelCaptureFlow();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to discard active recording: {ex}");
        }
        finally
        {
            HideProcessingIndicator();
            HideRecordingIndicator();
            CloseRecordingRegionIndicator();
            UpdateRecordingState();
            if (clearActiveSelection)
            {
                _activeRecordingSelection = null;
                _activeRecordingType = null;
            }
        }
    }

    private void ShowRecordingRegionIndicator(TargetSelection selection)
    {
        if (selection.Region is null)
        {
            return;
        }

        CloseRecordingRegionIndicator();
        var indicator = ShowRegionIndicator(selection)!;
        _recordingRegionIndicator = indicator;
        indicator.Closed += (_, _) =>
        {
            if (ReferenceEquals(_recordingRegionIndicator, indicator))
            {
                _recordingRegionIndicator = null;
            }
        };
    }

    private RegionIndicatorWindow? ShowRegionIndicator(TargetSelection selection)
    {
        if (selection.Region is not { } region)
        {
            return null;
        }

        var indicator = new RegionIndicatorWindow();
        indicator.Show(ToVirtualDesktopRegion(selection.Target, region));
        return indicator;
    }

    private void CloseRecordingRegionIndicatorIfNotRecording()
    {
        var video = Services.GetRequiredService<IVideoRecordingService>();
        var gif = Services.GetRequiredService<IGifRecordingService>();

        if (!video.IsRecording && !gif.IsRecording)
        {
            CloseRecordingRegionIndicator();
        }
    }

    private void CloseRecordingRegionIndicator()
    {
        var window = _recordingRegionIndicator;
        _recordingRegionIndicator = null;
        window?.ClosePanel();
    }

    private void CancelCaptureFlow()
    {
        _captureFlowCts?.Cancel();
    }

    private void ShowRecordingIndicator(CaptureType type, TargetSelection selection, bool stopEnabled = true, bool startTimer = true)
    {
        HideRecordingIndicator();

        var hotKeys = Services.GetRequiredService<IHotKeyService>();
        var settings = Services.GetRequiredService<ICaptureSettings>();
        var window = new RecordingIndicatorWindow(hotKeys.StopRecordingDisplayString);
        window.StopRequested = () => _ = StopActiveRecordingAsync();
        window.PauseRequested = () => _ = PauseActiveRecordingAsync();
        window.ResumeRequested = () => _ = ResumeActiveRecordingAsync();
        window.RestartRequested = () => _ = RestartActiveRecordingAsync();
        window.DiscardRequested = () => _ = DiscardActiveRecordingAsync();
        window.SystemAudioMuteChanged = muted =>
            Services.GetRequiredService<IVideoRecordingService>().SetSystemAudioMuted(muted);
        window.MicrophoneMuteChanged = muted =>
            Services.GetRequiredService<IVideoRecordingService>().SetMicrophoneMuted(muted);
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_recordingIndicator, window))
            {
                StopRecordingTimer();
                _recordingIndicator = null;
            }
        };

        _recordingIndicator = window;
        window.UpdateElapsed(TimeSpan.Zero);
        window.SetStopEnabled(stopEnabled);

        var monitor = selection.Monitor ?? ResolveMonitorForTarget(selection.Target);
        PixelRect? region = null;
        if (selection.Region is { } selectedRegion)
        {
            region = ToVirtualDesktopRegion(selection.Target, selectedRegion);
        }
        window.ShowNear(monitor, region);

        if (startTimer)
        {
            _recordingStartedUtc = DateTime.UtcNow;
            _recordingElapsedBeforePause = TimeSpan.Zero;
            StartRecordingTimer();
        }
    }

    private void ActivateRecordingIndicatorForStartedCapture()
    {
        _recordingStartedUtc = DateTime.UtcNow;
        _recordingElapsedBeforePause = TimeSpan.Zero;
        _recordingIndicator?.SetStopEnabled(true);
        var video = Services.GetRequiredService<IVideoRecordingService>();
        _recordingIndicator?.ConfigureAudioControls(
            video.CanMuteSystemAudio,
            video.IsSystemAudioMuted,
            video.CanMuteMicrophone,
            video.IsMicrophoneMuted);
        StartRecordingTimer();
    }

    private void StartRecordingTimer()
    {
        StopRecordingTimer();
        _recordingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _recordingTimer.Tick += OnRecordingTimerTick;
        _recordingTimer.Start();
    }

    private void OnRecordingTimerTick(object? sender, object e)
    {
        _recordingIndicator?.UpdateElapsed(_recordingElapsedBeforePause + (DateTime.UtcNow - _recordingStartedUtc));
    }

    private void HideRecordingIndicatorIfNotRecording()
    {
        var video = Services.GetRequiredService<IVideoRecordingService>();
        var gif = Services.GetRequiredService<IGifRecordingService>();

        if (!video.IsRecording && !gif.IsRecording)
        {
            HideRecordingIndicator();
        }
    }

    private void HideRecordingIndicator()
    {
        StopRecordingTimer();

        var window = _recordingIndicator;
        _recordingIndicator = null;
        window?.ClosePanel();
    }

    private void ShowProcessingIndicator(CaptureType type, TargetSelection? selection = null)
    {
        HideProcessingIndicator();

        var window = new ProcessingIndicatorWindow(type);
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_processingIndicator, window))
            {
                _processingIndicator = null;
            }
        };

        _processingIndicator = window;
        var monitor = selection is { } current ? current.Monitor ?? ResolveMonitorForTarget(current.Target) : null;
        PixelRect? region = null;
        if (selection is { Region: { } selectedRegion, Target: { } selectedTarget })
        {
            region = ToVirtualDesktopRegion(selectedTarget, selectedRegion);
        }
        window.ShowNear(monitor, region);
    }

    private void HideProcessingIndicator()
    {
        var window = _processingIndicator;
        _processingIndicator = null;
        window?.ClosePanel();
    }

    private void StopRecordingTimer()
    {
        if (_recordingTimer is null)
        {
            return;
        }

        _recordingTimer.Stop();
        _recordingTimer.Tick -= OnRecordingTimerTick;
        _recordingTimer = null;
    }

    private async Task FinalizeClipAsync(string path, CaptureType type)
    {
        await CopyToClipboardAsync(path, type);
        var settings = Services.GetRequiredService<ICaptureSettings>();
        if (settings.ShowInExplorer)
        {
            RevealInExplorer(path);
        }
        ShowSaveToast(path);
    }

    private void OpenTrimmer(
        string path,
        CaptureType type,
        bool isRecentCapture = false,
        bool pickerInitiated = false)
    {
        _trimmerWindow?.Close();
        _lastTrimmerSourcePath = path;

        if (type == CaptureType.Gif)
        {
            var gifTrimmer = new GifTrimmerWindow(path);
            gifTrimmer.Completed += (sender, result) => OnTrimmerCompleted(sender, result, isRecentCapture, pickerInitiated);
            _trimmerWindow = gifTrimmer;
        }
        else
        {
            var videoTrimmer = new VideoTrimmerWindow(path);
            videoTrimmer.Completed += (sender, result) => OnTrimmerCompleted(sender, result, isRecentCapture, pickerInitiated);
            _trimmerWindow = videoTrimmer;
        }

        _trimmerWindow.Closed += (_, _) => _trimmerWindow = null;
        ActivateWindowToForeground(_trimmerWindow);
    }

    private void OnTrimmerCompleted(
        object? sender,
        string? trimmedPath,
        bool isRecentCapture,
        bool pickerInitiated)
    {
        _dispatcher?.TryEnqueue(async () =>
        {
            if (_isExiting)
            {
                return;
            }

            if (isRecentCapture && string.IsNullOrEmpty(trimmedPath))
            {
                return;
            }

            var path = trimmedPath ?? _lastTrimmerSourcePath;
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            var type = Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase)
                ? CaptureType.Gif
                : CaptureType.Video;
            await FinalizeClipAsync(path, type);
            if (!string.IsNullOrEmpty(trimmedPath))
            {
                Services.GetRequiredService<IRecentCaptureService>().Record(path, type);
            }
            if (!isRecentCapture)
            {
                ReopenPickerAfterCaptureIfNeeded(type, pickerInitiated);
            }
        });
    }

    private void UpdateRecordingState()
    {
        var video = Services.GetRequiredService<IVideoRecordingService>();
        var gif = Services.GetRequiredService<IGifRecordingService>();
        var hotKeys = Services.GetRequiredService<IHotKeyService>();

        if (_videoTile is not null)
        {
            var recording = video.IsRecording;
            _videoTile.Label.Text = recording ? "Stop" : "Video";
            _videoTile.Icon.Glyph = recording ? GlyphStop : GlyphVideo;
            var accel = recording ? hotKeys.StopRecordingDisplayString : hotKeys.GetBinding(CaptureType.Video).DisplayString;
            var label = recording ? "Stop recording" : "Record video";
            ToolTipService.SetToolTip(_videoTile.Button, string.IsNullOrEmpty(accel) ? label : $"{label} ({accel})");
            _videoTile.Button.IsEnabled = !gif.IsRecording;
        }

        if (_gifTile is not null)
        {
            var recording = gif.IsRecording;
            _gifTile.Label.Text = recording ? "Stop" : "GIF";
            _gifTile.Icon.Glyph = recording ? GlyphStop : GlyphGif;
            var accel = recording ? hotKeys.StopRecordingDisplayString : hotKeys.GetBinding(CaptureType.Gif).DisplayString;
            var label = recording ? "Stop recording" : "Record GIF";
            ToolTipService.SetToolTip(_gifTile.Button, string.IsNullOrEmpty(accel) ? label : $"{label} ({accel})");
            _gifTile.Button.IsEnabled = !video.IsRecording;
        }
    }

    // Register app notifications only when a toast is actually needed. That keeps packaged
    // process launch lighter and avoids eagerly activating extra Windows App Runtime plumbing.
    private static void EnsureNotificationsRegistered()
    {
        if (_notificationsRegistered)
        {
            return;
        }

        lock (NotificationRegistrationGate)
        {
            if (_notificationsRegistered)
            {
                return;
            }

            try
            {
                AppNotificationManager.Default.Register();
                _notificationsRegistered = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Notification registration failed: {ex}");
            }
        }
    }

    private async Task CopyToClipboardAsync(string path, CaptureType type)
    {
        try
        {
            var settings = Services.GetRequiredService<ICaptureSettings>();
            if (!settings.ShouldCopyToClipboard(type))
            {
                return;
            }

            await ClipboardService.CopySavedClipAsync(path, type);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Clipboard copy failed: {ex}");
            ShowClipboardFailureNotification(Path.GetFileName(path));
        }
    }

    private void ShowSaveToast(string path)
    {
        ShowSaveNotification(path);
    }

    /// <summary>
    /// Shows a "Saved to Tiny Clips" toast for a freshly written file (honoring the user's
    /// notification preference). Safe to call from any window (e.g. the trimmers' frame export).
    /// </summary>
    internal static void ShowSaveNotification(string path)
    {
        try
        {
            var settings = Services.GetRequiredService<ICaptureSettings>();
            if (!settings.ShowSaveNotifications)
            {
                return;
            }

            EnsureNotificationsRegistered();
            var notification = new AppNotificationBuilder()
                .AddText("Saved to Tiny Clips")
                .AddText(Path.GetFileName(path))
                .BuildNotification();

            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to show save notification: {ex}");
        }
    }

    internal static void ShowClipboardFailureNotification(string fileName)
    {
        try
        {
            EnsureNotificationsRegistered();
            var notification = new AppNotificationBuilder()
                .AddText("Couldn't copy to clipboard")
                .AddText(fileName)
                .BuildNotification();

            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to show clipboard failure notification: {ex}");
        }
    }

    internal static void ShowImageLoadFailureNotification(string fileName)
    {
        try
        {
            EnsureNotificationsRegistered();
            var notification = new AppNotificationBuilder()
                .AddText("Couldn't open image")
                .AddText($"{fileName}. The image may be unsupported or its decoder is unavailable.")
                .BuildNotification();

            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to show image load failure notification: {ex}");
        }
    }

    private static void ShowUpdateCheckNotification(string title, string details)
    {
        try
        {
            EnsureNotificationsRegistered();
            var notification = new AppNotificationBuilder()
                .AddText(title)
                .AddText(details)
                .BuildNotification();

            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to show update notification: {ex}");
        }
    }

    /// <summary>
    /// Shows a "Couldn't save" toast when a file write (e.g. the screenshot editor's Save /
    /// Save a copy) fails. Mirrors <see cref="ShowClipboardFailureNotification"/> so save
    /// failures surface to the user the same way copy failures already do.
    /// </summary>
    internal static void ShowSaveFailureNotification(string fileName)
    {
        try
        {
            EnsureNotificationsRegistered();
            var notification = new AppNotificationBuilder()
                .AddText("Couldn't save file")
                .AddText(fileName)
                .BuildNotification();

            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to show save failure notification: {ex}");
        }
    }

    private GlobalHotKeyRegistrationResult RegisterGlobalHotKeys()
    {
        if (_dispatcher is null)
        {
            return GlobalHotKeyRegistrationResult.Failed(
                new GlobalHotKeyRegistrationFailure(
                    "TinyClips hotkey service",
                    0,
                    "The UI dispatcher is not available."));
        }

        // Allow re-registration after the user edits a shortcut: tear down the old manager first.
        _hotKeyManager?.Dispose();
        _hotKeyManager = null;
        try
        {
            var hotKeys = Services.GetRequiredService<IHotKeyService>();
            var manager = new GlobalHotKeyManager(_dispatcher);

            var screenshot = hotKeys.GetBinding(CaptureType.Screenshot);
            manager.Add(
                $"Screenshot ({screenshot.DisplayString})",
                screenshot.ModifiersValue,
                screenshot.VirtualKey,
                () => _ = CaptureScreenshotAsync());

            var videoBinding = hotKeys.GetBinding(CaptureType.Video);
            manager.Add(
                $"Record video ({videoBinding.DisplayString})",
                videoBinding.ModifiersValue,
                videoBinding.VirtualKey,
                () => _ = ToggleVideoAsync());

            var gifBinding = hotKeys.GetBinding(CaptureType.Gif);
            manager.Add(
                $"Record GIF ({gifBinding.DisplayString})",
                gifBinding.ModifiersValue,
                gifBinding.VirtualKey,
                () => _ = ToggleGifAsync());

            var stopBinding = hotKeys.GetStopBinding();
            manager.Add(
                $"Stop recording ({stopBinding.DisplayString})",
                stopBinding.ModifiersValue,
                stopBinding.VirtualKey,
                () => _ = StopActiveRecordingAsync());

            var result = manager.Start();
            if (!result.IsSuccess)
            {
                foreach (var failure in result.Failures)
                {
                    Debug.WriteLine(
                        $"Global hotkey registration failed for {failure.Name}: " +
                        $"{failure.Message} Win32 error {failure.NativeErrorCode}.");
                }

                // Keep any other shortcuts that Windows accepted active. A Settings rollback
                // will dispose this manager before restoring the previous complete set.
                _hotKeyManager = manager;
                return result;
            }

            _hotKeyManager = manager;
            return result;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Global hotkey registration failed: {ex}");
            return GlobalHotKeyRegistrationResult.Failed(
                new GlobalHotKeyRegistrationFailure(
                    "TinyClips hotkey service",
                    ex.HResult,
                    ex.Message));
        }
    }

    /// <summary>Re-registers the global hotkeys after the user edits a shortcut in Settings.</summary>
    internal GlobalHotKeyRegistrationResult ReapplyGlobalHotKeys()
    {
        if (_dispatcher is null || !_dispatcher.HasThreadAccess)
        {
            return GlobalHotKeyRegistrationResult.Failed(
                new GlobalHotKeyRegistrationFailure(
                    "TinyClips hotkey service",
                    0,
                    "Hotkeys can only be updated from the TinyClips UI thread."));
        }

        return RegisterGlobalHotKeys();
    }

    private static void RevealInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to reveal file in Explorer: {ex}");
        }
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"OpenFolder failed: {ex}");
        }
    }

    private void OpenRecentCapture(RecentCapture capture)
    {
        if (!File.Exists(capture.Path))
        {
            Services.GetRequiredService<IRecentCaptureService>().Remove(capture.Path);
            return;
        }

        if (capture.Type == CaptureType.Screenshot)
        {
            OpenScreenshotEditor(capture.Path, reopenPickerAfterClose: false);
        }
        else
        {
            OpenTrimmer(capture.Path, capture.Type, isRecentCapture: true);
        }
    }

    private void OpenSettingsWindow()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow();
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }

        ActivateWindowToForeground(_settingsWindow);
    }

    private void OpenGuideWindow()
    {
        if (_guideWindow is null)
        {
            _guideWindow = new GuideWindow();
            _guideWindow.Closed += (_, _) => _guideWindow = null;
        }

        ActivateWindowToForeground(_guideWindow);
    }

    private Task OpenQuickBugReportFromTrayAsync(Microsoft.UI.Xaml.XamlRoot? xamlRoot)
        => QuickBugReport.ShowQuickBugDialogAndOpenAsync(
            xamlRoot,
            QuickBugReport.GetAppVersion(),
            QuickBugReport.GetDistributionChannel()
        );

    private void OpenScreenshotEditor(string path, bool reopenPickerAfterClose = false)
    {
        try
        {
            var oldWindow = _editorWindow;
            _editorWindow = null;
            oldWindow?.Close();

            var window = new ScreenshotEditorWindow(path);
            _editorWindow = window;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_editorWindow, window))
                {
                    _editorWindow = null;
                    if (reopenPickerAfterClose)
                    {
                        ReopenPickerAfterCaptureIfNeeded(CaptureType.Screenshot, pickerInitiated: true);
                    }
                }
            };
            ActivateWindowToForeground(window);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"OpenScreenshotEditor failed: {ex}");
            RevealInExplorer(path);
            ShowSaveToast(path);
            if (reopenPickerAfterClose)
            {
                ReopenPickerAfterCaptureIfNeeded(CaptureType.Screenshot, pickerInitiated: true);
            }
        }
    }

    private void ShowOnboardingIfNeeded()
    {
        var settings = Services.GetRequiredService<ICaptureSettings>();
        if (settings.HasCompletedOnboarding)
        {
            return;
        }

        _onboardingWindow = new OnboardingWindow();
        _onboardingWindow.Closed += (_, _) => _onboardingWindow = null;
        ActivateWindowToForeground(_onboardingWindow);
    }

    private async Task ExitApplicationAsync()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        try
        {
            var video = Services.GetRequiredService<IVideoRecordingService>();
            var gif = Services.GetRequiredService<IGifRecordingService>();
            if (video.IsRecording)
            {
                await video.StopAsync();
            }

            if (gif.IsRecording)
            {
                await gif.StopAsync();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to stop recording on exit: {ex}");
        }

        HideRecordingIndicator();
        CloseRecordingRegionIndicator();
        _hotKeyManager?.Dispose();
        _hotKeyManager = null;
        _taskbarIcon?.Dispose();
        _taskbarIcon = null;
        _settingsWindow?.Close();
        _guideWindow?.Close();
        _onboardingWindow?.Close();
        _editorWindow?.Close();
        _trimmerWindow?.Close();
        Application.Current.Exit();
        // No persistent host window keeps the process alive, so force termination
        // to guarantee the user can always quit from the tray menu.
        Environment.Exit(0);
    }

    private void ReopenPickerAfterCaptureIfNeeded(CaptureType type, bool pickerInitiated)
    {
        var settings = Services.GetRequiredService<ICaptureSettings>();
        if (!pickerInitiated || !settings.ShouldShowCapturePickerAfterCapture(type) || _isExiting || IsAnyRecordingActive())
        {
            return;
        }

        _ = ReopenPickerAfterCaptureAsync(type);
    }

    private async Task ReopenPickerAfterCaptureAsync(CaptureType type)
    {
        await Task.Delay(150);
        if (_isExiting || IsAnyRecordingActive())
        {
            return;
        }

        await BeginCaptureAsync(type, abortIfRecording: true);
    }

    private static bool IsAnyRecordingActive()
    {
        var video = Services.GetRequiredService<IVideoRecordingService>();
         var gif = Services.GetRequiredService<IGifRecordingService>();
        return video.IsRecording || gif.IsRecording;
    }

    private void ActivateWindowToForeground(Window window)
    {
        try
        {
            window.Activate();
            BringWindowToForeground(window);
            _ = ActivateWindowToForegroundDelayedAsync(window);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to activate window: {ex}");
        }
    }

    private async Task ActivateWindowToForegroundDelayedAsync(Window window)
    {
        await Task.Delay(100);
        if (_isExiting)
        {
            return;
        }

        try
        {
            window.Activate();
            BringWindowToForeground(window);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to reactivate window: {ex}");
        }
    }

    private static void BringWindowToForeground(Window window)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (hwnd != IntPtr.Zero)
            {
                SetForegroundWindow(hwnd);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to foreground window: {ex}");
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);

    private void ApplyTheme()
    {
        var settings = Services.GetRequiredService<ISettingsService>();

        // RequestedTheme can only be set once, before any window is created. Leaving it
        // unset for AppTheme.Default lets the app follow the current system theme.
        switch (settings.Theme)
        {
            case AppTheme.Light:
                RequestedTheme = ApplicationTheme.Light;
                break;
            case AppTheme.Dark:
                RequestedTheme = ApplicationTheme.Dark;
                break;
        }
    }
}
