using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Windows.Foundation;

namespace TinyClips.Core.Editing;

public sealed record SensitiveTextWord(string Text, Rect Bounds);
public sealed record SensitiveTextSuggestion(string Kind, string Text, Rect Bounds);

/// <summary>Deterministic heuristics over local OCR words. These are suggestions, not a secret scanner.</summary>
public static class SensitiveTextDetector
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex Email = new(
        @"(?<![\w.+%-])[\w.!#$%&'*+/=?^`{|}~-]+@[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?)*\.[A-Za-z]{2,63}(?![\w-])",
        RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex Ip = new(
        @"(?<![\w.])[0-9]{1,3}(?:\.[0-9]{1,3}){3}(?!\w|\.[0-9])|(?<![\w.:])[0-9A-Fa-f:.]*:[0-9A-Fa-f:.]*:[0-9A-Fa-f:]*(?![\w:])",
        RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex Phone = new(
        @"(?<![\w.+])\+?(?:\([0-9]{2,4}\)|[0-9]{1,4})(?:[ .-]?(?:\([0-9]{2,4}\)|[0-9]{1,4})){1,7}(?![\w.])",
        RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex Date = new(
        @"^(?:[0-9]{4}-[0-9]{1,2}-[0-9]{1,2}|[0-9]{1,2}-[0-9]{1,2}-[0-9]{4})$",
        RegexOptions.CultureInvariant, Timeout);

    /// <summary>
    /// Match within each OCR line; take whole overlapping word boxes (never estimate character
    /// widths). OCR dimensions may differ independently after downscale rounding. No display DPI
    /// or editor zoom enters this conversion. Outward rounding and padding cover glyph edges.
    /// </summary>
    public static IReadOnlyList<SensitiveTextSuggestion> Detect(
        IEnumerable<IReadOnlyList<SensitiveTextWord>> lines,
        int ocrWidth, int ocrHeight, int imageWidth, int imageHeight,
        CancellationToken cancellationToken = default, double textAngleDegrees = 0)
    {
        if (ocrWidth <= 0 || ocrHeight <= 0 || imageWidth <= 0 || imageHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(ocrWidth));
        var suggestions = new List<SensitiveTextSuggestion>();
        foreach (var words in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = new StringBuilder();
            var spans = new List<(int Start, int End, Rect Bounds)>();
            string? previous = null;
            foreach (var word in words)
            {
                // OCR sometimes splits address punctuation into separate words. Join only explicit
                // punctuation, not arbitrary neighboring words (which creates false positives).
                var joinsAddress = word.Text is "@" or "." or ":" || previous is "@" or "." or ":" ||
                    word.Text.StartsWith('@') || (previous?.EndsWith('@') ?? false);
                if (text.Length > 0 && !joinsAddress) text.Append(' ');
                var start = text.Length;
                text.Append(word.Text);
                spans.Add((start, text.Length, word.Bounds));
                previous = word.Text;
            }
            var line = text.ToString();
            var occupied = new List<(int Start, int End)>();
            Add(Email, "Email address", _ => true);
            Add(Ip, "IP address", value =>
            {
                if (!IPAddress.TryParse(value, out var address)) return false;
                return address.AddressFamily == AddressFamily.InterNetworkV6 ||
                    value.Split('.').All(part => part.Length == 1 || part[0] != '0');
            });
            Add(Phone, "Phone number", value =>
            {
                var digits = value.Count(char.IsAsciiDigit);
                // Shared platform policy: 7–15 digits with explicit phone formatting. Dots
                // are accepted only as 3-3-4, never as version-like or mixed dotted identifiers.
                if (digits is < 7 or > 15 || Date.IsMatch(value)) return false;
                if (value.Contains('.'))
                    return Regex.IsMatch(value, @"^[0-9]{3}\.[0-9]{3}\.[0-9]{4}$",
                        RegexOptions.CultureInvariant, Timeout);
                return value.StartsWith('+') || value.Contains('(') ||
                    value.Contains(' ') || value.Contains('-');
            });

            void Add(Regex pattern, string kind, Func<string, bool> accept)
            {
                foreach (Match match in pattern.Matches(line))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!accept(match.Value) ||
                        occupied.Any(s => s.Start < match.Index + match.Length && s.End > match.Index))
                        continue;
                    var boxes = spans.Where(s => s.Start < match.Index + match.Length && s.End > match.Index)
                        .Select(s => s.Bounds).ToArray();
                    if (boxes.Length == 0) continue;
                    var left = boxes.Min(b => b.X);
                    var top = boxes.Min(b => b.Y);
                    var right = boxes.Max(b => b.Right);
                    var bottom = boxes.Max(b => b.Bottom);
                    var bounds = MapBounds(new Rect(left, top, right - left, bottom - top),
                        ocrWidth, ocrHeight, imageWidth, imageHeight, textAngleDegrees);
                    if (bounds.Width < 1 || bounds.Height < 1) continue;
                    occupied.Add((match.Index, match.Index + match.Length));
                    suggestions.Add(new(kind, match.Value, bounds));
                }
            }
        }
        return suggestions;
    }

    public static Rect MapBounds(Rect bounds, int ocrWidth, int ocrHeight, int imageWidth, int imageHeight,
        double textAngleDegrees = 0)
    {
        if (ocrWidth <= 0 || ocrHeight <= 0 || imageWidth <= 0 || imageHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(ocrWidth));
        if (!double.IsFinite(textAngleDegrees)) throw new ArgumentOutOfRangeException(nameof(textAngleDegrees));
        if (textAngleDegrees != 0)
        {
            // Windows OCR boxes are deskewed. TextAngle rotates them clockwise about the OCR
            // image center (not the word center) to overlay the original image correctly.
            var radians = textAngleDegrees * Math.PI / 180;
            // Exact cardinal rotations must not gain a pixel from sin/cos residuals before
            // outward rounding. Leave genuinely non-cardinal angles conservative and unchanged.
            var (cosine, sine) = (textAngleDegrees % 360) switch
            {
                0 => (1d, 0d),
                90 or -270 => (0d, 1d),
                180 or -180 => (-1d, 0d),
                270 or -90 => (0d, -1d),
                _ => (Math.Cos(radians), Math.Sin(radians)),
            };
            var centerX = ocrWidth / 2.0;
            var centerY = ocrHeight / 2.0;
            var corners = new[]
            {
                Rotate(bounds.X, bounds.Y), Rotate(bounds.Right, bounds.Y),
                Rotate(bounds.X, bounds.Bottom), Rotate(bounds.Right, bounds.Bottom),
            };
            var x = corners.Min(p => p.X);
            var y = corners.Min(p => p.Y);
            bounds = new Rect(x, y, corners.Max(p => p.X) - x, corners.Max(p => p.Y) - y);

            Point Rotate(double x, double y) => new(
                centerX + (x - centerX) * cosine - (y - centerY) * sine,
                centerY + (x - centerX) * sine + (y - centerY) * cosine);
        }
        var sx = (double)imageWidth / ocrWidth;
        var sy = (double)imageHeight / ocrHeight;
        var left = Math.Clamp(Math.Floor((bounds.X - 2) * sx), 0, imageWidth);
        var top = Math.Clamp(Math.Floor((bounds.Y - 2) * sy), 0, imageHeight);
        var right = Math.Clamp(Math.Ceiling((bounds.Right + 2) * sx), left, imageWidth);
        var bottom = Math.Clamp(Math.Ceiling((bounds.Bottom + 2) * sy), top, imageHeight);
        return new Rect(left, top, right - left, bottom - top);
    }

    /// <summary>Use the editor's existing solid, opaque black rendering path, not blur defaults.</summary>
    public static ScreenshotAnnotation CreateRedaction(SensitiveTextSuggestion suggestion) => new()
    {
        Tool = EditTool.Redact,
        Bounds = suggestion.Bounds,
        RedactStyle = RedactionStyle.Solid,
    };
}
