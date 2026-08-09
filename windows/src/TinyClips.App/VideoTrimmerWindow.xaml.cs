using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TinyClips.Core.Models;
using TinyClips.Core.Services;
using Windows.Graphics.Imaging;
using Windows.Media.Core;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Storage;

namespace TinyClips.App;

/// <summary>
/// Lightweight video trimmer: previews the recorded MP4 and lets the user pick a start/end
/// range with two sliders, then renders a trimmed copy via <see cref="MediaComposition"/>.
/// "Keep original" closes without trimming. The original file is never modified in place.
/// </summary>
public sealed partial class VideoTrimmerWindow : Window
{
    private readonly string _filePath;
    private TimeSpan _duration = TimeSpan.Zero;
    private double _startSeconds;
    private double _endSeconds;
    private double _speed = 1.0;
    private bool _ready;
    private bool _suppressToggle;

    // Step a 1/30s "frame" since the recorded fps isn't exposed by the WinRT clip API.
    private static readonly TimeSpan FrameStep = TimeSpan.FromSeconds(1.0 / 30.0);

    public VideoTrimmerWindow(string filePath)
    {
        _filePath = filePath;

        InitializeComponent();

        // Default to 1x (the "1x" preset item).
        SpeedCombo.SelectedIndex = 4;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindowPlacement.CenterInCurrentWorkAreaAtHalfSize(AppWindow);

        var settings = App.Services.GetRequiredService<ICaptureSettings>();
        RootGrid.RequestedTheme = settings.Theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        Closed += OnWindowClosed;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(_filePath);
            var clip = await MediaClip.CreateFromFileAsync(file);
            _duration = clip.OriginalDuration;
            _startSeconds = 0;
            _endSeconds = _duration.TotalSeconds;

            var player = new MediaPlayer { Source = MediaSource.CreateFromStorageFile(file) };
            player.PlaybackRate = _speed;
            player.PlaybackSession.PositionChanged += OnPositionChanged;
            player.PlaybackSession.PlaybackStateChanged += OnPlaybackStateChanged;
            Player.SetMediaPlayer(player);

            _ready = true;
            TrimBar.StartFraction = 0;
            TrimBar.EndFraction = 1;
            TrimBar.PlayheadFraction = 0;
            TrimBar.KeyboardStep = Math.Min(1, FrameStep.TotalSeconds / _duration.TotalSeconds);
            UpdateLabels();
            UpdatePositionLabel(TimeSpan.Zero);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Video trimmer load failed: {ex}");
        }
    }

    // -- Range handling -------------------------------------------------------

    private void OnTrimStartChanged(object sender, double fraction)
    {
        if (!_ready || _duration <= TimeSpan.Zero)
        {
            return;
        }

        var dur = _duration.TotalSeconds;
        var start = Math.Clamp(fraction * dur, 0, _endSeconds - 0.1);
        _startSeconds = start;
        TrimBar.StartFraction = start / dur;
        TrimBar.PlayheadFraction = start / dur;
        SeekTo(start);
        UpdateLabels();
    }

    private void OnTrimEndChanged(object sender, double fraction)
    {
        if (!_ready || _duration <= TimeSpan.Zero)
        {
            return;
        }

        var dur = _duration.TotalSeconds;
        var end = Math.Clamp(fraction * dur, _startSeconds + 0.1, dur);
        _endSeconds = end;
        TrimBar.EndFraction = end / dur;
        TrimBar.PlayheadFraction = end / dur;
        SeekTo(end);
        UpdateLabels();
    }

    private void OnTrimRangeChanged(object sender, (double Start, double End) range)
    {
        if (!_ready || _duration <= TimeSpan.Zero)
        {
            return;
        }

        var dur = _duration.TotalSeconds;
        _startSeconds = Math.Clamp(range.Start * dur, 0, dur);
        _endSeconds = Math.Clamp(range.End * dur, _startSeconds + 0.1, dur);
        TrimBar.StartFraction = _startSeconds / dur;
        TrimBar.EndFraction = _endSeconds / dur;
        TrimBar.PlayheadFraction = _startSeconds / dur;
        SeekTo(_startSeconds);
        UpdateLabels();
    }

    private void OnTrimSeek(object sender, double fraction)
    {
        if (!_ready || _duration <= TimeSpan.Zero)
        {
            return;
        }

        var seconds = fraction * _duration.TotalSeconds;
        TrimBar.PlayheadFraction = fraction;
        SeekTo(seconds);
        UpdatePositionLabel(TimeSpan.FromSeconds(seconds));
    }

    private void SeekTo(double seconds)
    {
        var player = Player.MediaPlayer;
        if (player?.PlaybackSession is { } session && _duration > TimeSpan.Zero)
        {
            session.Position = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, _duration.TotalSeconds));
        }
    }

    // -- Frame stepper --------------------------------------------------------

    private void OnPrevFrame(object sender, RoutedEventArgs e) => StepFrame(-1);

    private void OnNextFrame(object sender, RoutedEventArgs e) => StepFrame(1);

    private void StepFrame(int direction)
    {
        if (!_ready || _duration <= TimeSpan.Zero)
        {
            return;
        }

        var player = Player.MediaPlayer;
        if (player?.PlaybackSession is not { } session)
        {
            return;
        }

        player.Pause();
        var target = session.Position + TimeSpan.FromTicks(FrameStep.Ticks * direction);
        if (target < TimeSpan.Zero)
        {
            target = TimeSpan.Zero;
        }
        else if (target > _duration)
        {
            target = _duration;
        }

        session.Position = target;
        UpdatePositionLabel(target);
    }

    private void OnPositionChanged(MediaPlaybackSession sender, object args)
    {
        var position = sender.Position;

        // Stop playback at the trim end so previewing stays within the selected range.
        if (sender.PlaybackState == MediaPlaybackState.Playing &&
            _ready && _endSeconds > 0 && position.TotalSeconds >= _endSeconds)
        {
            sender.Position = TimeSpan.FromSeconds(_endSeconds);
            DispatcherQueue.TryEnqueue(() =>
            {
                Player.MediaPlayer?.Pause();
                UpdatePositionLabel(TimeSpan.FromSeconds(_endSeconds));
            });
            return;
        }

        DispatcherQueue.TryEnqueue(() => UpdatePositionLabel(position));
    }

    // -- Play / pause ---------------------------------------------------------

    private void OnPlayToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle || !_ready)
        {
            return;
        }

        var player = Player.MediaPlayer;
        if (player is null)
        {
            return;
        }

        if (PlayToggle.IsChecked == true)
        {
            // Restart from the trim start if the playhead is at (or past) the trim end.
            if (player.PlaybackSession is { } session &&
                session.Position.TotalSeconds >= _endSeconds - 0.05)
            {
                session.Position = TimeSpan.FromSeconds(_startSeconds);
            }

            player.Play();
        }
        else
        {
            player.Pause();
        }
    }

    private void OnPlaybackStateChanged(MediaPlaybackSession sender, object args)
    {
        var playing = sender.PlaybackState == MediaPlaybackState.Playing;
        DispatcherQueue.TryEnqueue(() => SyncPlayToggle(playing));
    }

    private void SyncPlayToggle(bool playing)
    {
        _suppressToggle = true;
        PlayToggle.IsChecked = playing;
        PlayIcon.Glyph = playing ? "\uE769" : "\uE768"; // Pause : Play
        var name = playing ? "Pause preview" : "Play preview";
        PlayToggle.SetValue(AutomationProperties.NameProperty, name);
        ToolTipService.SetToolTip(PlayToggle, name);
        _suppressToggle = false;
    }

    private void UpdatePositionLabel(TimeSpan position)
    {
        PositionLabel.Text = $"Position: {Format(position.TotalSeconds)}";
        if (_ready && _duration > TimeSpan.Zero)
        {
            TrimBar.PlayheadFraction = position.TotalSeconds / _duration.TotalSeconds;
        }
    }

    private void UpdateLabels()
    {
        StartLabel.Text = Format(_startSeconds);
        EndLabel.Text = Format(_endSeconds);
        DurationLabel.Text = $"Duration: {Format(_endSeconds - _startSeconds)}";
    }

    private static string Format(double seconds)
    {
        seconds = Math.Max(0, seconds);
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}.{ts.Milliseconds / 100}";
    }

    // -- Speed ----------------------------------------------------------------

    private void OnSpeedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SpeedCombo.SelectedItem is ComboBoxItem { Tag: string tag } &&
            double.TryParse(tag, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rate))
        {
            _speed = rate;
        }

        // Preview speed: MediaPlayer.PlaybackRate fully supports speeding up / slowing down
        // the on-screen preview in real time.
        if (Player.MediaPlayer is { } player)
        {
            player.PlaybackRate = _speed;
        }
    }

    // -- Output ---------------------------------------------------------------

    private async void OnExportFrame(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        var session = Player.MediaPlayer?.PlaybackSession;
        if (session is null)
        {
            return;
        }

        var position = session.Position;
        Player.MediaPlayer?.Pause();

        BusyBar.Visibility = Visibility.Visible;
        ExportFrameButton.IsEnabled = false;
        string? outputPath = null;

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(_filePath);
            var clip = await MediaClip.CreateFromFileAsync(file);
            var composition = new MediaComposition();
            composition.Clips.Add(clip);

            // 0,0 => original frame resolution. NearestFrame keeps the still aligned to the
            // frame the user is paused on.
            var thumb = await composition.GetThumbnailAsync(position, 0, 0, VideoFramePrecision.NearestFrame);
            var decoder = await BitmapDecoder.CreateAsync(thumb);
            var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

            var storage = App.Services.GetRequiredService<IClipStorageService>();
            outputPath = storage.GenerateFilePath(CaptureType.Screenshot, ".png", "(frame)");
            var folder = await StorageFolder.GetFolderFromPathAsync(System.IO.Path.GetDirectoryName(outputPath)!);
            var outFile = await folder.CreateFileAsync(
                System.IO.Path.GetFileName(outputPath), CreationCollisionOption.GenerateUniqueName);

            using (var outStream = await outFile.OpenAsync(FileAccessMode.ReadWrite))
            {
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outStream);
                encoder.SetSoftwareBitmap(bitmap);
                await encoder.FlushAsync();
            }

            App.ShowSaveNotification(outFile.Path);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Video frame export failed: {ex}");
            App.ShowSaveFailureNotification(System.IO.Path.GetFileName(outputPath ?? "video frame export"));
        }
        finally
        {
            BusyBar.Visibility = Visibility.Collapsed;
            ExportFrameButton.IsEnabled = true;
        }
    }

    private async void OnSaveTrimmed(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        var outputPath = await ExportVideoAsync(trimmed: true);
        Completed?.Invoke(this, outputPath);
        Close();
    }

    private async void OnSaveOriginal(object sender, RoutedEventArgs e)
    {
        if (RemoveAudioCheck.IsChecked == true)
        {
            var outputPath = await ExportVideoAsync(trimmed: false);
            Completed?.Invoke(this, outputPath);
        }
        else
        {
            Completed?.Invoke(this, null);
        }

        Close();
    }

    private async Task<string?> ExportVideoAsync(bool trimmed)
    {
        BusyBar.Visibility = Visibility.Visible;
        SaveOriginalButton.IsEnabled = false;
        SaveTrimmedButton.IsEnabled = false;
        RemoveAudioCheck.IsEnabled = false;
        string? outputPath = null;

        try
        {
            Player.MediaPlayer?.Pause();

            var file = await StorageFile.GetFileFromPathAsync(_filePath);
            var clip = await MediaClip.CreateFromFileAsync(file);
            if (trimmed)
            {
                clip.TrimTimeFromStart = TimeSpan.FromSeconds(_startSeconds);
                clip.TrimTimeFromEnd = TimeSpan.FromSeconds(Math.Max(0, _duration.TotalSeconds - _endSeconds));
            }

            var composition = new MediaComposition();
            composition.Clips.Add(clip);

            // NOTE: Speed is applied to the PREVIEW only (MediaPlayer.PlaybackRate). The
            // Windows.Media.Editing MediaComposition/MediaClip API exposes no playback-rate or
            // re-timing knob, and RenderToFileAsync ignores MediaPlayer.PlaybackRate, so the
            // rendered MP4 keeps its original timing. Re-timing the output would require a
            // frame-level pipeline (e.g. FFmpeg) that is outside this WinRT-only trimmer.
            var storage = App.Services.GetRequiredService<IClipStorageService>();
            var removeAudio = RemoveAudioCheck.IsChecked == true;
            var suffix = trimmed
                ? (removeAudio ? " (trimmed, no audio)" : " (trimmed)")
                : " (no audio)";
            outputPath = storage.GenerateFilePath(CaptureType.Video, ".mp4", suffix);
            var folder = await StorageFolder.GetFolderFromPathAsync(System.IO.Path.GetDirectoryName(outputPath)!);
            var outFile = await folder.CreateFileAsync(
                System.IO.Path.GetFileName(outputPath), CreationCollisionOption.GenerateUniqueName);

            if (removeAudio)
            {
                var profile = composition.CreateDefaultEncodingProfile();
                profile.Audio = null;
                await composition.RenderToFileAsync(outFile, MediaTrimmingPreference.Precise, profile);
            }
            else
            {
                await composition.RenderToFileAsync(outFile, MediaTrimmingPreference.Precise);
            }

            outputPath = outFile.Path;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Video trim failed: {ex}");
            App.ShowSaveFailureNotification(System.IO.Path.GetFileName(outputPath ?? _filePath));
            outputPath = null;
        }
        finally
        {
            BusyBar.Visibility = Visibility.Collapsed;
            SaveOriginalButton.IsEnabled = true;
            SaveTrimmedButton.IsEnabled = true;
            RemoveAudioCheck.IsEnabled = true;
        }

        return outputPath;
    }

    private void OnDone(object sender, RoutedEventArgs e)
    {
        Completed?.Invoke(this, null);
        Close();
    }

    private void OnWindowClosed(object sender, WindowEventArgs e)
    {
        var player = Player.MediaPlayer;
        if (player?.PlaybackSession is { } session)
        {
            session.PositionChanged -= OnPositionChanged;
        }

        Player.SetMediaPlayer(null);
        player?.Dispose();
    }

    /// <summary>Raised once when the window closes. Carries the trimmed file path, or null if untrimmed.</summary>
    public event EventHandler<string?>? Completed;
}
