using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using TinyClips.App.ScreenshotEditor;
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
    private readonly string _filePath;
    private readonly EditorController _controller;
    private string _activeSavePath;

    public ScreenshotEditorWindow(string filePath)
    {
        _filePath = filePath;
        _activeSavePath = filePath;

        InitializeComponent();

        _controller = new EditorController(DispatcherQueue);
        Toolbar.Attach(_controller);
        Inspector.Attach(_controller);
        Canvas.Attach(_controller);

        _controller.ImageChanged += OnControllerImageChanged;
        Canvas.CropSelectionAvailabilityChanged += (_, available) => ApplyCropButton.IsEnabled = available;

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

        RootGrid.KeyDown += OnRootKeyDown;
        Closed += OnClosed;

        _ = LoadAsync();
    }

    private void OnControllerImageChanged(object? sender, EventArgs e)
    {
        var bitmap = _controller.Bitmap;
        ImageSizeText.Text = bitmap is null ? string.Empty : $"{bitmap.PixelWidth} × {bitmap.PixelHeight} px";
    }

    private void OnClosed(object sender, WindowEventArgs args) => _controller.Dispose();

    // -- Load -------------------------------------------------------------------------------

    private async Task LoadAsync()
    {
        try
        {
            await _controller.LoadAsync(_filePath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Editor load failed: {ex}");
        }
    }

    // -- Keyboard shortcuts -------------------------------------------------------------------

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (ctrl && e.Key == Windows.System.VirtualKey.Z)
        {
            OnUndo(this, new RoutedEventArgs());
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
            Windows.System.VirtualKey.B => EditTool.Redact,
            _ => (EditTool?)null,
        };
        if (tool is { } t)
        {
            _controller.SetTool(t);
            e.Handled = true;
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
        await SaveToPathAsync(_activeSavePath, updateActiveSavePath: true);
    }

    private async void OnSaveCopy(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeChoices.Add("PNG image", new[] { ".png" });
        picker.FileTypeChoices.Add("JPEG image", new[] { ".jpg" });
        picker.SuggestedFileName = System.IO.Path.GetFileNameWithoutExtension(_activeSavePath) + " (edited)";

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

            App.ShowSaveNotification(path);
        }

        return saved;
    }

    private void OnOpenSaveFolder(object sender, RoutedEventArgs e)
    {
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

    private void OnClose(object sender, RoutedEventArgs e) => Close();

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
