using System;
using System.ComponentModel;
using System.Linq;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace TinyClips.App.Settings.Sections;

/// <summary>Teleprompter overlay settings with a live, speed-accurate transcript preview.</summary>
public sealed partial class TeleprompterSettingsSection : UserControl, ISettingsSectionLifecycle
{
    private static readonly TimeSpan PreviewInterval = TimeSpan.FromMilliseconds(16);
    private const string DefaultPreviewTranscript =
        "Your transcript will scroll here while you record.\n" +
        "Adjust the speed until it feels comfortable to read.";

    private readonly IDisposable _realizationScope;
    private readonly DispatcherTimer _previewTimer;
    private bool _isPreviewRequested;
    private bool _windowClosed;

    public SettingsViewModel ViewModel { get; }

    /// <summary>Raised when the user requests to load a transcript file.</summary>
    public event EventHandler? LoadTranscriptRequested;

    public TeleprompterSettingsSection(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        _realizationScope = viewModel.BeginSectionRealization();
        InitializeComponent();

        _previewTimer = new DispatcherTimer { Interval = PreviewInterval };
        _previewTimer.Tick += OnPreviewTick;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        UpdatePreviewTranscript();
        SectionLifecycle.HookFirstLoad(this, viewModel, _realizationScope);
    }

    public void NotifyWindowClosed()
    {
        if (_windowClosed)
        {
            return;
        }

        _windowClosed = true;
        StopPreview();
        _previewTimer.Tick -= OnPreviewTick;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PreviewScroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopPreview();
    }

    private void OnPreviewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        StartPreviewIfPossible();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.TeleprompterEnabled))
        {
            if (!ViewModel.TeleprompterEnabled)
            {
                StopPreview();
            }

            return;
        }

        if (e.PropertyName != nameof(SettingsViewModel.TeleprompterTranscript))
        {
            return;
        }

        UpdatePreviewTranscript();
        if (IsLoaded)
        {
            PreviewScroller.ChangeView(null, 0, null, disableAnimation: true);
            if (_isPreviewRequested)
            {
                DispatcherQueue.TryEnqueue(StartPreviewIfPossible);
            }
        }
    }

    private void OnPreviewToggleClicked(object sender, RoutedEventArgs e)
    {
        if (_isPreviewRequested)
        {
            StopPreview();
            return;
        }

        _isPreviewRequested = true;
        UpdatePreviewButton();
        PreviewScroller.ChangeView(null, 0, null, disableAnimation: true);
        StartPreviewIfPossible();
    }

    private void OnLoadTranscriptClicked(object sender, RoutedEventArgs e)
    {
        LoadTranscriptRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnPreviewTick(object? sender, object e)
    {
        var maximumOffset = PreviewScroller.ExtentHeight - PreviewScroller.ViewportHeight;
        if (maximumOffset <= 0)
        {
            _previewTimer.Stop();
            return;
        }

        var nextOffset = PreviewScroller.VerticalOffset +
            (ViewModel.TeleprompterScrollSpeed * PreviewInterval.TotalSeconds);
        PreviewScroller.ChangeView(
            null,
            nextOffset >= maximumOffset ? 0 : nextOffset,
            null,
            disableAnimation: true);
    }

    private void StartPreviewIfPossible()
    {
        if (_isPreviewRequested &&
            !_windowClosed &&
            IsLoaded &&
            PreviewScroller.ExtentHeight > PreviewScroller.ViewportHeight)
        {
            _previewTimer.Start();
        }
    }

    private void StopPreview()
    {
        _isPreviewRequested = false;
        _previewTimer.Stop();
        UpdatePreviewButton();
    }

    private void UpdatePreviewButton()
    {
        PreviewToggleButton.Content = _isPreviewRequested ? "Stop Preview" : "Start Preview";
        AutomationProperties.SetName(
            PreviewToggleButton,
            _isPreviewRequested ? "Stop teleprompter preview" : "Start teleprompter preview");
    }

    private void UpdatePreviewTranscript()
    {
        var transcript = string.IsNullOrWhiteSpace(ViewModel.TeleprompterTranscript)
            ? DefaultPreviewTranscript
            : ViewModel.TeleprompterTranscript.Trim();
        var preview = string.Concat(
            transcript
                .EnumerateRunes()
                .Take(600)
                .Select(rune => rune.ToString()));
        PreviewText.Text = string.Join("\n\n", Enumerable.Repeat(preview, 3));
    }
}
