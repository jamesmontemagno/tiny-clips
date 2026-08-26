using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using TinyClips.App.ViewModels.ClipsLibrary;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace TinyClips.App.Controls.ClipsLibrary;

/// <summary>
/// Right-hand detail pane: media preview plus metadata editor. Owns the
/// <see cref="MediaPlayer"/> so playback stops and releases the file whenever the selection
/// changes or the pane is torn down.
/// </summary>
public sealed partial class ClipDetailPane : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(ClipDetailViewModel), typeof(ClipDetailPane), new PropertyMetadata(null, OnViewModelChanged));

    public static readonly DependencyProperty LibraryProperty = DependencyProperty.Register(
        nameof(Library), typeof(ClipsLibraryViewModel), typeof(ClipDetailPane), new PropertyMetadata(null));

    private MediaPlayer? _player;

    public ClipDetailPane()
    {
        InitializeComponent();
        Unloaded += (_, _) => ReleaseMedia();
    }

    public ClipDetailViewModel? ViewModel
    {
        get => (ClipDetailViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public ClipsLibraryViewModel? Library
    {
        get => (ClipsLibraryViewModel?)GetValue(LibraryProperty);
        set => SetValue(LibraryProperty, value);
    }

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var pane = (ClipDetailPane)d;
        if (e.OldValue is ClipDetailViewModel old)
        {
            old.PropertyChanged -= pane.OnViewModelPropertyChanged;
        }

        if (e.NewValue is ClipDetailViewModel vm)
        {
            vm.PropertyChanged += pane.OnViewModelPropertyChanged;
            pane.UpdateMedia();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ClipDetailViewModel.Clip) or nameof(ClipDetailViewModel.MediaUri))
        {
            UpdateMedia();
        }
    }

    /// <summary>Swaps the preview source; called on selection change and after edits re-save the file.</summary>
    public void UpdateMedia()
    {
        var clip = ViewModel?.Clip;
        if (clip is null)
        {
            ReleaseMedia();
            Preview.Source = null;
            return;
        }

        if (clip.IsVideo)
        {
            Preview.Source = null;
            _player ??= new MediaPlayer { IsLoopingEnabled = false, AutoPlay = false };
            _player.Source = MediaSource.CreateFromUri(new Uri(clip.Path));
            Player.SetMediaPlayer(_player);
        }
        else
        {
            ReleaseMedia();
            // GIFs animate automatically through BitmapImage.
            Preview.Source = new BitmapImage(new Uri(clip.Path));
        }
    }

    public void TogglePlayback()
    {
        if (_player is null)
        {
            return;
        }

        if (_player.CurrentState == MediaPlayerState.Playing)
        {
            _player.Pause();
        }
        else
        {
            _player.Play();
        }
    }

    public void ReleaseMedia()
    {
        if (_player is null)
        {
            return;
        }

        try
        {
            _player.Pause();
            (_player.Source as IDisposable)?.Dispose();
            _player.Source = null;
            Player.SetMediaPlayer(null);
            _player.Dispose();
        }
        catch
        {
        }
        finally
        {
            _player = null;
        }
    }

    private void OnFavoriteClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.Clip is { } clip && Library is not null)
        {
            Library.ToggleFavoriteCommand.Execute(clip);
        }
    }

    private void OnMoreClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.Clip is { } clip)
        {
            ClipContextMenu.Build(clip, "Detail").ShowAt(MoreButton);
        }
    }

    private void OnRemoveTagClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag })
        {
            ViewModel?.RemoveTagCommand.Execute(tag);
        }
    }

    private void OnTagQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (ViewModel is null)
        {
            return;
        }

        ViewModel.NewTagText = args.ChosenSuggestion as string ?? args.QueryText;
        ViewModel.AddTagCommand.Execute(null);
        sender.Text = string.Empty;
    }

    private void OnTagTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput || Library is null)
        {
            return;
        }

        var text = sender.Text.Trim();
        sender.ItemsSource = string.IsNullOrEmpty(text)
            ? Library.Tags.ToList()
            : Library.Tags.Where(tag => tag.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
    }
}
