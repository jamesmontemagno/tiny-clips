using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace TinyClips.App.Settings.Sections;

public sealed class AnalyticsChart : ListView
{
    public AnalyticsChart()
    {
        PreviewKeyDown += OnChartPreviewKeyDown;
    }

    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);
        var summary = item switch
        {
            CaptureAnalyticsDayViewModel day => day.AccessibilitySummary,
            WeekdayBreakdownViewModel weekday => weekday.AccessibilitySummary,
            HourBreakdownViewModel hour => hour.AccessibilitySummary,
            _ => item.ToString() ?? string.Empty,
        };
        AutomationProperties.SetName(element, summary);
    }

    private void OnChartPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (Items.Count == 0
            || e.Key is not (VirtualKey.Left or VirtualKey.Right or VirtualKey.Home or VirtualKey.End))
        {
            return;
        }

        var forward = FlowDirection == FlowDirection.RightToLeft ? -1 : 1;
        var index = e.Key switch
        {
            VirtualKey.Left => SelectedIndex - forward,
            VirtualKey.Right => SelectedIndex + forward,
            VirtualKey.Home => 0,
            VirtualKey.End => Items.Count - 1,
            _ => SelectedIndex,
        };
        SelectedIndex = Math.Clamp(index, 0, Items.Count - 1);
        if (ContainerFromIndex(SelectedIndex) is Control container)
        {
            container.Focus(FocusState.Keyboard);
        }
        e.Handled = true;
    }
}
