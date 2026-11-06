using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TinyClips.App.Settings;
using TinyClips.Core.Editing.Themes;

namespace TinyClips.App;

/// <summary>The Editor Themes picker. Switching applies here; Settings holds the manager.</summary>
public sealed partial class ScreenshotEditorWindow
{
    /// <summary>
    /// The first load uses the requested theme or the active one. A Reset keeps this window's
    /// choice, read again in case it was edited or deleted in Settings.
    /// </summary>
    private void ActivateThemeAfterLoad()
    {
        var theme = _hasActivatedTheme
            ? _themeStore.Get(_controller.ActiveTheme?.Id)
            : _requestedTheme ?? _themeStore.ActiveTheme;
        _hasActivatedTheme = true;
        _controller.ActivateThemeOnOpen(theme);
        UpdateThemePicker();
    }

    private void OnThemeStateChanged(object? sender, EventArgs e)
    {
        if (!_isClosed)
        {
            UpdateThemePicker();
        }
    }

    private void UpdateThemePicker()
    {
        var theme = _controller.ActiveTheme;
        var modified = theme is not null && _controller.IsThemeModified;
        ThemeNameText.Text = theme?.Name ?? "No theme";
        ThemeModifiedText.Visibility = modified ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetHelpText(
            ThemePickerButton,
            theme is null ? "No theme is active." : modified ? $"{theme.Name}, modified." : theme.Name);
    }

    private void OnThemeMenuOpening(object? sender, object e)
    {
        var active = _controller.ActiveTheme;
        var canApply = _controller.CanExport && !_isClosed;
        ThemeMenu.Items.Clear();

        var none = new RadioMenuFlyoutItem
        {
            Text = "None",
            GroupName = "EditorThemes",
            IsChecked = active is null,
        };
        AutomationProperties.SetAutomationId(none, "EditorThemeNoneItem");
        none.Click += OnThemeNoneClick;
        ThemeMenu.Items.Add(none);

        foreach (var theme in _themeStore.Themes.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = theme.Name,
                GroupName = "EditorThemes",
                IsChecked = theme.Id == active?.Id,
                IsEnabled = canApply,
                Tag = theme.Id,
            };
            item.Click += OnThemeItemClick;
            ThemeMenu.Items.Add(item);
        }

        ThemeMenu.Items.Add(new MenuFlyoutSeparator());

        var update = new MenuFlyoutItem
        {
            Text = "Update Theme",
            IsEnabled = active is not null && _controller.IsThemeModified,
        };
        AutomationProperties.SetAutomationId(update, "EditorThemeUpdateItem");
        update.Click += OnThemeUpdateClick;
        ThemeMenu.Items.Add(update);

        var saveAsNew = new MenuFlyoutItem { Text = "Save as New…", IsEnabled = canApply };
        AutomationProperties.SetAutomationId(saveAsNew, "EditorThemeSaveAsNewItem");
        saveAsNew.Click += OnThemeSaveAsNewClick;
        ThemeMenu.Items.Add(saveAsNew);

        var manage = new MenuFlyoutItem { Text = "Manage Themes…" };
        AutomationProperties.SetAutomationId(manage, "EditorThemeManageItem");
        manage.Click += OnThemeManageClick;
        ThemeMenu.Items.Add(manage);
    }

    private void OnThemeNoneClick(object sender, RoutedEventArgs e)
    {
        _controller.ClearTheme();
        SetActiveThemeForNewWindows(null);
    }

    private void OnThemeItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string id } || _themeStore.Get(id) is not { } theme)
        {
            return;
        }

        // Applying replaces the styling of annotations a drag may still be holding.
        Canvas.CancelActiveInteraction();
        if (_controller.ApplyTheme(theme))
        {
            SetActiveThemeForNewWindows(theme.Id);
        }
    }

    private void SetActiveThemeForNewWindows(string? id)
    {
        try
        {
            _themeStore.SetActive(id);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            App.ShowMessageNotification($"Couldn't remember the editor theme. {ex.Message}");
        }
    }

    private void OnThemeUpdateClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _controller.UpdateActiveTheme(_themeStore);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or EditorThemeFormatException or EditorThemeLimitException)
        {
            App.ShowMessageNotification($"Couldn't update the editor theme. {ex.Message}");
        }
    }

    private void OnThemeSaveAsNewClick(object sender, RoutedEventArgs e)
    {
        NewThemeNameBox.Text = _controller.ActiveTheme is { } theme ? $"{theme.Name} copy"[..Math.Min(theme.Name.Length + 5, EditorTheme.MaximumNameLength)] : string.Empty;
        NewThemeError.Visibility = Visibility.Collapsed;
        UpdateSaveNewThemeButton();

        // Wait for the menu to finish closing; only one light-dismiss flyout can be open at a time.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isClosed)
            {
                Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase.ShowAttachedFlyout(ThemePickerButton);
            }
        });
    }

    private void OnNewThemeFlyoutOpened(object? sender, object e)
    {
        NewThemeNameBox.Focus(FocusState.Programmatic);
        NewThemeNameBox.SelectAll();
    }

    private void OnNewThemeNameChanged(object sender, TextChangedEventArgs e)
    {
        NewThemeError.Visibility = Visibility.Collapsed;
        UpdateSaveNewThemeButton();
    }

    private void UpdateSaveNewThemeButton() =>
        SaveNewThemeButton.IsEnabled = !string.IsNullOrWhiteSpace(NewThemeNameBox.Text);

    private void OnNewThemeNameKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && SaveNewThemeButton.IsEnabled)
        {
            e.Handled = true;
            SaveNewTheme();
        }
    }

    private void OnSaveNewThemeClick(object sender, RoutedEventArgs e) => SaveNewTheme();

    private void SaveNewTheme()
    {
        try
        {
            _controller.SaveAsNewTheme(_themeStore, NewThemeNameBox.Text);
            NewThemeFlyout.Hide();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or EditorThemeFormatException or EditorThemeLimitException)
        {
            NewThemeError.Text = ex is EditorThemeFormatException { Field: "name" }
                ? $"Use a name of {EditorTheme.MaximumNameLength} characters or fewer."
                : ex.Message;
            NewThemeError.Visibility = Visibility.Visible;
        }
    }

    private void OnThemeManageClick(object sender, RoutedEventArgs e) =>
        ((App)Application.Current).OpenSettingsWindow(SettingsSectionKind.EditorThemes);
}
