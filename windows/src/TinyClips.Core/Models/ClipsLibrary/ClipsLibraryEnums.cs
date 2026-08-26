namespace TinyClips.Core.Models.ClipsLibrary;

public enum ClipsViewMode
{
    Grid,
    List,
}

public enum ClipSortOption
{
    NewestFirst,
    OldestFirst,
    Largest,
    Name,
    FavoritesFirst,
}

public enum ClipTypeFilter
{
    All,
    Screenshots,
    Videos,
    Gifs,
    Favorites,
}

public enum ClipDateFilter
{
    Any,
    Today,
    Last7Days,
    Last30Days,
}

public enum SmartCollection
{
    AllClips,
    Recent,
    ThisWeek,
    ThisMonth,
    LargeFiles,
    Favorites,
    Screenshots,
    Videos,
    Gifs,
}
