using System.Text.Json;

namespace TinyClips.Core.Studio;

/// <summary>UI-only interchange, not a project schema or automation command.</summary>
public sealed record StudioAnnotationTransfer(StudioAnnotation Annotation, double OutputDurationSeconds);

public static class StudioAnnotationClipboard
{
    public const string Format = "com.tinyclips.studio.annotation.v1";
    public const int MaximumPayloadBytes = 1024 * 1024;

    public static bool IsSupportedKind(string? kind) => kind is
        StudioAnnotationKinds.Arrow or StudioAnnotationKinds.Callout or StudioAnnotationKinds.Text
        or StudioAnnotationKinds.Rectangle or StudioAnnotationKinds.Ellipse
        or StudioAnnotationKinds.Redact or StudioAnnotationKinds.Emoji;

    public static byte[] Encode(StudioAnnotationTransfer transfer)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        if (transfer.Annotation is not { } source || !Enum.IsDefined(source.Attach)
            || !Enum.IsDefined(source.Style) || !Enum.IsDefined(source.Size)
            || !Enum.IsDefined(source.RedactStyle) || !Enum.IsDefined(source.RedactLevel))
        {
            throw Invalid("Unsupported annotation enum value.");
        }
        var annotation = JsonSerializer.SerializeToElement(transfer.Annotation, StudioJsonContext.Default.StudioAnnotation);
        ValidateAnnotation(annotation);
        if (!double.IsFinite(transfer.OutputDurationSeconds) || transfer.OutputDurationSeconds <= 0)
        {
            throw Invalid("The annotation has no remaining playback duration.");
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 1);
            writer.WritePropertyName("annotation");
            annotation.WriteTo(writer);
            writer.WriteNumber("outputDurationSeconds", transfer.OutputDurationSeconds);
            writer.WriteEndObject();
        }

        if (stream.Length > MaximumPayloadBytes)
        {
            throw Invalid("The annotation clipboard payload exceeds 1 MiB.");
        }

        return stream.ToArray();
    }

    public static StudioAnnotationTransfer Decode(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length > MaximumPayloadBytes)
        {
            throw Invalid("The annotation clipboard payload exceeds 1 MiB.");
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            RequireObject(root);
            var version = Required(root, "version");
            if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var value) || value != 1)
            {
                throw Invalid("Unsupported annotation clipboard version.");
            }

            var duration = Number(root, "outputDurationSeconds");
            if (duration <= 0)
            {
                throw Invalid("Invalid annotation playback duration.");
            }

            var annotation = Required(root, "annotation");
            ValidateAnnotation(annotation);
            // Project decoding is deliberately tolerant. Validate every field BEFORE using its converters.
            return new(annotation.Deserialize(StudioJsonContext.Default.StudioAnnotation)!, duration);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Invalid Tiny Clips annotation clipboard data.", exception);
        }
    }

    private static void ValidateAnnotation(JsonElement annotation)
    {
        RequireObject(annotation);
        foreach (var field in new[] { "id", "kind", "attach", "text", "style", "size", "fill", "textColor", "shapeFill", "redactStyle", "redactLevel" })
        {
            if (Required(annotation, field).ValueKind != JsonValueKind.String)
            {
                throw Invalid($"Invalid annotation field: {field}.");
            }
        }

        if (!IsSupportedKind(annotation.GetProperty("kind").GetString()))
        {
            throw Invalid("Unsupported annotation kind.");
        }
        Choice(annotation, "attach", ["screen", "frame"]);
        Choice(annotation, "style", ["red", "blue", "yellow", "white", "dark"]);
        Choice(annotation, "size", ["small", "medium", "large"]);
        Choice(annotation, "redactStyle", ["blur", "pixelate", "solid"]);
        Choice(annotation, "redactLevel", ["light", "medium", "heavy"]);
        if (Required(annotation, "shadow").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw Invalid("Invalid annotation shadow.");
        }

        var start = Number(annotation, "start");
        if (Number(annotation, "end") <= start)
        {
            throw Invalid("Invalid annotation time range.");
        }
        // Project geometry is clamped by rendering, not by storage or clipboard interchange.
        // Preserve finite saved values exactly, even outside the editor controls' ranges.
        foreach (var field in new[] { "x", "y", "width", "height", "shapeFillOpacity", "offsetX", "offsetY", "scale", "cornerRadius", "rotation" })
        {
            _ = Number(annotation, field);
        }
    }

    private static void RequireObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("Expected an annotation clipboard object.");
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw Invalid($"Duplicate annotation clipboard field: {property.Name}.");
            }
        }
    }

    private static JsonElement Required(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value : throw Invalid($"Missing annotation clipboard field: {name}.");

    private static double Number(JsonElement element, string name)
    {
        var value = Required(element, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number)
            ? number : throw Invalid($"Invalid annotation number: {name}.");
    }

    private static void Choice(JsonElement element, string name, string[] choices)
    {
        if (!choices.Contains(Required(element, name).GetString(), StringComparer.Ordinal))
        {
            throw Invalid($"Unsupported annotation {name}.");
        }
    }

    private static InvalidDataException Invalid(string message) => new(message);
}
