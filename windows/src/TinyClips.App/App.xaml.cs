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
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using TinyClips.Core.Capture;
using TinyClips.Core.Models;
using TinyClips.Core.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace TinyClips.App;

public partial class App : Application
{
    private static readonly FontFamily FluentIconFont = new("Segoe Fluent Icons");

    // Segoe Fluent Icons glyphs.
    private const string GlyphScreenshot = "\uE722";
    private const string GlyphVideo = "\uE714";
    private const string GlyphGif = "\uE8B9";
    private const string GlyphStop = "\uE71A";
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
    private DispatcherTimer? _recordingTimer;
    private DateTime _recordingStartedUtc;
    private TargetSelection? _activeRecordingSelection;
    private CaptureTile? _videoTile;
    private CaptureTile? _gifTile;
    private TrayPopupWindow? _trayPopup;
    private const double TrayPopupWidth = 288;
    private const double TrayPopupHeight = 196;
    private GlobalHotKeyManager? _hotKeyManager;
    private DispatcherQueue? _dispatcher;
    private bool _isExiting;

    public static IServiceProvider Services { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
        Services = new ServiceCollection()
            .AddTinyClipsCore()
            .BuildServiceProvider();

        ApplyTheme();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        RegisterNotifications();
        WireRecordingEvents();
        CreateTrayIcon();
        RegisterGlobalHotKeys();
        ShowOnboardingIfNeeded();
        HandleFileActivation();
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
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
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

            var pick = await CapturePickerWindow.RunAsync(type, cdEnabled, cdDuration, settings.VideoRecordingTimeLimitMinutes);
            if (pick is null)
            {
                return;
            }

            var resolved = await ResolveTargetAsync(pick.Mode);
            if (resolved is not { } selection)
            {
                return;
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
                    if (selection.Region is { } region)
                    {
                        regionIndicator = new RegionIndicatorWindow();
                        regionIndicator.Show(ToVirtualDesktopRegion(selection.Target, region));
                    }

                    await CountdownWindow.RunAsync(pick.CountdownDuration, selection.Monitor);
                }
                finally
                {
                    regionIndicator?.ClosePanel();
                }
            }

            switch (type)
            {
                case CaptureType.Screenshot:
                    var screenshots = Services.GetRequiredService<IScreenshotService>();
                    var path = await screenshots.CaptureTargetAsync(selection.Target, selection.Region);
                    await CopyToClipboardAsync(path, CaptureType.Screenshot);
                    if (settings.ShowScreenshotEditor)
                    {
                        OpenScreenshotEditor(path);
                    }
                    else
                    {
                        RevealInExplorer(path);
                        ShowSaveToast(path);
                        ReopenPickerAfterCaptureIfNeeded(CaptureType.Screenshot);
                    }
                    break;

                case CaptureType.Video:
                    _activeRecordingSelection = selection;
                    ShowRecordingRegionIndicator(selection);
                    if (!showDisabledStopDuringCountdown)
                    {
                        ShowRecordingIndicator(CaptureType.Video, selection);
                    }
                    await Services.GetRequiredService<IVideoRecordingService>().StartAsync(selection.Target, selection.Region, pick.VideoTimeLimitMinutes);
                    ActivateRecordingIndicatorForStartedCapture();
                    UpdateRecordingState();
                    break;

                case CaptureType.Gif:
                    _activeRecordingSelection = selection;
                    ShowRecordingRegionIndicator(selection);
                    if (!showDisabledStopDuringCountdown)
                    {
                        ShowRecordingIndicator(CaptureType.Gif, selection);
                    }
                    await Services.GetRequiredService<IGifRecordingService>().StartAsync(selection.Target, selection.Region);
                    ActivateRecordingIndicatorForStartedCapture();
                    UpdateRecordingState();
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Capture failed: {ex}");
            UpdateRecordingState();
            CloseRecordingRegionIndicator();
            _activeRecordingSelection = null;
            HideRecordingIndicatorIfNotRecording();
        }
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

            var settings = Services.GetRequiredService<ICaptureSettings>();
            var showTrimmer = type == CaptureType.Gif ? settings.ShowGifTrimmer : settings.ShowTrimmer;
            if (showTrimmer)
            {
                OpenTrimmer(path, type);
            }
            else
            {
                await FinalizeClipAsync(path, type);
                ReopenPickerAfterCaptureIfNeeded(type);
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
        }
    }

    private void ShowRecordingRegionIndicator(TargetSelection selection)
    {
        if (selection.Region is not { } region)
        {
            return;
        }

        CloseRecordingRegionIndicator();
        var indicator = new RegionIndicatorWindow();
        _recordingRegionIndicator = indicator;
        indicator.Closed += (_, _) =>
        {
            if (ReferenceEquals(_recordingRegionIndicator, indicator))
            {
                _recordingRegionIndicator = null;
            }
        };
        indicator.Show(ToVirtualDesktopRegion(selection.Target, region));
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

    private void ShowRecordingIndicator(CaptureType type, TargetSelection selection, bool stopEnabled = true, bool startTimer = true)
    {
        HideRecordingIndicator();

        var hotKeys = Services.GetRequiredService<IHotKeyService>();
        var settings = Services.GetRequiredService<ICaptureSettings>();
        var showAudioControls = type == CaptureType.Video;
        var window = new RecordingIndicatorWindow(
            hotKeys.StopRecordingDisplayString,
            showAudioControls,
            settings.RecordMicrophone,
            settings.RecordAudio);
        window.StopRequested = () => _ = StopActiveRecordingAsync();
        window.MicrophoneToggled = on => settings.RecordMicrophone = on;
        window.SystemAudioToggled = on => settings.RecordAudio = on;
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
            StartRecordingTimer();
        }
    }

    private void ActivateRecordingIndicatorForStartedCapture()
    {
        _recordingStartedUtc = DateTime.UtcNow;
        _recordingIndicator?.SetStopEnabled(true);
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
        _recordingIndicator?.UpdateElapsed(DateTime.UtcNow - _recordingStartedUtc);
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

    private void OpenTrimmer(string path, CaptureType type)
    {
        _trimmerWindow?.Close();
        _lastTrimmerSourcePath = path;

        if (type == CaptureType.Gif)
        {
            var gifTrimmer = new GifTrimmerWindow(path);
            gifTrimmer.Completed += OnTrimmerCompleted;
            _trimmerWindow = gifTrimmer;
        }
        else
        {
            var videoTrimmer = new VideoTrimmerWindow(path);
            videoTrimmer.Completed += OnTrimmerCompleted;
            _trimmerWindow = videoTrimmer;
        }

        _trimmerWindow.Closed += (_, _) => _trimmerWindow = null;
        ActivateWindowToForeground(_trimmerWindow);
    }

    private void OnTrimmerCompleted(object? sender, string? trimmedPath)
    {
        _dispatcher?.TryEnqueue(async () =>
        {
            if (_isExiting)
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
            ReopenPickerAfterCaptureIfNeeded(type);
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

    private static void RegisterNotifications()
    {
        try
        {
            AppNotificationManager.Default.Register();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Notification registration failed: {ex}");
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

            var file = await StorageFile.GetFileFromPathAsync(path);
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetStorageItems(new[] { file });

            // For still images, also place the bitmap so it can be pasted directly into editors.
            if (type == CaptureType.Screenshot)
            {
                package.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
            }

            Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Clipboard copy failed: {ex}");
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

    private void RegisterGlobalHotKeys()
    {
        if (_dispatcher is null)
        {
            return;
        }

        // Allow re-registration after the user edits a shortcut: tear down the old manager first.
        _hotKeyManager?.Dispose();
        _hotKeyManager = null;
        try
        {
            var hotKeys = Services.GetRequiredService<IHotKeyService>();
            _hotKeyManager = new GlobalHotKeyManager(_dispatcher);

            var screenshot = hotKeys.GetBinding(CaptureType.Screenshot);
            _hotKeyManager.Add(screenshot.ModifiersValue, screenshot.VirtualKey, () => _ = CaptureScreenshotAsync());

            var videoBinding = hotKeys.GetBinding(CaptureType.Video);
            _hotKeyManager.Add(videoBinding.ModifiersValue, videoBinding.VirtualKey, () => _ = ToggleVideoAsync());

            var gifBinding = hotKeys.GetBinding(CaptureType.Gif);
            _hotKeyManager.Add(gifBinding.ModifiersValue, gifBinding.VirtualKey, () => _ = ToggleGifAsync());

            var stopBinding = hotKeys.GetStopBinding();
            _hotKeyManager.Add(stopBinding.ModifiersValue, stopBinding.VirtualKey, () => _ = StopActiveRecordingAsync());

            _hotKeyManager.Start();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Global hotkey registration failed: {ex}");
        }
    }

    /// <summary>Re-registers the global hotkeys after the user edits a shortcut in Settings.</summary>
    public void ReapplyGlobalHotKeys()
    {
        if (_dispatcher is null)
        {
            return;
        }

        _dispatcher.TryEnqueue(RegisterGlobalHotKeys);
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

    private void OpenScreenshotEditor(string path)
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
                    ReopenPickerAfterCaptureIfNeeded(CaptureType.Screenshot);
                }
            };
            ActivateWindowToForeground(window);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"OpenScreenshotEditor failed: {ex}");
            RevealInExplorer(path);
            ShowSaveToast(path);
            ReopenPickerAfterCaptureIfNeeded(CaptureType.Screenshot);
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

    private void ReopenPickerAfterCaptureIfNeeded(CaptureType type)
    {
        var settings = Services.GetRequiredService<ICaptureSettings>();
        if (!settings.ReopenPickerAfterCapture || _isExiting || IsAnyRecordingActive())
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
