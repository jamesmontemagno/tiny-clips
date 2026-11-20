using TinyClips.Core.Services;

namespace TinyClips.App;

public static class WhatsNewViewModel
{
    // Bump when release highlights change, independently of the package version.
    internal const int ContentRevision = 4;

    public static bool ShouldShow(ICaptureSettings settings) =>
        ParseSeenRevision(settings.LastSeenWhatsNewVersion) < ContentRevision;

    public static void MarkSeen(ICaptureSettings settings, string version) =>
        settings.LastSeenWhatsNewVersion = $"rev{ContentRevision};{version}";

    internal static int ParseSeenRevision(string? stored)
    {
        if (string.IsNullOrEmpty(stored) || !stored.StartsWith("rev", StringComparison.Ordinal))
        {
            return 0;
        }

        var end = stored.IndexOf(';');
        var number = end > 3 ? stored[3..end] : stored[3..];
        return int.TryParse(number, out var revision) && revision >= 0 ? revision : 0;
    }
}
