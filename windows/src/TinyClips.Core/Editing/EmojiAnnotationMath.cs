using System.Globalization;
using System.Text;

namespace TinyClips.Core.Editing;

/// <summary>Sizing, glyph, and palette helpers for emoji sticker annotations.</summary>
public static class EmojiAnnotationMath
{
    /// <summary>Default sticker side as a fraction of the image width.</summary>
    public const double DefaultSideRatio = 0.10;

    public const double MinimumSidePixels = 16;

    /// <summary>Fraction of the sticker square used as the emoji glyph's font size.</summary>
    public const double GlyphFontRatio = 0.8;

    public const string DefaultEmoji = "😀";

    public const int MaximumRecent = 12;

    public static double DefaultSidePixels(double imageWidth, double imageHeight) =>
        ClampSidePixels(imageWidth * DefaultSideRatio, imageWidth, imageHeight);

    public static double ClampSidePixels(double side, double imageWidth, double imageHeight)
    {
        var maximum = Math.Max(MinimumSidePixels, Math.Min(imageWidth, imageHeight));
        return Math.Clamp(side, MinimumSidePixels, maximum);
    }

    /// <summary>Square bounds of <paramref name="side"/> centered on <paramref name="center"/>.</summary>
    public static RectD SquareBounds(PointD center, double side) =>
        new(center.X - side / 2, center.Y - side / 2, side, side);

    public static double GlyphFontSize(double side) => Math.Max(1, side * GlyphFontRatio);

    /// <summary>
    /// Returns the last user-perceived character of <paramref name="text"/> if it looks like an
    /// emoji, otherwise null. Digits and letters (which Unicode technically flags as emoji-capable)
    /// are rejected unless they carry a variation selector or keycap.
    /// </summary>
    public static string? ExtractEmoji(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        string? last = null;
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            last = enumerator.GetTextElement();
        }

        if (string.IsNullOrEmpty(last))
        {
            return null;
        }

        foreach (var rune in last.EnumerateRunes())
        {
            if (IsEmojiRune(rune))
            {
                return last;
            }
        }

        return null;
    }

    private static bool IsEmojiRune(Rune rune)
    {
        var value = rune.Value;
        return value is >= 0x1F000 and <= 0x1FAFF // Pictographs, emoticons, transport, symbols, flags
            || value is >= 0x2600 and <= 0x27BF // Misc symbols + dingbats (☀ ✅ ❌)
            || value is >= 0x2B00 and <= 0x2BFF // Misc symbols and arrows (⭐ ⬆)
            || value is >= 0x2300 and <= 0x23FF // Misc technical (⌚ ⏰)
            || value == 0xFE0F // Variation selector-16: text symbol shown as emoji (❤️)
            || value == 0x20E3 // Combining keycap (1️⃣)
            || value == 0x200D; // Zero-width joiner sequences (👨‍💻)
    }

    /// <summary>Adds <paramref name="emoji"/> to the front of a most-recent list, capped at <see cref="MaximumRecent"/>.</summary>
    public static List<string> PushRecent(IEnumerable<string> recent, string emoji)
    {
        var updated = new List<string> { emoji };
        foreach (var item in recent)
        {
            if (item != emoji && !updated.Contains(item))
            {
                updated.Add(item);
            }
            if (updated.Count >= MaximumRecent)
            {
                break;
            }
        }
        return updated;
    }

    public static readonly IReadOnlyList<EmojiPaletteCategory> Palette =
    [
        new("Smileys",
        [
            "😀", "😂", "🤣", "😍", "🥰", "😎", "🤩", "😉", "🙂", "🤔", "🤨", "😮",
            "😱", "😭", "😡", "🥳", "🤯", "🙄", "😴", "🤮", "🤡", "👻", "💀", "🤖",
        ]),
        new("Gestures",
        [
            "👍", "👎", "👏", "🙌", "🙏", "👋", "✌️", "🤞", "🤙", "💪", "👉", "👈",
            "👆", "👇", "☝️", "✋", "🤝", "🫶", "👀", "👁️", "🧠", "🫡",
        ]),
        new("Symbols",
        [
            "❤️", "🧡", "💛", "💚", "💙", "💜", "🔥", "⭐", "✨", "💯", "✅", "❌",
            "⚠️", "❗", "❓", "💡", "🎯", "🚀", "🎉", "🏆", "🔔", "🔒", "🔑", "⏰",
        ]),
        new("Objects",
        [
            "💻", "🖥️", "📱", "⌨️", "🖱️", "📷", "🎥", "🎬", "🎧", "🎮", "📌", "📎",
            "✏️", "📝", "📊", "📈", "📉", "🗂️", "🔍", "🧩", "🐛", "🛠️", "⚙️", "🧪",
        ]),
        new("Nature",
        [
            "🐶", "🐱", "🦊", "🐼", "🐸", "🦄", "🐙", "🦋", "🌈", "☀️", "🌙", "⚡",
            "🌊", "🍀", "🌸", "🌵", "🍕", "🍩", "☕", "🍺",
        ]),
    ];
}

public sealed record EmojiPaletteCategory(string Name, IReadOnlyList<string> Emoji);
