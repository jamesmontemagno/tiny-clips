using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
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
    private const double CardWidth = 176;
    private readonly StackPanel _cards;
    private readonly ScrollViewer _scroller;
    private readonly TrayRecentItems<RecentCaptureCard> _retained = new();
    private readonly IThumbnailCache _thumbnails;
    private readonly IStudioProjectStore _projects;
    private readonly DispatcherQueue _dispatcher;
    private readonly Func<RecentMenuEntry, RecentCaptureCardAction, Task> _action;

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

        _cards = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
        };
        _scroller = new ScrollViewer
        {
            Content = _cards,
            HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 0, 6),
        };
        _scroller.ViewChanging += (_, args) =>
            RequestThumbnailsNear(args.FinalView.HorizontalOffset);

        Content = new Grid
        {
            Width = 566,
            MinHeight = 190,
            Padding = new Thickness(14),
            Children = { _scroller },
        };
        Opening += (_, _) => RequestThumbnailsNear(_scroller.HorizontalOffset);
    }

    public int Count => _retained.Items.Count;

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
        var start = Math.Max(0, (int)Math.Floor(horizontalOffset / (CardWidth + 10)) - 1);
        var count = Math.Min(_retained.Items.Count - start, 5);
        for (var index = 0; index < count; index++)
        {
            _retained.Items[start + index].RequestThumbnail();
        }
    }
}

internal sealed class RecentCaptureCard : Border
{
    private const string ScreenshotGlyph = "\uE722";
    private const string VideoGlyph = "\uE714";
    private const string GifGlyph = "\uF4A9";
    private const string StudioGlyph = "\uE81E";
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
        Spacing = 4,
        Opacity = 0,
        IsHitTestVisible = false,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(0, 31, 0, 0),
    };
    private RecentMenuEntry _entry;
    private CancellationTokenSource? _thumbnailCancellation;
    private bool _thumbnailRequested;
    private bool _isPointerOver;
    private bool _hasFocusWithin;

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

        Width = 176;
        Padding = new Thickness(9);
        CornerRadius = new CornerRadius(10);
        BorderThickness = new Thickness(1);
        BorderBrush = Application.Current.Resources["CardStrokeColorDefaultBrush"] as Brush;
        Background = Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] as Brush;

        _mainButton.Padding = new Thickness(0);
        _mainButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        _mainButton.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _mainButton.BorderThickness = new Thickness(0);
        _mainButton.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _mainButton.Click += (_, _) => _ = Invoke(RecentCaptureCardAction.Edit);
        var thumbnail = new Grid
        {
            Width = 156,
            Height = 88,
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
        _deleteButton.Foreground = new SolidColorBrush(Colors.Red);

        var layout = new Grid();
        layout.Children.Add(_mainButton);
        layout.Children.Add(_actions);
        Child = layout;
        _actions.Children.Add(_copyButton);
        _actions.Children.Add(_editButton);
        _actions.Children.Add(_revealButton);
        _actions.Children.Add(_deleteButton);
        PointerEntered += (_, _) => SetPointerOver(true);
        PointerExited += (_, _) => SetPointerOver(false);
        _mainButton.GotFocus += (_, _) => SetFocusWithin(true);
        _mainButton.LostFocus += (_, _) => SetFocusWithin(false);
        foreach (var action in _actions.Children.OfType<Button>())
        {
            action.GotFocus += (_, _) => SetFocusWithin(true);
            action.LostFocus += (_, _) => SetFocusWithin(false);
        }

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
                var size = await Task.Run(() => new FileInfo(capture.Path).Length, cancellationToken);
                path = await _thumbnails.GetThumbnailPathAsync(
                    new ClipEntry(capture.Path, capture.Type, Path.GetFileName(capture.Path), capture.CapturedAt, size),
                    cancellationToken);
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
        var icon = new FontIcon { Glyph = glyph, FontFamily = FluentIconFont, FontSize = 13 };
        var button = new Button
        {
            Content = icon,
            Width = 30,
            Height = 26,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(ColorHelper.FromArgb(232, 32, 32, 32)),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(14),
        };
        AutomationProperties.SetAutomationId(button, $"{AutomationId(_entry)}{action}");
        AutomationProperties.SetName(button, label);
        ToolTipService.SetToolTip(button, label);
        button.Click += async (_, _) =>
        {
            if (await Invoke(action) && action == RecentCaptureCardAction.Copy)
            {
                icon.Glyph = "\uE73E";
                ToolTipService.SetToolTip(button, "Copied");
                _ = Task.Delay(TimeSpan.FromSeconds(1.2)).ContinueWith(
                    _ => _dispatcher.TryEnqueue(() => icon.Glyph = glyph),
                    TaskScheduler.Default);
            }
        };
        return button;
    }

    private void SetPointerOver(bool value)
    {
        _isPointerOver = value;
        UpdateActionVisibility();
    }

    private void SetFocusWithin(bool value)
    {
        _hasFocusWithin = value;
        if (value)
        {
            UpdateActionVisibility();
        }
        else
        {
            _dispatcher.TryEnqueue(UpdateActionVisibility);
        }
    }

    private void UpdateActionVisibility()
    {
        var visible = _isPointerOver || _hasFocusWithin;
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
