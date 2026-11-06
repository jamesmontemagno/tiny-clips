using System.Numerics;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TinyClips.App.Models.Studio;
using TinyClips.Core.Studio;
using Windows.UI;

namespace TinyClips.App.Controls.Studio;

/// <summary>
/// One look of the captions in the Captions panel: a sample caption drawn as the video draws that
/// look, from its panel, shadow, outline and letters (<see cref="StudioCaptionStyle"/>), over a
/// dark picture, with the look's name under it. The item of the list around it carries the name
/// for a screen reader, so the tile itself says nothing.
/// </summary>
public sealed partial class StudioCaptionLookTile : StackPanel
{
    private const string Sample = "Your captions";
    private const string Font = "Segoe UI";
    private const double PictureHeight = 52;
    private const float Inset = 8;

    // A dark picture in either theme, as a video mostly is behind its captions.
    private static readonly Color PictureTop = Color.FromArgb(0xFF, 0x4A, 0x55, 0x68);
    private static readonly Color PictureBottom = Color.FromArgb(0xFF, 0x1B, 0x1F, 0x27);

    // Grey and see-through, so the edge shows in either theme: the Studio window can have a theme
    // of its own, which the app's resources do not follow.
    private static readonly Color FrameEdge = Color.FromArgb(0x59, 0x80, 0x80, 0x80);

    private readonly StudioCaptionAppearance _appearance;
    private readonly Border _frame;
    private CanvasControl? _canvas;

    public StudioCaptionLookTile(StudioCaptionAppearance appearance)
    {
        _appearance = appearance;
        Spacing = 4;
        _frame = new Border
        {
            Height = PictureHeight,
            CornerRadius = new CornerRadius(4),
            BorderBrush = new SolidColorBrush(FrameEdge),
            BorderThickness = new Thickness(1),
        };
        AutomationProperties.SetAccessibilityView(_frame, AccessibilityView.Raw);
        Children.Add(_frame);

        var name = new TextBlock
        {
            Text = StudioCaptionPanelText.GetLookName(appearance),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        };
        AutomationProperties.SetAccessibilityView(name, AccessibilityView.Raw);
        Children.Add(name);

        // Win2D lets go of its device only when the canvas leaves the tree, so the canvas is made
        // for as long as the tile is loaded.
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_canvas is null)
        {
            _canvas = new CanvasControl();
            _canvas.Draw += OnDraw;
            _frame.Child = _canvas;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_canvas is not null)
        {
            _canvas.Draw -= OnDraw;
            _canvas.RemoveFromVisualTree();
            _frame.Child = null;
            _canvas = null;
        }
    }

    /// <summary>From the back, as the video draws a caption: the panel, the shadow, the outline, and the letters.</summary>
    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var width = (float)sender.ActualWidth;
        var height = (float)sender.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var session = args.DrawingSession;
        using (var picture = new CanvasLinearGradientBrush(sender, PictureTop, PictureBottom)
        {
            StartPoint = Vector2.Zero,
            EndPoint = new Vector2(0, height),
        })
        {
            session.FillRectangle(0, 0, width, height, picture);
        }

        // The look on a canvas whose short side is the unit's 288, so a unit is a pixel here.
        var overlay = new StudioCaptionOverlay { Appearance = _appearance, Size = StudioCaptionSize.Medium };
        var style = StudioCaptionStyle.For(overlay, StudioCaptionStyle.UnitsInShortSide, StudioCaptionStyle.UnitsInShortSide);
        using var format = new CanvasTextFormat
        {
            FontFamily = Font,
            FontSize = (float)style.TextHeight,
            FontWeight = style.IsBold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
            WordWrapping = CanvasWordWrapping.NoWrap,
        };
        using var layout = new CanvasTextLayout(sender, Sample, format, 1000, 1000);
        var bounds = layout.LayoutBounds;
        var reach = (float)style.Reach;

        // Smaller where the tile is too narrow for the sample, and never larger than the video draws it.
        var scale = Math.Min(1f, Math.Min((width - (Inset * 2)) / (float)(bounds.Width + (reach * 2)), (height - Inset) / (float)(bounds.Height + (reach * 2))));
        var origin = new Vector2(
            (float)(((width / scale) - bounds.Width) / 2 - bounds.X),
            (float)(((height / scale) - bounds.Height) / 2 - bounds.Y));
        session.Transform = Matrix3x2.CreateScale(scale);
        try
        {
            if (style.PanelReach > 0)
            {
                var panelReach = (float)style.PanelReach;
                session.FillRectangle(
                    origin.X + (float)bounds.X - panelReach,
                    origin.Y + (float)bounds.Y - panelReach,
                    (float)bounds.Width + (panelReach * 2),
                    (float)bounds.Height + (panelReach * 2),
                    ToColor(style.Panel));
            }

            if (style.OutlineWidth > 0 || style.ShadowOffset > 0)
            {
                using var letters = CanvasGeometry.CreateText(layout);
                using var stroke = new CanvasStrokeStyle
                {
                    StartCap = CanvasCapStyle.Round,
                    EndCap = CanvasCapStyle.Round,
                    DashCap = CanvasCapStyle.Round,
                    LineJoin = CanvasLineJoin.Round,
                };
                var strokeWidth = (float)(style.OutlineWidth * 2);

                // A paint that is not solid goes on as one sheet, so overlapping strokes do not darken.
                if (style.ShadowOffset > 0)
                {
                    var shadow = ToColor(style.Shadow with { Opacity = 1 });
                    var at = origin + new Vector2((float)style.ShadowOffset);
                    using (session.CreateLayer((float)style.Shadow.Opacity))
                    {
                        if (strokeWidth > 0)
                        {
                            session.DrawGeometry(letters, at, shadow, strokeWidth, stroke);
                        }

                        session.FillGeometry(letters, at, shadow);
                    }
                }

                if (style.OutlineWidth > 0)
                {
                    using (session.CreateLayer((float)style.Outline.Opacity))
                    {
                        session.DrawGeometry(letters, origin, ToColor(style.Outline with { Opacity = 1 }), strokeWidth, stroke);
                    }
                }
            }

            session.DrawTextLayout(layout, origin, ToColor(style.Text));
        }
        finally
        {
            session.Transform = Matrix3x2.Identity;
        }
    }

    private static Color ToColor(StudioCaptionPaint paint) =>
        Color.FromArgb((byte)Math.Round(Math.Clamp(paint.Opacity, 0, 1) * 255), paint.Red, paint.Green, paint.Blue);
}
