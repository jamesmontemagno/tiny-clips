using System.Text.Json;
using System.Text.Json.Nodes;
using TinyClips.Core.Studio;

namespace TinyClips.Core.Automation;

/// <summary>Reads the <c>timing</c> of <c>studio.captions.import</c>.</summary>
public static class AutomationCaptionTiming
{
    /// <summary>
    /// The timing an agent asked for: the recording when it left it out, and an invalid-params
    /// error for anything other than <c>recording</c> or <c>editedVideo</c>.
    /// </summary>
    public static StudioCaptionTiming Parse(JsonNode? node)
    {
        if (node is null)
        {
            return StudioCaptionTiming.Recording;
        }

        if (node is JsonValue value && value.GetValueKind() == JsonValueKind.String)
        {
            switch (value.GetValue<string>())
            {
                case "recording":
                    return StudioCaptionTiming.Recording;
                case "editedVideo":
                    return StudioCaptionTiming.EditedVideo;
            }
        }

        throw new AutomationException(AutomationErrorCodes.InvalidParams, "timing must be recording or editedVideo.");
    }
}
