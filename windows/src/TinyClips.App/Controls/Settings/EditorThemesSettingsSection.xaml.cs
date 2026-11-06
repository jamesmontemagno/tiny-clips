using System.ComponentModel;
using System.Numerics;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TinyClips.App.ScreenshotEditor;
using TinyClips.App.ViewModels;
using TinyClips.Core.Editing;
using TinyClips.Core.Editing.Themes;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Pickers;
using Color = Windows.UI.Color;

namespace TinyClips.App.Settings.Sections;

/// <summary>Creates, edits, imports, and exports editor themes, with a live sample of the selected one.</summary>
public sealed partial class EditorThemesSettingsSection : UserControl, ISettingsSectionLifecycle
{
    private const ulong MaximumImportSizeInBytes = 1_000_000;

    // The sample stands in for an 840 px wide image shown at half size.
    private const double PreviewImageWidth = 840;
    private const double PreviewScale = 0.5;

    private readonly nint _windowHandle;
    private bool _closed;

    public EditorThemesSettingsViewModel ViewModel { get; }

    public List<string> FontChoices { get; } = [.. EditorFonts.Choices];

    public EditorThemesSettingsSection(EditorThemesSettingsViewModel viewModel, nint windowHandle)
    {
        ViewModel = viewModel;
        _windowHandle = windowHandle;
        // Inline on the UI thread, so a theme just saved is in the list before it is selected.
        ViewModel.UiDispatcher = action =>
        {
            if (!_closed && DispatcherQueue.HasThreadAccess)
            {
                action();
            }
            else if (!_closed)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!_closed)
                    {
                        action();
                    }
                });
            }
        };
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdatePreview();
    }

    public void NotifyWindowClosed()
    {
        _closed = true;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.Detach();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e) => UpdatePreview();

    private void OnNewClick(object sender, RoutedEventArgs e) => ViewModel.New();

    private void OnDuplicateClick(object sender, RoutedEventArgs e) => ViewModel.Duplicate();

    private void OnSetActiveClick(object sender, RoutedEventArgs e) => ViewModel.SetActive();

    private void OnConfirmDeleteClick(object sender, RoutedEventArgs e)
    {
        DeleteFlyout.Hide();
        ViewModel.Delete();
    }

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add(".json");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandle);
            var file = await picker.PickSingleFileAsync();
            if (_closed || file is null)
            {
                return;
            }

            var properties = await file.GetBasicPropertiesAsync();
            if (properties.Size > MaximumImportSizeInBytes)
            {
                ViewModel.ReportError("That file is too large to be an editor theme.");
                return;
            }

            var json = await FileIO.ReadTextAsync(file);
            if (!_closed)
            {
                ViewModel.Import(json);
            }
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                ViewModel.ReportError($"The theme could not be imported. {ex.Message}");
            }
        }
    }

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ExportSelected() is not { } json)
        {
            return;
        }

        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = ViewModel.ExportFileName,
            };
            picker.FileTypeChoices.Add("Editor theme", new List<string> { ".json" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandle);
            var file = await picker.PickSaveFileAsync();
            if (!_closed && file is not null)
            {
                await FileIO.WriteTextAsync(file, json);
            }
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                ViewModel.ReportError($"The theme could not be exported. {ex.Message}");
            }
        }
    }

    private void UpdatePreview()
    {
        if (ViewModel.Theme is not { } theme)
        {
            return;
        }

        PreviewBackground.Background = BackgroundBrush(theme.Canvas);
        PreviewCard.CornerRadius = new CornerRadius(theme.Canvas.CornerRadius * PreviewScale);

        PreviewRectangle.Stroke = new SolidColorBrush(EditorTheme.ColorOf(theme.Shape.StrokeColor));
        PreviewRectangle.StrokeThickness = EditorThemeUnits.StrokePixels(theme.Shape.StrokeWidth, PreviewImageWidth) * PreviewScale;
        PreviewRectangle.Fill = ShapeFillBrush(theme.Shape);

        var lineColor = new SolidColorBrush(EditorTheme.ColorOf(theme.Line.Color));
        var lineWidth = EditorThemeUnits.StrokePixels(theme.Line.Width, PreviewImageWidth) * PreviewScale;
        PreviewLine.Stroke = lineColor;
        PreviewLine.StrokeThickness = lineWidth;
        UpdateArrowHead(lineColor, lineWidth);

        PreviewText.Foreground = new SolidColorBrush(EditorTheme.ColorOf(theme.Text.Color));
        PreviewText.FontFamily = new FontFamily(EditorThemeStyling.FontFamilyOf(theme.Text));
        PreviewText.FontSize = Math.Max(1, EditorThemeUnits.TextPixels(theme.Text.FontSize, PreviewImageWidth) * PreviewScale);
        PreviewText.FontWeight = theme.Text.Bold ? FontWeights.Bold : FontWeights.Normal;
        PreviewText.FontStyle = theme.Text.Italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal;
        PreviewText.TextDecorations = theme.Text.Underline ? Windows.UI.Text.TextDecorations.Underline : Windows.UI.Text.TextDecorations.None;

        var box = EditorThemeStyling.TextBoxStyleOf(theme.Text.Box, PreviewImageWidth);
        PreviewTextBox.Background = new SolidColorBrush(box.BackgroundColor);
        PreviewTextBox.BorderBrush = new SolidColorBrush(box.BorderColor);
        PreviewTextBox.BorderThickness = new Thickness(box.BorderWidth * PreviewScale);
        PreviewTextBox.Padding = new Thickness(box.Padding * PreviewScale);
        PreviewTextBox.CornerRadius = new CornerRadius(box.CornerRadius * PreviewScale);

        var diameter = EditorController.CounterRadius(theme.Number.Size) * 2 * PreviewScale;
        PreviewBadge.Width = diameter;
        PreviewBadge.Height = diameter;
        PreviewBadgeCircle.Fill = new SolidColorBrush(EditorTheme.ColorOf(theme.Number.BadgeColor));
        PreviewBadgeText.Foreground = new SolidColorBrush(EditorTheme.ColorOf(theme.Number.TextColor));
        PreviewBadgeText.FontSize = Math.Max(1, diameter * 0.55);
    }

    private void UpdateArrowHead(Brush brush, double lineWidth)
    {
        var tip = new Vector2((float)PreviewLine.X2, (float)PreviewLine.Y2);
        var direction = Vector2.Normalize(tip - new Vector2((float)PreviewLine.X1, (float)PreviewLine.Y1));
        var normal = new Vector2(-direction.Y, direction.X);
        var length = (float)Math.Max(8, lineWidth * 3.5);
        var left = tip - direction * length + normal * length * 0.5f;
        var right = tip - direction * length - normal * length * 0.5f;
        PreviewArrowHead.Fill = brush;
        PreviewArrowHead.Points = new PointCollection
        {
            new Point(tip.X, tip.Y),
            new Point(left.X, left.Y),
            new Point(right.X, right.Y),
        };
    }

    private static Brush ShapeFillBrush(EditorThemeShape shape)
    {
        var fill = EditorTheme.ColorOf(shape.FillColor);
        return shape.GradientEndColor is { } end
            ? GradientBrush(fill, EditorTheme.ColorOf(end), shape.GradientDirection, 130, 84)
            : new SolidColorBrush(fill);
    }

    private Brush? BackgroundBrush(EditorThemeCanvas canvas)
    {
        var width = Math.Max(1, PreviewBackground.ActualWidth);
        var height = Math.Max(1, PreviewBackground.ActualHeight);
        return canvas.Background switch
        {
            "solid" => new SolidColorBrush(EditorTheme.ColorOf(canvas.Color)),
            "gradient" => GradientBrush(EditorTheme.ColorOf(canvas.Color), EditorTheme.ColorOf(canvas.SecondaryColor), canvas.GradientDirection, width, height),
            _ => null,
        };
    }

    private static LinearGradientBrush GradientBrush(Color start, Color end, double direction, double width, double height)
    {
        var points = ScreenshotGradientMath.Endpoints(direction, width, height);
        return new LinearGradientBrush
        {
            StartPoint = new Point(points.Start.X, points.Start.Y),
            EndPoint = new Point(points.End.X, points.End.Y),
            GradientStops =
            {
                new GradientStop { Color = start, Offset = 0 },
                new GradientStop { Color = end, Offset = 1 },
            },
        };
    }
}
