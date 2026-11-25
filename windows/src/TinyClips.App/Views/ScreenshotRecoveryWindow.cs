using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using TinyClips.Core.Models;
using TinyClips.Core.Services;
using Windows.Storage.Pickers;

namespace TinyClips.App;

/// <summary>Retains a failed capture until the user retries, saves it, or explicitly discards it.</summary>
internal sealed class ScreenshotRecoveryWindow : Window
{
    private Func<Task<bool>>? _retry;
    private readonly Func<Task<bool>> _save;
    private readonly Func<string, Task<bool>> _saveAs;
    private readonly ImageFormat _format;
    private readonly Grid _root;
    private readonly InfoBar _status;
    private readonly Button _retryButton;
    private readonly Button _saveButton;
    private readonly Button _saveAsButton;
    private readonly Button _discardButton;
    private readonly WindowChromeController _chromeController;
    private bool _busy;
    private bool _resolved;
    private bool _promptOpen;

    public ScreenshotRecoveryWindow(
        string reason, Func<Task<bool>>? retry, Func<Task<bool>> save,
        Func<string, Task<bool>> saveAs, ICaptureSettings settings)
    {
        _retry = retry;
        _save = save;
        _saveAs = saveAs;
        _format = settings.ImageFormat;
        Title = "Screenshot needs attention";

        _status = new InfoBar
        {
            IsOpen = true,
            IsClosable = false,
            Severity = InfoBarSeverity.Warning,
            Title = "Your screenshot is still available",
            Message = reason,
        };
        AutomationProperties.SetAutomationId(_status, "ScreenshotRecoveryStatus");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(_status, reason);

        _retryButton = new Button
        {
            Content = "Try again",
            IsEnabled = retry is not null,
            Visibility = retry is null ? Visibility.Collapsed : Visibility.Visible,
        };
        _saveButton = new Button
        {
            Content = "Save screenshot",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
        };
        _saveAsButton = new Button { Content = "Save as\u2026" };
        _discardButton = new Button { Content = "Discard" };
        AutomationProperties.SetAutomationId(_retryButton, "ScreenshotRecoveryRetryButton");
        AutomationProperties.SetAutomationId(_saveButton, "ScreenshotRecoverySaveButton");
        AutomationProperties.SetAutomationId(_saveAsButton, "ScreenshotRecoverySaveAsButton");
        AutomationProperties.SetAutomationId(_discardButton, "ScreenshotRecoveryDiscardButton");
        _retryButton.Click += async (_, _) => await RunAsync(_retry);
        _saveButton.Click += async (_, _) => await RunAsync(_save);
        _saveAsButton.Click += async (_, _) => await RunAsync(PickAndSaveAsAsync);
        _discardButton.Click += async (_, _) => await ConfirmDiscardAsync();

        var actions = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        actions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        foreach (var button in new[] { _retryButton, _saveButton, _saveAsButton, _discardButton })
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetColumn(_saveButton, 1);
        Grid.SetRow(_saveAsButton, 1);
        Grid.SetRow(_discardButton, 1);
        Grid.SetColumn(_discardButton, 1);
        actions.Children.Add(_retryButton);
        actions.Children.Add(_saveButton);
        actions.Children.Add(_saveAsButton);
        actions.Children.Add(_discardButton);
        var content = new StackPanel { Spacing = 16, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(new TextBlock
        {
            Text = "Screenshot needs attention",
            Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(_status);
        content.Children.Add(actions);
        _root = new Grid { Padding = new Thickness(24) };
        _root.Children.Add(content);
        _root.RequestedTheme = settings.Theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        Content = _root;
        WindowCaptureVisibility.Register(this);
        AppWindowPlacement.CenterInCurrentWorkAreaAtDipSize(
            AppWindow, WinRT.Interop.WindowNative.GetWindowHandle(this), 560, 360);
        _chromeController = new WindowChromeController(this, _root, 480, 320);
        AppWindow.Closing += OnClosing;
    }

    private async Task RunAsync(Func<Task<bool>>? action)
    {
        if (_busy || action is null) return;
        _busy = true;
        _retryButton.IsEnabled = false;
        _saveButton.IsEnabled = false;
        _saveAsButton.IsEnabled = false;
        _discardButton.IsEnabled = false;
        try
        {
            if (await action())
            {
                _resolved = true;
                Close();
                return;
            }
            SetStatus("Your screenshot is still available. Try again, save it, or choose Save as.");
        }
        catch (Exception ex)
        {
            CrashDiagnostics.Log("Screenshot recovery", ex, handled: true);
            SetStatus("Couldn't complete that action. Try again, or choose Save as to use another folder.");
        }
        finally
        {
            _busy = false;
            if (!_resolved)
            {
                _retryButton.IsEnabled = _retry is not null;
                _saveButton.IsEnabled = true;
                _saveAsButton.IsEnabled = true;
                _discardButton.IsEnabled = true;
            }
        }
    }

    private async Task<bool> PickAndSaveAsAsync()
    {
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeChoices.Add("PNG image", new[] { ".png" });
        picker.FileTypeChoices.Add("JPEG image", new[] { ".jpg" });
        picker.FileTypeChoices.Add("WebP image", new[] { ".webp" });
        picker.DefaultFileExtension = _format switch
        {
            ImageFormat.Jpeg => ".jpg",
            ImageFormat.Webp => ".webp",
            _ => ".png",
        };
        picker.SuggestedFileName = "Screenshot";
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        return file is not null && await _saveAs(file.Path);
    }

    private void SetStatus(string message)
    {
        _status.Message = message;
        AutomationProperties.SetName(_status, message);
        var peer = FrameworkElementAutomationPeer.FromElement(_status)
            ?? FrameworkElementAutomationPeer.CreatePeerForElement(_status);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    internal void CloseForPresentationRetry()
    {
        _resolved = true;
        Close();
    }

    internal void DisableRetry()
    {
        _retry = null;
        _retryButton.IsEnabled = false;
        _retryButton.Visibility = Visibility.Collapsed;
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_resolved) return;
        args.Cancel = true;
        if (!_busy) await ConfirmDiscardAsync();
    }

    private async Task ConfirmDiscardAsync()
    {
        if (_promptOpen || _busy) return;
        _promptOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                Title = "Discard this screenshot?",
                Content = "This screenshot may not be saved or copied. Discarding it cannot be undone.",
                PrimaryButtonText = "Discard",
                CloseButtonText = "Keep screenshot",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = _root.XamlRoot,
                RequestedTheme = _root.RequestedTheme,
            };
            AutomationProperties.SetAutomationId(dialog, "ScreenshotRecoveryDiscardDialog");
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                _resolved = true;
                Close();
            }
        }
        finally
        {
            _promptOpen = false;
        }
    }
}
