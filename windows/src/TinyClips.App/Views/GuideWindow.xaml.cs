using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using TinyClips.Core.Models;
using TinyClips.Core.Services;
using Windows.Graphics;

namespace TinyClips.App;

/// <summary>
/// Read-only help reference describing capture modes, the region/screen/window picker,
/// and the current keyboard shortcuts.
/// </summary>
public sealed partial class GuideWindow : Window
{
    public GuideWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new SizeInt32(720, 760));

        var settings = App.Services.GetRequiredService<ICaptureSettings>();
        RootGrid.RequestedTheme = settings.Theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        var hotKeys = App.Services.GetRequiredService<IHotKeyService>();
        ScreenshotShortcut.Text = hotKeys.GetBinding(CaptureType.Screenshot).DisplayString;
        VideoShortcut.Text = hotKeys.GetBinding(CaptureType.Video).DisplayString;
        GifShortcut.Text = hotKeys.GetBinding(CaptureType.Gif).DisplayString;
        StopRecordingShortcut.Text = hotKeys.StopRecordingDisplayString;
    }
}
