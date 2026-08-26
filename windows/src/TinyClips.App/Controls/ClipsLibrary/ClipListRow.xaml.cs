using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TinyClips.App.ViewModels.ClipsLibrary;

namespace TinyClips.App.Controls.ClipsLibrary;

/// <summary>List row for one clip with optional quick actions; right-click opens the full menu.</summary>
public sealed partial class ClipListRow : UserControl
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(
        nameof(Item), typeof(ClipItemViewModel), typeof(ClipListRow), new PropertyMetadata(null, OnItemChanged));

    public ClipListRow()
    {
        InitializeComponent();
        ContextRequested += OnContextRequested;
    }

    public ClipItemViewModel? Item
    {
        get => (ClipItemViewModel?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    private static void OnItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ClipListRow row && e.NewValue is ClipItemViewModel item)
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(row, item.AutomationIdRoot + "-Row");
        }
    }

    private void OnMoreClicked(object sender, RoutedEventArgs e)
    {
        if (Item is not null)
        {
            ClipContextMenu.Build(Item, "List").ShowAt(MoreButton);
        }
    }

    private void OnContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (Item is null)
        {
            return;
        }

        var menu = ClipContextMenu.Build(Item, "List");
        if (args.TryGetPosition(this, out var point))
        {
            menu.ShowAt(this, point);
        }
        else
        {
            menu.ShowAt(this);
        }

        args.Handled = true;
    }

    public static string Id(ClipItemViewModel? item, string action) => item is null ? action : $"{item.AutomationIdRoot}-List-{action}";
}
