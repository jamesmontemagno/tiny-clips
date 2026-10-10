using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using TinyClips.App.ScreenshotEditor;
using TinyClips.Core.Capture;
using TinyClips.Core.Editing;
using TinyClips.Core.Models;
using TinyClips.Core.Services;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace TinyClips.App;

/// <summary>
/// Screenshot editor shell with annotation parity to the macOS app: crop plus rectangle, ellipse,
/// arrow, line, freehand draw, text, numbered badges and redaction. This window owns only
/// window-level concerns — HWND/file-picker interop, clipboard/save coordination, lifecycle, and
/// top-level command wiring. All editing state and logic lives in <see cref="EditorController"/>,
/// and the toolbar/inspector/canvas UI lives in the <c>ScreenshotEditor</c> UserControls.
/// </summary>
public sealed partial class ScreenshotEditorWindow : Window
{
    // Minimum dimensions chosen to keep the tool rail + inspector + a usable canvas visible.
    // Width 760 DIP: tool rail (~52) + inspector (~200) + canvas floor (~300) + margins (~208).
    // The CommandBar will gracefully overflow AppBarButtons into its "More" menu below this width.
    // Height 520 DIP: TitleBar (~48) + CommandBar row (~60) + canvas floor (~300) + padding (~112).
    private const int MinimumWidthDip  = 760;
    private const int MinimumHeightDip = 520;

    private string _filePath;
    private readonly EditorController _controller;
    private readonly WindowChromeController _chromeController;
    private string _activeSavePath;
    private readonly CapturedFrame? _initialFrame;
    private readonly Task<string>? _pendingSave;
    private readonly WindowOpenTrace? _openTrace;

    // Discard-changes-on-close tracking (parity with macOS's hasUnsavedChanges exit
    // confirmation). EditorController.IsDirty is the source of truth for annotation/crop-apply
    // and export-style edits (it is only set at genuine committed-mutation call sites, so it isn't confused by
    // transient drag previews, async redaction-preview refreshes, or self-cancelling
    // add-then-undo sequences); _hasPendingCropSelection separately tracks an in-progress crop
    // rectangle that hasn't been applied yet, since that never becomes an annotation. Combined,
    // HasUnsavedChanges below decides whether closing needs to be guarded.
    private bool _hasPendingCropSelection;
    private bool _closeConfirmed;
    private bool _isClosePromptOpen;
    private bool _isDeletingSource;
    private bool _isClosed;
    private int _outputScalePercent = 100;
    private bool _isOutputBusy;
    private long _loadRequest;

    public ScreenshotEditorWindow(string filePath)
        : this(filePath, initialFrame: null, pendingSave: null, WindowOpenTrace.Start(WindowOpenKind.ScreenshotEditor))
    {
    }

    /// <summary>
    /// Opens the editor straight from captured pixels while the file is still being encoded and
    /// written by <paramref name="pendingSave"/>. The editor becomes file-backed (Save, Reset,
    /// Open folder) as soon as that task yields the final path.
    /// </summary>
    public ScreenshotEditorWindow(CapturedFrame frame, Task<string> pendingSave)
        : this(string.Empty, frame, pendingSave, WindowOpenTrace.Start(WindowOpenKind.ScreenshotEditor))
    {
    }

    private ScreenshotEditorWindow(string filePath, CapturedFrame? initialFrame, Task<string>? pendingSave, WindowOpenTrace? openTrace)
    {
        _openTrace = openTrace;
        _openTrace?.Mark(WindowOpenMilestone.ConstructorEntered);
        using var construction = _openTrace?.Measure(WindowOpenPhase.ConstructorBody);
        _filePath = filePath;
        _activeSavePath = filePath;
        _initialFrame = initialFrame;
        _pendingSave = pendingSave;

        using (_openTrace?.Measure(WindowOpenPhase.Xaml))
        {
            InitializeComponent();
        }

        WindowOpenDiagnostics.Observe(this, RootGrid, _openTrace);
        ICaptureSettings settings;
        using (_openTrace?.Measure(WindowOpenPhase.ControllerAndBindings))
        {
            settings = App.Services.GetRequiredService<ICaptureSettings>();
            _controller = new EditorController(DispatcherQueue);
            Toolbar.Attach(_controller);
            Inspector.Attach(_controller);
            Canvas.Attach(_controller, settings);

            _controller.ImageChanged += OnControllerImageChanged;
            // Padding, corner radius, shadow and frame presets change the exported frame size without
            // touching the source bitmap, so the advertised output resolution has to follow them too.
            _controller.BackgroundChanged += OnControllerImageChanged;
            Canvas.CropSelectionAvailabilityChanged += (_, available) =>
            {
                ApplyCropButton.IsEnabled = available;
                _hasPendingCropSelection = available;
            };
        }

        using (_openTrace?.Measure(WindowOpenPhase.ChromeAndPlacement))
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            AppWindowPlacement.CenterInCurrentWorkAreaAtHalfSize(AppWindow);

            // WindowChromeController owns: icon-on-activation, DIP minimum enforcement, XamlRoot
            // scale tracking, and cleanup of all three on Closed. The Closed subscription here is
            // additive; both this controller's cleanup and the existing OnClosed handler below run.
            _chromeController = new WindowChromeController(this, RootGrid, MinimumWidthDip, MinimumHeightDip);
        }

        using (_openTrace?.Measure(WindowOpenPhase.ThemeAndSubscriptions))
        {
            RootGrid.RequestedTheme = settings.Theme switch
            {
                AppTheme.Light => ElementTheme.Light,
                AppTheme.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };

            RootGrid.KeyDown += OnRootKeyDown;
            RootGrid.KeyUp += OnRootKeyUp;
            Activated += OnActivated;
            Closed += OnClosed;
            AppWindow.Closing += OnAppWindowClosing;
        }

        _ = LoadAsync();
        _openTrace?.Mark(WindowOpenMilestone.ConstructorCompleted);
    }

    private void OnControllerImageChanged(object? sender, EventArgs e) => UpdateOutputResolutionText();

    /// <summary>
    /// Uses the renderer's shared frame truncation/scale rounding so the advertised size matches
    /// Copy and Save, including padding and frame presets.
    /// </summary>
    private void UpdateOutputResolutionText()
    {
        // The slider raises ValueChanged while InitializeComponent builds the flyout, which is
        // before the controller field is assigned, so it and the controls are checked before use.
        if (_controller is null || ImageSizeText is null || OutputScaleButton is null)
        {
            return;
        }

        if (_controller.Bitmap is null)
        {
            ImageSizeText.Text = string.Empty;
            AutomationProperties.SetName(OutputScaleButton, "Output resolution");
            return;
        }

        var frame = _controller.GetExportFrameLayout();
        var size = ScreenshotExportSize.Create(frame.FrameSize, _outputScalePercent);
        var outputWidth = size.Width;
        var outputHeight = size.Height;
        ImageSizeText.Text = $"{outputWidth} × {outputHeight} px";
        AutomationProperties.SetName(OutputScaleButton, $"Output resolution, {outputWidth} by {outputHeight} pixels");
    }

    private void OnOutputScaleChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        var scalePercent = (int)Math.Round(e.NewValue);
        if (_outputScalePercent != scalePercent)
        {
            _outputScalePercent = scalePercent;
            _controller?.NotifyExportSettingsEdited();
        }
        if (OutputScaleValueText is not null)
        {
            OutputScaleValueText.Text = $"{_outputScalePercent}%";
        }

        UpdateOutputResolutionText();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _isClosed = true;
        _controller.Dispose();
        UpdateOutputResolutionText();
    }

    /// <summary>
    /// Guards the ✕ button, Alt+F4, and system close — anything that raises the AppWindow's
    /// Closing event — the same way the toolbar Close button is guarded in <see cref="OnClose"/>.
    /// </summary>
    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeConfirmed || !HasUnsavedChanges)
        {
            return;
        }

        args.Cancel = true;

        if (await ShowDiscardChangesDialogAsync())
        {
            _closeConfirmed = true;
            Close();
        }
    }

    private bool HasUnsavedChanges => _controller.IsDirty || _hasPendingCropSelection;

    private Task<bool> ShowDiscardChangesDialogAsync() => ShowClosePromptAsync(EditorEscapePrompt.DiscardChanges);

    /// <summary>
    /// Every close path (Esc, the toolbar Close button, the title-bar X) asks through here, so a
    /// second prompt is never stacked on one that is already showing.
    /// </summary>
    private async Task<bool> ShowClosePromptAsync(EditorEscapePrompt prompt)
    {
        if (_isClosePromptOpen)
        {
            return false;
        }

        _isClosePromptOpen = true;
        try
        {
            return await EditorEscapeConfirmation.ConfirmAsync(RootGrid, prompt, EditorEscapeSurface.ScreenshotEditor);
        }
        finally
        {
            _isClosePromptOpen = false;
        }
    }

    private void OnEscapeKey(KeyRoutedEventArgs e)
    {
        var action = EditorEscape.ResolveEditorAction(IsTextInputSource(e.OriginalSource), _hasPendingCropSelection);
        if (action == ScreenshotEditorEscapeAction.LeaveToTextInput)
        {
            return;
        }

        e.Handled = true;
        if (_isClosed || _isOutputBusy || _isDeletingSource || _isClosePromptOpen)
        {
            return;
        }

        if (action == ScreenshotEditorEscapeAction.ClearCropSelection)
        {
            Canvas.ClearCropSelection();
            return;
        }

        _ = CloseFromEscapeAsync();
    }

    private async Task CloseFromEscapeAsync()
    {
        var confirmOnEscape = App.Services.GetRequiredService<ICaptureSettings>().ConfirmEditorEscape;
        if (EditorEscape.ResolvePrompt(confirmOnEscape, HasUnsavedChanges) is { } prompt &&
            !await ShowClosePromptAsync(prompt))
        {
            return;
        }

        if (_isClosed)
        {
            return;
        }

        _closeConfirmed = true;
        Close();
    }

    // -- Load -------------------------------------------------------------------------------

    private async Task LoadAsync()
    {
        if (_isClosed) return;
        var request = ++_loadRequest;
        using var loading = _openTrace?.Measure(WindowOpenPhase.ContentLoad);
        try
        {
            if (_initialFrame is { } frame)
            {
                // Fast path: show the captured pixels immediately; the file is still being written.
                await _controller.LoadCapturedFrameAsync(frame);
                if (_isClosed || request != _loadRequest) return;
                CaptureFlowTrace.Mark("editor: image visible (from memory)");
                _openTrace?.Mark(WindowOpenMilestone.ContentReady);
                if (string.IsNullOrEmpty(_filePath))
                {
                    _ = BindToPendingSaveAsync();
                }
                return;
            }

            await _controller.LoadAsync(_filePath);
            if (_isClosed || request != _loadRequest) return;
            CaptureFlowTrace.Mark("editor: image visible (from file)");
            _openTrace?.Mark(WindowOpenMilestone.ContentReady);
        }
        catch (Exception ex)
        {
            if (_isClosed || request != _loadRequest) return;
            _openTrace?.Mark(WindowOpenMilestone.ContentFailed);
            System.Diagnostics.Debug.WriteLine($"Editor load failed: {ex}");
            App.ShowImageLoadFailureNotification(System.IO.Path.GetFileName(_filePath));
            Close();
        }
    }

    private async Task BindToPendingSaveAsync()
    {
        if (_pendingSave is null)
        {
            return;
        }

        try
        {
            var path = await _pendingSave;
            _filePath = path;
            _activeSavePath = path;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Background screenshot save failed: {ex}");
            App.ShowSaveFailureNotification("the screenshot capture");
        }
    }

    /// <summary>Save/Open-folder need the file path; wait for the background save when it's still running.</summary>
    private async Task<bool> EnsureFileBackingAsync()
    {
        if (!string.IsNullOrEmpty(_activeSavePath))
        {
            return true;
        }

        if (_pendingSave is null)
        {
            return false;
        }

        try
        {
            await _pendingSave;
        }
        catch
        {
            // Already reported by BindToPendingSaveAsync.
        }

        return !string.IsNullOrEmpty(_activeSavePath);
    }

    // -- Keyboard shortcuts -------------------------------------------------------------------

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (EditorEscapeConfirmation.IsUnmodifiedEscape(e))
        {
            OnEscapeKey(e);
            return;
        }

        var ctrl = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (ctrl && (e.Key == Windows.System.VirtualKey.Add || (int)e.Key == 187))
        {
            Canvas.ZoomIn();
            e.Handled = true;
            return;
        }

        if (ctrl && (e.Key == Windows.System.VirtualKey.Subtract || (int)e.Key == 189))
        {
            Canvas.ZoomOut();
            e.Handled = true;
            return;
        }

        if (ctrl && (e.Key == Windows.System.VirtualKey.Number0 || e.Key == Windows.System.VirtualKey.NumberPad0))
        {
            Canvas.Fit();
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Windows.System.VirtualKey.Z)
        {
            OnUndo(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Windows.System.VirtualKey.C)
        {
            if (IsTextInputSource(e.OriginalSource))
            {
                return;
            }

            OnCopy(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        // Typing in an inspector text field (e.g. the custom emoji box) must not trigger
        // single-letter tool hotkeys, Space panning, or Delete-selected-annotation.
        if (IsTextInputSource(e.OriginalSource))
        {
            return;
        }

        if (!ctrl && e.Key == Windows.System.VirtualKey.Space)
        {
            Canvas.SetSpacePressed(true);
            e.Handled = true;
            return;
        }

        if (e.Key is Windows.System.VirtualKey.Delete or Windows.System.VirtualKey.Back && _controller.SelectedAnnotation is not null)
        {
            OnDeleteSelected(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        var tool = e.Key switch
        {
            Windows.System.VirtualKey.V => EditTool.Select,
            Windows.System.VirtualKey.C => EditTool.Crop,
            Windows.System.VirtualKey.R => EditTool.Rectangle,
            Windows.System.VirtualKey.O => EditTool.Ellipse,
            Windows.System.VirtualKey.A => EditTool.Arrow,
            Windows.System.VirtualKey.L => EditTool.Line,
            Windows.System.VirtualKey.D => EditTool.Pen,
            Windows.System.VirtualKey.T => EditTool.Text,
            Windows.System.VirtualKey.N => EditTool.Counter,
            Windows.System.VirtualKey.E => EditTool.Emoji,
            Windows.System.VirtualKey.B => EditTool.Redact,
            _ => (EditTool?)null,
        };
        if (tool is { } t)
        {
            _controller.SetTool(t);
            e.Handled = true;
        }
    }

    private static bool IsTextInputSource(object originalSource) => originalSource is TextBox;

    private void OnRootKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Space)
        {
            Canvas.SetSpacePressed(false);
            e.Handled = true;
        }
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            Canvas.SetSpacePressed(false);
        }
    }

    private void OnUndo(object sender, RoutedEventArgs e)
    {
        // Cancel any in-flight drag/move on the canvas before the controller mutates its
        // annotation list — otherwise a redaction mid-move (or mid-draw) can keep tracking the
        // pointer against an annotation Undo just removed, leaving a "ghost" that redraws itself
        // on the next pointer move even though it no longer exists in Annotations.
        Canvas.CancelActiveInteraction();
        _controller.Undo();
    }

    private void OnDeleteSelected(object sender, RoutedEventArgs e)
    {
        // See OnUndo: Delete removes SelectedAnnotation from the controller's list, so any local
        // drag/move interaction targeting it must be cancelled first.
        Canvas.CancelActiveInteraction();
        _controller.DeleteSelected();
    }

    private void OnReset(object sender, RoutedEventArgs e) => _ = LoadAsync();

    // -- Crop -------------------------------------------------------------------------------

    private async void OnApplyCrop(object sender, RoutedEventArgs e)
    {
        var bounds = Canvas.GetCropSelectionPixelBounds();
        if (bounds is not { } rect || rect.Width < 1 || rect.Height < 1)
        {
            return;
        }

        try
        {
            await _controller.ApplyCropAsync(rect);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Crop failed: {ex}");
        }
    }

    // -- Output ---------------------------------------------------------------------------

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (!TryBeginOutput()) return;
        try
        {
            if (!await EnsureFileBackingAsync() || _isClosed) return;
            await SaveToPathAsync(_activeSavePath, updateActiveSavePath: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Save failed: {ex}");
            if (!_isClosed) App.ShowSaveFailureNotification(System.IO.Path.GetFileName(_activeSavePath));
        }
        finally
        {
            _isOutputBusy = false;
        }
    }

    private async void OnSaveCopy(object sender, RoutedEventArgs e)
    {
        if (!TryBeginOutput()) return;
        try
        {
            await EnsureFileBackingAsync();
            if (_isClosed) return;
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            picker.FileTypeChoices.Add("PNG image", new[] { ".png" });
            picker.FileTypeChoices.Add("JPEG image", new[] { ".jpg" });
            picker.FileTypeChoices.Add("WebP image", new[] { ".webp" });
            picker.DefaultFileExtension = System.IO.Path.GetExtension(_activeSavePath ?? string.Empty).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => ".jpg",
                ".webp" => ".webp",
                _ => ".png",
            };
            picker.SuggestedFileName = (string.IsNullOrEmpty(_activeSavePath)
                ? "Screenshot"
                : System.IO.Path.GetFileNameWithoutExtension(_activeSavePath)) + " (edited)";

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSaveFileAsync();
            if (file is not null && !_isClosed)
            {
                await SaveToPathAsync(file.Path, updateActiveSavePath: true);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Save picker failed: {ex}");
            if (!_isClosed) App.ShowSaveFailureNotification("the edited screenshot");
        }
        finally
        {
            _isOutputBusy = false;
        }
    }

    // Coalesce repeated clicks/shortcuts instead of queuing obsolete snapshots or competing writes.
    private bool TryBeginOutput()
    {
        if (_isOutputBusy || _isDeletingSource || !_controller.CanExport) return false;
        _isOutputBusy = true;
        return true;
    }

    private async Task<bool> SaveToPathAsync(string path, bool updateActiveSavePath)
    {
        if (!_controller.CanExport) return false;
        using var snapshot = _controller.CaptureRenderSnapshot(_outputScalePercent);
        var quality = App.Services.GetRequiredService<ICaptureSettings>().JpegQuality;
        var saved = await EncodeToFileAsync(path, snapshot, quality);
        if (saved && _controller.IsSameDocument(snapshot.Revision))
        {
            if (updateActiveSavePath)
            {
                _activeSavePath = path;
            }

            _controller.MarkSaved(snapshot.Revision);
            App.ShowSaveNotification(path);
        }

        return saved;
    }

    private async void OnOpenSaveFolder(object sender, RoutedEventArgs e)
    {
        if (!await EnsureFileBackingAsync())
        {
            return;
        }

        var folder = System.IO.Path.GetDirectoryName(_activeSavePath);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to open save folder: {ex}");
        }
    }

    private async void OnDeleteScreenshot(object sender, RoutedEventArgs e)
    {
        if (_isDeletingSource || _isOutputBusy || !await EnsureFileBackingAsync())
        {
            return;
        }

        if (!await ShowDeleteScreenshotDialogAsync())
        {
            return;
        }
        if (_isClosed || _isOutputBusy) return;

        _isDeletingSource = true;
        DeleteScreenshotButton.IsEnabled = false;
        try
        {
            File.Delete(_filePath);
            App.Services.GetRequiredService<IRecentCaptureService>().Remove(_filePath);
            _closeConfirmed = true;
            Close();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Delete screenshot failed: {ex}");
            await ShowDeleteFailureDialogAsync(ex);
            DeleteScreenshotButton.IsEnabled = true;
            _isDeletingSource = false;
        }
    }

    private async Task<bool> ShowDeleteScreenshotDialogAsync()
    {
        var filename = System.IO.Path.GetFileName(_filePath);
        var hasSavedCopy = !string.Equals(_activeSavePath, _filePath, StringComparison.OrdinalIgnoreCase);
        var content = HasUnsavedChanges
            ? $"This permanently deletes {filename} and discards your unsaved edits."
            : $"This permanently deletes {filename}.";

        if (hasSavedCopy)
        {
            content += $" Your saved copy, {System.IO.Path.GetFileName(_activeSavePath)}, will remain.";
        }

        var dialog = new ContentDialog
        {
            Title = "Delete original screenshot?",
            Content = $"{content} This cannot be undone.",
            PrimaryButtonText = "Delete screenshot",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.RequestedTheme,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(dialog, "EditorDeleteScreenshotDialog");
        dialog.PrimaryButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"];

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task ShowDeleteFailureDialogAsync(Exception error)
    {
        var dialog = new ContentDialog
        {
            Title = "Couldn't delete screenshot",
            Content = $"Tiny Clips couldn't delete {System.IO.Path.GetFileName(_filePath)}. Check that the file is not in use and that you have permission, then try again. Details: {error.Message}",
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.RequestedTheme,
        };

        await dialog.ShowAsync();
    }

    private async void OnClose(object sender, RoutedEventArgs e)
    {
        if (HasUnsavedChanges && !await ShowDiscardChangesDialogAsync())
        {
            return;
        }

        _closeConfirmed = true;
        Close();
    }

    private async void OnCopy(object sender, RoutedEventArgs e)
    {
        if (!TryBeginOutput()) return;
        CancellationToken token = default;
        try
        {
            using var snapshot = _controller.CaptureRenderSnapshot(_outputScalePercent);
            token = snapshot.DocumentCancellation;
            using var rendered = await _controller.RenderToBitmapAsync(snapshot, token);
            using var stream = await ClipboardService.EncodeBitmapAsync(rendered, token);
            // Publication is synchronous on the UI thread, after all rendering/encoding awaits.
            if (_controller.IsCurrent(snapshot.Revision))
                ClipboardService.SetBitmapContent(stream);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Replacement or closure canceled the snapshot before clipboard publication.
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Copy failed: {ex}");
            if (!_isClosed) App.ShowClipboardFailureNotification(System.IO.Path.GetFileName(_filePath));
        }
        finally
        {
            _isOutputBusy = false;
        }
    }

    private async Task<bool> EncodeToFileAsync(string path, EditorRenderSnapshot snapshot, double quality)
    {
        var token = snapshot.DocumentCancellation;
        try
        {
            using var rendered = await _controller.RenderToBitmapAsync(snapshot, token);
            await Task.Run(() => ScreenshotExportFile.WriteAsync(path,
                (temporaryPath, cancellation) => EncodeBitmapToFileAsync(path, temporaryPath, rendered, quality, cancellation),
                token), token);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Save failed: {ex}");
            if (!_isClosed) App.ShowSaveFailureNotification(System.IO.Path.GetFileName(path));
            return false;
        }
    }

    private static async Task EncodeBitmapToFileAsync(
        string path, string temporaryPath, SoftwareBitmap bitmap, double quality, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
        {
            var pixels = new byte[checked(bitmap.PixelWidth * bitmap.PixelHeight * 4)];
            bitmap.CopyToBuffer(pixels.AsBuffer());
            var encoded = WebpImageEncoder.Encode(pixels, bitmap.PixelWidth, bitmap.PixelHeight, 100, quality);
            await System.IO.File.WriteAllBytesAsync(temporaryPath, encoded, token);
        }
        else
        {
            var isPng = path.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
            var encoderId = isPng ? BitmapEncoder.PngEncoderId : BitmapEncoder.JpegEncoderId;

            var folder = await StorageFolder.GetFolderFromPathAsync(System.IO.Path.GetDirectoryName(temporaryPath)!);
            token.ThrowIfCancellationRequested();
            var file = await folder.CreateFileAsync(System.IO.Path.GetFileName(temporaryPath), CreationCollisionOption.FailIfExists);
            using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
            var encoder = await BitmapEncoder.CreateAsync(encoderId, stream);

            SoftwareBitmap toEncode = bitmap;
            SoftwareBitmap? converted = null;
            try
            {
                if (!isPng && bitmap.BitmapAlphaMode != BitmapAlphaMode.Ignore)
                {
                    converted = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
                    toEncode = converted;
                }

                encoder.SetSoftwareBitmap(toEncode);
                await encoder.FlushAsync();
            }
            finally
            {
                // Dispose the converted copy even if encoder creation/SetSoftwareBitmap/FlushAsync
                // throws above — otherwise a failed encode leaks the native SoftwareBitmap.
                converted?.Dispose();
            }
        }
    }
}
