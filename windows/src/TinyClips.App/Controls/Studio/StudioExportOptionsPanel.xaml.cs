using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using TinyClips.Core.Studio;

namespace TinyClips.App.Controls.Studio;

/// <summary>
/// What the Export button's flyout holds: the resolution and the quality of the video, and what
/// they come to. The choices are kept in <see cref="StudioExportOptionsModel"/>.
/// </summary>
public sealed partial class StudioExportOptionsPanel : UserControl
{
    private readonly StudioExportOptionsModel _options;
    private bool _isFilling;

    public StudioExportOptionsPanel(StudioExportOptionsModel options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        InitializeComponent();

        _isFilling = true;
        foreach (var option in options.Resolutions)
        {
            ResolutionChoice.Items.Add(StudioExportPresets.GetOptionText(option));
        }

        ResolutionChoice.SelectedIndex = IndexOfResolution(options.Choice.Resolution);
        foreach (var option in options.Qualities)
        {
            QualityChoice.Items.Add(option.Label);
        }

        QualityChoice.SelectedIndex = IndexOfQuality(options.Choice.Quality);
        _isFilling = false;
        ShowWhatItComesTo();
    }

    /// <summary>Raised with whether the user asked to go on with these choices.</summary>
    public event EventHandler<bool>? Answered;

    /// <summary>
    /// Export is the first thing to answer to the keyboard, so Ctrl+E and Enter export as last
    /// time. A screen reader says what that comes to, as the button's help text.
    /// </summary>
    public void FocusConfirm() => ConfirmButton.Focus(FocusState.Programmatic);

    private int IndexOfResolution(StudioExportResolution resolution)
    {
        for (var index = 0; index < _options.Resolutions.Count; index++)
        {
            if (_options.Resolutions[index].Resolution == resolution)
            {
                return index;
            }
        }

        return 0;
    }

    private int IndexOfQuality(StudioExportQuality quality)
    {
        var qualities = _options.Qualities;
        for (var index = 0; index < qualities.Count; index++)
        {
            if (qualities[index].Quality == quality)
            {
                return index;
            }
        }

        return 0;
    }

    private void OnResolutionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isFilling && ResolutionChoice.SelectedIndex >= 0)
        {
            _options.Choice = _options.Choice with { Resolution = _options.Resolutions[ResolutionChoice.SelectedIndex].Resolution };
            ShowWhatItComesTo();
        }
    }

    private void OnQualityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isFilling && QualityChoice.SelectedIndex >= 0)
        {
            _options.Choice = _options.Choice with { Quality = _options.Qualities[QualityChoice.SelectedIndex].Quality };
            ShowWhatItComesTo();
        }
    }

    private void ShowWhatItComesTo()
    {
        EstimateText.Text = _options.EstimateText;
        DetailsText.Text = _options.DetailsText;
        AutomationProperties.SetHelpText(ConfirmButton, $"{_options.EstimateText}. {_options.DetailsText}.");
        FrameworkElementAutomationPeer.FromElement(EstimateText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e) => Answered?.Invoke(this, true);

    private void OnCancelClick(object sender, RoutedEventArgs e) => Answered?.Invoke(this, false);
}
