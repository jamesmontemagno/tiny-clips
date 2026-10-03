using System.Text;
using System.Text.Json;
using TinyClips.Core.Studio;

namespace TinyClips.Core.Services;

/// <summary>
/// Converts a saved Studio look to and from the JSON text kept in settings. The text holds the
/// <c>canvas</c>, <c>screen</c>, and <c>camera</c> objects with the same shape they have in
/// project.json. A look is shared by every new project, so it never keeps a crop.
/// </summary>
internal static class StudioLookText
{
    // Only present so the project reader accepts the text. Nothing from these reaches the look.
    private const string PlaceholderProjectId = "00000000-0000-0000-0000-000000000000";
    private const int PlaceholderScreenSize = 1;

    public static string Write(StudioLook look) =>
        JsonSerializer.Serialize(WithoutCrops(look), StudioJsonContext.Default.StudioLook);

    /// <summary>Returns null when there is no text or this build cannot read it.</summary>
    public static StudioLook? Read(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            // The source-generated reader leaves a missing member at zero instead of its default, and
            // the project reader is what puts the defaults back. Reading the three parts as a
            // project gives a look the same rules as project.json, including for members that a
            // later version adds.
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("id", PlaceholderProjectId);
                writer.WriteStartObject("sources");
                writer.WriteStartObject("screen");
                writer.WriteNumber("width", PlaceholderScreenSize);
                writer.WriteNumber("height", PlaceholderScreenSize);
                writer.WriteNumber("duration", 0);
                writer.WriteEndObject();
                writer.WriteEndObject();
                CopyPart(root, "canvas", writer);
                CopyPart(root, "screen", writer);
                CopyPart(root, "camera", writer);
                writer.WriteEndObject();
            }

            var project = StudioProjectJson.ReadProject(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
            return WithoutCrops(new StudioLook(project.Canvas, project.Screen, project.Camera));
        }
        catch (Exception ex) when (ex is JsonException or StudioProjectInvalidException or InvalidOperationException or FormatException or NotSupportedException)
        {
            return null;
        }
    }

    private static void CopyPart(JsonElement root, string name, Utf8JsonWriter writer)
    {
        if (root.TryGetProperty(name, out var part))
        {
            writer.WritePropertyName(name);
            part.WriteTo(writer);
        }
    }

    private static StudioLook WithoutCrops(StudioLook look) =>
        new(
            look.Canvas ?? new StudioCanvas(),
            (look.Screen ?? new StudioScreenStyle()) with { Crop = null },
            (look.Camera ?? new StudioCameraStyle()) with { Crop = null });
}
