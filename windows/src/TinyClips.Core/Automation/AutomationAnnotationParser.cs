using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using TinyClips.Core.Editing;
using TinyClips.Core.Editing.Themes;
using Windows.Foundation;
using Windows.UI;

namespace TinyClips.Core.Automation;

/// <summary>
/// Converts <c>editor.annotate</c> annotation objects into editor annotations in source-image
/// pixels. Every malformed or out-of-bounds input is rejected with <c>invalid_params</c>; nothing
/// is clamped silently.
/// </summary>
public static class AutomationAnnotationParser
{
    public const int MaximumAnnotations = 500;
    public const int MaximumTextLength = 2000;
    public const int MaximumPoints = 10_000;
    public const double MaximumLineWidth = 200;
    public const double MaximumFontSize = 1000;
    public const double MaximumShadowBlur = 500;

    public const double DefaultLineWidth = 6;
    public const double DefaultFontSize = 28;
    public static readonly Color DefaultColor = Color.FromArgb(255, 255, 0, 0);
    public static readonly Color DefaultNumberTextColor = Color.FromArgb(255, 255, 255, 255);

    private static readonly HashSet<string> AllowedKeys = new(StringComparer.Ordinal)
    {
        "type", "bounds", "points", "text", "color", "textColor", "fillColor", "lineWidth",
        "fontSize", "redactionLevel", "redactionStyle", "emoji",
        "fillEndColor", "fillDirection", "shadowBlur", "shadowColor",
    };

    private static readonly string[] RectKeys = ["x", "y", "width", "height"];
    private static readonly string[] PointKeys = ["x", "y"];

    /// <summary>Checks shape only (types, enums, colors, counts) before the image is decoded.</summary>
    public static void ValidateStructure(JsonNode? annotations) => ParseCore(annotations, null, null);

    /// <summary>
    /// Parses and validates annotations against the source image size. With a theme, values an
    /// annotation leaves out come from the theme; values it sets win.
    /// </summary>
    public static IReadOnlyList<ScreenshotAnnotation> Parse(JsonNode? annotations, int imageWidth, int imageHeight, EditorTheme? theme = null)
    {
        if (imageWidth <= 0 || imageHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(imageWidth), "Image size must be positive.");
        }

        return ParseCore(annotations, (imageWidth, imageHeight), theme);
    }

    public static bool TryParseColor(string? value, out Color color)
    {
        color = default;
        if (value is null || value.Length is not (7 or 9) || value[0] != '#')
        {
            return false;
        }

        if (!uint.TryParse(value.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var raw))
        {
            return false;
        }

        if (value.Length == 7)
        {
            color = Color.FromArgb(255, (byte)(raw >> 16), (byte)(raw >> 8), (byte)raw);
        }
        else
        {
            color = Color.FromArgb((byte)(raw >> 24), (byte)(raw >> 16), (byte)(raw >> 8), (byte)raw);
        }

        return true;
    }

    private static IReadOnlyList<ScreenshotAnnotation> ParseCore(JsonNode? node, (int Width, int Height)? image, EditorTheme? theme)
    {
        if (node is not JsonArray array)
        {
            throw Invalid("annotations must be an array.");
        }

        if (array.Count == 0)
        {
            throw Invalid("annotations must contain at least one annotation.");
        }

        if (array.Count > MaximumAnnotations)
        {
            throw Invalid($"annotations can contain at most {MaximumAnnotations} items.");
        }

        var result = new List<ScreenshotAnnotation>(array.Count);
        var nextNumber = 1;
        for (var i = 0; i < array.Count; i++)
        {
            var item = ParseOne(array[i], $"annotations[{i}]", image, ref nextNumber);
            if (theme is not null && image is { } size)
            {
                ApplyTheme(item, (JsonObject)array[i]!, theme, size.Width);
            }

            result.Add(item);
        }

        return result;
    }

    /// <summary>Styles an annotation from its own theme section, then puts back what its JSON set.</summary>
    private static void ApplyTheme(ScreenshotAnnotation annotation, JsonObject obj, EditorTheme theme, int imageWidth)
    {
        var color = annotation.Color;
        var thickness = annotation.Thickness;
        var fill = annotation.FillColor;
        var fontSize = annotation.FontSize;
        var textColor = annotation.TextColor;
        var textBox = annotation.TextBoxStyle;
        var effects = annotation.ShapeEffects;
        EditorThemeStyling.Apply(annotation, theme, imageWidth);

        bool Has(string key) => obj[key] is not null;
        switch (annotation.Tool)
        {
            case EditTool.Rectangle:
            case EditTool.Ellipse:
                if (Has("color")) annotation.Color = color;
                if (Has("lineWidth")) annotation.Thickness = thickness;
                if (Has("fillColor")) annotation.FillColor = fill;
                var themed = annotation.ShapeEffects;
                if (Has("fillEndColor"))
                {
                    themed = themed with { IsGradientFill = true, GradientEndColor = effects.GradientEndColor };
                }

                if (Has("fillDirection")) themed = themed with { GradientDirectionDegrees = effects.GradientDirectionDegrees };
                if (Has("shadowBlur"))
                {
                    themed = themed with
                    {
                        HasShadow = effects.HasShadow,
                        ShadowBlur = effects.ShadowBlur,
                        ShadowColor = Has("shadowColor") ? effects.ShadowColor : themed.ShadowColor,
                    };
                }

                annotation.ShapeEffects = themed;
                break;

            case EditTool.Arrow:
            case EditTool.Line:
            case EditTool.Pen:
                if (Has("color")) annotation.Color = color;
                if (Has("lineWidth")) annotation.Thickness = thickness;
                break;

            case EditTool.Text:
                if (Has("textColor") || Has("color")) annotation.Color = color;
                if (Has("fontSize")) annotation.FontSize = fontSize;
                if (Has("fillColor")) annotation.TextBoxStyle = textBox;
                break;

            case EditTool.Counter:
                if (Has("color")) annotation.Color = color;
                if (Has("textColor")) annotation.TextColor = textColor;
                break;
        }
    }

    private static ScreenshotAnnotation ParseOne(JsonNode? node, string path, (int Width, int Height)? image, ref int nextNumber)
    {
        if (node is not JsonObject obj)
        {
            throw Invalid($"{path} must be an object.");
        }

        foreach (var (key, _) in obj)
        {
            if (!AllowedKeys.Contains(key))
            {
                throw Invalid($"{path} has an unknown property {key}.");
            }
        }

        var typeName = RequiredString(obj, "type", path);
        var tool = typeName switch
        {
            "rectangle" => EditTool.Rectangle,
            "ellipse" => EditTool.Ellipse,
            "arrow" => EditTool.Arrow,
            "line" => EditTool.Line,
            "pen" => EditTool.Pen,
            "text" => EditTool.Text,
            "number" => EditTool.Counter,
            "emoji" => EditTool.Emoji,
            "redact" => EditTool.Redact,
            _ => throw Invalid($"{path}.type must be one of rectangle, ellipse, arrow, line, pen, text, number, emoji, redact."),
        };

        var bounds = ParseBounds(obj["bounds"], $"{path}.bounds");
        var points = ParsePoints(obj["points"], $"{path}.points");
        var color = OptionalColor(obj, "color", path) ?? DefaultColor;
        var textColor = OptionalColor(obj, "textColor", path);
        var fillColor = OptionalColor(obj, "fillColor", path);
        var lineWidth = OptionalNumber(obj, "lineWidth", path, 0, MaximumLineWidth) ?? DefaultLineWidth;
        var fontSize = OptionalNumber(obj, "fontSize", path, 1, MaximumFontSize);
        var text = OptionalString(obj, "text", path);
        var emoji = OptionalString(obj, "emoji", path);
        var level = OptionalString(obj, "redactionLevel", path) switch
        {
            null or "medium" => RedactionLevel.Medium,
            "light" => RedactionLevel.Light,
            "heavy" => RedactionLevel.Heavy,
            _ => throw Invalid($"{path}.redactionLevel must be light, medium, or heavy."),
        };
        var style = OptionalString(obj, "redactionStyle", path) switch
        {
            null or "blur" => RedactionStyle.Blur,
            "pixelate" => RedactionStyle.Pixelate,
            "solid" => RedactionStyle.Solid,
            _ => throw Invalid($"{path}.redactionStyle must be blur, pixelate, or solid."),
        };

        if (text is { Length: > MaximumTextLength })
        {
            throw Invalid($"{path}.text can be at most {MaximumTextLength} characters.");
        }

        var annotation = new ScreenshotAnnotation
        {
            Tool = tool,
            Bounds = bounds,
            Color = color,
            Thickness = lineWidth,
            ShapeEffects = ParseShapeEffects(obj, tool, fillColor, path),
        };

        switch (tool)
        {
            case EditTool.Rectangle:
            case EditTool.Ellipse:
                RequireArea(bounds, path);
                if (fillColor is { } fill)
                {
                    annotation.FillColor = fill;
                }

                break;

            case EditTool.Redact:
                RequireArea(bounds, path);
                annotation.Redaction = level;
                annotation.RedactStyle = style;
                break;

            case EditTool.Line:
            case EditTool.Arrow:
                if (points.Count is > 0 and < 2)
                {
                    throw Invalid($"{path}.points needs a start and end point.");
                }

                var (start, end) = points.Count >= 2
                    ? (points[0], points[^1])
                    : (new Vector2((float)bounds.X, (float)bounds.Y),
                        new Vector2((float)(bounds.X + bounds.Width), (float)(bounds.Y + bounds.Height)));
                if (start == end)
                {
                    throw Invalid($"{path} needs distinct start and end points (use points, or bounds with a non-zero size).");
                }

                annotation.Points.Add(start);
                annotation.Points.Add(end);
                break;

            case EditTool.Pen:
                if (points.Count < 2)
                {
                    throw Invalid($"{path}.points needs at least two points for a pen stroke.");
                }

                annotation.Points.AddRange(points);
                break;

            case EditTool.Text:
                if (string.IsNullOrWhiteSpace(text))
                {
                    throw Invalid($"{path}.text is required for text annotations.");
                }

                annotation.Text = text;
                annotation.Color = textColor ?? color;
                annotation.FontSize = fontSize ?? DefaultFontSize;
                if (fillColor is { A: > 0 } background)
                {
                    annotation.TextBoxStyle = new TextBoxStyle(TextBoxPreset.Custom, background, Color.FromArgb(0, 0, 0, 0), 0, 8, 4);
                }

                break;

            case EditTool.Counter:
                RequireArea(bounds, path);
                if (text is null)
                {
                    annotation.Number = nextNumber;
                }
                else if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number <= 9999)
                {
                    annotation.Number = number;
                }
                else
                {
                    throw Invalid($"{path}.text must be a whole number from 0 to 9999 for number annotations.");
                }

                // Callouts are circles; center the largest one that fits the requested bounds.
                var side = Math.Min(bounds.Width, bounds.Height);
                annotation.Bounds = new Rect(
                    bounds.X + (bounds.Width - side) / 2,
                    bounds.Y + (bounds.Height - side) / 2,
                    side,
                    side);
                nextNumber = annotation.Number + 1;
                annotation.TextColor = textColor ?? DefaultNumberTextColor;
                break;

            case EditTool.Emoji:
                RequireArea(bounds, path);
                var glyph = emoji ?? text;
                if (string.IsNullOrWhiteSpace(glyph) || glyph.Length > 32)
                {
                    throw Invalid($"{path}.emoji is required for emoji annotations.");
                }

                annotation.Text = glyph;
                break;
        }

        if (image is { } size)
        {
            EnsureInside(bounds, size, $"{path}.bounds");
            for (var p = 0; p < points.Count; p++)
            {
                var point = points[p];
                if (point.X < 0 || point.Y < 0 || point.X > size.Width || point.Y > size.Height)
                {
                    throw Invalid($"{path}.points[{p}] is outside the {size.Width} × {size.Height} image.");
                }
            }
        }

        return annotation;
    }

    private static ShapeEffects ParseShapeEffects(JsonObject obj, EditTool tool, Color? fillColor, string path)
    {
        var effects = ShapeEffects.Default;
        string[] keys = ["fillEndColor", "fillDirection", "shadowBlur", "shadowColor"];
        if (!tool.SupportsShapeEffects())
        {
            foreach (var key in keys)
            {
                if (obj[key] is not null)
                {
                    throw Invalid($"{path}.{key} is only used by rectangle and ellipse.");
                }
            }

            return effects;
        }

        if (OptionalColor(obj, "fillEndColor", path) is { } endColor)
        {
            if (fillColor is not { A: > 0 })
            {
                throw Invalid($"{path}.fillEndColor needs a visible fillColor to start from.");
            }

            effects = effects with { IsGradientFill = true, GradientEndColor = endColor };
        }

        if (OptionalNumber(obj, "fillDirection", path, 0, 360) is { } direction)
        {
            if (!effects.IsGradientFill)
            {
                throw Invalid($"{path}.fillDirection needs fillEndColor.");
            }

            effects = effects with { GradientDirectionDegrees = direction };
        }

        var shadowColor = OptionalColor(obj, "shadowColor", path);
        if (OptionalNumber(obj, "shadowBlur", path, 0, MaximumShadowBlur) is { } blur)
        {
            effects = effects with
            {
                HasShadow = blur > 0,
                ShadowBlur = blur,
                ShadowColor = shadowColor ?? effects.ShadowColor,
            };
        }
        else if (shadowColor is not null)
        {
            throw Invalid($"{path}.shadowColor needs shadowBlur.");
        }

        return effects;
    }

    private static void RequireArea(Rect bounds, string path)
    {
        if (bounds.Width < 1 || bounds.Height < 1)
        {
            throw Invalid($"{path}.bounds width and height must be at least 1.");
        }
    }

    private static void EnsureInside(Rect bounds, (int Width, int Height) image, string path)
    {
        if (bounds.X < 0 || bounds.Y < 0 || bounds.X + bounds.Width > image.Width || bounds.Y + bounds.Height > image.Height)
        {
            throw Invalid($"{path} is outside the {image.Width} × {image.Height} image.");
        }
    }

    private static Rect ParseBounds(JsonNode? node, string path)
    {
        if (node is not JsonObject obj)
        {
            throw Invalid($"{path} is required and must be an object with x, y, width, and height.");
        }

        foreach (var (key, _) in obj)
        {
            if (!RectKeys.Contains(key, StringComparer.Ordinal))
            {
                throw Invalid($"{path} has an unknown property {key}.");
            }
        }

        var x = RequiredNumber(obj, "x", path);
        var y = RequiredNumber(obj, "y", path);
        var width = RequiredNumber(obj, "width", path);
        var height = RequiredNumber(obj, "height", path);
        if (width < 0 || height < 0)
        {
            throw Invalid($"{path} width and height must not be negative.");
        }

        return new Rect(x, y, width, height);
    }

    private static List<Vector2> ParsePoints(JsonNode? node, string path)
    {
        var points = new List<Vector2>();
        if (node is null)
        {
            return points;
        }

        if (node is not JsonArray array)
        {
            throw Invalid($"{path} must be an array of {{x, y}} points.");
        }

        if (array.Count > MaximumPoints)
        {
            throw Invalid($"{path} can contain at most {MaximumPoints} points.");
        }

        for (var i = 0; i < array.Count; i++)
        {
            var itemPath = $"{path}[{i}]";
            if (array[i] is not JsonObject point)
            {
                throw Invalid($"{itemPath} must be an object with x and y.");
            }

            foreach (var (key, _) in point)
            {
                if (!PointKeys.Contains(key, StringComparer.Ordinal))
                {
                    throw Invalid($"{itemPath} has an unknown property {key}.");
                }
            }

            points.Add(new Vector2((float)RequiredNumber(point, "x", itemPath), (float)RequiredNumber(point, "y", itemPath)));
        }

        return points;
    }

    private static double RequiredNumber(JsonObject obj, string name, string path)
    {
        if (obj[name] is not JsonValue value || value.GetValueKind() != JsonValueKind.Number
            || !value.TryGetValue<double>(out var d) || !double.IsFinite(d))
        {
            throw Invalid($"{path}.{name} must be a finite number.");
        }

        return d;
    }

    private static double? OptionalNumber(JsonObject obj, string name, string path, double minimum, double maximum)
    {
        if (obj[name] is null)
        {
            return null;
        }

        var d = RequiredNumber(obj, name, path);
        if (d < minimum || d > maximum)
        {
            throw Invalid($"{path}.{name} must be between {minimum.ToString(CultureInfo.InvariantCulture)} and {maximum.ToString(CultureInfo.InvariantCulture)}.");
        }

        return d;
    }

    private static Color? OptionalColor(JsonObject obj, string name, string path)
    {
        var raw = OptionalString(obj, name, path);
        if (raw is null)
        {
            return null;
        }

        return TryParseColor(raw, out var color)
            ? color
            : throw Invalid($"{path}.{name} must be #RRGGBB or #AARRGGBB.");
    }

    private static string RequiredString(JsonObject obj, string name, string path) =>
        OptionalString(obj, name, path) ?? throw Invalid($"{path}.{name} is required.");

    private static string? OptionalString(JsonObject obj, string name, string path)
    {
        var node = obj[name];
        if (node is null)
        {
            return null;
        }

        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.String)
        {
            throw Invalid($"{path}.{name} must be a string.");
        }

        return value.GetValue<string>();
    }

    private static AutomationException Invalid(string message) => new(AutomationErrorCodes.InvalidParams, message);
}
