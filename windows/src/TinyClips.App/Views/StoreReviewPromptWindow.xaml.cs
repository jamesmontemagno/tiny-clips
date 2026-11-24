using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using TinyClips.Core.Models;
using TinyClips.Core.Services;
#if TINYCLIPS_STORE_BUILD
using Windows.Services.Store;
using WinRT.Interop;
#endif

namespace TinyClips.App;

/// <summary>One-time Store review and feedback prompt with a stable HWND for the native Store flow.</summary>
public sealed partial class StoreReviewPromptWindow : Window
{
    private const int MinimumWidthDip = 440;
    private const int MinimumHeightDip = 340;
    private const int WindowWidthDip = 500;
    private const int WindowHeightDip = 370;

    private readonly Action<bool> _onClosed;
    private readonly WindowChromeController _chromeController;
    private bool _reviewAttempted;
    private bool _reviewInProgress;
    private bool _isClosed;
    private bool _feedbackRequested;

    public StoreReviewPromptWindow(Action<bool> onClosed)
    {
        _onClosed = onClosed;
        InitializeComponent();

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WindowCaptureVisibility.Register(this, alwaysExcluded: true);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindowPlacement.CenterInCurrentWorkAreaAtDipSize(
            AppWindow,
            hwnd,
            WindowWidthDip,
            WindowHeightDip);
        _chromeController = new WindowChromeController(
            this,
            RootGrid,
            MinimumWidthDip,
            MinimumHeightDip);

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        RootGrid.RequestedTheme = App.Services.GetRequiredService<ICaptureSettings>().Theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        RootGrid.KeyDown += OnRootKeyDown;
        RootGrid.Loaded += OnRootLoaded;
        AppWindow.Closing += OnAppWindowClosing;
        Closed += OnWindowClosed;
    }

    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= OnRootLoaded;
        NotNowButton.Focus(FocusState.Programmatic);
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape || _reviewInProgress || _isClosed)
        {
            return;
        }

        e.Handled = true;
        Close();
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        // Alt+F4 and the title-bar close button must not invalidate the owner HWND while the
        // native Store surface is active.
        if (_reviewInProgress)
        {
            args.Cancel = true;
        }
    }

    private void OnNotNowClicked(object sender, RoutedEventArgs e)
    {
        if (!_reviewInProgress && !_isClosed)
        {
            Close();
        }
    }

    private void OnFeedbackClicked(object sender, RoutedEventArgs e)
    {
        if (_reviewInProgress || _isClosed)
        {
            return;
        }

        _feedbackRequested = true;
        Close();
    }

    private void OnReviewClicked(object sender, RoutedEventArgs e)
#if TINYCLIPS_STORE_BUILD
        => _ = RequestReviewAsync();
#else
    {
        if (_reviewAttempted || _reviewInProgress || _isClosed)
        {
            return;
        }

        _reviewAttempted = true;
        _reviewInProgress = true;
        ReviewButton.IsEnabled = false;
        FeedbackButton.IsEnabled = false;
        NotNowButton.IsEnabled = false;
        ReviewBusyPanel.Visibility = Visibility.Visible;
        ReviewStatusBar.IsOpen = false;

        ReportReviewFailure(
            "This review flow is available only in the Microsoft Store build. Tiny Clips won't ask again.");
    }
#endif

#if TINYCLIPS_STORE_BUILD
    private async Task RequestReviewAsync()
    {
        if (_reviewAttempted || _reviewInProgress || _isClosed)
        {
            return;
        }

        _reviewAttempted = true;
        _reviewInProgress = true;
        ReviewButton.IsEnabled = false;
        FeedbackButton.IsEnabled = false;
        NotNowButton.IsEnabled = false;
        ReviewBusyPanel.Visibility = Visibility.Visible;
        ReviewStatusBar.IsOpen = false;

        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hwnd == 0 || _isClosed)
            {
                ReportReviewFailure(
                    "Tiny Clips no longer has a valid window for the Microsoft Store. Tiny Clips won't ask again.");
                return;
            }

            var context = StoreContext.GetDefault();
            InitializeWithWindow.Initialize(context, hwnd);
            var result = await context.RequestRateAndReviewAppAsync();
            if (result.ExtendedError is { } error)
            {
                ReportReviewFailure(
                    GetReviewFailureMessage(result.Status),
                    error);
                return;
            }

            switch (result.Status)
            {
                case StoreRateAndReviewStatus.Succeeded:
                case StoreRateAndReviewStatus.CanceledByUser:
                    _reviewInProgress = false;
                    Close();
                    return;
                case StoreRateAndReviewStatus.NetworkError:
                case StoreRateAndReviewStatus.Error:
                    ReportReviewFailure(GetReviewFailureMessage(result.Status));
                    return;
                default:
                    ReportReviewFailure(
                        "The Microsoft Store did not complete the review request. Tiny Clips won't ask again.");
                    return;
            }
        }
        catch (Exception ex)
        {
            ReportReviewFailure(
                "The Microsoft Store couldn't open the review flow. Tiny Clips won't ask again.",
                ex);
        }
    }

    private static string GetReviewFailureMessage(StoreRateAndReviewStatus status) =>
        status == StoreRateAndReviewStatus.NetworkError
            ? "The Microsoft Store could not be reached. Tiny Clips won't ask again."
            : "The Microsoft Store could not complete the review request. Tiny Clips won't ask again.";
#endif

    private void ReportReviewFailure(string message, Exception? exception = null)
    {
        _reviewInProgress = false;
        ReviewBusyPanel.Visibility = Visibility.Collapsed;
        ReviewStatusBar.Message = message;
        ReviewStatusBar.IsOpen = true;
        ReviewButton.IsEnabled = false;
        FeedbackButton.IsEnabled = true;
        NotNowButton.IsEnabled = true;
        NotNowButton.Content = "Close";
        AutomationProperties.SetName(NotNowButton, "Close review prompt");
        AutomationProperties.SetHelpText(NotNowButton, "Close the review prompt.");

        if (exception is not null)
        {
            Debug.WriteLine($"Store review request failed: {exception}");
        }

        NotNowButton.Focus(FocusState.Programmatic);
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _isClosed = true;
        RootGrid.KeyDown -= OnRootKeyDown;
        RootGrid.Loaded -= OnRootLoaded;
        AppWindow.Closing -= OnAppWindowClosing;
        Closed -= OnWindowClosed;
        _onClosed(_feedbackRequested);
    }
}
