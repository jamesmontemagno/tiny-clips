using System.Text.Json;
using System.Text.Json.Nodes;
using TinyClips.Core.Capture;

namespace TinyClips.Core.Automation;

public enum AutomationCaptureMode
{
    Screen,
    Region,
    Window,
}

/// <summary>
/// The target of an unattended <c>capture.screenshot</c> or <c>capture.ocr</c>. Parsing never
/// falls back to a picker: a missing or malformed target is <c>invalid_params</c>.
/// </summary>
public sealed record AutomationCaptureRequest(
    AutomationCaptureMode Mode,
    string? DisplayId,
    string? WindowId,
    PixelRect? Region)
{
    private static readonly string[] RegionKeys = ["x", "y", "width", "height"];

    public static AutomationCaptureRequest Parse(JsonObject parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var mode = OptionalString(parameters, "mode") switch
        {
            null or "screen" => AutomationCaptureMode.Screen,
            "region" => AutomationCaptureMode.Region,
            "window" => AutomationCaptureMode.Window,
            var other => throw Invalid($"mode must be screen, region, or window, not {other}."),
        };
        var displayId = OptionalString(parameters, "displayId");
        var windowId = OptionalString(parameters, "windowId");
        var regionNode = parameters["region"];

        switch (mode)
        {
            case AutomationCaptureMode.Screen:
                if (windowId is not null || regionNode is not null)
                {
                    throw Invalid("Screen captures take only an optional displayId. Use mode region or window for a region or window.");
                }

                return new AutomationCaptureRequest(mode, displayId, null, null);

            case AutomationCaptureMode.Region:
                if (windowId is not null)
                {
                    throw Invalid("Region captures take displayId and region, not windowId.");
                }

                if (displayId is null)
                {
                    throw Invalid("Region captures need a displayId from capture.targets.");
                }

                if (regionNode is null)
                {
                    throw Invalid("Region captures need region: {x, y, width, height}.");
                }

                return new AutomationCaptureRequest(mode, displayId, null, ParseRegion(regionNode));

            default:
                if (displayId is not null || regionNode is not null)
                {
                    throw Invalid("Window captures take only a windowId.");
                }

                if (windowId is null)
                {
                    throw Invalid("Window captures need a windowId from capture.targets.");
                }

                return new AutomationCaptureRequest(mode, null, windowId, null);
        }
    }

    /// <summary>Rejects a region that does not fit inside a display of the given physical size.</summary>
    public static void EnsureRegionFits(PixelRect region, int displayWidth, int displayHeight)
    {
        if ((long)region.X + region.Width > displayWidth || (long)region.Y + region.Height > displayHeight)
        {
            throw Invalid($"region must fit inside the display ({displayWidth} × {displayHeight} physical pixels).");
        }
    }

    internal static PixelRect ParseRegion(JsonNode? node)
    {
        if (node is not JsonObject region)
        {
            throw Invalid("region must be an object with x, y, width, and height.");
        }

        foreach (var (key, _) in region)
        {
            if (!RegionKeys.Contains(key, StringComparer.Ordinal))
            {
                throw Invalid($"region has an unknown property {key}.");
            }
        }

        var x = Integer(region, "x", 0);
        var y = Integer(region, "y", 0);
        var width = Integer(region, "width", 1);
        var height = Integer(region, "height", 1);
        return new PixelRect(x, y, width, height);
    }

    private static int Integer(JsonObject obj, string name, int minimum)
    {
        if (obj[name] is not JsonValue value || value.GetValueKind() != JsonValueKind.Number
            || !value.TryGetValue<double>(out var d) || Math.Floor(d) != d)
        {
            throw Invalid($"region.{name} must be an integer.");
        }

        if (d < minimum || d > int.MaxValue)
        {
            throw Invalid($"region.{name} must be at least {minimum}.");
        }

        return (int)d;
    }

    private static string? OptionalString(JsonObject obj, string name)
    {
        var node = obj[name];
        if (node is null)
        {
            return null;
        }

        if (node is not JsonValue value || !value.TryGetValue<string>(out var s))
        {
            throw Invalid($"{name} must be a string.");
        }

        if (string.IsNullOrWhiteSpace(s))
        {
            throw Invalid($"{name} must not be empty.");
        }

        return s;
    }

    private static AutomationException Invalid(string message) => new(AutomationErrorCodes.InvalidParams, message);
}
