using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using TinyClips.Core.Automation;
using Windows.UI;

namespace TinyClips.Core.Editing.Themes;

/// <summary>A theme was rejected. <see cref="Field"/> is the JSON path of the offending value.</summary>
public sealed class EditorThemeFormatException : FormatException
{
    public EditorThemeFormatException(string field, string message)
        : base($"{field}: {message}")
    {
        Field = field;
    }

    public string Field { get; }
}

public sealed record EditorThemeShape(
    string StrokeColor,
    double StrokeWidth,
    string FillColor,
    string? GradientEndColor,
    double GradientDirection,
    double ShadowBlur,
    string ShadowColor);

public sealed record EditorThemeLine(string Color, double Width, string ArrowStyle);

public sealed record EditorThemeTextBox(
    string BackgroundColor,
    string BorderColor,
    double BorderWidth,
    double Padding,
    double CornerRadius);

public sealed record EditorThemeText(
    string Color,
    string? FontFamily,
    double FontSize,
    bool Bold,
    bool Italic,
    bool Underline,
    EditorThemeTextBox Box);

public sealed record EditorThemeNumber(string BadgeColor, string TextColor, double Size);

public sealed record EditorThemeCanvas(
    bool ApplyOnOpen,
    string Background,
    string Color,
    string SecondaryColor,
    double GradientDirection,
    double Padding,
    double CornerRadius,
    double Shadow,
    string Frame,
    string HorizontalAlignment,
    string VerticalAlignment);

/// <summary>
/// A named look for screenshot annotations and the export canvas, in the format macOS, Windows,
/// and agents exchange. Sizes are editor units; see shared/editor-themes/README.md.
/// </summary>
public sealed record EditorTheme(
    string Id,
    string Name,
    EditorThemeShape Shape,
    EditorThemeLine Line,
    EditorThemeText Text,
    EditorThemeNumber Number,
    EditorThemeCanvas Canvas)
{
    public const int CurrentVersion = 1;
    public const int MaximumIdLength = 64;
    public const int MaximumNameLength = 60;

    public const double MaximumStrokeWidth = 100;
    public const double MaximumShadowBlur = 100;
    public const double MaximumDirection = 360;
    public const double MinimumFontSize = 4;
    public const double MaximumFontSize = 200;
    public const double MaximumBoxBorderWidth = 12;
    public const double MaximumBoxPadding = 48;
    public const double MaximumBoxCornerRadius = 32;
    public const double MinimumNumberSize = 0.25;
    public const double MaximumNumberSize = 4;
    public const double MaximumCanvasPadding = 500;
    public const double MaximumCanvasCornerRadius = 200;
    public const double MaximumCanvasShadow = 200;

    public static readonly string[] ArrowStyles = ["straight", "curvedLeft", "curvedRight"];
    public static readonly string[] Backgrounds = ["transparent", "solid", "gradient"];
    public static readonly string[] Frames = ["original", "square", "landscape4x3", "landscape16x9", "portrait3x4", "portrait9x16"];
    public static readonly string[] HorizontalAlignments = ["left", "center", "right"];
    public static readonly string[] VerticalAlignments = ["top", "center", "bottom"];

    private static readonly EditorThemeShape DefaultShape = new("#FFFF3B30", 4, "#00000000", null, 45, 0, "#59000000");
    private static readonly EditorThemeLine DefaultLine = new("#FFFF3B30", 4, "straight");
    private static readonly EditorThemeTextBox DefaultBox = new("#00000000", "#00000000", 0, 0, 0);
    private static readonly EditorThemeText DefaultText = new("#FFFF3B30", null, 16, false, false, false, DefaultBox);
    private static readonly EditorThemeNumber DefaultNumber = new("#FFFF3B30", "#FFFFFFFF", 1);
    private static readonly EditorThemeCanvas DefaultCanvas = new(
        false, "transparent", "#FFF5F5FA", "#FFD6E6FC", 45, 0, 0, 0, "original", "center", "center");

    /// <summary>A new theme with every value at its documented default.</summary>
    public static EditorTheme CreateDefault(string id, string name) =>
        new(id, name, DefaultShape, DefaultLine, DefaultText, DefaultNumber, DefaultCanvas);

    /// <summary>Parses and validates a theme. Throws <see cref="EditorThemeFormatException"/>.</summary>
    public static EditorTheme Parse(string json)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new EditorThemeFormatException("theme", $"is not valid JSON. {ex.Message}");
        }

        return Parse(node);
    }

    /// <summary>
    /// Parses and validates a theme. A missing or empty <c>id</c> yields an empty
    /// <see cref="Id"/>, which <see cref="EditorThemeStore"/> replaces with a new one.
    /// </summary>
    public static EditorTheme Parse(JsonNode? node)
    {
        // Values built in code don't convert between number types the way parsed values do.
        if (node is null || JsonNode.Parse(node.ToJsonString()) is not JsonObject root)
        {
            throw new EditorThemeFormatException("theme", "must be an object.");
        }

        ReadVersion(root);
        var id = ReadId(root);
        var name = ReadName(root);

        var shape = ReadGroup(root, "shape", "shape");
        var fillColor = ReadColor(shape, "fillColor", "shape", DefaultShape.FillColor);
        var gradientEnd = ReadOptionalColor(shape, "gradientEndColor", "shape", DefaultShape.GradientEndColor);
        if (gradientEnd is not null && ColorOf(fillColor).A == 0)
        {
            throw new EditorThemeFormatException("shape.gradientEndColor", "needs a visible shape.fillColor.");
        }

        var line = ReadGroup(root, "line", "line");
        var text = ReadGroup(root, "text", "text");
        var box = ReadGroup(text, "box", "text.box");
        var number = ReadGroup(root, "number", "number");
        var canvas = ReadGroup(root, "canvas", "canvas");

        return new EditorTheme(
            id,
            name,
            new EditorThemeShape(
                ReadColor(shape, "strokeColor", "shape", DefaultShape.StrokeColor),
                ReadNumber(shape, "strokeWidth", "shape", DefaultShape.StrokeWidth, 0, MaximumStrokeWidth),
                fillColor,
                gradientEnd,
                ReadNumber(shape, "gradientDirection", "shape", DefaultShape.GradientDirection, 0, MaximumDirection),
                ReadNumber(shape, "shadowBlur", "shape", DefaultShape.ShadowBlur, 0, MaximumShadowBlur),
                ReadColor(shape, "shadowColor", "shape", DefaultShape.ShadowColor)),
            new EditorThemeLine(
                ReadColor(line, "color", "line", DefaultLine.Color),
                ReadNumber(line, "width", "line", DefaultLine.Width, 0, MaximumStrokeWidth),
                ReadChoice(line, "arrowStyle", "line", DefaultLine.ArrowStyle, ArrowStyles)),
            new EditorThemeText(
                ReadColor(text, "color", "text", DefaultText.Color),
                ReadFontFamily(text),
                ReadNumber(text, "fontSize", "text", DefaultText.FontSize, MinimumFontSize, MaximumFontSize),
                ReadBool(text, "bold", "text", DefaultText.Bold),
                ReadBool(text, "italic", "text", DefaultText.Italic),
                ReadBool(text, "underline", "text", DefaultText.Underline),
                new EditorThemeTextBox(
                    ReadColor(box, "backgroundColor", "text.box", DefaultBox.BackgroundColor),
                    ReadColor(box, "borderColor", "text.box", DefaultBox.BorderColor),
                    ReadNumber(box, "borderWidth", "text.box", DefaultBox.BorderWidth, 0, MaximumBoxBorderWidth),
                    ReadNumber(box, "padding", "text.box", DefaultBox.Padding, 0, MaximumBoxPadding),
                    ReadNumber(box, "cornerRadius", "text.box", DefaultBox.CornerRadius, 0, MaximumBoxCornerRadius))),
            new EditorThemeNumber(
                ReadColor(number, "badgeColor", "number", DefaultNumber.BadgeColor),
                ReadColor(number, "textColor", "number", DefaultNumber.TextColor),
                ReadNumber(number, "size", "number", DefaultNumber.Size, MinimumNumberSize, MaximumNumberSize)),
            new EditorThemeCanvas(
                ReadBool(canvas, "applyOnOpen", "canvas", DefaultCanvas.ApplyOnOpen),
                ReadChoice(canvas, "background", "canvas", DefaultCanvas.Background, Backgrounds),
                ReadColor(canvas, "color", "canvas", DefaultCanvas.Color),
                ReadColor(canvas, "secondaryColor", "canvas", DefaultCanvas.SecondaryColor),
                ReadNumber(canvas, "gradientDirection", "canvas", DefaultCanvas.GradientDirection, 0, MaximumDirection),
                ReadNumber(canvas, "padding", "canvas", DefaultCanvas.Padding, 0, MaximumCanvasPadding),
                ReadNumber(canvas, "cornerRadius", "canvas", DefaultCanvas.CornerRadius, 0, MaximumCanvasCornerRadius),
                ReadNumber(canvas, "shadow", "canvas", DefaultCanvas.Shadow, 0, MaximumCanvasShadow),
                ReadChoice(canvas, "frame", "canvas", DefaultCanvas.Frame, Frames),
                ReadChoice(canvas, "horizontalAlignment", "canvas", DefaultCanvas.HorizontalAlignment, HorizontalAlignments),
                ReadChoice(canvas, "verticalAlignment", "canvas", DefaultCanvas.VerticalAlignment, VerticalAlignments)));
    }

    public JsonObject ToJson() => new()
    {
        ["version"] = CurrentVersion,
        ["id"] = Id,
        ["name"] = Name,
        ["shape"] = new JsonObject
        {
            ["strokeColor"] = Shape.StrokeColor,
            ["strokeWidth"] = Shape.StrokeWidth,
            ["fillColor"] = Shape.FillColor,
            ["gradientEndColor"] = Shape.GradientEndColor,
            ["gradientDirection"] = Shape.GradientDirection,
            ["shadowBlur"] = Shape.ShadowBlur,
            ["shadowColor"] = Shape.ShadowColor,
        },
        ["line"] = new JsonObject
        {
            ["color"] = Line.Color,
            ["width"] = Line.Width,
            ["arrowStyle"] = Line.ArrowStyle,
        },
        ["text"] = new JsonObject
        {
            ["color"] = Text.Color,
            ["fontFamily"] = Text.FontFamily,
            ["fontSize"] = Text.FontSize,
            ["bold"] = Text.Bold,
            ["italic"] = Text.Italic,
            ["underline"] = Text.Underline,
            ["box"] = new JsonObject
            {
                ["backgroundColor"] = Text.Box.BackgroundColor,
                ["borderColor"] = Text.Box.BorderColor,
                ["borderWidth"] = Text.Box.BorderWidth,
                ["padding"] = Text.Box.Padding,
                ["cornerRadius"] = Text.Box.CornerRadius,
            },
        },
        ["number"] = new JsonObject
        {
            ["badgeColor"] = Number.BadgeColor,
            ["textColor"] = Number.TextColor,
            ["size"] = Number.Size,
        },
        ["canvas"] = new JsonObject
        {
            ["applyOnOpen"] = Canvas.ApplyOnOpen,
            ["background"] = Canvas.Background,
            ["color"] = Canvas.Color,
            ["secondaryColor"] = Canvas.SecondaryColor,
            ["gradientDirection"] = Canvas.GradientDirection,
            ["padding"] = Canvas.Padding,
            ["cornerRadius"] = Canvas.CornerRadius,
            ["shadow"] = Canvas.Shadow,
            ["frame"] = Canvas.Frame,
            ["horizontalAlignment"] = Canvas.HorizontalAlignment,
            ["verticalAlignment"] = Canvas.VerticalAlignment,
        },
    };

    /// <summary>The same theme with every size rounded the way Modified compares them.</summary>
    public EditorTheme Rounded() => this with
    {
        Shape = Shape with
        {
            StrokeWidth = Round(Shape.StrokeWidth),
            GradientDirection = Round(Shape.GradientDirection),
            ShadowBlur = Round(Shape.ShadowBlur),
        },
        Line = Line with { Width = Round(Line.Width) },
        Text = Text with
        {
            FontSize = Round(Text.FontSize),
            Box = Text.Box with
            {
                BorderWidth = Round(Text.Box.BorderWidth),
                Padding = Round(Text.Box.Padding),
                CornerRadius = Round(Text.Box.CornerRadius),
            },
        },
        Number = Number with { Size = Round(Number.Size) },
        Canvas = Canvas with
        {
            GradientDirection = Round(Canvas.GradientDirection),
            Padding = Round(Canvas.Padding),
            CornerRadius = Round(Canvas.CornerRadius),
            Shadow = Round(Canvas.Shadow),
        },
    };

    internal static double Round(double value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>The color a validated theme value stands for.</summary>
    public static Color ColorOf(string hex) =>
        AutomationAnnotationParser.TryParseColor(hex, out var color)
            ? color
            : throw new FormatException($"{hex} is not a color.");

    /// <summary>The form both apps store and compare: uppercase <c>#AARRGGBB</c>.</summary>
    public static string HexOf(Color color) =>
        string.Create(CultureInfo.InvariantCulture, $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}");

    private static void ReadVersion(JsonObject root)
    {
        if (!root.TryGetPropertyValue("version", out var node))
        {
            return;
        }

        if (node is not JsonValue value
            || value.GetValueKind() != JsonValueKind.Number
            || !value.TryGetValue<double>(out var version)
            || version != CurrentVersion)
        {
            throw new EditorThemeFormatException("version", $"must be {CurrentVersion}. This theme may need a newer version of Tiny Clips.");
        }
    }

    private static string ReadId(JsonObject root)
    {
        if (root["id"] is null)
        {
            return string.Empty;
        }

        var id = ReadString(root["id"], "id");
        if (id.Length > MaximumIdLength || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
        {
            throw new EditorThemeFormatException("id", $"must be 1 to {MaximumIdLength} letters, digits, or hyphens.");
        }

        return id;
    }

    private static string ReadName(JsonObject root)
    {
        var name = root["name"] is null ? string.Empty : ReadString(root["name"], "name").Trim();
        if (name.Length == 0 || new StringInfo(name).LengthInTextElements > MaximumNameLength)
        {
            throw new EditorThemeFormatException("name", $"must be 1 to {MaximumNameLength} characters.");
        }

        return name;
    }

    private static string? ReadFontFamily(JsonObject text)
    {
        if (text["fontFamily"] is null)
        {
            return DefaultText.FontFamily;
        }

        var family = ReadString(text["fontFamily"], "text.fontFamily").Trim();
        return family.Length == 0 ? null : family;
    }

    private static JsonObject ReadGroup(JsonObject parent, string key, string path)
    {
        if (!parent.TryGetPropertyValue(key, out var node))
        {
            return [];
        }

        return node as JsonObject ?? throw new EditorThemeFormatException(path, "must be an object.");
    }

    private static string ReadColor(JsonObject group, string key, string path, string fallback) =>
        group.TryGetPropertyValue(key, out var node) ? ParseColor(node, $"{path}.{key}") : fallback;

    private static string? ReadOptionalColor(JsonObject group, string key, string path, string? fallback)
    {
        if (!group.TryGetPropertyValue(key, out var node))
        {
            return fallback;
        }

        return node is null ? null : ParseColor(node, $"{path}.{key}");
    }

    private static string ParseColor(JsonNode? node, string field)
    {
        var raw = node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
        return AutomationAnnotationParser.TryParseColor(raw, out var color)
            ? HexOf(color)
            : throw new EditorThemeFormatException(field, "must be #RRGGBB or #AARRGGBB.");
    }

    private static double ReadNumber(JsonObject group, string key, string path, double fallback, double minimum, double maximum)
    {
        var field = $"{path}.{key}";
        if (!group.TryGetPropertyValue(key, out var node))
        {
            return fallback;
        }

        if (node is not JsonValue value
            || value.GetValueKind() != JsonValueKind.Number
            || !value.TryGetValue<double>(out var number)
            || !double.IsFinite(number))
        {
            throw new EditorThemeFormatException(field, "must be a number.");
        }

        if (number < minimum || number > maximum)
        {
            throw new EditorThemeFormatException(field, FormattableString.Invariant($"must be between {minimum} and {maximum}."));
        }

        return number;
    }

    private static bool ReadBool(JsonObject group, string key, string path, bool fallback)
    {
        if (!group.TryGetPropertyValue(key, out var node))
        {
            return fallback;
        }

        return node?.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new EditorThemeFormatException($"{path}.{key}", "must be true or false."),
        };
    }

    private static string ReadChoice(JsonObject group, string key, string path, string fallback, string[] choices)
    {
        if (!group.TryGetPropertyValue(key, out var node))
        {
            return fallback;
        }

        var choice = node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
        return choice is not null && choices.Contains(choice, StringComparer.Ordinal)
            ? choice
            : throw new EditorThemeFormatException($"{path}.{key}", $"must be one of {string.Join(", ", choices)}.");
    }

    private static string ReadString(JsonNode? node, string field) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : throw new EditorThemeFormatException(field, "must be a string.");
}
