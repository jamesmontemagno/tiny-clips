using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using TinyClips.App.Services.ClipsLibrary;
using TinyClips.Core.Models;
using TinyClips.Core.Services;
using TinyClips.Core.Studio;

namespace TinyClips.App.Controls;

internal enum RecentCaptureCardAction
{
    Copy,
    Edit,
    Reveal,
    Delete,
}

internal sealed class RecentCapturesFlyout : Flyout
{
    internal const double CardWidth = 176;
    private const double CardSpacing = 10;
    private const double ContentPadding = 14;
    private readonly StackPanel _cards;
    private readonly ScrollViewer _scroller;
    private readonly TrayRecentItems<RecentCaptureCard> _retained = new();
    private readonly IThumbnailCache _thumbnails;
    private readonly IStudioProjectStore _projects;
    private readonly DispatcherQueue _dispatcher;
    private readonly Func<RecentMenuEntry, RecentCaptureCardAction, Task> _action;
    private RecentCaptureCard? _activeCard;

    public RecentCapturesFlyout(
        IThumbnailCache thumbnails,
        IStudioProjectStore projects,
        DispatcherQueue dispatcher,
        Func<RecentMenuEntry, RecentCaptureCardAction, Task> action)
    {
        _thumbnails = thumbnails;
        _projects = projects;
        _dispatcher = dispatcher;
        _action = action;
        Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom;
        ShouldConstrainToRootBounds = false;
        FlyoutPresenterStyle = new Style(typeof(FlyoutPresenter))
        {
            Setters =
            {
                new Setter(FrameworkElement.MaxWidthProperty, 3 * CardWidth + 2 * CardSpacing + 2 * ContentPadding + 2),
                new Setter(Control.PaddingProperty, new Thickness(ContentPadding)),
                // On opening, give focus to the flyout rather than lighting up the first card.
                new Setter(Control.IsTabStopProperty, true),
                // Only the card scroller should take horizontal input and request thumbnails.
                new Setter(ScrollViewer.HorizontalScrollModeProperty, ScrollMode.Disabled),
                new Setter(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled),
            },
        };

        _cards = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = CardSpacing,
            // WinUI overlays the horizontal scrollbar on its content.
            Margin = new Thickness(0, 0, 0, (double)Application.Current.Resources["ScrollBarSize"] + CardSpacing),
        };
        _scroller = new ScrollViewer
        {
            Content = _cards,
            HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        _scroller.ViewChanging += (_, args) =>
            RequestThumbnailsNear(args.FinalView.HorizontalOffset);
        _scroller.PointerEntered += (_, args) => ShowActionsFor(args.OriginalSource);
        _scroller.AddHandler(UIElement.PointerMovedEvent,
            new PointerEventHandler((_, args) => ShowActionsFor(args.OriginalSource)), true);
        _scroller.PointerExited += (_, _) => ShowActionsFor(null);
        _scroller.GotFocus += (_, args) =>
        {
            if (args.OriginalSource is Control { FocusState: FocusState.Keyboard } focused)
            {
                ShowActionsFor(focused);
            }
        };
        _scroller.LosingFocus += (_, args) =>
        {
            if (args.FocusState != FocusState.Pointer)
            {
                ShowActionsFor(null);
            }
        };

        Content = new StackPanel { Spacing = 8, Children = { CreateHeader(), _scroller } };
        Opening += (_, _) => RequestThumbnailsNear(_scroller.HorizontalOffset);
        Closed += (_, _) => ShowActionsFor(null);
    }

    public int Count => _retained.Items.Count;

    private Grid CreateHeader()
    {
        var history = new FontIcon { Glyph = "\uE81C", FontSize = 16 };
        AutomationProperties.SetAccessibilityView(history, AccessibilityView.Raw);
        var title = new TextBlock
        {
            Text = "Recent captures",
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level1);

        const string closeLabel = "Close recent captures";
        var close = new Button
        {
            Content = new FontIcon { Glyph = "\uE711", FontSize = 12 },
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
        };
        AutomationProperties.SetAutomationId(close, "TrayRecentCapturesCloseButton");
        AutomationProperties.SetName(close, closeLabel);
        ToolTipService.SetToolTip(close, closeLabel);
        close.Click += (_, _) => Hide();

        Grid.SetColumn(title, 1);
        Grid.SetColumn(close, 2);
        return new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition(),
                new ColumnDefinition { Width = GridLength.Auto },
            },
            Children = { history, title, close },
        };
    }

    private void ShowActionsFor(object? source)
    {
        var element = source as DependencyObject;
        while (element is not null && element != _scroller && element is not RecentCaptureCard)
        {
            element = VisualTreeHelper.GetParent(element);
        }

        var card = element as RecentCaptureCard;
        if (card != _activeCard)
        {
            _activeCard?.ShowActions(false);
            _activeCard = card;
            card?.ShowActions(true);
        }
    }

    public void Update(IReadOnlyList<RecentMenuEntry> entries)
    {
        _retained.Update(entries, CreateCard, static (card, entry) => card.Update(entry));
        var items = _retained.Items;
        if (_cards.Children.Count == items.Count &&
            _cards.Children.Zip(items).All(pair => ReferenceEquals(pair.First, pair.Second)))
        {
            return;
        }

        _cards.Children.Clear();
        foreach (var card in items)
        {
            _cards.Children.Add(card);
        }

        if (IsOpen)
        {
            RequestThumbnailsNear(_scroller.HorizontalOffset);
        }
    }

    private RecentCaptureCard CreateCard(RecentMenuEntry entry) =>
        new(entry, _thumbnails, _projects, _dispatcher, async (item, action) =>
        {
            if (action != RecentCaptureCardAction.Copy)
            {
                Hide();
            }

            await _action(item, action);
        });

    private void RequestThumbnailsNear(double horizontalOffset)
    {
        var start = Math.Max(0, (int)Math.Floor(horizontalOffset / (CardWidth + CardSpacing)) - 1);
        var count = Math.Min(_retained.Items.Count - start, 5);
        for (var index = 0; index < count; index++)
        {
            _retained.Items[start + index].RequestThumbnail();
        }
    }
}

internal sealed class RecentCaptureCard : UserControl
{
    private const string ScreenshotGlyph = "\uE722";
    private const string VideoGlyph = "\uE714";
    private const string GifGlyph = "\uF4A9";
    private const string StudioGlyph = "\uE81E";
    private const double ActionDiameter = 32;
    private const double ThumbnailHeight = 88;
    private static readonly FontFamily FluentIconFont = new("Segoe Fluent Icons");

    private readonly IThumbnailCache _thumbnails;
    private readonly IStudioProjectStore _projects;
    private readonly DispatcherQueue _dispatcher;
    private readonly Func<RecentMenuEntry, RecentCaptureCardAction, Task> _action;
    private readonly Image _preview = new();
    private readonly FontIcon _fallback = new() { FontFamily = FluentIconFont, FontSize = 28 };
    private readonly TextBlock _title = new();
    private readonly TextBlock _metadata = new();
    private readonly FontIcon _typeIcon = new() { FontFamily = FluentIconFont, FontSize = 11 };
    private readonly Button _mainButton = new();
    private readonly Button _copyButton;
    private readonly Button _editButton;
    private readonly Button _revealButton;
    private readonly Button _deleteButton;
    private readonly StackPanel _actions = new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 6,
        Opacity = 0,
        IsHitTestVisible = false,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(0, (ThumbnailHeight - ActionDiameter) / 2, 0, 0),
    };
    private RecentMenuEntry _entry;
    private CancellationTokenSource? _thumbnailCancellation;
    private bool _thumbnailRequested;

    public RecentCaptureCard(
        RecentMenuEntry entry,
        IThumbnailCache thumbnails,
        IStudioProjectStore projects,
        DispatcherQueue dispatcher,
        Func<RecentMenuEntry, RecentCaptureCardAction, Task> action)
    {
        _entry = entry;
        _thumbnails = thumbnails;
        _projects = projects;
        _dispatcher = dispatcher;
        _action = action;

        Width = RecentCapturesFlyout.CardWidth;

        _mainButton.Padding = new Thickness(0);
        _mainButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        _mainButton.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _mainButton.BorderThickness = new Thickness(0);
        _mainButton.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _mainButton.Click += (_, _) => _ = Invoke(RecentCaptureCardAction.Edit);
        var thumbnail = new Grid
        {
            Width = 156,
            Height = ThumbnailHeight,
            Background = Application.Current.Resources["ControlFillColorSecondaryBrush"] as Brush,
        };
        thumbnail.Children.Add(_preview);
        thumbnail.Children.Add(_fallback);
        var badge = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(5),
            Padding = new Thickness(4, 2, 4, 2),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(ColorHelper.FromArgb(220, 0, 0, 0)),
            Child = _typeIcon,
        };
        _typeIcon.Foreground = new SolidColorBrush(Colors.White);
        thumbnail.Children.Add(badge);

        var body = new StackPanel { Spacing = 5 };
        body.Children.Add(thumbnail);
        _title.TextTrimming = TextTrimming.CharacterEllipsis;
        _title.FontSize = 13;
        body.Children.Add(_title);
        _metadata.TextTrimming = TextTrimming.CharacterEllipsis;
        _metadata.FontSize = 11;
        _metadata.Opacity = 0.72;
        body.Children.Add(_metadata);
        _mainButton.Content = body;
        _copyButton = CreateActionButton("\uE8C8", "Copy", RecentCaptureCardAction.Copy);
        _editButton = CreateActionButton("\uE70F", "Edit", RecentCaptureCardAction.Edit);
        _revealButton = CreateActionButton("\uE8B7", "Show in File Explorer", RecentCaptureCardAction.Reveal);
        _deleteButton = CreateActionButton("\uE74D", "Delete", RecentCaptureCardAction.Delete);

        var layout = new Grid();
        layout.Children.Add(_mainButton);
        layout.Children.Add(_actions);
        Content = new Border
        {
            Padding = new Thickness(9),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            BorderBrush = Application.Current.Resources["CardStrokeColorDefaultBrush"] as Brush,
            Background = Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] as Brush,
            Child = layout,
        };
        _actions.Children.Add(_copyButton);
        _actions.Children.Add(_editButton);
        _actions.Children.Add(_revealButton);
        _actions.Children.Add(_deleteButton);
        Update(entry);
    }

    public void Update(RecentMenuEntry entry)
    {
        var changed = !_entry.Equals(entry) || !_thumbnailRequested;
        _entry = entry;
        var title = entry.Capture is { } capture
            ? Path.GetFileName(capture.Path)
            : entry.StudioDraft!.LibraryName;
        var kind = entry.Capture is { } saved ? TypeLabel(saved.Type) : "Studio project";
        var date = entry.Date.ToLocalTime().ToString("g");
        _title.Text = title;
        _metadata.Text = date;
        _fallback.Glyph = entry.Capture is { } item ? TypeGlyph(item.Type) : StudioGlyph;
        _typeIcon.Glyph = _fallback.Glyph;
        AutomationProperties.SetAutomationId(this, AutomationId(entry));
        AutomationProperties.SetName(this, $"{title}, {kind}, {date}");
        AutomationProperties.SetAutomationId(_mainButton, $"{AutomationId(entry)}Open");
        AutomationProperties.SetName(_mainButton, $"Open {title}, {kind}, {date}");
        AutomationProperties.SetHelpText(_mainButton, $"Opens {title}.");
        AutomationProperties.SetName(_copyButton, $"Copy {title}");
        AutomationProperties.SetName(_editButton, $"Edit {title}");
        AutomationProperties.SetName(_revealButton, $"Show {title} in File Explorer");
        AutomationProperties.SetName(_deleteButton, $"Delete {title}");
        ToolTipService.SetToolTip(_title, $"{title}\n{kind} · {date}");

        _copyButton.Visibility = entry.Capture is null ? Visibility.Collapsed : Visibility.Visible;
        if (changed)
        {
            _thumbnailRequested = false;
            _preview.Source = null;
            _preview.Visibility = Visibility.Collapsed;
            _fallback.Visibility = Visibility.Visible;
            _thumbnailCancellation?.Cancel();
            _thumbnailCancellation?.Dispose();
            _thumbnailCancellation = null;
        }
    }

    public void RequestThumbnail()
    {
        if (_thumbnailRequested)
        {
            return;
        }

        _thumbnailRequested = true;
        var entry = _entry;
        var cancellation = _thumbnailCancellation = new CancellationTokenSource();
        _ = LoadThumbnailAsync(entry, cancellation.Token);
    }

    private async Task LoadThumbnailAsync(RecentMenuEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            string? path;
            if (entry.Capture is { } capture)
            {
                var clip = await Task.Run(() =>
                {
                    var file = new FileInfo(capture.Path);
                    return new ClipEntry(capture.Path, capture.Type, file.Name, file.LastWriteTimeUtc, file.Length);
                }, cancellationToken).ConfigureAwait(false);
                path = await _thumbnails.GetThumbnailPathAsync(clip, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                path = await Task.Run(
                    () => {
                        var poster = _projects.GetPaths(entry.StudioDraft!.Id).PosterPath;
                        return File.Exists(poster) ? poster : null;
                    },
                    cancellationToken);
            }

            if (path is null || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            _dispatcher.TryEnqueue(() =>
            {
                if (cancellationToken.IsCancellationRequested || !_entry.Equals(entry))
                {
                    return;
                }

                _preview.Source = new BitmapImage(new Uri(path))
                {
                    DecodePixelWidth = ThumbnailCacheService.ThumbnailWidth,
                };
                _preview.Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill;
                _preview.Visibility = Visibility.Visible;
                _fallback.Visibility = Visibility.Collapsed;
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Recent capture thumbnail failed for '{entry.Id}': {ex.Message}");
        }
    }

    private Button CreateActionButton(string glyph, string label, RecentCaptureCardAction action)
    {
        var icon = new FontIcon { FontFamily = FluentIconFont, FontSize = 14, Glyph = glyph };
        var button = new Button
        {
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            Width = ActionDiameter,
            Height = ActionDiameter,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(ActionDiameter / 2),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = icon,
        };
        button.RegisterPropertyChangedCallback(Button.IsPointerOverProperty,
            (_, _) => icon.FontSize = button.IsPointerOver ? 17 : 14);
        AutomationProperties.SetAutomationId(button, $"{AutomationId(_entry)}{action}");
        AutomationProperties.SetName(button, label);
        ToolTipService.SetToolTip(button, label);
        var feedbackVersion = 0;
        button.Click += async (_, _) =>
        {
            if (await Invoke(action) && action == RecentCaptureCardAction.Copy)
            {
                var version = ++feedbackVersion;
                icon.Glyph = "\uE73E";
                ToolTipService.SetToolTip(button, "Copied");
                await Task.Delay(TimeSpan.FromSeconds(1.2));
                if (version == feedbackVersion)
                {
                    icon.Glyph = glyph;
                    ToolTipService.SetToolTip(button, label);
                }
            }
        };
        return button;
    }

    public void ShowActions(bool visible)
    {
        _actions.Opacity = visible ? 1 : 0;
        _actions.IsHitTestVisible = visible;
    }

    private async Task<bool> Invoke(RecentCaptureCardAction action)
    {
        try
        {
            await _action(_entry, action);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Recent capture action failed in card: {ex}");
            return false;
        }
    }

    private static string AutomationId(RecentMenuEntry entry) =>
        $"TrayRecent{(entry.Capture is not null ? "Capture" : "Studio")}{entry.Id.Replace(':', '-')}";

    private static string TypeLabel(CaptureType type) => type switch
    {
        CaptureType.Screenshot => "Screenshot",
        CaptureType.Video => "Video",
        CaptureType.Gif => "GIF",
        _ => type.ToString(),
    };

    private static string TypeGlyph(CaptureType type) => type switch
    {
        CaptureType.Screenshot => ScreenshotGlyph,
        CaptureType.Video => VideoGlyph,
        CaptureType.Gif => GifGlyph,
        _ => "\uE8B9",
    };
}
