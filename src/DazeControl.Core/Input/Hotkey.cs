using System.Diagnostics.CodeAnalysis;

namespace DazeControl.Core.Input;

/// <summary>Values are <c>RegisterHotKey</c>'s MOD_* flags.</summary>
[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 1,
    Ctrl = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>Why a pressed key can't be a shortcut; the recorder shows it under the box (UX §5.7 H1–H4, D-073).</summary>
public enum HotkeyRejection
{
    None,

    /// <summary>H1: not a key a shortcut can use (Space, Tab, arrows, the pad's * …).</summary>
    NotAKey,

    /// <summary>H2: a usable key, but Win, Ctrl or Alt is missing.</summary>
    NeedsModifier,

    /// <summary>H3: a number-pad navigation key (Num Lock off). Only the recorder can tell; <see cref="Hotkey.Check"/> never returns it.</summary>
    NumLockOff,

    /// <summary>H4: Shift with a number-pad key; with Num Lock on Windows turns Shift+Num1 into End.</summary>
    ShiftWithNumberPad,
}

/// <summary>
/// A global shortcut as written in config.yaml and shown in the editor (ported from OSD D-020..D-022): "Win+Alt+A".
/// <paramref name="Key"/> is a virtual-key code: A–Z, 0–9, F1–F24, the punctuation keys under their US names
/// (<c>[ ] \ ; ' , . / ` - =</c>) or the number pad's Num0–Num9 (D-071). Win, Ctrl or Alt is required, so a
/// hotkey never swallows plain typing, except for Num0–Num9, which may stand alone; Shift never goes with a Num key.
/// </summary>
public sealed record Hotkey(HotkeyModifiers Modifiers, uint Key)
{
    private const uint NumPad0 = 0x60, NumPad9 = 0x69;

    private static readonly (string Name, HotkeyModifiers Flag)[] ModifierNames =
    [
        ("Win", HotkeyModifiers.Win),
        ("Ctrl", HotkeyModifiers.Ctrl),
        ("Alt", HotkeyModifiers.Alt),
        ("Shift", HotkeyModifiers.Shift),
    ];

    /// <summary>VK_OEM_* codes with their US-layout names; YAML keeps these whatever the layout (D-071).</summary>
    private static readonly (char Name, uint Key)[] Punctuation =
    [
        (';', 0xBA), ('=', 0xBB), (',', 0xBC), ('-', 0xBD), ('.', 0xBE), ('/', 0xBF),
        ('`', 0xC0), ('[', 0xDB), ('\\', 0xDC), (']', 0xDD), ('\'', 0xDE),
    ];

    /// <summary>Case and spaces don't matter; "Control", "Windows" and "Numpad1" are accepted too.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out Hotkey? hotkey)
    {
        hotkey = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        var modifiers = HotkeyModifiers.None;
        foreach (var part in parts[..^1])
        {
            var flag = part.ToUpperInvariant() switch
            {
                "WIN" or "WINDOWS" => HotkeyModifiers.Win,
                "CTRL" or "CONTROL" => HotkeyModifiers.Ctrl,
                "ALT" => HotkeyModifiers.Alt,
                "SHIFT" => HotkeyModifiers.Shift,
                _ => HotkeyModifiers.None,
            };
            if (flag == HotkeyModifiers.None || modifiers.HasFlag(flag))
                return false;
            modifiers |= flag;
        }
        return KeyCode(parts[^1]) is { } key && TryCreate(modifiers, key, out hotkey);
    }

    /// <summary>From keys pressed in the editor: false unless <paramref name="key"/> and the modifiers make a valid hotkey.</summary>
    public static bool TryCreate(HotkeyModifiers modifiers, uint key, [NotNullWhen(true)] out Hotkey? hotkey)
    {
        var valid = Check(modifiers, key) == HotkeyRejection.None;
        hotkey = valid ? new Hotkey(modifiers, key) : null;
        return valid;
    }

    /// <summary>VK_NUMPAD0–VK_NUMPAD9: the pad's digits, which Windows sends only while Num Lock is on.</summary>
    public static bool IsNumberPad(uint key) => key is >= NumPad0 and <= NumPad9;

    /// <summary>Why <paramref name="key"/> with <paramref name="modifiers"/> can't be a shortcut, or <see cref="HotkeyRejection.None"/>.</summary>
    public static HotkeyRejection Check(HotkeyModifiers modifiers, uint key)
    {
        if (IsNumberPad(key))
            return modifiers.HasFlag(HotkeyModifiers.Shift) ? HotkeyRejection.ShiftWithNumberPad : HotkeyRejection.None;
        var usable = key is >= 0x41 and <= 0x5A or >= 0x30 and <= 0x39 or >= 0x70 and <= 0x87 // A–Z, 0–9, F1–F24
            || Array.Exists(Punctuation, p => p.Key == key);
        if (!usable)
            return HotkeyRejection.NotAKey;
        return (modifiers & (HotkeyModifiers.Win | HotkeyModifiers.Ctrl | HotkeyModifiers.Alt)) != 0
            ? HotkeyRejection.None
            : HotkeyRejection.NeedsModifier;
    }

    /// <summary>The held modifiers alone, for the recorder box while keys are going down: "Win+Alt+".</summary>
    public static string Prefix(HotkeyModifiers modifiers) =>
        string.Concat(ModifierNames.Where(m => modifiers.HasFlag(m.Flag)).Select(m => m.Name + "+"));

    private static uint? KeyCode(string name)
    {
        name = name.ToUpperInvariant();
        if (name.Length == 1 && (char.IsAsciiLetterUpper(name[0]) || char.IsAsciiDigit(name[0])))
            return name[0]; // VK_A–VK_Z and VK_0–VK_9 are the ASCII codes
        if (name.Length == 1 && Array.FindIndex(Punctuation, p => p.Name == name[0]) is >= 0 and var i)
            return Punctuation[i].Key;
        if (name.Length > 1 && name[0] == 'F' && int.TryParse(name.AsSpan(1), out var n) && n is >= 1 and <= 24
            && name[1] != '0')
            return (uint)(0x70 + n - 1); // VK_F1
        foreach (var prefix in (ReadOnlySpan<string>)["NUMPAD", "NUM"])
        {
            if (name.Length == prefix.Length + 1 && name.StartsWith(prefix, StringComparison.Ordinal) && char.IsAsciiDigit(name[^1]))
                return NumPad0 + (uint)(name[^1] - '0');
        }
        return null;
    }

    /// <summary>The key's own name, as written after the last "+": "A", "F11", "[", "Num1".</summary>
    public string KeyName => Key switch
    {
        >= 0x70 and <= 0x87 => "F" + (Key - 0x70 + 1),
        >= NumPad0 and <= NumPad9 => "Num" + (Key - NumPad0),
        _ when Array.FindIndex(Punctuation, p => p.Key == Key) is >= 0 and var i => Punctuation[i].Name.ToString(),
        _ => ((char)Key).ToString(),
    };

    /// <summary>Canonical form, modifiers in Windows' order: "Win+Ctrl+Alt+Shift+A".</summary>
    public override string ToString() =>
        string.Join('+', ModifierNames.Where(m => Modifiers.HasFlag(m.Flag)).Select(m => m.Name).Append(KeyName));
}
