namespace TinyClips.Core.Models;

public readonly record struct HotKeyDefinition(HotKeyModifiers Modifiers, uint VirtualKey)
{
    public int ModifiersValue => (int)Modifiers;

    /// <summary>
    /// True when no modifiers and no key are set; used for optional actions that are not
    /// assigned a global shortcut (nothing is registered for them).
    /// </summary>
    public bool IsUnbound => Modifiers == HotKeyModifiers.None && VirtualKey == 0;

    public string DisplayString => IsUnbound ? "Not set" : FormatModifiers(Modifiers) + FormatVirtualKey(VirtualKey);

    private static string FormatModifiers(HotKeyModifiers modifiers)
    {
        var tokens = new List<string>();

        if (modifiers.HasFlag(HotKeyModifiers.Control))
        {
            tokens.Add("Ctrl");
        }

        if (modifiers.HasFlag(HotKeyModifiers.Alt))
        {
            tokens.Add("Alt");
        }

        if (modifiers.HasFlag(HotKeyModifiers.Shift))
        {
            tokens.Add("Shift");
        }

        if (modifiers.HasFlag(HotKeyModifiers.Win))
        {
            tokens.Add("Win");
        }

        return string.Join("+", tokens) + (tokens.Count > 0 ? "+" : string.Empty);
    }

    private static string FormatVirtualKey(uint virtualKey) => virtualKey switch
    {
        >= 0x30 and <= 0x39 => ((char)virtualKey).ToString(),
        >= 0x41 and <= 0x5A => ((char)virtualKey).ToString(),
        >= 0x70 and <= 0x87 => $"F{virtualKey - 0x70 + 1}",
        0x20 => "Space",
        0x0D => "Enter",
        0x1B => "Esc",
        0x09 => "Tab",
        0x08 => "Backspace",
        0x2E => "Delete",
        0x2D => "Insert",
        0x24 => "Home",
        0x23 => "End",
        0x21 => "PageUp",
        0x22 => "PageDown",
        0x25 => "Left",
        0x26 => "Up",
        0x27 => "Right",
        0x28 => "Down",
        0x2C => "PrtSc",
        0xBB => "=",
        0xBD => "-",
        0xBC => ",",
        0xBE => ".",
        0xBF => "/",
        0xC0 => "`",
        0xDB => "[",
        0xDC => "\\",
        0xDD => "]",
        0xDE => "'",
        _ => "?",
    };
}
