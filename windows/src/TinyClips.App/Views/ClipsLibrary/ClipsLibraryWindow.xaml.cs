using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TinyClips.App.Services.ClipsLibrary;
using TinyClips.App.ViewModels.ClipsLibrary;
using TinyClips.Core.Models;
using TinyClips.Core.Models.ClipsLibrary;
using TinyClips.Core.Services;
using TinyClips.Core.Services.ClipsLibrary;

namespace TinyClips.App.Views.ClipsLibrary;

/// <summary>
/// The Clips Library window. Thin shell over <see cref="ClipsLibraryViewModel"/>: hosts the
/// navigation pane, toolbar, grid/list, and detail pane; owns dialogs and selection plumbing.
/// </summary>
[SupportedOSPlatform("windows10.0.22000.0")]
public sealed partial class ClipsLibraryWindow : Window, IClipsLibraryInteraction
{
    // Toolbar (search 240 + filter 160 + view bar 90 + 4 icon buttons 160 + gaps) ≈ 720 at the
    // narrowest useful layout; one grid column (248) + detail pane (340) + nav (232) fits in 880.
    private const int DefaultWidthDip = 1180;
    private const int DefaultHeightDip = 720;
    private const int MinimumWidthDip = 760;
    private const int MinimumHeightDip = 480;

    private readonly WindowChromeController _chromeController;
    private bool _syncingSelection;
    private bool _syncingNavigation;

    public ClipsLibraryWindow()
    {
        var services = App.Services;
        ViewModel = new ClipsLibraryViewModel(
            services.GetRequiredService<IClipLibraryService>(),
            services.GetRequiredService<IClipMetadataStore>(),
            services.GetRequiredService<IClipsLibrarySettings>(),
            services.GetRequiredService<ICaptureSettings>(),
            services.GetRequiredService<IUploadcareUploadService>(),
            services.GetRequiredService<IClipArchiveService>(),
            services.GetRequiredService<IClipLibraryWatcher>(),
            services.GetRequiredService<IThumbnailCache>(),
            services.GetRequiredService<TimeProvider>(),
            DispatcherQueue.GetForCurrentThread());
        ViewModel.Attach(this);

        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        AppWindowPlacement.CenterInCurrentWorkAreaAtDipSize(AppWindow, hwnd, DefaultWidthDip, DefaultHeightDip);
        _chromeController = new WindowChromeController(this, RootGrid, MinimumWidthDip, MinimumHeightDip);

        ApplyTheme();
        BuildNavigationItems();
        SyncViewModeBar();

        ViewModel.SmartCollections.CollectionChanged += (_, _) => BuildNavigationItems();
        ViewModel.Collections.CollectionChanged += (_, _) => BuildNavigationItems();
        ViewModel.TagEntries.CollectionChanged += (_, _) => BuildNavigationItems();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        Activated += OnFirstActivated;
        Closed += OnClosed;
    }

    public ClipsLibraryViewModel ViewModel { get; }

    // ------------------------------------------------------------------ lifecycle

    private void OnFirstActivated(object sender, WindowActivatedEventArgs e)
    {
        Activated -= OnFirstActivated;
        _ = ViewModel.InitializeAsync();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        Activated -= OnFirstActivated;
        Closed -= OnClosed;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        DetailPane.ReleaseMedia();
        ViewModel.Dispose();
    }

    private void ApplyTheme()
    {
        var captureSettings = App.Services.GetRequiredService<ICaptureSettings>();
        RootGrid.RequestedTheme = captureSettings.Theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark  => ElementTheme.Dark,
            _              => ElementTheme.Default,
        };
    }

    /// <summary>Called by <c>App</c> after Settings change so density/quick-action toggles apply live.</summary>
    public void ReloadSettings()
    {
        ApplyTheme();
        ViewModel.ReloadSettings();
    }

    /// <summary>Called by <c>App</c> after an editor or trimmer writes back to a clip on disk.</summary>
    public void NotifyClipChanged(string path)
    {
        ViewModel.NotifyClipChanged(path);
        DetailPane.UpdateMedia();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ClipsLibraryViewModel.ViewMode):
                SyncViewModeBar();
                SyncSelectionAcrossViews();
                break;
            case nameof(ClipsLibraryViewModel.SelectedNavigationEntry):
                SyncNavigationSelection();
                break;
            case nameof(ClipsLibraryViewModel.IsSelectionMode):
                if (!ViewModel.IsSelectionMode)
                {
                    ClearSelection();
                }

                break;
        }
    }

    // ------------------------------------------------------------------ navigation pane

    private void BuildNavigationItems()
    {
        _syncingNavigation = true;
        try
        {
            NavView.MenuItems.Clear();
            NavView.MenuItems.Add(new NavigationViewItemHeader { Content = "Smart Collections" });
            foreach (var entry in ViewModel.SmartCollections)
            {
                NavView.MenuItems.Add(CreateNavigationItem(entry));
            }

            if (ViewModel.Collections.Count > 0)
            {
                NavView.MenuItems.Add(new NavigationViewItemHeader { Content = "Collections" });
                foreach (var entry in ViewModel.Collections)
                {
                    NavView.MenuItems.Add(CreateNavigationItem(entry));
                }
            }

            if (ViewModel.TagEntries.Count > 0)
            {
                NavView.MenuItems.Add(new NavigationViewItemHeader { Content = "Tags" });
                foreach (var entry in ViewModel.TagEntries)
                {
                    NavView.MenuItems.Add(CreateNavigationItem(entry));
                }
            }
        }
        finally
        {
            _syncingNavigation = false;
        }

        SyncNavigationSelection();
    }

    private static NavigationViewItem CreateNavigationItem(NavigationEntryViewModel entry)
    {
        var item = new NavigationViewItem
        {
            Content = entry.Title,
            Icon = new FontIcon { Glyph = entry.Glyph },
            Tag = entry,
            InfoBadge = new InfoBadge { Value = entry.Count, Style = (Style)Application.Current.Resources["InformationalValueInfoBadgeStyle"] },
        };
        item.InfoBadge.SetBinding(InfoBadge.ValueProperty, new Microsoft.UI.Xaml.Data.Binding
        {
            Source = entry,
            Path = new PropertyPath(nameof(NavigationEntryViewModel.Count)),
            Mode = Microsoft.UI.Xaml.Data.BindingMode.OneWay,
        });
        item.SetBinding(AutomationProperties.NameProperty, new Microsoft.UI.Xaml.Data.Binding
        {
            Source = entry,
            Path = new PropertyPath(nameof(NavigationEntryViewModel.AutomationName)),
            Mode = Microsoft.UI.Xaml.Data.BindingMode.OneWay,
        });
        AutomationProperties.SetAutomationId(item, entry.AutomationId);
        return item;
    }

    private void SyncNavigationSelection()
    {
        if (_syncingNavigation)
        {
            return;
        }

        var target = ViewModel.SelectedNavigationEntry;
        var item = NavView.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(candidate => ReferenceEquals(candidate.Tag, target));
        if (!ReferenceEquals(NavView.SelectedItem, item))
        {
            _syncingNavigation = true;
            try
            {
                NavView.SelectedItem = item;
            }
            finally
            {
                _syncingNavigation = false;
            }
        }
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_syncingNavigation)
        {
            return;
        }

        if (args.SelectedItem is NavigationViewItem { Tag: NavigationEntryViewModel entry })
        {
            ViewModel.SelectedNavigationEntry = entry;
        }
    }

    private void OnPaneToggleRequested(TitleBar sender, object args) => NavView.IsPaneOpen = !NavView.IsPaneOpen;

    // ------------------------------------------------------------------ toolbar

    private void SyncViewModeBar()
    {
        var target = ViewModel.IsGridView ? GridViewModeItem : ListViewModeItem;
        if (!ReferenceEquals(ViewModeBar.SelectedItem, target))
        {
            ViewModeBar.SelectedItem = target;
        }
    }

    private void OnViewModeChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is null)
        {
            return;
        }

        ViewModel.ViewMode = ReferenceEquals(sender.SelectedItem, ListViewModeItem) ? ClipsViewMode.List : ClipsViewMode.Grid;
    }

    private void OnSearchQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is string suggestion)
        {
            if (suggestion.StartsWith('#'))
            {
                sender.Text = string.Empty;
                ViewModel.FilterByTagCommand.Execute(suggestion[1..]);
                return;
            }

            sender.Text = suggestion;
        }

        ViewModel.SearchText = sender.Text;
    }

    // ------------------------------------------------------------------ selection plumbing

    private ListViewBase ActiveList => ViewModel.IsGridView ? ClipsGridView : ClipsListView;

    private void OnGridSelectionChanged(object sender, SelectionChangedEventArgs e) => PushSelection(ClipsGridView);

    private void OnListSelectionChanged(object sender, SelectionChangedEventArgs e) => PushSelection(ClipsListView);

    private void PushSelection(ListViewBase source)
    {
        if (_syncingSelection || !ReferenceEquals(source, ActiveList))
        {
            return;
        }

        ViewModel.SetSelection(source.SelectedItems.OfType<ClipItemViewModel>());
    }

    private static void ClearListSelection(ListViewBase list)
    {
        if (list.SelectionMode is ListViewSelectionMode.Multiple or ListViewSelectionMode.Extended)
        {
            list.SelectedItems.Clear();
        }
        else if (list.SelectionMode == ListViewSelectionMode.Single)
        {
            list.SelectedItem = null;
        }
    }

    private void SyncSelectionAcrossViews()
    {
        // Carry the selection over when the user flips grid <-> list.
        _syncingSelection = true;
        try
        {
            var selected = ViewModel.CurrentSelection;
            var target = ActiveList;
            ListViewBase other = ReferenceEquals(target, ClipsGridView) ? ClipsListView : ClipsGridView;
            ClearListSelection(other);
            ClearListSelection(target);
            if (target.SelectionMode == ListViewSelectionMode.Single)
            {
                target.SelectedItem = selected.FirstOrDefault();
            }
            else if (target.SelectionMode is ListViewSelectionMode.Multiple or ListViewSelectionMode.Extended)
            {
                foreach (var item in selected)
                {
                    target.SelectedItems.Add(item);
                }
            }
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    public void SelectAllVisible()
    {
        if (ActiveList.SelectionMode is ListViewSelectionMode.Multiple or ListViewSelectionMode.Extended)
        {
            ActiveList.SelectAll();
        }
    }

    public void ClearSelection()
    {
        _syncingSelection = true;
        try
        {
            ClearListSelection(ClipsGridView);
            ClearListSelection(ClipsListView);
        }
        finally
        {
            _syncingSelection = false;
        }

        ViewModel.SetSelection([]);
    }

    private void OnItemsDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ViewModel.IsSelectionMode)
        {
            return;
        }

        if (FindItem(e.OriginalSource as DependencyObject) is { } item)
        {
            ViewModel.OpenCommand.Execute(item);
        }
    }

    private static ClipItemViewModel? FindItem(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is FrameworkElement { DataContext: ClipItemViewModel item })
            {
                return item;
            }

            if (source is Controls.ClipsLibrary.ClipGridCard card)
            {
                return card.Item;
            }

            if (source is Controls.ClipsLibrary.ClipListRow row)
            {
                return row.Item;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private void OnDragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var paths = e.Items.OfType<ClipItemViewModel>().Select(item => item.Path).ToList();
        ShareService.PopulateDragPackage(e.Data, paths);
    }

    // ------------------------------------------------------------------ keyboard accelerators

    private void OnFocusSearchAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        SearchBox.Focus(FocusState.Keyboard);
        args.Handled = true;
    }

    private void OnRefreshAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.RefreshCommand.Execute(null);
        args.Handled = true;
    }

    private void OnGridViewAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.SetGridViewCommand.Execute(null);
        args.Handled = true;
    }

    private void OnListViewAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.SetListViewCommand.Execute(null);
        args.Handled = true;
    }

    private void OnToggleDetailAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.ToggleDetailPaneCommand.Execute(null);
        args.Handled = true;
    }

    private void OnToggleSelectionAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.ToggleSelectionModeCommand.Execute(null);
        args.Handled = true;
    }

    private void OnOpenAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.OpenCommand.Execute(null);
        args.Handled = true;
    }

    private void OnDeleteAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.DeleteCommand.Execute(null);
        args.Handled = true;
    }

    private void OnRenameAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.RenameCommand.Execute(null);
        args.Handled = true;
    }

    private void OnCopyAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.CopyCommand.Execute(null);
        args.Handled = true;
    }

    private void OnSelectAllAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.SelectAllCommand.Execute(null);
        args.Handled = true;
    }

    private void OnTogglePlaybackAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.IsSelectionMode || ViewModel.SelectedClip?.IsVideo != true)
        {
            return;
        }

        DetailPane.TogglePlayback();
        args.Handled = true;
    }

    private void OnEscapeAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.IsSelectionMode)
        {
            ViewModel.ExitSelectionModeCommand.Execute(null);
            args.Handled = true;
        }
        else if (!string.IsNullOrEmpty(ViewModel.SearchText))
        {
            ViewModel.ClearSearchCommand.Execute(null);
            args.Handled = true;
        }
    }

    // ------------------------------------------------------------------ IClipsLibraryInteraction

    public async Task<bool> ConfirmAsync(string title, string message, string primaryButtonText, bool destructive)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = "Cancel",
            DefaultButton = destructive ? ContentDialogButton.Close : ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.RequestedTheme,
        };
        AutomationProperties.SetAutomationId(dialog, "LibraryConfirmDialog");
        if (destructive)
        {
            dialog.PrimaryButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"];
        }

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public async Task<string?> PromptTextAsync(string title, string label, string? initialValue, string primaryButtonText, string? placeholder = null)
    {
        var box = new TextBox
        {
            Header = label,
            Text = initialValue ?? string.Empty,
            PlaceholderText = placeholder ?? string.Empty,
            AcceptsReturn = string.Equals(label, "Notes", StringComparison.OrdinalIgnoreCase),
            TextWrapping = TextWrapping.Wrap,
            MinWidth = 360,
        };
        if (box.AcceptsReturn)
        {
            box.MinHeight = 120;
        }

        AutomationProperties.SetAutomationId(box, "LibraryPromptTextBox");
        var dialog = new ContentDialog
        {
            Title = title,
            Content = box,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.RequestedTheme,
        };
        AutomationProperties.SetAutomationId(dialog, "LibraryPromptDialog");
        dialog.Opened += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text : null;
    }

    public async Task<string?> PromptChoiceAsync(string title, string label, IReadOnlyList<string> choices, string? current, string primaryButtonText, bool allowNew)
    {
        var combo = new ComboBox
        {
            Header = label,
            IsEditable = allowNew,
            ItemsSource = choices,
            PlaceholderText = allowNew ? "Type a new collection or pick one" : string.Empty,
            MinWidth = 360,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        if (current is not null)
        {
            combo.SelectedItem = choices.FirstOrDefault(choice => string.Equals(choice, current, StringComparison.OrdinalIgnoreCase));
            if (combo.SelectedItem is null && allowNew)
            {
                combo.Text = current;
            }
        }

        AutomationProperties.SetAutomationId(combo, "LibraryChoiceBox");
        var dialog = new ContentDialog
        {
            Title = title,
            Content = combo,
            PrimaryButtonText = primaryButtonText,
            SecondaryButtonText = current is null ? string.Empty : "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.RequestedTheme,
        };
        AutomationProperties.SetAutomationId(dialog, "LibraryChoiceDialog");

        var result = await dialog.ShowAsync();
        return result switch
        {
            ContentDialogResult.Primary => (combo.SelectedItem as string ?? combo.Text ?? string.Empty).Trim(),
            ContentDialogResult.Secondary => string.Empty,
            _ => null,
        };
    }

    public void Share(IReadOnlyList<string> paths, string title) =>
        ShareService.Share(WinRT.Interop.WindowNative.GetWindowHandle(this), paths, title);

    public void OpenInEditor(RecentCapture capture) => (Application.Current as App)?.OpenRecentCaptureFromLibrary(capture);

    public void OpenSettings() => (Application.Current as App)?.OpenSettingsWindow(Settings.SettingsSectionKind.ClipsLibrary);

    // ------------------------------------------------------------------ x:Bind helpers

    public static bool IsSort(ClipSortOption current, string candidate) => current.ToString() == candidate;

    public static bool IsType(ClipTypeFilter current, string candidate) => current.ToString() == candidate;

    public static bool IsDate(ClipDateFilter current, string candidate) => current.ToString() == candidate;

    public static Visibility And(bool a, bool b) => a && b ? Visibility.Visible : Visibility.Collapsed;

    public static ListViewSelectionMode SelectionModeFor(bool isSelectionMode) =>
        isSelectionMode ? ListViewSelectionMode.Multiple : ListViewSelectionMode.Single;

    public static string SelectLabel(bool isSelectionMode) => isSelectionMode ? "Done" : "Select";

    public static string FilterButtonName(string summary) => $"Sort and filter clips. Current: {summary}";

    public static InfoBarSeverity SeverityFor(bool isError) => isError ? InfoBarSeverity.Error : InfoBarSeverity.Success;
}
