using System.Windows.Input;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TinyClips.App.ViewModels.ClipsLibrary;

namespace TinyClips.App.Controls.ClipsLibrary;

/// <summary>
/// Builds the full per-clip action menu shared by grid cards, list rows, the detail pane and the
/// right-click context menu, so every surface exposes the same verbs in the same order.
/// </summary>
internal static class ClipContextMenu
{
    public static MenuFlyout Build(ClipItemViewModel item, string surface)
    {
        var owner = item.Owner;
        var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight };

        menu.Items.Add(Item("Open", "\uE8A7", owner.OpenCommand, item, Id(item, surface, "Open"), "Enter"));
        menu.Items.Add(Item($"{item.EditVerb}…", item.EditGlyph, owner.OpenCommand, item, Id(item, surface, "Edit")));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item(item.IsFavorite ? "Remove from favorites" : "Add to favorites", item.IsFavorite ? "\uE8D9" : "\uE734", owner.ToggleFavoriteCommand, item, Id(item, surface, "Favorite")));
        menu.Items.Add(Item("Rename…", "\uE8AC", owner.RenameCommand, item, Id(item, surface, "Rename"), "F2"));
        menu.Items.Add(Item("Edit tags…", "\uE8EC", owner.EditTagsCommand, item, Id(item, surface, "Tags")));
        menu.Items.Add(Item("Notes…", "\uE70B", owner.EditNotesCommand, item, Id(item, surface, "Notes")));
        menu.Items.Add(Item("Set collection…", "\uE8B7", owner.SetCollectionCommand, item, Id(item, surface, "Collection")));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Copy", "\uE8C8", owner.CopyCommand, item, Id(item, surface, "Copy"), "Ctrl+C"));
        menu.Items.Add(Item("Share…", "\uE72D", owner.ShareCommand, item, Id(item, surface, "Share")));
        menu.Items.Add(Item("Show in Explorer", "\uEC50", owner.RevealCommand, item, Id(item, surface, "Reveal")));

        if (owner.IsUploadcareEnabled || item.HasUploadedUrl)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            if (owner.IsUploadcareEnabled)
            {
                menu.Items.Add(Item("Upload to Uploadcare", "\uE898", owner.UploadCommand, item, Id(item, surface, "Upload")));
            }

            if (item.HasUploadedUrl)
            {
                menu.Items.Add(Item("Copy upload link", "\uE71B", owner.CopyLinkCommand, item, Id(item, surface, "CopyLink")));
                menu.Items.Add(Item("Open upload link", "\uE8A7", owner.OpenLinkCommand, item, Id(item, surface, "OpenLink")));
            }
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Rename file on disk…", "\uE8E5", owner.RenameFileCommand, item, Id(item, surface, "RenameFile")));
        menu.Items.Add(Item("Move to Archive", "\uE7B8", owner.ArchiveCommand, item, Id(item, surface, "Archive")));
        menu.Items.Add(Item("Delete", "\uE74D", owner.DeleteCommand, item, Id(item, surface, "Delete"), "Delete", destructive: true));

        return menu;
    }

    private static string Id(ClipItemViewModel item, string surface, string action) => $"{item.AutomationIdRoot}-{surface}-{action}";

    private static MenuFlyoutItem Item(string text, string glyph, ICommand command, object parameter, string automationId, string? accelerator = null, bool destructive = false)
    {
        var flyoutItem = new MenuFlyoutItem
        {
            Text = text,
            Icon = new FontIcon { Glyph = glyph },
            Command = command,
            CommandParameter = parameter,
        };
        if (accelerator is not null)
        {
            flyoutItem.KeyboardAcceleratorTextOverride = accelerator;
        }

        if (destructive)
        {
            try
            {
                if (Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue("SystemFillColorCriticalBrush", out var brush) && brush is Microsoft.UI.Xaml.Media.Brush critical)
                {
                    flyoutItem.Foreground = critical;
                }
            }
            catch
            {
            }
        }

        AutomationProperties.SetAutomationId(flyoutItem, automationId);
        AutomationProperties.SetName(flyoutItem, text.TrimEnd('…'));
        return flyoutItem;
    }
}
