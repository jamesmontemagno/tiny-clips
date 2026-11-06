using TinyClips.Core.Editing;
using TinyClips.Core.Editing.Themes;
using Windows.UI;

namespace TinyClips.App.ScreenshotEditor;

/// <summary>Editor Themes: the active theme, its baseline, and per-group stroke defaults.</summary>
internal sealed partial class EditorController
{
    private EditorThemeBaseline? _themeBaseline;
    private readonly Dictionary<EditorThemeGroup, (Color Color, double Width)> _themeStrokes = new();
    private EditorThemeGroup _themeStrokeOwner = EditorThemeGroup.Shape;

    private sealed record ThemeUndoState(
        EditorThemeBaseline? Baseline,
        Dictionary<EditorThemeGroup, (Color Color, double Width)> Strokes,
        EditorThemeGroup StrokeOwner,
        Color StrokeColor,
        double StrokeThickness,
        bool FillEnabled,
        Color FillColor,
        ShapeEffects ShapeEffects,
        ArrowStyle ArrowStyle,
        string FontFamily,
        double FontSize,
        bool Bold,
        bool Italic,
        bool Underline,
        TextBoxStyle TextBox,
        Color NumberTextColor,
        double NumberScale,
        EditorThemeCanvasState Canvas);

    /// <summary>The active theme changed, or the document moved toward or away from it.</summary>
    public event EventHandler? ThemeStateChanged;

    public EditorTheme? ActiveTheme => _themeBaseline?.Theme;

    public bool IsThemeModified =>
        _themeBaseline is { } baseline && EditorThemeCapture.IsModified(baseline.Theme, CaptureCurrentTheme()!);

    private void RaiseThemeStateChanged() => ThemeStateChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>A selected annotation is saved through the document, so its edits stay out of the defaults.</summary>
    private bool KeepsThemeDefaultsWhileEditingSelection => _themeBaseline is not null && SelectedAnnotation is not null;

    /// <summary>While a theme is active, each group keeps its own color and width.</summary>
    private void SwapThemeStroke(EditTool nextTool)
    {
        if (_themeBaseline is null || EditorThemeStyling.GroupOf(nextTool) is not { } next)
        {
            return;
        }

        _themeStrokes[_themeStrokeOwner] = (StrokeColor, StrokeThickness);
        _themeStrokeOwner = next;
        if (_themeStrokes.TryGetValue(next, out var stroke))
        {
            StrokeColor = stroke.Color;
            StrokeThickness = stroke.Width;
        }
    }

    /// <summary>Restyles the document and canvas and sets the defaults, as one undo step.</summary>
    public bool ApplyTheme(EditorTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        VerifyAccess();
        if (!CanExport)
        {
            return false;
        }

        CancelActiveAnnotation();
        CancelBackgroundSelection();
        EndSelectedStyleEdit();

        var restyled = _annotations
            .Where(annotation => EditorThemeStyling.GroupOf(annotation.Tool) is not null)
            .Select(annotation => (annotation, annotation.CaptureSnapshot()))
            .ToArray();
        _styleUndo.Push(new StyleUndoEntry(restyled, CaptureThemeUndoState()));

        var width = _bitmap!.PixelWidth;
        foreach (var (annotation, _) in restyled)
        {
            EditorThemeStyling.Apply(annotation, theme, width);
            RefreshBoundsAfterRestyle(annotation);
        }

        SetThemeDefaults(theme, width);
        var keepsImage = BgStyle == ExportBackgroundStyle.Image && HasBackgroundImage;
        ApplyCanvasState(EditorThemeStyling.CanvasStateOf(theme, keepsImage));
        _themeBaseline = new EditorThemeBaseline(theme, CurrentCanvasState(), width);
        SelectedAnnotation = null;
        MarkDirty();
        RaiseThemeRestyled();
        return true;
    }

    /// <summary>
    /// Starts a freshly loaded document with a theme, or None. Sets the defaults, and the canvas
    /// only if the theme opts in. Not an edit, so an untouched editor doesn't prompt on close.
    /// </summary>
    public void ActivateThemeOnOpen(EditorTheme? theme)
    {
        VerifyAccess();
        if (!CanExport)
        {
            return;
        }

        if (theme is null)
        {
            ClearTheme();
            return;
        }

        var width = _bitmap!.PixelWidth;
        SetThemeDefaults(theme, width);
        if (theme.Canvas.ApplyOnOpen)
        {
            ApplyCanvasState(EditorThemeStyling.CanvasStateOf(theme));
        }

        _themeBaseline = new EditorThemeBaseline(theme, CurrentCanvasState(), width);
        RaiseThemeRestyled();
        _revision.MarkSaved(_revision.Current);
    }

    /// <summary>None: stops theming and changes nothing else. Not an undo step.</summary>
    public void ClearTheme()
    {
        VerifyAccess();
        if (_themeBaseline is null)
        {
            return;
        }

        _themeBaseline = null;
        _themeStrokes.Clear();
        RaiseThemeStateChanged();
    }

    /// <summary>The look to save: the theme updated from the document. Null with None.</summary>
    public EditorTheme? CaptureCurrentTheme() =>
        _themeBaseline is { } baseline
            ? EditorThemeCapture.Capture(baseline, _annotations, CurrentThemeDefaults(), CurrentCanvasState())
            : null;

    /// <summary>Saves the current look over the active theme.</summary>
    public EditorTheme? UpdateActiveTheme(EditorThemeStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        VerifyAccess();
        if (CaptureCurrentTheme() is not { } captured)
        {
            return null;
        }

        var saved = store.Save(captured);
        Rebaseline(saved, _themeBaseline!.ImageWidth);
        return saved;
    }

    /// <summary>Saves the current look under a new name and makes it the active theme.</summary>
    public EditorTheme SaveAsNewTheme(EditorThemeStore store, string name)
    {
        ArgumentNullException.ThrowIfNull(store);
        VerifyAccess();
        var width = _themeBaseline?.ImageWidth ?? _bitmap?.PixelWidth ?? 1;
        var captured = CaptureCurrentTheme() ?? CaptureWithoutTheme(width);
        var saved = store.Save(captured with { Id = string.Empty, Name = name });
        store.SetActive(saved.Id);
        Rebaseline(saved, width);
        return saved;
    }

    /// <summary>With None, everything that differs from the default theme is the look to save.</summary>
    private EditorTheme CaptureWithoutTheme(double width)
    {
        var start = EditorTheme.CreateDefault(string.Empty, "Theme");
        return EditorThemeCapture.Capture(
            start, _annotations, CurrentThemeDefaults(), CurrentCanvasState(), EditorThemeStyling.CanvasStateOf(start), width);
    }

    /// <summary>What's on screen now is the saved look. Older annotations that differ stay as they are.</summary>
    private void Rebaseline(EditorTheme theme, double width)
    {
        var settled = EditorThemeCapture.Settle(theme, _annotations, width);
        SetThemeDefaults(theme, width);
        _themeBaseline = new EditorThemeBaseline(theme, CurrentCanvasState(), width, settled);
        SelectionChanged?.Invoke(this, SelectedAnnotation);
        TextDefaultsChanged?.Invoke(this, EventArgs.Empty);
        RaiseThemeStateChanged();
    }

    private void UndoThemeSwitch(StyleUndoEntry entry, ThemeUndoState state)
    {
        foreach (var (annotation, snapshot) in entry.Annotations)
        {
            if (_annotations.Contains(annotation))
            {
                RestoreAnnotation(annotation, snapshot);
            }
        }

        RestoreThemeUndoState(state);
        SelectedAnnotation = null;
        MarkDirty();
        RaiseThemeRestyled();
    }

    private void RaiseThemeRestyled()
    {
        BackgroundChanged?.Invoke(this, EventArgs.Empty);
        AnnotationsStructureChanged?.Invoke(this, EventArgs.Empty);
        SelectionChanged?.Invoke(this, SelectedAnnotation);
        TextDefaultsChanged?.Invoke(this, EventArgs.Empty);
        RaiseThemeStateChanged();
    }

    /// <summary>Restyling never moves an annotation: text keeps its origin and a number its center.</summary>
    private static void RefreshBoundsAfterRestyle(Annotation annotation)
    {
        switch (annotation.Tool)
        {
            case EditTool.Text:
                UpdateTextBounds(annotation);
                break;
            case EditTool.Pen:
                UpdatePenBounds(annotation);
                break;
            case EditTool.Counter:
                var centerX = annotation.Bounds.X + annotation.Bounds.Width / 2;
                var centerY = annotation.Bounds.Y + annotation.Bounds.Height / 2;
                var radius = CounterRadius(annotation.SizeScale);
                annotation.Bounds = new Windows.Foundation.Rect(centerX - radius, centerY - radius, radius * 2, radius * 2);
                break;
        }
    }

    private void SetThemeDefaults(EditorTheme theme, double width)
    {
        var defaults = EditorThemeStyling.DefaultsOf(theme, width);
        _themeStrokes[EditorThemeGroup.Shape] = (defaults.ShapeColor, defaults.ShapeWidth);
        _themeStrokes[EditorThemeGroup.Line] = (defaults.LineColor, defaults.LineWidth);
        _themeStrokes[EditorThemeGroup.Text] = (defaults.TextColor, defaults.ShapeWidth);
        _themeStrokes[EditorThemeGroup.Number] = (defaults.NumberColor, defaults.ShapeWidth);
        _themeStrokeOwner = EditorThemeStyling.GroupOf(Tool) ?? EditorThemeGroup.Shape;
        (StrokeColor, StrokeThickness) = _themeStrokes[_themeStrokeOwner];

        FillEnabled = defaults.ShapeFill.A > 0;
        if (FillEnabled)
        {
            FillColor = defaults.ShapeFill;
        }

        ShapeEffectsDefault = defaults.ShapeEffects;
        ArrowStyleDefault = defaults.ArrowStyle;
        TextFontFamily = defaults.FontFamily;
        TextFontSize = defaults.FontSize;
        TextBold = defaults.Bold;
        TextItalic = defaults.Italic;
        TextUnderline = defaults.Underline;
        TextBoxStyleDefault = defaults.TextBox;
        NumberTextColor = defaults.NumberTextColor;
        NumberScale = defaults.NumberScale;
    }

    private EditorThemeDefaults CurrentThemeDefaults()
    {
        (Color Color, double Width) Stroke(EditorThemeGroup group) =>
            _themeBaseline is not null && group != _themeStrokeOwner && _themeStrokes.TryGetValue(group, out var stroke)
                ? stroke
                : (StrokeColor, StrokeThickness);

        var shape = Stroke(EditorThemeGroup.Shape);
        var line = Stroke(EditorThemeGroup.Line);
        return new EditorThemeDefaults(
            shape.Color,
            shape.Width,
            FillEnabled ? FillColor : Microsoft.UI.Colors.Transparent,
            ShapeEffectsDefault,
            line.Color,
            line.Width,
            ArrowStyleDefault,
            Stroke(EditorThemeGroup.Text).Color,
            TextFontFamily,
            TextFontSize,
            TextBold,
            TextItalic,
            TextUnderline,
            TextBoxStyleDefault,
            Stroke(EditorThemeGroup.Number).Color,
            NumberTextColor,
            NumberScale);
    }

    private EditorThemeCanvasState CurrentCanvasState() => new(
        BgStyle,
        BgColor,
        BgColor2,
        GradientDirectionDegrees,
        CanvasPadding,
        CanvasCornerRadius,
        CanvasShadow,
        FramePreset,
        HorizontalExportAlignment,
        VerticalExportAlignment);

    private void ApplyCanvasState(EditorThemeCanvasState canvas)
    {
        // Undo can come back to an image background that has since been removed.
        BgStyle = canvas.Background == ExportBackgroundStyle.Image && !HasBackgroundImage
            ? ExportBackgroundStyle.Transparent
            : canvas.Background;
        BgColor = canvas.Color;
        BgColor2 = canvas.SecondaryColor;
        GradientDirectionDegrees = canvas.GradientDirection;
        CanvasPadding = canvas.Padding;
        CanvasCornerRadius = canvas.CornerRadius;
        CanvasShadow = canvas.Shadow;
        FramePreset = canvas.Frame;
        HorizontalExportAlignment = canvas.HorizontalAlignment;
        VerticalExportAlignment = canvas.VerticalAlignment;
    }

    private ThemeUndoState CaptureThemeUndoState() => new(
        _themeBaseline,
        new Dictionary<EditorThemeGroup, (Color Color, double Width)>(_themeStrokes),
        _themeStrokeOwner,
        StrokeColor,
        StrokeThickness,
        FillEnabled,
        FillColor,
        ShapeEffectsDefault,
        ArrowStyleDefault,
        TextFontFamily,
        TextFontSize,
        TextBold,
        TextItalic,
        TextUnderline,
        TextBoxStyleDefault,
        NumberTextColor,
        NumberScale,
        CurrentCanvasState());

    private void RestoreThemeUndoState(ThemeUndoState state)
    {
        _themeBaseline = state.Baseline;
        _themeStrokes.Clear();
        foreach (var (group, stroke) in state.Strokes)
        {
            _themeStrokes[group] = stroke;
        }

        _themeStrokeOwner = state.StrokeOwner;
        StrokeColor = state.StrokeColor;
        StrokeThickness = state.StrokeThickness;
        FillEnabled = state.FillEnabled;
        FillColor = state.FillColor;
        ShapeEffectsDefault = state.ShapeEffects;
        ArrowStyleDefault = state.ArrowStyle;
        TextFontFamily = state.FontFamily;
        TextFontSize = state.FontSize;
        TextBold = state.Bold;
        TextItalic = state.Italic;
        TextUnderline = state.Underline;
        TextBoxStyleDefault = state.TextBox;
        NumberTextColor = state.NumberTextColor;
        NumberScale = state.NumberScale;
        ApplyCanvasState(state.Canvas);
    }
}
