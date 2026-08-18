using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using TinyClips.Core.Models;
using TinyClips.Core.Services;
using Windows.Graphics;

namespace TinyClips.App;

/// <summary>
/// Read-only overview of capture modes, shortcuts, editing, recording, and library features.
/// </summary>
public sealed partial class GuideWindow : Window
{
    // 460 x 400 DIP: scrollable content; guide opens at 720 x 860 so the minimum is deliberately
    // narrow; text wraps, and the MaxWidth=720 inner panel simply centers when the window is wider.
    private const int MinimumWidthDip  = 460;
    private const int MinimumHeightDip = 400;

    private readonly WindowChromeController _chromeController;

    public GuideWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        AppWindowPlacement.CenterInCurrentWorkAreaAtDipSize(AppWindow, hwnd, 720, 860);

        // WindowChromeController owns: icon-on-activation, DIP minimum enforcement, XamlRoot
        // scale tracking, and cleanup of all three on Closed. GuideWindow has no other
        // subscriptions so no additive Closed handler is needed.
        _chromeController = new WindowChromeController(this, RootGrid, MinimumWidthDip, MinimumHeightDip);

        var settings = App.Services.GetRequiredService<ICaptureSettings>();
        RootGrid.RequestedTheme = settings.Theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        var hotKeys = App.Services.GetRequiredService<IHotKeyService>();
        ScreenshotShortcut.Text = hotKeys.GetBinding(HotKeyAction.Screenshot).DisplayString;
        VideoShortcut.Text = hotKeys.GetBinding(HotKeyAction.RecordVideo).DisplayString;
        GifShortcut.Text = hotKeys.GetBinding(HotKeyAction.RecordGif).DisplayString;
        OcrShortcut.Text = hotKeys.GetBinding(HotKeyAction.RecognizeText).DisplayString;
        StopRecordingShortcut.Text = hotKeys.StopRecordingDisplayString;
    }
}
