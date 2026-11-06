using System.ComponentModel;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
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
    private static readonly Vector2 PreviewArrowStart = new(244, 66);
    private static readonly Vector2 PreviewArrowEnd = new(316, 18);
    private static readonly Point PreviewBadgeCenter = new(372, 42);
    private DropShadow? _previewRectangleShadow;
    private DropShadow? _previewEllipseShadow;

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
        PreviewBackground.SizeChanged += OnPreviewBackgroundSizeChanged;
        UpdatePreview();
    }

    public void NotifyWindowClosed()
    {
        _closed = true;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.Detach();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e) => UpdatePreview();

    // A gradient's angle depends on the shape it fills, which isn't known until layout.
    private void OnPreviewBackgroundSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ViewModel.Theme is { } theme)
        {
            PreviewBackground.Background = BackgroundBrush(theme.Canvas);
        }
    }

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

        UpdatePreviewShape(PreviewRectangle, theme.Rectangle);
        _previewRectangleShadow ??= CreatePreviewShadow(PreviewRectangle, PreviewRectangleShadowHost);
        UpdatePreviewShadow(_previewRectangleShadow, theme.Rectangle);
        UpdatePreviewShape(PreviewEllipse, theme.Ellipse);
        _previewEllipseShadow ??= CreatePreviewShadow(PreviewEllipse, PreviewEllipseShadowHost);
        UpdatePreviewShadow(_previewEllipseShadow, theme.Ellipse);

        UpdateArrow(theme.Arrow);
        UpdatePreviewStroke(PreviewLine, theme.Line);
        UpdatePreviewStroke(PreviewPen, theme.Pen);

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
        Canvas.SetLeft(PreviewBadge, PreviewBadgeCenter.X - diameter / 2);
        Canvas.SetTop(PreviewBadge, PreviewBadgeCenter.Y - diameter / 2);
        PreviewBadgeCircle.Fill = new SolidColorBrush(EditorTheme.ColorOf(theme.Number.BadgeColor));
        PreviewBadgeText.Foreground = new SolidColorBrush(EditorTheme.ColorOf(theme.Number.TextColor));
        PreviewBadgeText.FontSize = Math.Max(1, diameter * 0.55);
    }

    private static double PreviewStrokeWidth(double units) =>
        EditorThemeUnits.StrokePixels(units, PreviewImageWidth) * PreviewScale;

    private static void UpdatePreviewShape(Shape element, EditorThemeShape shape)
    {
        element.Stroke = new SolidColorBrush(EditorTheme.ColorOf(shape.StrokeColor));
        element.StrokeThickness = PreviewStrokeWidth(shape.StrokeWidth);
        element.Fill = ShapeFillBrush(shape, element.Width, element.Height);
    }

    private static void UpdatePreviewStroke(Shape element, EditorThemeStroke stroke)
    {
        element.Stroke = new SolidColorBrush(EditorTheme.ColorOf(stroke.Color));
        element.StrokeThickness = PreviewStrokeWidth(stroke.Width);
    }

    private void UpdateArrow(EditorThemeArrow theme)
    {
        var brush = new SolidColorBrush(EditorTheme.ColorOf(theme.Color));
        var width = PreviewStrokeWidth(theme.Width);
        var arrow = EditorController.BuildArrow(PreviewArrowStart, PreviewArrowEnd, width, EditorThemeStyling.ArrowStyleOf(theme.Style));
        var figure = new PathFigure { StartPoint = ToPoint(arrow.ShaftStart), IsFilled = false };
        figure.Segments.Add(arrow.Curved
            ? new QuadraticBezierSegment { Point1 = ToPoint(arrow.ShaftControl), Point2 = ToPoint(arrow.ShaftEnd) }
            : new LineSegment { Point = ToPoint(arrow.ShaftEnd) });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        PreviewArrowShaft.Data = geometry;
        PreviewArrowShaft.Stroke = brush;
        PreviewArrowShaft.StrokeThickness = width;

        PreviewArrowHead.Fill = brush;
        PreviewArrowHead.Points = new PointCollection
        {
            ToPoint(arrow.Tip),
            ToPoint(arrow.Head1),
            ToPoint(arrow.Head2),
        };
    }

    private static Point ToPoint(Vector2 point) => new(point.X, point.Y);

    private static void UpdatePreviewShadow(DropShadow shadow, EditorThemeShape shape)
    {
        var blur = PreviewStrokeWidth(shape.ShadowBlur);
        var color = EditorTheme.ColorOf(shape.ShadowColor);
        shadow.Color = Color.FromArgb(255, color.R, color.G, color.B);
        shadow.Opacity = shape.ShadowBlur > 0 ? color.A / 255f : 0;
        shadow.BlurRadius = (float)(blur / 2 * EditorCanvas.PreviewBlurRadiusPerDeviation);
        shadow.Offset = new Vector3(0, (float)(blur * ShapeEffects.ShadowOffsetRatio), 0);
    }

    /// <summary>Each shape needs its own shadow, cast by its own mask into the host behind it.</summary>
    private static DropShadow CreatePreviewShadow(Shape element, UIElement host)
    {
        var compositor = ElementCompositionPreview.GetElementVisual(element).Compositor;
        var shadow = compositor.CreateDropShadow();
        shadow.Mask = element.GetAlphaMask();
        var sprite = compositor.CreateSpriteVisual();
        sprite.Size = new Vector2((float)element.Width, (float)element.Height);
        sprite.Shadow = shadow;
        ElementCompositionPreview.SetElementChildVisual(host, sprite);
        return shadow;
    }

    private static Brush ShapeFillBrush(EditorThemeShape shape, double width, double height)
    {
        var fill = EditorTheme.ColorOf(shape.FillColor);
        return shape.GradientEndColor is { } end
            ? GradientBrush(fill, EditorTheme.ColorOf(end), shape.GradientDirection, width, height)
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
