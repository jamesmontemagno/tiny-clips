using CommunityToolkit.Mvvm.ComponentModel;
using TinyClips.Core.Services;

namespace TinyClips.App;

public sealed class WhatsNewViewModel : ObservableObject
{
    // Bump when release highlights change, independently of the package version.
    internal const int ContentRevision = 3;

    private readonly ICaptureSettings _settings;
    private bool _isStudioEnabled;

    public WhatsNewViewModel(ICaptureSettings settings)
    {
        _settings = settings;
        _isStudioEnabled = settings.StudioPreviewEnabled;
    }

    public bool IsStudioEnabled
    {
        get => _isStudioEnabled;
        set
        {
            if (_isStudioEnabled == value)
            {
                return;
            }

            _settings.StudioPreviewEnabled = value;
            SetProperty(ref _isStudioEnabled, value);
        }
    }

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
