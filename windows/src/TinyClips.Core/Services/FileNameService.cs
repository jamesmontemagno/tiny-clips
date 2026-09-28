using System.Globalization;
using System.Text.RegularExpressions;
using TinyClips.Core.Models;

namespace TinyClips.Core.Services;

public sealed class FileNameService : IFileNameService
{
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly char[] TrimChars = [' ', '.', '\t', '\n', '\r'];
    private static readonly char[] InvalidNameChars = ['/', '\\', ':', '?', '*', '"', '<', '>', '|'];
    private readonly ICaptureSettings _settings;

    public FileNameService(ICaptureSettings settings)
    {
        _settings = settings;
    }

    public string GeneratedFileName(CaptureType type, string fileExtension, DateTime? date = null)
    {
        var current = date ?? DateTime.Now;
        var template = string.IsNullOrWhiteSpace(_settings.FileNameTemplate)
            ? "TinyClips {date} at {time}"
            : _settings.FileNameTemplate.Trim();

        var stem = template
            .Replace("{app}", "TinyClips", StringComparison.OrdinalIgnoreCase)
            .Replace("{type}", TypeLabel(type), StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", current.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{time}", current.ToString("HH.mm.ss", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{datetime}", current.ToString("yyyy-MM-dd_HH.mm.ss", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);

        stem = Sanitize(stem, current);

        var extension = CleanExtension(fileExtension);
        return string.IsNullOrWhiteSpace(extension) ? stem : $"{stem}.{extension}";
    }

    public string NamingPreview(CaptureType type) => GeneratedFileName(type, FileExtensionFor(type), DateTime.Now);

    public string FileExtensionFor(CaptureType type) => type switch
    {
        CaptureType.Screenshot => _settings.ImageFormat switch
        {
            ImageFormat.Png => "png",
            ImageFormat.Webp => "webp",
            _ => "jpg",
        },
        CaptureType.Video => "mp4",
        CaptureType.Gif => "gif",
        _ => string.Empty,
    };

    private static string Sanitize(string value, DateTime current)
    {
        var invalidChars = Path.GetInvalidFileNameChars().Concat(InvalidNameChars).ToArray();
        var sanitized = new string(value.Select(c => invalidChars.Contains(c) ? '-' : c).ToArray());
        sanitized = WhitespaceRegex.Replace(sanitized, " ");
        sanitized = sanitized.Trim(TrimChars);

        return string.IsNullOrWhiteSpace(sanitized)
            ? $"TinyClips {current.ToString("yyyy-MM-dd_HH.mm.ss", CultureInfo.InvariantCulture)}"
            : sanitized;
    }

    private static string CleanExtension(string fileExtension)
    {
        if (string.IsNullOrWhiteSpace(fileExtension))
        {
            return string.Empty;
        }

        var extension = fileExtension.Trim().Trim('.').Trim();
        return extension.ToLowerInvariant();
    }

    private static string TypeLabel(CaptureType type) => type switch
    {
        CaptureType.Screenshot => "Screenshot",
        CaptureType.Video => "Video",
        CaptureType.Gif => "GIF",
        _ => string.Empty,
    };
}
