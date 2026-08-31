using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TinyClips.App.ScreenshotEditor;
using TinyClips.Core.Capture;
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

    // Discard-changes-on-close tracking (parity with macOS's hasUnsavedChanges exit
    // confirmation). EditorController.IsDirty is the source of truth for annotation/crop-apply
    // edits (it is only set at genuine committed-mutation call sites, so it isn't confused by
    // transient drag previews, async redaction-preview refreshes, or self-cancelling
    // add-then-undo sequences); _hasPendingCropSelection separately tracks an in-progress crop
    // rectangle that hasn't been applied yet, since that never becomes an annotation. Combined,
    // HasUnsavedChanges below decides whether closing needs to be guarded.
    private bool _hasPendingCropSelection;
    private bool _closeConfirmed;

    public ScreenshotEditorWindow(string filePath)
        : this(filePath, initialFrame: null, pendingSave: null)
    {
    }

    /// <summary>
    /// Opens the editor straight from captured pixels while the file is still being encoded and
    /// written by <paramref name="pendingSave"/>. The editor becomes file-backed (Save, Reset,
    /// Open folder) as soon as that task yields the final path.
    /// </summary>
    public ScreenshotEditorWindow(CapturedFrame frame, Task<string> pendingSave)
        : this(string.Empty, frame, pendingSave)
    {
    }

    private ScreenshotEditorWindow(string filePath, CapturedFrame? initialFrame, Task<string>? pendingSave)
    {
        _filePath = filePath;
        _activeSavePath = filePath;
        _initialFrame = initialFrame;
        _pendingSave = pendingSave;

        InitializeComponent();

        _controller = new EditorController(DispatcherQueue);
        Toolbar.Attach(_controller);
        Inspector.Attach(_controller);
        Canvas.Attach(_controller);

        _controller.ImageChanged += OnControllerImageChanged;
        Canvas.CropSelectionAvailabilityChanged += (_, available) =>
        {
            ApplyCropButton.IsEnabled = available;
            _hasPendingCropSelection = available;
        };

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindowPlacement.CenterInCurrentWorkAreaAtHalfSize(AppWindow);

        // WindowChromeController owns: icon-on-activation, DIP minimum enforcement, XamlRoot
        // scale tracking, and cleanup of all three on Closed. The Closed subscription here is
        // additive; both this controller's cleanup and the existing OnClosed handler below run.
        _chromeController = new WindowChromeController(this, RootGrid, MinimumWidthDip, MinimumHeightDip);

        var settings = App.Services.GetRequiredService<ICaptureSettings>();
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

        _ = LoadAsync();
    }

    private void OnControllerImageChanged(object? sender, EventArgs e)
    {
        var bitmap = _controller.Bitmap;
        ImageSizeText.Text = bitmap is null ? string.Empty : $"{bitmap.PixelWidth} × {bitmap.PixelHeight} px";
    }

    private void OnClosed(object sender, WindowEventArgs args) => _controller.Dispose();

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

    private void MarkChangesSaved() => _controller.MarkSaved();

    private async Task<bool> ShowDiscardChangesDialogAsync()
    {
        var dialog = new ContentDialog
        {
            Title = "Discard changes?",
            Content = "You have unsaved annotations. Close anyway?",
            PrimaryButtonText = "Discard",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.RequestedTheme,
        };
        dialog.PrimaryButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"];

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    // -- Load -------------------------------------------------------------------------------

    private async Task LoadAsync()
    {
        try
        {
            if (_initialFrame is { } frame)
            {
                // Fast path: show the captured pixels immediately; the file is still being written.
                var bitmap = await Task.Run(() => SoftwareBitmap.CreateCopyFromBuffer(
                    frame.BgraPixels.AsBuffer(),
                    BitmapPixelFormat.Bgra8,
                    frame.Width,
                    frame.Height,
                    BitmapAlphaMode.Premultiplied));
                await _controller.SetBitmapFromCaptureAsync(bitmap);
                CaptureFlowTrace.Mark("editor: image visible (from memory)");
                MarkChangesSaved();
                if (string.IsNullOrEmpty(_filePath))
                {
                    _ = BindToPendingSaveAsync();
                }
                return;
            }

            await _controller.LoadAsync(_filePath);
            CaptureFlowTrace.Mark("editor: image visible (from file)");
            MarkChangesSaved();
        }
        catch (Exception ex)
        {
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

        // Typing in an inspector text field (e.g. the custom emoji box) must not trigger
        // single-letter tool hotkeys, Space panning, or Delete-selected-annotation.
        if (e.OriginalSource is Microsoft.UI.Xaml.Controls.TextBox)
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
        if (!await EnsureFileBackingAsync())
        {
            return;
        }

        await SaveToPathAsync(_activeSavePath, updateActiveSavePath: true);
    }

    private async void OnSaveCopy(object sender, RoutedEventArgs e)
    {
        await EnsureFileBackingAsync();
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeChoices.Add("PNG image", new[] { ".png" });
        picker.FileTypeChoices.Add("JPEG image", new[] { ".jpg" });
        picker.SuggestedFileName = (string.IsNullOrEmpty(_activeSavePath)
            ? "Screenshot"
            : System.IO.Path.GetFileNameWithoutExtension(_activeSavePath)) + " (edited)";

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSaveFileAsync();
        if (file is not null)
        {
            await SaveToPathAsync(file.Path, updateActiveSavePath: true);
        }
    }

    private async Task<bool> SaveToPathAsync(string path, bool updateActiveSavePath)
    {
        if (_controller.Bitmap is null)
        {
            return false;
        }

        var saved = await EncodeToFileAsync(path);
        if (saved)
        {
            if (updateActiveSavePath)
            {
                _activeSavePath = path;
            }

            MarkChangesSaved();
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
        if (_controller.Bitmap is null)
        {
            return;
        }

        try
        {
            using var flattened = await _controller.RenderToBitmapAsync();
            await ClipboardService.CopyBitmapAsync(flattened);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Copy failed: {ex}");
            App.ShowClipboardFailureNotification(System.IO.Path.GetFileName(_filePath));
        }
    }

    private async Task<bool> EncodeToFileAsync(string path)
    {
        if (_controller.Bitmap is null)
        {
            return false;
        }

        try
        {
            using var flattened = await _controller.RenderToBitmapAsync();
            var isPng = path.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
            var encoderId = isPng ? BitmapEncoder.PngEncoderId : BitmapEncoder.JpegEncoderId;

            var folder = await StorageFolder.GetFolderFromPathAsync(System.IO.Path.GetDirectoryName(path)!);
            var file = await folder.CreateFileAsync(System.IO.Path.GetFileName(path), CreationCollisionOption.ReplaceExisting);
            using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
            var encoder = await BitmapEncoder.CreateAsync(encoderId, stream);

            SoftwareBitmap toEncode = flattened;
            SoftwareBitmap? converted = null;
            try
            {
                if (!isPng && flattened.BitmapAlphaMode != BitmapAlphaMode.Ignore)
                {
                    converted = SoftwareBitmap.Convert(flattened, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
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

            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Save failed: {ex}");
            App.ShowSaveFailureNotification(System.IO.Path.GetFileName(path));
            return false;
        }
    }
}
