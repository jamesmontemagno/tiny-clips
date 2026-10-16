using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TinyClips.App.Controls.Studio;
using Windows.UI;

namespace TinyClips.Tools.StudioPreviewCheck.Windowed;

/// <summary>
/// The window of the window checks: the app's <see cref="StudioPreviewPanel"/> in a host grid, with
/// XAML elements over it. Everything that is checked is in <see cref="WindowChecks"/>.
/// </summary>
public sealed partial class CheckWindow : Microsoft.UI.Xaml.Window
{
    private bool _nudged;

    internal CheckWindow()
    {
        InitializeComponent();
    }

    internal Grid HostElement => PanelHost;

    internal StudioPreviewPanel PanelElement => Panel;

    /// <summary>
    /// Changes the window's background by an amount nobody can see, so that the system composes the
    /// window again and delivers a screenshot even when the panel has nothing new to show.
    /// </summary>
    internal void Nudge()
    {
        _nudged = !_nudged;
        Root.Background = new SolidColorBrush(Color.FromArgb(255, (byte)(_nudged ? 29 : 28), 28, 28));
    }
}
