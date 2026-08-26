using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TinyClips.App.ViewModels.ClipsLibrary;

namespace TinyClips.App.Controls.ClipsLibrary;

/// <summary>Grid tile for one clip. Hover reveals quick actions; right-click opens the full menu.</summary>
public sealed partial class ClipGridCard : UserControl
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(
        nameof(Item), typeof(ClipItemViewModel), typeof(ClipGridCard), new PropertyMetadata(null, OnItemChanged));

    public ClipGridCard()
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
        if (d is ClipGridCard card && e.NewValue is ClipItemViewModel item)
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(card, item.AutomationIdRoot + "-Card");
        }
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e) => VisualStateManager.GoToState(this, "Hovered", true);

    private void OnPointerExited(object sender, PointerRoutedEventArgs e) => VisualStateManager.GoToState(this, "Normal", true);

    private void OnMoreClicked(object sender, RoutedEventArgs e)
    {
        if (Item is null)
        {
            return;
        }

        ClipContextMenu.Build(Item, "Grid").ShowAt(MoreButton);
    }

    private void OnContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (Item is null)
        {
            return;
        }

        var menu = ClipContextMenu.Build(Item, "Grid");
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

    public static Visibility Not(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility And(bool a, bool b) => a && b ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Or(bool a, bool b) => a || b ? Visibility.Visible : Visibility.Collapsed;

    public static string Id(ClipItemViewModel? item, string action) => item is null ? action : $"{item.AutomationIdRoot}-Grid-{action}";

    public static string FavoriteLabel(bool isFavorite) => isFavorite ? "Remove from favorites" : "Add to favorites";

    public static string FavoriteGlyph(bool isFavorite) => isFavorite ? "\uE735" : "\uE734";

    public static string UploadText(bool isUploading, bool hasUrl) => isUploading ? "Uploading…" : hasUrl ? "Uploaded" : string.Empty;
}
