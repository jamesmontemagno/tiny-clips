using CommunityToolkit.Mvvm.ComponentModel;
using TinyClips.Core.Models.ClipsLibrary;

namespace TinyClips.App.ViewModels.ClipsLibrary;

/// <summary>Sidebar entry for a smart collection, user collection, or tag.</summary>
public sealed partial class NavigationEntryViewModel : ObservableObject
{
    public NavigationEntryViewModel(NavigationEntryKind kind, string title, string glyph, SmartCollection? smartCollection = null, string? value = null)
    {
        Kind = kind;
        Title = title;
        Glyph = glyph;
        SmartCollection = smartCollection;
        Value = value;
        AutomationId = kind switch
        {
            NavigationEntryKind.SmartCollection => $"Nav-Smart-{smartCollection}",
            NavigationEntryKind.Collection      => $"Nav-Collection-{Sanitize(value)}",
            _                                   => $"Nav-Tag-{Sanitize(value)}",
        };
    }

    public NavigationEntryKind Kind { get; }

    public string Title { get; }

    public string Glyph { get; }

    public SmartCollection? SmartCollection { get; }

    public string? Value { get; }

    public string AutomationId { get; }

    [ObservableProperty]
    private int _count;

    public string AutomationName => $"{Title}, {Count} clips";

    partial void OnCountChanged(int value) => OnPropertyChanged(nameof(AutomationName));

    public static string TitleFor(SmartCollection collection) => collection switch
    {
        Core.Models.ClipsLibrary.SmartCollection.AllClips    => "All Clips",
        Core.Models.ClipsLibrary.SmartCollection.Recent      => "Recent",
        Core.Models.ClipsLibrary.SmartCollection.ThisWeek    => "This Week",
        Core.Models.ClipsLibrary.SmartCollection.ThisMonth   => "This Month",
        Core.Models.ClipsLibrary.SmartCollection.LargeFiles  => "Large Files",
        Core.Models.ClipsLibrary.SmartCollection.Favorites   => "Favorites",
        Core.Models.ClipsLibrary.SmartCollection.Screenshots => "Screenshots",
        Core.Models.ClipsLibrary.SmartCollection.Videos      => "Videos",
        Core.Models.ClipsLibrary.SmartCollection.Gifs        => "GIFs",
        _                                                    => collection.ToString(),
    };

    public static string GlyphFor(SmartCollection collection) => collection switch
    {
        Core.Models.ClipsLibrary.SmartCollection.AllClips    => "\uE8B7",
        Core.Models.ClipsLibrary.SmartCollection.Recent      => "\uE823",
        Core.Models.ClipsLibrary.SmartCollection.ThisWeek    => "\uE787",
        Core.Models.ClipsLibrary.SmartCollection.ThisMonth   => "\uE8BF",
        Core.Models.ClipsLibrary.SmartCollection.LargeFiles  => "\uE8A9",
        Core.Models.ClipsLibrary.SmartCollection.Favorites   => "\uE735",
        Core.Models.ClipsLibrary.SmartCollection.Screenshots => "\uE722",
        Core.Models.ClipsLibrary.SmartCollection.Videos      => "\uE714",
        Core.Models.ClipsLibrary.SmartCollection.Gifs        => "\uE8B9",
        _                                                    => "\uE8B7",
    };

    private static string Sanitize(string? value) =>
        new((value ?? string.Empty).Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
}

public enum NavigationEntryKind
{
    SmartCollection,
    Collection,
    Tag,
}
