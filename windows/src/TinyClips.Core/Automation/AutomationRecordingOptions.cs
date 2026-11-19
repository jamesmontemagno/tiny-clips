using System.Text.Json.Nodes;
using TinyClips.Core.Capture;
using TinyClips.Core.Services;

namespace TinyClips.Core.Automation;

public static class AutomationRecordingOptions
{
    public static VideoRecordingOptions Resolve(
        JsonObject parameters,
        ICaptureSettings settings,
        bool hasMicrophoneAccess,
        bool hasCameraAccess)
    {
        var options = new VideoRecordingOptions
        {
            RecordMicrophone = OptionalBoolean(parameters, "microphone") ?? settings.RecordMicrophone,
            WebcamEnabled = OptionalBoolean(parameters, "camera") ?? settings.WebcamEnabled,
        };
        EnsureAuthorized(options, settings, hasMicrophoneAccess, hasCameraAccess);
        return options;
    }

    public static void EnsureAuthorized(
        VideoRecordingOptions options,
        ICaptureSettings settings,
        bool hasMicrophoneAccess,
        bool hasCameraAccess)
    {
        if (options.RecordMicrophone == true && (!settings.RecordMicrophone || !hasMicrophoneAccess))
        {
            throw new AutomationException(AutomationErrorCodes.PermissionRequired,
                "The user must enable microphone recording in Tiny Clips and grant microphone access in Windows Settings before an agent can use it.");
        }

        if (options.WebcamEnabled == true && (!settings.WebcamEnabled || !hasCameraAccess))
        {
            throw new AutomationException(AutomationErrorCodes.PermissionRequired,
                "The user must enable the camera in Tiny Clips and grant camera access in Windows Settings before an agent can use it.");
        }
    }

    private static bool? OptionalBoolean(JsonObject parameters, string name)
    {
        if (parameters[name] is null)
        {
            return null;
        }

        if (parameters[name] is JsonValue value && value.TryGetValue<bool>(out var enabled))
        {
            return enabled;
        }

        throw new AutomationException(AutomationErrorCodes.InvalidParams, $"{name} must be true or false.");
    }
}
