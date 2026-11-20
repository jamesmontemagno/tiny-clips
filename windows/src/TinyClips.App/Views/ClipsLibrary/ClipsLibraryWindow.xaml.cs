using System.Collections.Specialized;
using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TinyClips.App.Services.ClipsLibrary;
using TinyClips.App.Services.Studio;
using TinyClips.App.ViewModels.ClipsLibrary;
using TinyClips.Core.Models;
using TinyClips.Core.Models.ClipsLibrary;
using TinyClips.Core.Services;
using TinyClips.Core.Services.ClipsLibrary;
using TinyClips.Core.Studio;

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
    private readonly WindowOpenTrace? _openTrace;
    private readonly CoalescedAction _navigationRebuild;
    private readonly IStudioProjectStore _studioProjects;
    private readonly StudioProjectTracker _studioTracker;
    private readonly object _studioNavigationTag = new();
    private IReadOnlyList<StudioProjectSummary> _studioSummaries = [];
    private IReadOnlyList<StudioUnreadableProject> _unreadableStudioProjects = [];
    private bool _showStudio;
    private bool _studioLoaded;
    private bool _closed;
    private int _studioLoadGeneration;
    private bool _syncingSelection;
    private bool _syncingNavigation;

    public ClipsLibraryWindow()
        : this(WindowOpenTrace.Start(WindowOpenKind.Library))
    {
    }

    private ClipsLibraryWindow(WindowOpenTrace? openTrace)
    {
        _openTrace = openTrace;
        _openTrace?.Mark(WindowOpenMilestone.ConstructorEntered);
        using var construction = _openTrace?.Measure(WindowOpenPhase.ConstructorBody);
        _navigationRebuild = new CoalescedAction(
            callback => DispatcherQueue.TryEnqueue(callback.Invoke), BuildNavigationItems);
        using (_openTrace?.Measure(WindowOpenPhase.ServicesAndViewModel))
        {
            var services = App.Services;
            _studioProjects = services.GetRequiredService<IStudioProjectStore>();
            _studioTracker = services.GetRequiredService<StudioProjectTracker>();
            ViewModel = new ClipsLibraryViewModel(
                services.GetRequiredService<IClipLibraryService>(),
                services.GetRequiredService<IClipMetadataStore>(),
                services.GetRequiredService<IClipsLibrarySettings>(),
                services.GetRequiredService<ICaptureSettings>(),
                services.GetRequiredService<IUploadcareUploadService>(),
                services.GetRequiredService<IClipArchiveService>(),
                services.GetRequiredService<IClipLibraryWatcher>(),
                services.GetRequiredService<IThumbnailCache>(),
                _studioProjects,
                services.GetRequiredService<TimeProvider>(),
                DispatcherQueue.GetForCurrentThread());
            ViewModel.Attach(this);
        }

        using (_openTrace?.Measure(WindowOpenPhase.Xaml))
        {
            InitializeComponent();
            WindowCaptureVisibility.Register(this);
        }

        WindowOpenDiagnostics.Observe(this, RootGrid, _openTrace);
        using (_openTrace?.Measure(WindowOpenPhase.ChromeAndPlacement))
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            AppWindowPlacement.CenterInCurrentWorkAreaAtDipSize(AppWindow, hwnd, DefaultWidthDip, DefaultHeightDip);
            _chromeController = new WindowChromeController(this, RootGrid, MinimumWidthDip, MinimumHeightDip);
        }

        using (_openTrace?.Measure(WindowOpenPhase.ThemeAndSubscriptions))
        {
            ApplyTheme();
            BuildNavigationItems();
            SyncViewModeBar();
            UpdateSubtitle();

            ViewModel.SmartCollections.CollectionChanged += OnNavigationEntriesChanged;
            ViewModel.Collections.CollectionChanged += OnNavigationEntriesChanged;
            ViewModel.TagEntries.CollectionChanged += OnNavigationEntriesChanged;
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            _studioTracker.Changed += OnStudioProjectsChanged;

            Activated += OnFirstActivated;
            Activated += OnActivated;
            Closed += OnClosed;
        }
        _openTrace?.Mark(WindowOpenMilestone.ConstructorCompleted);
    }

    public ClipsLibraryViewModel ViewModel { get; }

    // ------------------------------------------------------------------ lifecycle

    private void OnFirstActivated(object sender, WindowActivatedEventArgs e)
    {
        Activated -= OnFirstActivated;
        _ = _openTrace is null ? ViewModel.InitializeAsync() : InitializeContentAsync();
    }

    private async Task InitializeContentAsync()
    {
        using var loading = _openTrace?.Measure(WindowOpenPhase.ContentLoad);
        try
        {
            await ViewModel.InitializeAsync();
            _openTrace?.Mark(ViewModel.IsStatusError
                ? WindowOpenMilestone.ContentFailed
                : WindowOpenMilestone.ContentReady);
        }
        catch
        {
            _openTrace?.Mark(WindowOpenMilestone.ContentFailed);
            throw;
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        _studioLoadGeneration++;
        Activated -= OnFirstActivated;
        Activated -= OnActivated;
        Closed -= OnClosed;
        _studioTracker.Changed -= OnStudioProjectsChanged;
        _navigationRebuild.Dispose();
        ViewModel.SmartCollections.CollectionChanged -= OnNavigationEntriesChanged;
        ViewModel.Collections.CollectionChanged -= OnNavigationEntriesChanged;
        ViewModel.TagEntries.CollectionChanged -= OnNavigationEntriesChanged;
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
        if (_showStudio)
        {
            _ = LoadStudioProjectsAsync();
        }
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
            case nameof(ClipsLibraryViewModel.CountSubtitle):
                UpdateSubtitle();
                break;
        }
    }

    // ------------------------------------------------------------------ navigation pane

    private void OnNavigationEntriesChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (!_navigationRebuild.Request())
        {
            Debug.WriteLine("Clips Library navigation rebuild could not be queued: dispatcher unavailable.");
        }
    }

    private void BuildNavigationItems()
    {
        using var rebuilding = _openTrace?.Measure(WindowOpenPhase.NavigationRebuild);
        _syncingNavigation = true;
        try
        {
            NavView.MenuItems.Clear();
            NavView.MenuItems.Add(new NavigationViewItemHeader { Content = "Smart Collections" });
            foreach (var entry in ViewModel.SmartCollections)
            {
                NavView.MenuItems.Add(CreateNavigationItem(entry));
            }

            NavView.MenuItems.Add(new NavigationViewItemHeader { Content = "Studio" });
            var studioItem = new NavigationViewItem
            {
                Content = "Projects",
                Icon = new FontIcon { Glyph = "\uE8B7" },
                Tag = _studioNavigationTag,
            };
            AutomationProperties.SetAutomationId(studioItem, "Nav-StudioProjects");
            NavView.MenuItems.Add(studioItem);

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

        object? target = _showStudio ? _studioNavigationTag : ViewModel.SelectedNavigationEntry;
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

        if (args.SelectedItem is NavigationViewItem item && ReferenceEquals(item.Tag, _studioNavigationTag))
        {
            _showStudio = true;
            LibraryContent.Visibility = Visibility.Collapsed;
            StudioContent.Visibility = Visibility.Visible;
            UpdateSubtitle();
            _ = LoadStudioProjectsAsync();
        }
        else if (args.SelectedItem is NavigationViewItem { Tag: NavigationEntryViewModel entry })
        {
            _showStudio = false;
            LibraryContent.Visibility = Visibility.Visible;
            StudioContent.Visibility = Visibility.Collapsed;
            UpdateSubtitle();
            ViewModel.SelectedNavigationEntry = entry;
        }
    }

    private void OnPaneToggleRequested(TitleBar sender, object args) => NavView.IsPaneOpen = !NavView.IsPaneOpen;

    private void UpdateSubtitle() => AppTitleBar.Subtitle = _showStudio ? "Studio" : ViewModel.CountSubtitle;

    private void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        if (_showStudio && e.WindowActivationState != WindowActivationState.Deactivated)
        {
            _ = LoadStudioProjectsAsync();
        }
    }

    private void OnStudioProjectsChanged(object? sender, EventArgs e)
    {
        if (_showStudio && !_closed)
        {
            _ = LoadStudioProjectsAsync();
        }
    }

    private void OnRefreshStudioProjects(object sender, RoutedEventArgs e) => _ = LoadStudioProjectsAsync();

    private void OnStudioSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_showStudio && _studioLoaded)
        {
            RenderStudioProjects();
        }
    }

    private async Task LoadStudioProjectsAsync()
    {
        var generation = ++_studioLoadGeneration;
        _studioLoaded = false;
        StudioProjectRows.Children.Clear();
        if (!ViewModel.IsStudioEnabled)
        {
            _studioSummaries = [];
            _unreadableStudioProjects = [];
            StudioStatus.Text = "Turn on Tiny Clips Studio in Settings to open projects.";
            return;
        }

        StudioStatus.Text = "Loading Studio projects…";
        try
        {
            var result = await Task.Run(() => (
                _studioProjects.ListSummaries(),
                _studioProjects.ListUnreadableProjects()));
            if (_closed || generation != _studioLoadGeneration)
            {
                return;
            }

            _studioSummaries = result.Item1;
            _unreadableStudioProjects = result.Item2.OrderByDescending(project => project.CreatedAt).ToArray();
            _studioLoaded = true;
            RenderStudioProjects();
        }
        catch (Exception ex)
        {
            if (!_closed && generation == _studioLoadGeneration)
            {
                Debug.WriteLine($"Clips Library Studio projects failed: {ex}");
                StudioStatus.Text = $"Couldn't read Studio projects: {ex.Message}";
            }
        }
    }

    private void RenderStudioProjects()
    {
        StudioProjectRows.Children.Clear();
        var query = StudioSearch.Text.Trim();
        var count = 0;
        foreach (var project in StudioProjectSummary.LibraryProjects(_studioSummaries, query))
        {
            var name = project.LibraryName;
            var row = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 12) };
            row.Children.Add(new TextBlock { Text = name, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
            var kind = project.ExportMissing ? "Export missing" : project.IsDraft ? "Draft" : "Project";
            var missing = project.SourceExists ? "" : " · Recording is missing";
            row.Children.Add(new TextBlock { Text = $"{kind} · {project.LastUsedAt.LocalDateTime:g}{missing}", TextWrapping = TextWrapping.Wrap });
            var open = new Button { Content = "Open in Studio", HorizontalAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetName(open, $"Open {name}, {project.LastUsedAt.LocalDateTime:g}, in Studio");
            open.Click += (_, _) =>
            {
                if ((Application.Current as App)?.OpenStudioWindow(project.Id) is null)
                {
                    StudioStatus.Text = "Couldn't open this project. Check that Studio is enabled and the project still exists.";
                }
            };
            row.Children.Add(open);
            StudioProjectRows.Children.Add(row);
            count++;
        }

        if (query.Length == 0 || "Unreadable Project".Contains(query, StringComparison.CurrentCultureIgnoreCase))
        {
            foreach (var project in _unreadableStudioProjects)
            {
                var row = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 12) };
                row.Children.Add(new TextBlock { Text = "Unreadable Project" });
                row.Children.Add(new TextBlock
                {
                    Text = $"Studio cannot read this project from {project.CreatedAt.LocalDateTime:g}. It may be damaged or made by a newer version.",
                    TextWrapping = TextWrapping.Wrap,
                });
                StudioProjectRows.Children.Add(row);
                count++;
            }
        }

        StudioStatus.Text = count > 0 ? "" : query.Length == 0
            ? "No Studio projects yet."
            : "No Studio projects match your search.";
    }

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

    public void OpenInStudio(string projectId) => (Application.Current as App)?.OpenStudioWindow(projectId);

    public void OpenVideoInStudio(string videoPath)
    {
        if (Application.Current is App app)
        {
            _ = app.OpenVideoInStudioAsync(videoPath);
        }
    }

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
