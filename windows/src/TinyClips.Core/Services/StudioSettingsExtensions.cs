using TinyClips.Core.Studio;

namespace TinyClips.Core.Services;

public static class StudioSettingsExtensions
{
    private const long BytesPerGigabyte = 1024L * 1024 * 1024;

    /// <summary>Builds the Studio cleanup rules from the retention and storage-limit settings.</summary>
    public static StudioCleanupOptions GetStudioCleanupOptions(this ICaptureSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new StudioCleanupOptions(
            settings.StudioSourceRetentionDays,
            settings.StudioStorageCapGigabytes * BytesPerGigabyte);
    }
}
