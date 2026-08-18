using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using TinyClips.Core.Models;
using TinyClips.Core.Services;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace TinyClips.App;

/// <summary>
/// Lightweight GIF trimmer: decodes every frame (with its delay), lets the user pick a
/// start/end frame range with two sliders, previews the boundary frames, then re-encodes the
/// kept frames to a new animated GIF. "Keep original" closes without trimming.
/// </summary>
public sealed partial class GifTrimmerWindow : Window
{
    // Minimum dimensions chosen to keep the trim bar, frame stepper, and footer legible.
    // Width 560 DIP: trim bar needs at least ~360px; footer has PlayToggle + SpeedCombo (96) +
    //   three action buttons (~300px) + spacing/padding, totalling ~480 + 80 margins.
    // Height 480 DIP: TitleBar (~48) + preview floor (~160) + trim section (~160) + footer (~92)
    //   + margins (~20). GIF frames are often smaller than video so the floor is set lower.
    private const int MinimumWidthDip  = 560;
    private const int MinimumHeightDip = 480;

    private readonly string _filePath;
    private readonly List<SoftwareBitmap> _frames = new();
    private readonly List<ushort> _delays = new();
    private int _start;
    private int _end;
    private int _current;
    private double _speed = 1.0;
    private bool _ready;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _playTimer;
    private int _playIndex;

    private readonly WindowChromeController _chromeController;

    public GifTrimmerWindow(string filePath)
    {
        _filePath = filePath;

        InitializeComponent();

        // Default to 1x (the "1x" preset item).
        SpeedCombo.SelectedIndex = 4;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindowPlacement.CenterInCurrentWorkAreaAtHalfSize(AppWindow);

        // WindowChromeController owns: icon-on-activation, DIP minimum enforcement, XamlRoot
        // scale tracking, and cleanup of all three on Closed. The Closed subscription is
        // additive; both the controller's cleanup and the existing OnWindowClosed handler run.
        _chromeController = new WindowChromeController(this, RootGrid, MinimumWidthDip, MinimumHeightDip);

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
            using var stream = await file.OpenAsync(FileAccessMode.Read);
            var decoder = await BitmapDecoder.CreateAsync(BitmapDecoder.GifDecoderId, stream);

            for (uint i = 0; i < decoder.FrameCount; i++)
            {
                var frame = await decoder.GetFrameAsync(i);
                var bitmap = await frame.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                _frames.Add(bitmap);
                _delays.Add(await ReadDelayAsync(frame));
            }

            if (_frames.Count == 0)
            {
                return;
            }

            _start = 0;
            _end = _frames.Count - 1;
            _current = 0;

            _ready = true;
            TrimBar.StartFraction = 0;
            TrimBar.EndFraction = 1;
            TrimBar.KeyboardStep = LastFrame == 0 ? 1 : 1.0 / LastFrame;
            UpdateLabels();
            SetCurrent(0);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"GIF trimmer load failed: {ex}");
        }
    }

    private static async Task<ushort> ReadDelayAsync(BitmapFrame frame)
    {
        try
        {
            var props = await frame.BitmapProperties.GetPropertiesAsync(new[] { "/grctlext/Delay" });
            if (props.TryGetValue("/grctlext/Delay", out var value) && value.Value is ushort delay)
            {
                return Math.Max((ushort)2, delay);
            }
        }
        catch
        {
            // Some frames may not carry the extension; fall back to a sensible default.
        }

        return 10;
    }

    // -- Range handling -------------------------------------------------------

    private int LastFrame => Math.Max(0, _frames.Count - 1);

    private double FractionFromFrame(int index) => LastFrame <= 0 ? 0 : (double)index / LastFrame;

    private int FrameFromFraction(double fraction) => (int)Math.Round(fraction * LastFrame);

    private void OnTrimStartChanged(object sender, double fraction)
    {
        if (!_ready)
        {
            return;
        }

        _start = Math.Min(FrameFromFraction(fraction), _end);
        TrimBar.StartFraction = FractionFromFrame(_start);
        StopPlayback();
        SetCurrent(_start);
        UpdateLabels();
    }

    private void OnTrimEndChanged(object sender, double fraction)
    {
        if (!_ready)
        {
            return;
        }

        _end = Math.Max(FrameFromFraction(fraction), _start);
        TrimBar.EndFraction = FractionFromFrame(_end);
        StopPlayback();
        SetCurrent(_end);
        UpdateLabels();
    }

    private void OnTrimRangeChanged(object sender, (double Start, double End) range)
    {
        if (!_ready)
        {
            return;
        }

        var newStart = FrameFromFraction(range.Start);
        var width = _end - _start;
        newStart = Math.Clamp(newStart, 0, Math.Max(0, LastFrame - width));
        _start = newStart;
        _end = newStart + width;
        TrimBar.StartFraction = FractionFromFrame(_start);
        TrimBar.EndFraction = FractionFromFrame(_end);
        StopPlayback();
        SetCurrent(_start);
        UpdateLabels();
    }

    private void OnTrimSeek(object sender, double fraction)
    {
        if (!_ready)
        {
            return;
        }

        StopPlayback();
        SetCurrent(FrameFromFraction(fraction));
    }

    // -- Current frame stepper ------------------------------------------------

    private void OnPrevFrame(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        SetCurrent(_current - 1);
    }

    private void OnNextFrame(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        SetCurrent(_current + 1);
    }

    private void SetCurrent(int index)
    {
        if (!_ready || _frames.Count == 0)
        {
            return;
        }

        _current = Math.Clamp(index, 0, _frames.Count - 1);
        TrimBar.PlayheadFraction = FractionFromFrame(_current);

        CurrentLabel.Text = $"Frame {_current + 1} / {_frames.Count}";
        if (PrevFrameButton is not null)
        {
            PrevFrameButton.IsEnabled = _current > 0;
            NextFrameButton.IsEnabled = _current < _frames.Count - 1;
        }

        _ = ShowFrameAsync(_current);
    }

    private async Task ShowFrameAsync(int index)
    {
        if (index < 0 || index >= _frames.Count)
        {
            return;
        }

        var source = new SoftwareBitmapSource();
        await source.SetBitmapAsync(_frames[index]);
        PreviewImage.Source = source;
    }

    private void UpdateLabels()
    {
        StartLabel.Text = (_start + 1).ToString();
        EndLabel.Text = (_end + 1).ToString();
        CountLabel.Text = $"{_end - _start + 1} of {_frames.Count} frames";
    }

    // -- Speed ----------------------------------------------------------------

    private void OnSpeedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SpeedCombo.SelectedItem is ComboBoxItem { Tag: string tag } &&
            double.TryParse(tag, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rate) &&
            rate > 0)
        {
            _speed = rate;
        }
    }

    /// <summary>
    /// Scales a per-frame delay (GIF delays are in centiseconds, i.e. 1/100s) by the inverse of
    /// the chosen speed so a higher speed shortens each delay. Clamped to a 20ms minimum
    /// (2 centiseconds) to stay within what real GIF players honor.
    /// </summary>
    private ushort ScaleDelay(ushort delayCentis)
    {
        var scaled = (int)Math.Round(delayCentis / _speed);
        return (ushort)Math.Clamp(scaled, 2, ushort.MaxValue);
    }

    // -- Preview playback -----------------------------------------------------

    private void OnPlayToggled(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            PlayToggle.IsChecked = false;
            return;
        }

        if (PlayToggle.IsChecked == true)
        {
            StartPlayback();
        }
        else
        {
            StopPlayback();
        }
    }

    private void StartPlayback()
    {
        _playIndex = _start;
        _playTimer ??= DispatcherQueue.CreateTimer();
        _playTimer.Tick -= OnPlayTick;
        _playTimer.Tick += OnPlayTick;
        UpdatePlayToggleVisual(true);
        ScheduleNextPlayFrame();
    }

    private void ScheduleNextPlayFrame()
    {
        if (_playTimer is null)
        {
            return;
        }

        // GIF delays are in centiseconds; scale by speed and clamp to a sane minimum.
        var centis = _playIndex >= 0 && _playIndex < _delays.Count ? _delays[_playIndex] : (ushort)10;
        var ms = Math.Clamp((int)Math.Round(centis * 10 / _speed), 20, 5000);
        _playTimer.Interval = TimeSpan.FromMilliseconds(ms);
        _playTimer.Start();
    }

    private async void OnPlayTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (PlayToggle.IsChecked != true)
        {
            return;
        }

        await ShowFrameAsync(_playIndex);
        TrimBar.PlayheadFraction = FractionFromFrame(_playIndex);
        _playIndex++;
        if (_playIndex > _end)
        {
            _playIndex = _start;
        }

        ScheduleNextPlayFrame();
    }

    private void StopPlayback()
    {
        _playTimer?.Stop();
        PlayToggle.IsChecked = false;
        UpdatePlayToggleVisual(false);
        _ = ShowFrameAsync(_current);
    }

    private void UpdatePlayToggleVisual(bool playing)
    {
        PlayIcon.Glyph = playing ? "\uE769" : "\uE768"; // Pause : Play
        var name = playing ? "Pause preview" : "Play preview";
        PlayToggle.SetValue(AutomationProperties.NameProperty, name);
        ToolTipService.SetToolTip(PlayToggle, name);
    }

    // -- Output ---------------------------------------------------------------

    private async void OnExportFrame(object sender, RoutedEventArgs e)
    {
        if (!_ready || _current < 0 || _current >= _frames.Count)
        {
            return;
        }

        StopPlayback();
        ExportFrameButton.IsEnabled = false;
        string? outputPath = null;

        try
        {
            var storage = App.Services.GetRequiredService<IClipStorageService>();
            outputPath = storage.GenerateFilePath(CaptureType.Screenshot, ".png", "(frame)");
            var folder = await StorageFolder.GetFolderFromPathAsync(System.IO.Path.GetDirectoryName(outputPath)!);
            var file = await folder.CreateFileAsync(
                System.IO.Path.GetFileName(outputPath), CreationCollisionOption.GenerateUniqueName);

            using (var stream = await file.OpenAsync(FileAccessMode.ReadWrite))
            {
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                encoder.SetSoftwareBitmap(_frames[_current]);
                await encoder.FlushAsync();
            }

            App.ShowSaveNotification(file.Path);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"GIF frame export failed: {ex}");
            App.ShowSaveFailureNotification(System.IO.Path.GetFileName(outputPath ?? "GIF frame export"));
        }
        finally
        {
            ExportFrameButton.IsEnabled = true;
        }
    }

    private async void OnSaveTrimmed(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        StopPlayback();

        BusyBar.Visibility = Visibility.Visible;
        SaveTrimmedButton.IsEnabled = false;
        string? outputPath = null;

        try
        {
            var storage = App.Services.GetRequiredService<IClipStorageService>();
            outputPath = storage.GenerateFilePath(CaptureType.Gif, ".gif", " (trimmed)");
            var folder = await StorageFolder.GetFolderFromPathAsync(System.IO.Path.GetDirectoryName(outputPath)!);
            var outFile = await folder.CreateFileAsync(
                System.IO.Path.GetFileName(outputPath), CreationCollisionOption.GenerateUniqueName);

            using (var stream = await outFile.OpenAsync(FileAccessMode.ReadWrite))
            {
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.GifEncoderId, stream);

                var loopProps = new BitmapPropertySet
                {
                    { "/appext/application", new BitmapTypedValue(Encoding("NETSCAPE2.0"), PropertyType.UInt8Array) },
                    { "/appext/data", new BitmapTypedValue(new byte[] { 3, 1, 0, 0 }, PropertyType.UInt8Array) },
                };
                await encoder.BitmapProperties.SetPropertiesAsync(loopProps);

                for (var i = _start; i <= _end; i++)
                {
                    encoder.SetSoftwareBitmap(_frames[i]);

                    var delayProps = new BitmapPropertySet
                    {
                        { "/grctlext/Delay", new BitmapTypedValue(ScaleDelay(_delays[i]), PropertyType.UInt16) },
                        { "/grctlext/Disposal", new BitmapTypedValue((byte)1, PropertyType.UInt8) },
                    };
                    await encoder.BitmapProperties.SetPropertiesAsync(delayProps);

                    if (i < _end)
                    {
                        await encoder.GoToNextFrameAsync();
                    }
                }

                await encoder.FlushAsync();
            }

            outputPath = outFile.Path;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"GIF trim failed: {ex}");
            App.ShowSaveFailureNotification(System.IO.Path.GetFileName(outputPath ?? _filePath));
            outputPath = null;
        }
        finally
        {
            BusyBar.Visibility = Visibility.Collapsed;
            SaveTrimmedButton.IsEnabled = true;
        }

        Completed?.Invoke(this, outputPath);
        Close();
    }

    private static byte[] Encoding(string ascii)
    {
        var bytes = new byte[ascii.Length];
        for (var i = 0; i < ascii.Length; i++)
        {
            bytes[i] = (byte)ascii[i];
        }

        return bytes;
    }

    private void OnDone(object sender, RoutedEventArgs e)
    {
        Completed?.Invoke(this, null);
        Close();
    }

    private void OnWindowClosed(object sender, WindowEventArgs e)
    {
        _playTimer?.Stop();
        foreach (var frame in _frames)
        {
            frame.Dispose();
        }

        _frames.Clear();
    }

    /// <summary>Raised once when the window closes. Carries the trimmed file path, or null if untrimmed.</summary>
    public event EventHandler<string?>? Completed;
}
