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
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using TinyClips.App.Services.ClipsLibrary;
using TinyClips.App.Settings;
using TinyClips.App.Views.ClipsLibrary;
using TinyClips.Core.Capture;
using TinyClips.Core.Models;
using TinyClips.Core.Services;
using TinyClips.Core.Services.ClipsLibrary;
using Windows.Storage;

namespace TinyClips.App;

public partial class App : Application
{
    private static readonly FontFamily FluentIconFont = new("Segoe Fluent Icons");
    private static readonly object NotificationRegistrationGate = new();

    // Segoe Fluent Icons glyphs.
    private const string GlyphScreenshot = "\uE722";
    private const string GlyphVideo = "\uE714";
    private const string GlyphGif = "\uF4A9";
    private const string GlyphStop = "\uE71A";
    private const string GlyphCheckForUpdates = "\uE895";
    private const string GlyphFolder = "\uE8B7";
    private const string GlyphHistory = "\uE81C";
    private const string GlyphDocument = "\uE8A5";
    private const string GlyphMediaGallery = "\uE7AA";
    private const string GlyphTextRecognition = "\uE8D2";
    private const string GlyphBug = "\uEBE8";
    private const string GlyphSettings = "\uE713";
    private const string GlyphExit = "\uE7E8";
    private const uint MonitorDefaultToNearest = 2;

    private TaskbarIcon? _taskbarIcon;
    private DispatcherQueueTimer? _trayIconRetryTimer;
    private int _trayIconRetryAttempts;
    private SettingsWindow? _settingsWindow;
    private GuideWindow? _guideWindow;
    private ClipsLibraryWindow? _clipsManagerWindow;
    private QuickBugReportWindow? _quickBugReportWindow;
    private OnboardingWindow? _onboardingWindow;
    private ScreenshotEditorWindow? _editorWindow;
    private Window? _trimmerWindow;
    private string? _lastTrimmerSourcePath;
    private RecordingIndicatorWindow? _recordingIndicator;
    private TeleprompterWindow? _teleprompter;
    private WebcamPreviewWindow? _webcamPreview;
    private ProcessingIndicatorWindow? _processingIndicator;
    private RegionIndicatorWindow? _recordingRegionIndicator;
    private ScrollingCaptureWindow? _scrollingPanel;
    private RegionIndicatorWindow? _scrollingRegionIndicator;
    private bool _scrollingWasPickerInitiated;
    private bool _scrollingStopping;
    private Action<int>? _scrollingProgressHandler;
    private Action<PanoramaCaptureLimitReason>? _scrollingLimitHandler;
    private Action<Exception>? _scrollingFailedHandler;
    private CancellationTokenSource? _captureFlowCts;
    private DispatcherTimer? _recordingTimer;
    private DateTime _recordingStartedUtc;
    private TimeSpan _recordingElapsedBeforePause;
    private TargetSelection? _activeRecordingSelection;
    private CaptureType? _activeRecordingType;
    private bool _activeRecordingWasPickerInitiated;
    private bool _recordingStopAnnounced;
    private CaptureTile? _videoTile;
    private CaptureTile? _gifTile;
    private TrayPopupWindow? _trayPopup;
    private AutomationNotificationAnnouncer? _automationNotificationAnnouncer;
    private const double TrayPopupWidth = 344;
    private const double TrayPopupHeight = 242;
    private const double TrayPopupFooterHeight = 48;
    private const double TrayPopupFooterButtonSize = 32;
    // Shell_NotifyIcon(NIM_ADD) fails while Explorer's taskbar is not yet up (fresh sign-in,
    // Explorer restart, or a bare automation session such as winget's validation VM). Retry so
    // the icon appears once the shell is ready; after that H.NotifyIcon re-adds it on the
    // TaskbarCreated broadcast without further help from us.
    //
    // Each failed Shell_NotifyIcon call can block the calling (UI) thread for the shell's
    // 4-second reply timeout, so the retries back off exponentially: a fixed 2 s cadence would
    // keep the UI thread pinned and make the process look hung to the user and to automation.
    private static readonly TimeSpan TrayIconInitialRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TrayIconMaxRetryDelay = TimeSpan.FromSeconds(40);
    private const int TrayIconMaxRetryAttempts = 6; // 5 + 10 + 20 + 40 + 40 + 40 s ≈ 2.5 min
    private GlobalHotKeyManager? _hotKeyManager;
    private DispatcherQueue? _dispatcher;
    private bool _isExiting;
    private bool _isInStartupPhase = true;
    private static bool _notificationsRegistered;

    public static IServiceProvider Services { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
        RegisterGlobalExceptionHandlers();
        Services = new ServiceCollection()
            .AddTinyClipsCore()
            .AddSingleton<IUploadcareCredentialStore, WindowsCredentialStore>()
            .AddSingleton<IThumbnailCache, ThumbnailCacheService>()
            .AddSingleton<IMediaDevicePermissionService, MediaDevicePermissionService>()
            .AddSingleton<IDisplaySleepAssertion, WindowsDisplaySleepAssertion>()
            .BuildServiceProvider();

        ApplyTheme();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        // Nothing may escape OnLaunched: XAML treats an exception here as fatal and terminates the
        // process with a stowed exception (0xC000027B) *even when* Application.UnhandledException
        // marks it handled. That exit code is what winget's Validation-Executable-Error reported
        // for 1.5.3 / 1.7.0 / 1.7.1, where Shell_NotifyIcon failed on the validation VM. Every
        // step is therefore guarded; the tray icon and hotkeys are the app, everything else is
        // best-effort.
        RunStartupStep(nameof(WireRecordingEvents), WireRecordingEvents);
        RunStartupStep(nameof(CreateTrayIcon), CreateTrayIcon);
        RunStartupStep(nameof(RegisterGlobalHotKeys), () => RegisterGlobalHotKeys());
        RunStartupStep(nameof(ShowOnboardingIfNeeded), ShowOnboardingIfNeeded);
        RunStartupStep(nameof(HandleFileActivation), HandleFileActivation);
        // Create the shared D3D capture device off the UI thread so the first capture is instant.
        _ = Task.Run(() => RunStartupStep("ScreenCaptureWarmUp", () =>
            Services.GetRequiredService<IScreenCaptureService>().WarmUp()));
#if !TINYCLIPS_STORE_BUILD
        RunStartupStep(nameof(RunStartupUpdateCheckAsync), () => _ = RunStartupUpdateCheckAsync());
#endif
        RunStartupStep(nameof(EndStartupPhaseAfterFirstDispatcherPass), EndStartupPhaseAfterFirstDispatcherPass);
    }

    private static void RunStartupStep(string name, Action step)
    {
        try
        {
            step();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Startup step '{name}' failed: {ex}");
            CrashDiagnostics.Log($"Startup step '{name}'", ex, handled: true);
        }
    }

    /// <summary>
    /// Keeps <see cref="_isInStartupPhase"/> set until the queued work that launch scheduled on the
    /// UI thread (window activation, tray icon creation callbacks, first layout) has drained, so
    /// startup-time framework exceptions are treated as recoverable while later ones are not.
    /// </summary>
    private void EndStartupPhaseAfterFirstDispatcherPass()
    {
        var dispatcher = _dispatcher;
        if (dispatcher is null ||
            !dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => _isInStartupPhase = false))
        {
            _isInStartupPhase = false;
        }
    }

    private void RegisterGlobalExceptionHandlers()
    {
        // A XAML-thread exception that reaches the framework terminates the process with a stowed
        // exception (0xC000027B). During launch we log and swallow so a non-fatal startup hiccup
        // cannot produce a crash exit code for a tray app that has no main window to tear down.
        // NOTE: this does not cover exceptions thrown out of OnLaunched itself - XAML fail-fasts
        // on those even when Handled is set - which is why OnLaunched guards each step directly.
        // After launch the handler only records diagnostics: continuing past an arbitrary
        // mid-operation failure could leave app state partially mutated, so we let it terminate.
        UnhandledException += (_, e) =>
        {
            var handled = _isInStartupPhase && !_isExiting;
            Debug.WriteLine($"Unhandled XAML exception (startup={_isInStartupPhase}): {e.Exception}");
            CrashDiagnostics.Log("Application.UnhandledException", e.Exception, handled);
            e.Handled = handled;
        };

        // Unobserved task faults are raised from the finalizer thread and would otherwise be
        // silently dropped (or, with ThrowUnobservedTaskExceptions, crash the process); record them.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Debug.WriteLine($"Unobserved task exception: {e.Exception}");
            CrashDiagnostics.Log("TaskScheduler.UnobservedTaskException", e.Exception, handled: true);
            e.SetObserved();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Debug.WriteLine($"Unhandled AppDomain exception (terminating={e.IsTerminating}): {e.ExceptionObject}");
            CrashDiagnostics.Log("AppDomain.UnhandledException", e.ExceptionObject, handled: false);
        };
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

        UpdateRecordingState();

        if (!TryRegisterTrayIconWithShell())
        {
            ScheduleTrayIconRetry();
        }
    }

    /// <summary>
    /// Asks the shell to add the notification icon. Returns <see langword="false"/> instead of
    /// throwing when <c>Shell_NotifyIcon</c> refuses (no taskbar yet); the <see cref="TaskbarIcon"/>
    /// stays alive so a later attempt, or the shell's own <c>TaskbarCreated</c> broadcast, can add it.
    /// </summary>
    private bool TryRegisterTrayIconWithShell()
    {
        if (_taskbarIcon is null || _isExiting)
        {
            return true;
        }

        if (_taskbarIcon.IsCreated)
        {
            return true;
        }

        try
        {
            _taskbarIcon.ForceCreate();
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Tray icon registration failed (attempt {_trayIconRetryAttempts + 1}): {ex.Message}");
            if (_trayIconRetryAttempts == 0)
            {
                CrashDiagnostics.Log("TaskbarIcon.ForceCreate", ex, handled: true);
            }

            return false;
        }
    }

    private void ScheduleTrayIconRetry()
    {
        if (_trayIconRetryTimer is not null || _dispatcher is null || _isExiting)
        {
            return;
        }

        _trayIconRetryTimer = _dispatcher.CreateTimer();
        _trayIconRetryTimer.Interval = TrayIconInitialRetryDelay;
        _trayIconRetryTimer.IsRepeating = false;
        _trayIconRetryTimer.Tick += OnTrayIconRetryTick;
        _trayIconRetryTimer.Start();
    }

    private void OnTrayIconRetryTick(DispatcherQueueTimer sender, object args)
    {
        _trayIconRetryAttempts++;
        var registered = TryRegisterTrayIconWithShell();
        if (registered)
        {
            Debug.WriteLine($"Tray icon registered after {_trayIconRetryAttempts} retr{(_trayIconRetryAttempts == 1 ? "y" : "ies")}.");
        }
        else if (_trayIconRetryAttempts < TrayIconMaxRetryAttempts && !_isExiting)
        {
            // Exponential backoff, capped: the next delay doubles until TrayIconMaxRetryDelay.
            var next = TimeSpan.FromTicks(Math.Min(sender.Interval.Ticks * 2, TrayIconMaxRetryDelay.Ticks));
            sender.Interval = next;
            sender.Start();
            return;
        }
        else
        {
            // Give up polling; H.NotifyIcon still listens for TaskbarCreated and the hotkeys keep
            // the app usable. Recorded once so a persistent failure leaves evidence in crash.log.
            CrashDiagnostics.Log(
                "TaskbarIcon.ForceCreate",
                new InvalidOperationException($"Tray icon still not registered after {_trayIconRetryAttempts} attempts; waiting for TaskbarCreated."),
                handled: true);
        }

        StopTrayIconRetry();
    }

    private void StopTrayIconRetry()
    {
        if (_trayIconRetryTimer is null)
        {
            return;
        }

        _trayIconRetryTimer.Stop();
        _trayIconRetryTimer.Tick -= OnTrayIconRetryTick;
        _trayIconRetryTimer = null;
    }

    /// <summary>
    /// Lazily creates the off-screen UI Automation anchor window. Creation is deferred to the first
    /// announcement so tray-first launch creates no XAML window, and failures degrade to a log line.
    /// </summary>
    private AutomationNotificationAnnouncer? GetOrCreateAutomationNotificationAnnouncer()
    {
        if (_automationNotificationAnnouncer is not null || _isExiting)
        {
            return _automationNotificationAnnouncer;
        }

        try
        {
            _automationNotificationAnnouncer = new AutomationNotificationAnnouncer();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to create automation announcer: {ex}");
            CrashDiagnostics.Log("AutomationNotificationAnnouncer", ex, handled: true);
        }

        return _automationNotificationAnnouncer;
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

    // PowerToys-style "quick access" popup: capture actions on a layered acrylic content
    // surface with a separate acrylic command bar along the bottom.
    private UIElement BuildTrayPopupContent(IHotKeyService hotKeys)
    {
        void Dismiss() => _trayPopup?.Hide();

        var content = new StackPanel
        {
            Padding = new Thickness(16),
            Spacing = 12,
        };

        content.Children.Add(new TextBlock
        {
            Text = "Tiny Clips",
            Margin = new Thickness(0, 0, 0, 2),
            Style = ResourceStyle("BodyStrongTextBlockStyle"),
        });

        var tiles = new Grid { ColumnSpacing = 6 };
        for (var i = 0; i < 3; i++)
        {
            tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        var screenshot = CreateCaptureTile(
            "Screenshot",
            GlyphScreenshot,
            hotKeys.GetBinding(HotKeyAction.Screenshot).DisplayString,
            new AsyncRelayCommand(CaptureScreenshotAsync),
            Dismiss);
        Grid.SetColumn(screenshot.Button, 0);
        tiles.Children.Add(screenshot.Button);

        _videoTile = CreateCaptureTile(
            "Video",
            GlyphVideo,
            hotKeys.GetBinding(HotKeyAction.RecordVideo).DisplayString,
            new AsyncRelayCommand(ToggleVideoAsync),
            Dismiss);
        Grid.SetColumn(_videoTile.Button, 1);
        tiles.Children.Add(_videoTile.Button);

        _gifTile = CreateCaptureTile(
            "GIF",
            GlyphGif,
            hotKeys.GetBinding(HotKeyAction.RecordGif).DisplayString,
            new AsyncRelayCommand(ToggleGifAsync),
            Dismiss);
        Grid.SetColumn(_gifTile.Button, 2);
        tiles.Children.Add(_gifTile.Button);

        content.Children.Add(tiles);

        var quickAccess = new Grid { ColumnSpacing = 6 };
        quickAccess.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        quickAccess.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var folders = CreateFolderButton(Dismiss);
        Grid.SetColumn(folders, 0);
        quickAccess.Children.Add(folders);

        var recent = CreateRecentCapturesButton(Dismiss);
        Grid.SetColumn(recent, 1);
        quickAccess.Children.Add(recent);
        content.Children.Add(quickAccess);

        var contentArea = new Border
        {
            Child = content,
            Background = ThemeBrush("LayerOnAcrylicFillColorDefaultBrush"),
            BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };

        var footer = new Grid
        {
            Height = TrayPopupFooterHeight,
            Padding = new Thickness(12, 0, 12, 0),
        };

        var footerActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 8,
        };
        footerActions.Children.Add(CreateFooterButton(
            GlyphMediaGallery,
            "Clips Library",
            "TrayClipsLibraryButton",
            new RelayCommand(OpenClipsManagerWindow),
            Dismiss));

        footerActions.Children.Add(CreateFooterButton(
            GlyphTextRecognition,
            $"Recognize Text ({hotKeys.GetBinding(HotKeyAction.RecognizeText).DisplayString})",
            "TrayCaptureTextButton",
            new AsyncRelayCommand(RecognizeTextAsync),
            Dismiss));
#if !TINYCLIPS_STORE_BUILD
        footerActions.Children.Add(CreateFooterButton(
            GlyphCheckForUpdates,
            "Check for updates",
            "TrayCheckForUpdatesButton",
            new AsyncRelayCommand(CheckForUpdatesFromTrayAsync),
            Dismiss));
#endif

        footerActions.Children.Add(CreateFooterButton(
            GlyphDocument,
            "Guide",
            "TrayGuideButton",
            new RelayCommand(OpenGuideWindow),
            Dismiss));
        footerActions.Children.Add(CreateFooterButton(
            GlyphBug,
            "File a Bug",
            "TrayFileBugButton",
            new RelayCommand(OpenQuickBugReportWindow),
            Dismiss));
		footerActions.Children.Add(CreateFooterButton(
	        GlyphSettings,
	        "Settings",
	        "TraySettingsButton",
	        new RelayCommand(OpenSettingsWindow),
	        Dismiss));
		footerActions.Children.Add(CreateFooterButton(
            GlyphExit,
            "Exit",
            "TrayExitButton",
            new RelayCommand(() => _ = ExitApplicationAsync()),
            Dismiss));
        footer.Children.Add(footerActions);

        var layout = new Grid { Width = TrayPopupWidth };
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TrayPopupFooterHeight) });
        layout.Children.Add(contentArea);
        Grid.SetRow(footer, 1);
        layout.Children.Add(footer);

        return layout;
    }

    private ButtonBase CreateFolderButton(Action dismiss)
    {
        var storage = Services.GetRequiredService<IClipStorageService>();

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
            Content = QuickAccessContent(GlyphHistory, captures.Count == 0 ? "No captures" : $"Recent ({captures.Count})"),
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

    private Button CreateFooterButton(
        string glyph,
        string tooltip,
        string automationId,
        ICommand command,
        Action dismiss)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontFamily = FluentIconFont, FontSize = 16 },
            Width = TrayPopupFooterButtonSize,
            Height = TrayPopupFooterButtonSize,
            Padding = new Thickness(6),
            Command = command,
            Style = ResourceStyle("SubtleButtonStyle"),
        };
        ToolTipService.SetToolTip(button, tooltip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(button, automationId);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, tooltip);
        button.Click += (_, _) => dismiss();
        return button;
    }

    private static Style? ResourceStyle(string key)
        => Application.Current.Resources.TryGetValue(key, out var value) ? value as Style : null;

    private static Brush? ThemeBrush(string key)
        => Application.Current.Resources.TryGetValue(key, out var value) ? value as Brush : null;

    private Task CaptureScreenshotAsync() => BeginCaptureAsync(CaptureType.Screenshot);

    private Task CaptureScreenshotRegionAsync() => BeginCaptureAsync(CaptureType.Screenshot, CapturePickerMode.Region);

    private Task CaptureScreenshotWindowAsync() => BeginCaptureAsync(CaptureType.Screenshot, CapturePickerMode.Window);

    private Task RecognizeTextAsync() => BeginCaptureAsync(CaptureType.Screenshot, CapturePickerMode.RecognizeText);

    /// <summary>
    /// Shows the capture picker bar (Region / Screen / Window + countdown), resolves the
    /// chosen target, runs the countdown, then performs the capture or starts recording.
    /// </summary>
    private async Task BeginCaptureAsync(
        CaptureType type,
        CapturePickerMode? forcedMode = null,
        bool abortIfRecording = false)
    {
        if (_captureFlowCts is not null || _scrollingPanel is not null)
        {
            return;
        }

        var captureFlowCts = new CancellationTokenSource();
        _captureFlowCts = captureFlowCts;
        IReadOnlyList<Task<CapturedFrame?>>? earlyBackdrops = null;
        IReadOnlyList<MonitorInfo>? earlyBackdropMonitors = null;
        long earlyBackdropStarted = 0;
        try
        {
            CaptureFlowTrace.Begin(type.ToString().ToLowerInvariant() + (forcedMode is null ? string.Empty : $"/{forcedMode}"));

            // The tray popup and picker are excluded from capture (WDA_EXCLUDEFROMCAPTURE), so
            // there is no longer any need to pause for the menu to dismiss before capturing.
            // Warm the shared D3D capture device in the background so the first capture is fast.
            Services.GetRequiredService<IScreenCaptureService>().WarmUp();

            // For an auto-reopened picker, bail out if a recording started during the delay.
            if (abortIfRecording && (_isExiting || IsAnyRecordingActive()))
            {
                return;
            }

            var settings = Services.GetRequiredService<ICaptureSettings>();
            var (cdEnabled, cdDuration) = GetCountdown(settings, type);
            var wasPickerInitiated = forcedMode is null && settings.ShouldShowCapturePicker(type);
            if (wasPickerInitiated)
            {
                // Start grabbing the region-overlay backdrop while the user reads the picker bar,
                // so choosing "Region" shows the overlay immediately. If the user dawdles the
                // frame is refreshed on selection (see ResolveTargetAsync) so it never goes stale.
                earlyBackdropMonitors = ResolveBackdropMonitors(settings);
                if (earlyBackdropMonitors.Count > 0)
                {
                    earlyBackdropStarted = Stopwatch.GetTimestamp();
                    earlyBackdrops = RegionSelectController.CaptureBackdropsAsync(earlyBackdropMonitors);
                }
            }

            var pick = wasPickerInitiated
                ? await CapturePickerWindow.RunAsync(type, cdEnabled, cdDuration, settings.VideoRecordingTimeLimitMinutes)
                : new CapturePickerResult(
                    forcedMode ?? CapturePickerMode.Region,
                    cdEnabled,
                    cdDuration,
                    settings.VideoRecordingTimeLimitMinutes);
            CaptureFlowTrace.Mark($"picker: result {(pick is null ? "cancelled" : pick.Mode.ToString())}");
            if (pick is null)
            {
                return;
            }

            var isTextRecognition = pick.Mode == CapturePickerMode.RecognizeText;
            var isScrolling = type == CaptureType.Screenshot && pick.Mode == CapturePickerMode.Scrolling;
            var earlyBackdrop = earlyBackdrops is null
                ? null
                : new EarlyBackdrop(earlyBackdropMonitors!, earlyBackdrops, earlyBackdropStarted);
            var resolved = await ResolveTargetAsync(
                isTextRecognition || isScrolling ? CapturePickerMode.Region : pick.Mode,
                earlyBackdrop);
            CaptureFlowTrace.Mark($"target: {(resolved is null ? "cancelled" : "resolved")}");
            if (resolved is not { } selection)
            {
                return;
            }

            RecordingSetupResult? recordingSetup = null;
            Task? recorderPrepare = null;
            if (type is CaptureType.Video or CaptureType.Gif)
            {
                recordingSetup = await ShowRecordingSetupAsync(type, selection, settings);
                CaptureFlowTrace.Mark($"setup: {(recordingSetup is null ? "cancelled" : "confirmed")}");
                if (recordingSetup is null)
                {
                    CloseRecordingRegionIndicator();
                    return;
                }

                ApplyRecordingSetup(type, recordingSetup, settings);

                // Pre-warm the whole recording pipeline (capture session, encoder, webcam, audio)
                // while the countdown runs so the recording starts the instant it hits zero.
                recorderPrepare = PrepareRecorderAsync(type, selection, captureFlowCts.Token);
            }

            var showDisabledStopDuringCountdown = type is CaptureType.Video or CaptureType.Gif
                && pick.CountdownEnabled
                && pick.CountdownDuration > 0;
            if (showDisabledStopDuringCountdown)
            {
                ShowRecordingIndicator(type, selection, stopEnabled: false, startTimer: false);
            }

            RegionIndicatorWindow? regionIndicator = null;
            // Scrolling capture has no countdown: the user controls timing by scrolling.
            var countdownRan = pick.CountdownEnabled && pick.CountdownDuration > 0 && !isScrolling;
            if (countdownRan)
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
                case CaptureType.Screenshot when isTextRecognition:
                    captureFlowCts.Token.ThrowIfCancellationRequested();
                    try
                    {
                        var text = await Services.GetRequiredService<IOcrService>()
                            .RecognizeAsync(selection.Target, selection.Region, captureFlowCts.Token);
                        if (string.IsNullOrWhiteSpace(text))
                        {
                            ShowTextRecognitionNotification("No text recognized");
                        }
                        else
                        {
                            await ClipboardService.CopyTextAsync(text);
                            ShowTextRecognitionNotification("Text copied to clipboard");
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Debug.WriteLine($"Text recognition failed: {ex}");
                        ShowTextRecognitionNotification("Couldn't recognize text");
                    }

                    break;

                case CaptureType.Screenshot when isScrolling:
                    captureFlowCts.Token.ThrowIfCancellationRequested();
                    await StartScrollingCaptureAsync(selection, settings, wasPickerInitiated, captureFlowCts.Token);
                    break;

                case CaptureType.Screenshot:
                    captureFlowCts.Token.ThrowIfCancellationRequested();
                    await CaptureScreenshotAndPresentAsync(selection, settings, countdownRan, wasPickerInitiated, captureFlowCts.Token);
                    break;

                case CaptureType.Video:
                    captureFlowCts.Token.ThrowIfCancellationRequested();
                    settings.VideoRecordingTimeLimitMinutes = (int)Math.Round(Math.Max(0, pick.VideoTimeLimitMinutes));
                    _activeRecordingSelection = selection with { Backdrop = null };
                    _activeRecordingType = CaptureType.Video;
                    _activeRecordingWasPickerInitiated = wasPickerInitiated;
                    ShowRecordingRegionIndicator(selection);
                    if (!showDisabledStopDuringCountdown)
                    {
                        ShowRecordingIndicator(CaptureType.Video, selection);
                    }
                    AcquireDisplaySleepAssertionIfEnabled();
                    await AwaitRecorderPrepareAsync(recorderPrepare);
                    await Services.GetRequiredService<IVideoRecordingService>()
                        .StartAsync(selection.Target, selection.Region, pick.VideoTimeLimitMinutes, captureFlowCts.Token);
                    CaptureFlowTrace.Mark("video: StartAsync returned");
                    ActivateRecordingIndicatorForStartedCapture(CaptureType.Video);
                    UpdateRecordingState();
                    break;

                case CaptureType.Gif:
                    captureFlowCts.Token.ThrowIfCancellationRequested();
                    _activeRecordingSelection = selection with { Backdrop = null };
                    _activeRecordingType = CaptureType.Gif;
                    _activeRecordingWasPickerInitiated = wasPickerInitiated;
                    ShowRecordingRegionIndicator(selection);
                    if (!showDisabledStopDuringCountdown)
                    {
                        ShowRecordingIndicator(CaptureType.Gif, selection);
                    }
                    AcquireDisplaySleepAssertionIfEnabled();
                    await AwaitRecorderPrepareAsync(recorderPrepare);
                    await Services.GetRequiredService<IGifRecordingService>()
                        .StartAsync(selection.Target, selection.Region, captureFlowCts.Token);
                    CaptureFlowTrace.Mark("gif: StartAsync returned");
                    ActivateRecordingIndicatorForStartedCapture(CaptureType.Gif);
                    UpdateRecordingState();
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            CaptureFlowTrace.Mark("flow cancelled");
            _ = DiscardPreparedRecordersAsync();
            CloseRecordingRegionIndicator();
            HideRecordingIndicatorIfNotRecording();
            _activeRecordingSelection = null;
            _activeRecordingType = null;
            _activeRecordingWasPickerInitiated = false;
            if (!IsAnyRecordingActive())
            {
                ReleaseDisplaySleepAssertion();
            }
            UpdateRecordingState();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Capture failed: {ex}");
            CaptureFlowTrace.Mark($"flow failed: {ex.GetType().Name}");
            _ = DiscardPreparedRecordersAsync();
            ShowSaveFailureNotification(CaptureOutputDescription(type));
            UpdateRecordingState();
            CloseRecordingRegionIndicator();
            _activeRecordingSelection = null;
            _activeRecordingType = null;
            _activeRecordingWasPickerInitiated = false;
            if (!IsAnyRecordingActive())
            {
                ReleaseDisplaySleepAssertion();
            }
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

    /// <summary>Monitor(s) the region overlay will cover, mirroring <see cref="ResolveTargetAsync"/>.</summary>
    private static IReadOnlyList<MonitorInfo> ResolveBackdropMonitors(ICaptureSettings settings)
    {
        var monitors = Services.GetRequiredService<IMonitorService>();
        var all = monitors.GetMonitors();
        if (all.Count == 0)
        {
            return all;
        }

        var preferred = ResolvePreferredMonitor(monitors, settings.MultiMonitorCaptureMode);
        return preferred is { } single ? new[] { single } : all;
    }

    // A backdrop captured while the picker was showing; refreshed if older than this on use.
    private static readonly TimeSpan EarlyBackdropMaxAge = TimeSpan.FromMilliseconds(600);

    private sealed record EarlyBackdrop(IReadOnlyList<MonitorInfo> Monitors, IReadOnlyList<Task<CapturedFrame?>> Frames, long StartedTimestamp)
    {
        public bool IsFreshFor(IReadOnlyList<MonitorInfo> monitors) =>
            Stopwatch.GetElapsedTime(StartedTimestamp) <= EarlyBackdropMaxAge
            && monitors.Count == Monitors.Count
            && monitors.Zip(Monitors).All(pair => pair.First.HMonitor == pair.Second.HMonitor);
    }

    private Task PrepareRecorderAsync(CaptureType type, TargetSelection selection, CancellationToken cancellationToken)
    {
        try
        {
            return type == CaptureType.Video
                ? Services.GetRequiredService<IVideoRecordingService>().PrepareAsync(selection.Target, selection.Region, cancellationToken)
                : Services.GetRequiredService<IGifRecordingService>().PrepareAsync(selection.Target, selection.Region, cancellationToken);
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }
    }

    /// <summary>
    /// Waits for a background pre-warm to settle. Failures are swallowed here: StartAsync will
    /// rebuild the pipeline itself and surface any real error through the normal path.
    /// </summary>
    private static async Task AwaitRecorderPrepareAsync(Task? prepare)
    {
        if (prepare is null)
        {
            return;
        }

        try
        {
            await prepare;
            CaptureFlowTrace.Mark("recorder: pre-warm complete");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Recorder pre-warm failed (will retry in StartAsync): {ex}");
            CaptureFlowTrace.Mark("recorder: pre-warm failed");
        }
    }

    private static async Task DiscardPreparedRecordersAsync()
    {
        try
        {
            await Services.GetRequiredService<IVideoRecordingService>().DiscardPreparedAsync();
            await Services.GetRequiredService<IGifRecordingService>().DiscardPreparedAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Discarding prepared recorder failed: {ex}");
        }
    }

    /// <summary>
    /// Produces the screenshot and gets it in front of the user as fast as possible. When the
    /// region overlay's frozen backdrop is usable (no countdown, live re-capture not requested)
    /// the screenshot is a crop of that frame: no second capture. The editor is opened from the
    /// in-memory frame while encoding/saving/clipboard run concurrently, so the window appears
    /// before the PNG hits disk.
    /// </summary>
    private async Task CaptureScreenshotAndPresentAsync(
        TargetSelection selection,
        ICaptureSettings settings,
        bool countdownRan,
        bool wasPickerInitiated,
        CancellationToken cancellationToken)
    {
        CapturedFrame frame;
        if (selection.Backdrop is { } backdrop && !countdownRan && !settings.ScreenshotUsesLiveCapture)
        {
            frame = selection.Region is { } region ? backdrop.Crop(region) : backdrop;
            CaptureFlowTrace.Mark("screenshot: cropped from frozen backdrop");
        }
        else
        {
            frame = await Services.GetRequiredService<IScreenCaptureService>()
                .CaptureAsync(selection.Target, selection.Region, includeCursor: false, cancellationToken);
            CaptureFlowTrace.Mark("screenshot: live frame captured");
        }

        if (settings.ShowBrandingOverlay)
        {
            // Brand once, up front, so the in-memory editor preview matches the saved file.
            new BrandingOverlayCompositor().Draw(frame.BgraPixels, frame.Width, frame.Height);
        }

        await PresentScreenshotFrameAsync(frame, settings, wasPickerInitiated, alreadyBranded: true);
    }

    /// <summary>
    /// Shared post-capture path for screenshots and scrolling captures: save (and copy to the
    /// clipboard) in the background, then open the editor from memory or from the saved file, or
    /// reveal + toast when the editor is disabled (or <paramref name="allowEditor"/> is false).
    /// </summary>
    private async Task PresentScreenshotFrameAsync(
        CapturedFrame frame,
        ICaptureSettings settings,
        bool wasPickerInitiated,
        bool alreadyBranded,
        bool allowEditor = true)
    {
        var screenshots = Services.GetRequiredService<IScreenshotService>();
        var saveTask = SaveScreenshotFrameAsync(screenshots, frame, alreadyBranded);
        if (settings.ShowScreenshotEditor && allowEditor)
        {
            // With a downscale configured, the saved file's dimensions differ from the frame; keep
            // the editor file-backed so Save/Reset/Copy operate on the same pixels as the file.
            var scaleApplied = settings.ScreenshotScale is > 0 and < 100;
            if (!scaleApplied)
            {
                OpenScreenshotEditor(frame, saveTask, reopenPickerAfterClose: wasPickerInitiated);
                CaptureFlowTrace.Mark("screenshot: editor opened from memory");
                try
                {
                    await saveTask;
                }
                catch (Exception ex)
                {
                    // The editor already reports the failure via BindToPendingSaveAsync.
                    Debug.WriteLine($"Background screenshot save failed: {ex}");
                }
                return;
            }

            var savedPath = await saveTask;
            OpenScreenshotEditor(savedPath, reopenPickerAfterClose: wasPickerInitiated);
            CaptureFlowTrace.Mark("screenshot: editor opened from file (scaled)");
            return;
        }

        var path = await saveTask;
        RevealInExplorer(path);
        ShowSaveToast(path);
        ReopenPickerAfterCaptureIfNeeded(CaptureType.Screenshot, wasPickerInitiated);
    }

    private async Task<string> SaveScreenshotFrameAsync(IScreenshotService screenshots, CapturedFrame frame, bool alreadyBranded)
    {
        var path = await screenshots.SaveFrameAsync(frame, applyBranding: !alreadyBranded);
        Services.GetRequiredService<IRecentCaptureService>().Record(path, CaptureType.Screenshot);
        await CopyToClipboardAsync(path, CaptureType.Screenshot);
        CaptureFlowTrace.Mark("screenshot: saved + clipboard");
        return path;
    }

    // -- Scrolling (panorama) capture --------------------------------------------------------

    /// <summary>
    /// Starts a scrolling capture of the selected region and shows the floating Done/Cancel
    /// panel. Returns as soon as capture is streaming; the panel's callbacks drive stop/cancel.
    /// </summary>
    private async Task StartScrollingCaptureAsync(
        TargetSelection selection,
        ICaptureSettings settings,
        bool wasPickerInitiated,
        CancellationToken cancellationToken)
    {
        var service = Services.GetRequiredService<IScrollingCaptureService>();
        if (service.IsActive || _scrollingPanel is not null)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // The editor renders through Win2D/XAML textures, which cap at 16 384 px; only let the
        // panorama grow past that when it will be saved straight to disk.
        var limits = settings.ShowScreenshotEditor
            ? PanoramaCaptureLimits.ForEditor
            : PanoramaCaptureLimits.Default;

        var panel = new ScrollingCaptureWindow();
        _scrollingPanel = panel;
        _scrollingWasPickerInitiated = wasPickerInitiated;
        _scrollingStopping = false;
        panel.StopRequested = () => _ = StopScrollingCaptureAsync();
        panel.CancelRequested = CancelScrollingCapture;

        _scrollingProgressHandler = count => _dispatcher?.TryEnqueue(() =>
        {
            if (ReferenceEquals(_scrollingPanel, panel))
            {
                panel.UpdateFrameCount(count);
            }
        });
        _scrollingLimitHandler = reason => _dispatcher?.TryEnqueue(() =>
        {
            if (!ReferenceEquals(_scrollingPanel, panel))
            {
                return;
            }

            var message = reason.ToMessage();
            panel.ShowStatus(message);
            Announce(
                AutomationNotificationKind.Other,
                AutomationNotificationProcessing.ImportantMostRecent,
                message,
                "ScrollingCaptureLimit");
            _ = StopScrollingCaptureAsync();
        });
        _scrollingFailedHandler = error => _dispatcher?.TryEnqueue(() =>
        {
            if (!ReferenceEquals(_scrollingPanel, panel))
            {
                return;
            }

            service.Cancel();
            FinishScrollingCapture(error);
        });
        service.Progress += _scrollingProgressHandler;
        service.LimitReached += _scrollingLimitHandler;
        service.Failed += _scrollingFailedHandler;

        if (settings.ShowRegionIndicator)
        {
            _scrollingRegionIndicator = ShowRegionIndicator(selection);
        }

        var monitor = selection.Monitor ?? ResolveMonitorForTarget(selection.Target);
        PixelRect? regionInVirtualDesktop = selection.Region is { } region
            ? ToVirtualDesktopRegion(selection.Target, region)
            : null;
        panel.ShowNear(monitor, regionInVirtualDesktop);
        Announce(
            AutomationNotificationKind.Other,
            AutomationNotificationProcessing.MostRecent,
            "Scrolling capture started. Scroll the page, then press Enter to finish.",
            "ScrollingCaptureStarted");

        try
        {
            await service.StartAsync(selection.Target, selection.Region, limits, cancellationToken);
        }
        catch (Exception ex)
        {
            FinishScrollingCapture(ex);
            if (ex is OperationCanceledException)
            {
                throw;
            }
        }
    }

    private async Task StopScrollingCaptureAsync()
    {
        if (_scrollingStopping || _scrollingPanel is not { } panel)
        {
            return;
        }

        _scrollingStopping = true;
        panel.MarkFinishing();
        CaptureFlowTrace.Mark("scrolling: stop requested");

        var service = Services.GetRequiredService<IScrollingCaptureService>();
        CapturedFrame frame;
        try
        {
            frame = await service.StopAsync();
        }
        catch (Exception ex)
        {
            FinishScrollingCapture(ex);
            return;
        }

        var wasPickerInitiated = _scrollingWasPickerInitiated;
        FinishScrollingCapture(null);

        var settings = Services.GetRequiredService<ICaptureSettings>();
        try
        {
            if (settings.ShowBrandingOverlay)
            {
                new BrandingOverlayCompositor().Draw(frame.BgraPixels, frame.Width, frame.Height);
            }

            // The editor setting is re-read here; if it was toggled on mid-capture the image may
            // exceed the Win2D/XAML texture cap, so fall back to a direct save in that case.
            var fitsEditor = frame.Width <= PanoramaCaptureLimits.EditorMaxOutputHeight
                && frame.Height <= PanoramaCaptureLimits.EditorMaxOutputHeight;
            await PresentScreenshotFrameAsync(frame, settings, wasPickerInitiated, alreadyBranded: true, allowEditor: fitsEditor);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Scrolling capture save failed: {ex}");
            ShowSaveFailureNotification("the scrolling capture");
            ReopenPickerAfterCaptureIfNeeded(CaptureType.Screenshot, wasPickerInitiated);
        }
    }

    private void CancelScrollingCapture()
    {
        if (_scrollingPanel is null)
        {
            return;
        }

        Services.GetRequiredService<IScrollingCaptureService>().Cancel();
        FinishScrollingCapture(new PanoramaCaptureException(PanoramaCaptureError.Cancelled));
    }

    /// <summary>
    /// Tears down the scrolling panel, region indicator and service subscriptions. With an
    /// <paramref name="error"/>, reports it (unless it is a cancellation) and reopens the picker
    /// if configured; on success the caller presents the stitched image instead.
    /// </summary>
    private void FinishScrollingCapture(Exception? error)
    {
        var service = Services.GetRequiredService<IScrollingCaptureService>();
        if (_scrollingProgressHandler is { } progress)
        {
            service.Progress -= progress;
        }

        if (_scrollingLimitHandler is { } limit)
        {
            service.LimitReached -= limit;
        }

        if (_scrollingFailedHandler is { } failed)
        {
            service.Failed -= failed;
        }

        _scrollingProgressHandler = null;
        _scrollingLimitHandler = null;
        _scrollingFailedHandler = null;

        var panel = _scrollingPanel;
        _scrollingPanel = null;
        panel?.ClosePanel();

        var indicator = _scrollingRegionIndicator;
        _scrollingRegionIndicator = null;
        indicator?.ClosePanel();

        var wasPickerInitiated = _scrollingWasPickerInitiated;
        _scrollingWasPickerInitiated = false;
        _scrollingStopping = false;

        if (error is null)
        {
            return;
        }

        if (error is PanoramaCaptureException { IsCancellation: true } || error is OperationCanceledException)
        {
            CaptureFlowTrace.Mark("scrolling: cancelled");
        }
        else
        {
            Debug.WriteLine($"Scrolling capture failed: {error}");
            CaptureFlowTrace.Mark($"scrolling: failed ({error.GetType().Name})");
            ShowScrollingCaptureFailureNotification(error.Message);
        }

        ReopenPickerAfterCaptureIfNeeded(CaptureType.Screenshot, wasPickerInitiated);
    }

    private void ShowScrollingCaptureFailureNotification(string details)
    {
        Announce(
            AutomationNotificationKind.ActionAborted,
            AutomationNotificationProcessing.ImportantMostRecent,
            $"Scrolling capture failed. {details}",
            "ScrollingCaptureFailed");

        try
        {
            EnsureNotificationsRegistered();
            AppNotificationManager.Default.Show(
                new AppNotificationBuilder()
                    .AddText("Scrolling capture failed")
                    .AddText(details)
                    .BuildNotification());
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to show scrolling capture notification: {ex}");
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

    private async Task<TargetSelection?> ResolveTargetAsync(CapturePickerMode mode, EarlyBackdrop? earlyBackdrop = null)
    {
        var monitors = Services.GetRequiredService<IMonitorService>();
        var settings = Services.GetRequiredService<ICaptureSettings>();

        switch (mode)
        {
            case CapturePickerMode.Region:
            case CapturePickerMode.RecognizeText:
            {
                var all = monitors.GetMonitors();
                if (all.Count == 0)
                {
                    return null;
                }

                var preferredMonitor = ResolvePreferredMonitor(monitors, settings.MultiMonitorCaptureMode);
                var overlayMonitors = preferredMonitor is { } single ? new[] { single } : all;

                // Reuse the backdrop grabbed while the picker was up if it is still fresh;
                // otherwise the controller captures a new one (in parallel with window creation).
                var backdrops = earlyBackdrop is { } early && early.IsFreshFor(overlayMonitors)
                    ? early.Frames
                    : null;
                CaptureFlowTrace.Mark(backdrops is null ? "region: capturing fresh backdrop" : "region: reusing early backdrop");

                var result = await RegionSelectController.RunAsync(overlayMonitors, backdrops);
                if (result is not { } selection)
                {
                    return null;
                }

                var selectedMonitor = overlayMonitors.FirstOrDefault(m => m.HMonitor == selection.HMonitor)
                    ?? monitors.GetPrimaryMonitor();
                return new TargetSelection(CaptureTarget.Monitor(selection.HMonitor), selection.Region, selectedMonitor, selection.Backdrop);
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

    private readonly record struct TargetSelection(CaptureTarget Target, PixelRect? Region, MonitorInfo? Monitor, CapturedFrame? Backdrop = null);

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
        _dispatcher?.TryEnqueue(() =>
        {
            HideWebcamPreview();
            ShowWebcamFailureNotification(reason);
        });
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

    private static void ShowTeleprompterFailureNotification()
    {
        try
        {
            EnsureNotificationsRegistered();
            var notification = new AppNotificationBuilder()
                .AddText("Teleprompter unavailable")
                .AddText("Windows could not exclude the overlay from capture, so it was kept hidden. Recording continues.")
                .BuildNotification();

            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to show teleprompter failure notification: {ex}");
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
            CaptureFlowTrace.Mark("recording: completed event on UI thread");
            UpdateRecordingState();
            HideRecordingIndicator();
            HideProcessingIndicator();
            CloseRecordingRegionIndicator();
            ReleaseDisplaySleepAssertion();
            _activeRecordingSelection = null;
            _activeRecordingType = null;
            var wasPickerInitiated = _activeRecordingWasPickerInitiated;
            _activeRecordingWasPickerInitiated = false;
            if (_isExiting)
            {
                return;
            }

            var type = sender is IGifRecordingService
                ? CaptureType.Gif
                : CaptureType.Video;
            AnnounceRecordingStopped(type);

            if (string.IsNullOrEmpty(path))
            {
                return;
            }

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
        CaptureType? stoppedType = null;

        try
        {
            var video = Services.GetRequiredService<IVideoRecordingService>();
            var gif = Services.GetRequiredService<IGifRecordingService>();

            if (video.IsRecording)
            {
                stoppedType = CaptureType.Video;
                CaptureFlowTrace.Begin("stop-video");
                HideRecordingIndicator();
                ShowProcessingIndicator(CaptureType.Video, _activeRecordingSelection);
                await video.StopAsync();
                CaptureFlowTrace.Mark("video: StopAsync returned");
            }
            else if (gif.IsRecording)
            {
                stoppedType = CaptureType.Gif;
                CaptureFlowTrace.Begin("stop-gif");
                HideRecordingIndicator();
                ShowProcessingIndicator(CaptureType.Gif, _activeRecordingSelection);
                await gif.StopAsync();
                CaptureFlowTrace.Mark("gif: StopAsync returned");
            }
            else
            {
                CancelCaptureFlow();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to stop active recording: {ex}");
            if (stoppedType is { } type)
            {
                ShowSaveFailureNotification(CaptureOutputDescription(type));
            }
        }
        finally
        {
            if (stoppedType is { } type && !IsAnyRecordingActive())
            {
                AnnounceRecordingStopped(type);
            }

            HideProcessingIndicator();
            UpdateRecordingState();
            CloseRecordingRegionIndicatorIfNotRecording();
            HideRecordingIndicatorIfNotRecording();
            if (!IsAnyRecordingActive())
            {
                ReleaseDisplaySleepAssertion();
            }
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
        _teleprompter?.PauseScrolling();
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
        _teleprompter?.ResumeScrolling();
        StartRecordingTimer();
    }

    private async Task RestartActiveRecordingAsync()
    {
        if (_activeRecordingSelection is not { } selection || _activeRecordingType is not { } type)
        {
            return;
        }

        try
        {
            await DiscardActiveRecordingAsync(clearActiveSelection: false);
            _activeRecordingSelection = selection;
            _activeRecordingType = type;
            ShowRecordingRegionIndicator(selection);
            ShowRecordingIndicator(type, selection, stopEnabled: false, startTimer: false);

            if (type == CaptureType.Video)
            {
                var settings = Services.GetRequiredService<ICaptureSettings>();
                AcquireDisplaySleepAssertionIfEnabled();
                await Services.GetRequiredService<IVideoRecordingService>()
                    .StartAsync(selection.Target, selection.Region, settings.VideoRecordingTimeLimitMinutes);
            }
            else
            {
                AcquireDisplaySleepAssertionIfEnabled();
                await Services.GetRequiredService<IGifRecordingService>().StartAsync(selection.Target, selection.Region);
            }

            ActivateRecordingIndicatorForStartedCapture(type);
            UpdateRecordingState();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to restart {type} recording: {ex}");
            ShowSaveFailureNotification(CaptureOutputDescription(type));
            HideRecordingIndicator();
            CloseRecordingRegionIndicator();
            _activeRecordingSelection = null;
            _activeRecordingType = null;
            _activeRecordingWasPickerInitiated = false;
            if (!IsAnyRecordingActive())
            {
                ReleaseDisplaySleepAssertion();
            }
            UpdateRecordingState();
        }
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
            if (!IsAnyRecordingActive())
            {
                ReleaseDisplaySleepAssertion();
            }
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

    private void ActivateRecordingIndicatorForStartedCapture(CaptureType type)
    {
        _recordingStopAnnounced = false;
        _recordingStartedUtc = DateTime.UtcNow;
        _recordingElapsedBeforePause = TimeSpan.Zero;
        _recordingIndicator?.SetStopEnabled(true);
        var video = Services.GetRequiredService<IVideoRecordingService>();
        _recordingIndicator?.ConfigureAudioControls(
            video.CanMuteSystemAudio,
            video.IsSystemAudioMuted,
            video.CanMuteMicrophone,
            video.IsMicrophoneMuted);
        ShowWebcamPreviewForActiveRecording();
        if (_activeRecordingSelection is { } selection)
        {
            var monitor = selection.Monitor ?? ResolveMonitorForTarget(selection.Target);
            ShowTeleprompterIfNeeded(
                type,
                monitor,
                Services.GetRequiredService<ICaptureSettings>());
        }
        StartRecordingTimer();
        AnnounceRecordingStarted(type);
    }

    private void ShowWebcamPreviewForActiveRecording()
    {
        HideWebcamPreview();
        if (_activeRecordingType != CaptureType.Video ||
            _activeRecordingSelection is not { } selection)
        {
            return;
        }

        var settings = Services.GetRequiredService<ICaptureSettings>();
        var capture = Services.GetRequiredService<IWebcamCaptureService>();
        if (!settings.WebcamEnabled || !capture.IsRunning)
        {
            return;
        }

        var video = Services.GetRequiredService<IVideoRecordingService>();
        var monitor = selection.Monitor ?? ResolveMonitorForTarget(selection.Target);
        PixelRect? region = selection.Region is { } selectedRegion
            ? ToVirtualDesktopRegion(selection.Target, selectedRegion)
            : null;
        var window = new WebcamPreviewWindow(
            capture,
            selection.Target,
            monitor,
            region,
            settings.WebcamCornerPosition,
            settings.WebcamSizePreset,
            settings.WebcamShape,
            settings.WebcamCornerRadius,
            corner => video.SetWebcamCorner(corner));
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_webcamPreview, window))
            {
                _webcamPreview = null;
            }
        };
        _webcamPreview = window;
        window.Show();
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

    private void ShowTeleprompterIfNeeded(CaptureType type, MonitorInfo? monitor, ICaptureSettings settings)
    {
        HideTeleprompter();

        // Video recordings only — the teleprompter never appears for GIFs or screenshots,
        // and stays hidden when disabled or when there is no transcript to scroll.
        if (type != CaptureType.Video ||
            !settings.TeleprompterEnabled ||
            string.IsNullOrWhiteSpace(settings.TeleprompterTranscript))
        {
            return;
        }

        var monitorService = Services.GetRequiredService<IMonitorService>();
        var window = new TeleprompterWindow(settings, monitorService, monitor);
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_teleprompter, window))
            {
                _teleprompter = null;
            }
        };
        if (!window.TryShow())
        {
            ShowTeleprompterFailureNotification();
            return;
        }

        _teleprompter = window;
    }

    private void HideTeleprompter()
    {
        var window = _teleprompter;
        _teleprompter = null;
        window?.ClosePanel();
    }

    private void HideRecordingIndicator()
    {
        StopRecordingTimer();
        HideWebcamPreview();
        HideTeleprompter();

        var window = _recordingIndicator;
        _recordingIndicator = null;
        window?.ClosePanel();
    }

    private void AcquireDisplaySleepAssertionIfEnabled()
    {
        if (!Services.GetRequiredService<ICaptureSettings>().KeepDisplayAwakeWhileRecording)
        {
            return;
        }

        try
        {
            Services.GetRequiredService<IDisplaySleepAssertion>().Acquire();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to acquire display sleep assertion: {ex}");
        }
    }

    private static void ReleaseDisplaySleepAssertion()
    {
        try
        {
            Services.GetRequiredService<IDisplaySleepAssertion>().Release();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to release display sleep assertion: {ex}");
        }
    }

    private void HideWebcamPreview()
    {
        var window = _webcamPreview;
        _webcamPreview = null;
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
            var accel = recording ? hotKeys.StopRecordingDisplayString : hotKeys.GetBinding(HotKeyAction.RecordVideo).DisplayString;
            var label = recording ? "Stop recording" : "Record video";
            ToolTipService.SetToolTip(_videoTile.Button, string.IsNullOrEmpty(accel) ? label : $"{label} ({accel})");
            _videoTile.Button.IsEnabled = !gif.IsRecording;
        }

        if (_gifTile is not null)
        {
            var recording = gif.IsRecording;
            _gifTile.Label.Text = recording ? "Stop" : "GIF";
            _gifTile.Icon.Glyph = recording ? GlyphStop : GlyphGif;
            var accel = recording ? hotKeys.StopRecordingDisplayString : hotKeys.GetBinding(HotKeyAction.RecordGif).DisplayString;
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

    private static void ShowTextRecognitionNotification(string message)
    {
        try
        {
            EnsureNotificationsRegistered();
            AppNotificationManager.Default.Show(
                new AppNotificationBuilder().AddText(message).BuildNotification());
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to show text recognition notification: {ex}");
        }
    }

    /// <summary>
    /// Shows a "Saved to Tiny Clips" toast for a freshly written file (honoring the user's
    /// notification preference). Safe to call from any window (e.g. the trimmers' frame export).
    /// </summary>
    internal static void ShowSaveNotification(string path)
    {
        AnnounceCaptureSaved(path);
        (Current as App)?.QueueAutoUpload(path);

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

    private void QueueAutoUpload(string path)
    {
        var settings = Services.GetRequiredService<ICaptureSettings>();
        if (!settings.UploadcareEnabled ||
            !settings.UploadcareAutoUpload ||
            string.IsNullOrWhiteSpace(settings.UploadcarePublicKey))
        {
            return;
        }

        _ = UploadSavedClipAsync(path);
    }

    private async Task UploadSavedClipAsync(string path)
    {
        try
        {
            var result = await Services.GetRequiredService<IUploadcareUploadService>().UploadAsync(path);
            var settings = Services.GetRequiredService<ICaptureSettings>();
            RecordUploadLink(path, result.DeliveryUri.AbsoluteUri);
            if (settings.UploadcareCopyUrl)
            {
                try
                {
                    await ClipboardService.CopyTextAsync(result.DeliveryUri.AbsoluteUri);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Upload URL clipboard copy failed: {ex}");
                    ShowClipboardFailureNotification(Path.GetFileName(path));
                }
            }
        }
        catch (UploadcareUploadException)
        {
            ShowUploadFailureNotification(Path.GetFileName(path));
        }
    }

    /// <summary>Remembers an Uploadcare link with the clip so the Library can show/copy it later.</summary>
    private static void RecordUploadLink(string path, string url)
    {
        try
        {
            var store = Services.GetRequiredService<IClipMetadataStore>();
            store.Upsert(store.Get(path) with { UploadedUrl = url });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to record upload link: {ex}");
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

    internal static void ShowUploadFailureNotification(string fileName)
    {
        try
        {
            EnsureNotificationsRegistered();
            var notification = new AppNotificationBuilder()
                .AddText("Couldn't upload to Uploadcare")
                .AddText(fileName)
                .BuildNotification();

            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to show upload failure notification: {ex}");
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
        AnnounceSaveFailure(fileName);

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

    private void AnnounceRecordingStarted(CaptureType type) =>
        Announce(
            AutomationNotificationKind.Other,
            AutomationNotificationProcessing.MostRecent,
            $"{CaptureTypeName(type)} recording started.",
            $"{CaptureTypeName(type)}RecordingStarted");

    private void AnnounceRecordingStopped(CaptureType type)
    {
        if (_recordingStopAnnounced)
        {
            return;
        }

        _recordingStopAnnounced = true;
        Announce(
            AutomationNotificationKind.ActionCompleted,
            AutomationNotificationProcessing.MostRecent,
            $"{CaptureTypeName(type)} recording stopped.",
            $"{CaptureTypeName(type)}RecordingStopped");
    }

    private static void AnnounceCaptureSaved(string path)
    {
        var type = Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase)
            ? CaptureType.Gif
            : Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase)
                ? CaptureType.Video
                : CaptureType.Screenshot;
        var fileName = Path.GetFileName(path);
        var message = string.IsNullOrWhiteSpace(fileName)
            ? $"{CaptureTypeName(type)} saved."
            : $"{CaptureTypeName(type)} saved: {fileName}.";

        if (Application.Current is App app)
        {
            app.Announce(
                AutomationNotificationKind.ActionCompleted,
                AutomationNotificationProcessing.MostRecent,
                message,
                $"{CaptureTypeName(type)}Saved");
        }
    }

    private static void AnnounceSaveFailure(string fileName)
    {
        var message = string.IsNullOrWhiteSpace(fileName)
            ? "Couldn't save file."
            : $"Couldn't save {fileName}.";

        if (Application.Current is App app)
        {
            app.Announce(
                AutomationNotificationKind.ActionAborted,
                AutomationNotificationProcessing.ImportantMostRecent,
                message,
                "CaptureSaveFailed");
        }
    }

    private void Announce(
        AutomationNotificationKind kind,
        AutomationNotificationProcessing processing,
        string message,
        string activityId)
    {
        var dispatcher = _dispatcher;
        if (dispatcher is null)
        {
            Debug.WriteLine($"Automation announcement dispatcher unavailable: {message}");
            return;
        }

        if (dispatcher.HasThreadAccess)
        {
            AnnounceOnUiThread(kind, processing, message, activityId);
            return;
        }

        if (!dispatcher.TryEnqueue(() => AnnounceOnUiThread(kind, processing, message, activityId)))
        {
            Debug.WriteLine($"Automation announcement dispatch failed: {message}");
        }
    }

    private void AnnounceOnUiThread(
        AutomationNotificationKind kind,
        AutomationNotificationProcessing processing,
        string message,
        string activityId)
    {
        var announcer = GetOrCreateAutomationNotificationAnnouncer();
        if (announcer is null)
        {
            Debug.WriteLine($"Automation announcement unavailable: {message}");
            return;
        }

        try
        {
            announcer.Announce(kind, processing, message, activityId);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Automation announcement failed: {ex}");
        }
    }

    private static string CaptureTypeName(CaptureType type) => type switch
    {
        CaptureType.Gif => "GIF",
        CaptureType.Video => "Video",
        _ => "Screenshot",
    };

    private static string CaptureOutputDescription(CaptureType type) =>
        $"the {CaptureTypeName(type).ToLowerInvariant()} capture";

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

            var screenshot = hotKeys.GetBinding(HotKeyAction.Screenshot);
            manager.Add(
                $"Screenshot ({screenshot.DisplayString})",
                screenshot.ModifiersValue,
                screenshot.VirtualKey,
                () => _ = CaptureScreenshotAsync());

            var screenshotRegion = hotKeys.GetBinding(HotKeyAction.ScreenshotRegion);
            if (!screenshotRegion.IsUnbound)
            {
                manager.Add(
                    $"Screenshot region ({screenshotRegion.DisplayString})",
                    screenshotRegion.ModifiersValue,
                    screenshotRegion.VirtualKey,
                    () => _ = CaptureScreenshotRegionAsync());
            }

            var screenshotWindow = hotKeys.GetBinding(HotKeyAction.ScreenshotWindow);
            if (!screenshotWindow.IsUnbound)
            {
                manager.Add(
                    $"Screenshot window ({screenshotWindow.DisplayString})",
                    screenshotWindow.ModifiersValue,
                    screenshotWindow.VirtualKey,
                    () => _ = CaptureScreenshotWindowAsync());
            }

            var videoBinding = hotKeys.GetBinding(HotKeyAction.RecordVideo);
            manager.Add(
                $"Record video ({videoBinding.DisplayString})",
                videoBinding.ModifiersValue,
                videoBinding.VirtualKey,
                () => _ = ToggleVideoAsync());

            var gifBinding = hotKeys.GetBinding(HotKeyAction.RecordGif);
            manager.Add(
                $"Record GIF ({gifBinding.DisplayString})",
                gifBinding.ModifiersValue,
                gifBinding.VirtualKey,
                () => _ = ToggleGifAsync());

            var ocrBinding = hotKeys.GetBinding(HotKeyAction.RecognizeText);
            manager.Add(
                $"Recognize text ({ocrBinding.DisplayString})",
                ocrBinding.ModifiersValue,
                ocrBinding.VirtualKey,
                () => _ = RecognizeTextAsync());

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

    private void OpenSettingsWindow() => OpenSettingsWindow(section: null);

    internal void OpenSettingsWindow(SettingsSectionKind? section)
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow();
            _settingsWindow.ViewModel.TeleprompterDisplayChanged += () => _teleprompter?.ApplyDisplaySettings();
            _settingsWindow.ViewModel.ClipsLibrarySettingsChanged += () => _clipsManagerWindow?.ReloadSettings();
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }

        if (section is { } kind)
        {
            _settingsWindow.NavigateTo(kind);
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

    private void OpenClipsManagerWindow()
    {
        if (_clipsManagerWindow is null)
        {
            _clipsManagerWindow = new ClipsLibraryWindow();
            _clipsManagerWindow.Closed += (_, _) => _clipsManagerWindow = null;
        }

        ActivateWindowToForeground(_clipsManagerWindow);
    }

    /// <summary>
    /// Opens a clip from the Clips Library in its appropriate editor or trimmer.
    /// Called by <see cref="ClipsLibraryWindow"/> when the user opens a clip.
    /// </summary>
    internal void OpenRecentCaptureFromLibrary(RecentCapture capture)
    {
        if (!File.Exists(capture.Path))
        {
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

    internal void OpenQuickBugReportWindow()
    {
        if (_quickBugReportWindow is null)
        {
            _quickBugReportWindow = new QuickBugReportWindow(
                QuickBugReport.GetAppVersion(),
                QuickBugReport.GetDistributionChannel());
            _quickBugReportWindow.Closed += (_, _) => _quickBugReportWindow = null;
        }

        ActivateWindowToForeground(_quickBugReportWindow);
    }

    private void OpenScreenshotEditor(string path, bool reopenPickerAfterClose = false)
        => OpenScreenshotEditorCore(() => new ScreenshotEditorWindow(path), path, reopenPickerAfterClose);

    /// <summary>
    /// Opens the editor directly from the captured pixels while <paramref name="saveTask"/>
    /// encodes and writes the file in the background. The editor binds to the final path once
    /// the save completes so Save/Save-a-copy work exactly as before.
    /// </summary>
    private void OpenScreenshotEditor(CapturedFrame frame, Task<string> saveTask, bool reopenPickerAfterClose)
        => OpenScreenshotEditorCore(() => new ScreenshotEditorWindow(frame, saveTask), null, reopenPickerAfterClose);

    private void OpenScreenshotEditorCore(Func<ScreenshotEditorWindow> create, string? fallbackPath, bool reopenPickerAfterClose)
    {
        try
        {
            var oldWindow = _editorWindow;
            _editorWindow = null;
            oldWindow?.Close();

            var window = create();
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
            if (fallbackPath is not null)
            {
                RevealInExplorer(fallbackPath);
                ShowSaveToast(fallbackPath);
            }
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
            Services.GetRequiredService<IScrollingCaptureService>().Cancel();
            _scrollingPanel?.ClosePanel();
            _scrollingPanel = null;
            _scrollingRegionIndicator?.ClosePanel();
            _scrollingRegionIndicator = null;

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
        ReleaseDisplaySleepAssertion();
        _hotKeyManager?.Dispose();
        _hotKeyManager = null;
        StopTrayIconRetry();
        _taskbarIcon?.Dispose();
        _taskbarIcon = null;
        _automationNotificationAnnouncer?.Close();
        _automationNotificationAnnouncer = null;
        _settingsWindow?.Close();
        _guideWindow?.Close();
        _clipsManagerWindow?.Close();
        _onboardingWindow?.Close();
        _editorWindow?.Close();
        _trimmerWindow?.Close();
        CapturePickerWindow.ReleasePooled();
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
        // Yield one dispatcher turn so the previous flow's windows finish closing.
        await Task.Yield();
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
