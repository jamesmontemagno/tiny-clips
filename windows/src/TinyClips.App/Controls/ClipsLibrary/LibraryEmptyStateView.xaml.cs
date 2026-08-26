using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TinyClips.App.ViewModels.ClipsLibrary;

namespace TinyClips.App.Controls.ClipsLibrary;

/// <summary>
/// Renders loading / no clips / no match / folder missing states with a contextual call to action.
/// </summary>
public sealed partial class LibraryEmptyStateView : UserControl, INotifyPropertyChanged
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(LibraryEmptyState), typeof(LibraryEmptyStateView), new PropertyMetadata(LibraryEmptyState.Loading, OnStateChanged));

    public static readonly DependencyProperty LibraryProperty = DependencyProperty.Register(
        nameof(Library), typeof(ClipsLibraryViewModel), typeof(LibraryEmptyStateView), new PropertyMetadata(null, OnStateChanged));

    public LibraryEmptyStateView()
    {
        InitializeComponent();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public LibraryEmptyState State
    {
        get => (LibraryEmptyState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public ClipsLibraryViewModel? Library
    {
        get => (ClipsLibraryViewModel?)GetValue(LibraryProperty);
        set => SetValue(LibraryProperty, value);
    }

    public bool IsLoading => State == LibraryEmptyState.Loading;

    public string Glyph => State switch
    {
        LibraryEmptyState.NoMatch       => "\uE721",
        LibraryEmptyState.FolderMissing => "\uE8B7",
        _                               => "\uEB9F",
    };

    public string Title => State switch
    {
        LibraryEmptyState.Loading       => "Scanning your clips…",
        LibraryEmptyState.NoMatch       => "No clips match your filters",
        LibraryEmptyState.FolderMissing => "Your clips folder is missing",
        _                               => "No clips yet",
    };

    public string Message => State switch
    {
        LibraryEmptyState.Loading       => "This only takes a moment.",
        LibraryEmptyState.NoMatch       => "Try a different search, or clear the filters to see everything.",
        LibraryEmptyState.FolderMissing => "Pick a save location in Settings and new captures will show up here.",
        _                               => "Screenshots, recordings and GIFs you capture with Tiny Clips will appear here.",
    };

    public string ActionText => State switch
    {
        LibraryEmptyState.NoMatch       => "Clear filters",
        LibraryEmptyState.FolderMissing => "Open Settings",
        LibraryEmptyState.NoClips       => "Open save folder",
        _                               => string.Empty,
    };

    public ICommand? ActionCommand => State switch
    {
        LibraryEmptyState.NoMatch       => Library?.ClearFiltersCommand,
        LibraryEmptyState.FolderMissing => Library?.OpenSettingsCommand,
        LibraryEmptyState.NoClips       => Library?.OpenSaveFolderCommand,
        _                               => null,
    };

    public bool HasAction => ActionCommand is not null;

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (LibraryEmptyStateView)d;
        view.Raise(nameof(IsLoading));
        view.Raise(nameof(Glyph));
        view.Raise(nameof(Title));
        view.Raise(nameof(Message));
        view.Raise(nameof(ActionText));
        view.Raise(nameof(ActionCommand));
        view.Raise(nameof(HasAction));
    }

    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
