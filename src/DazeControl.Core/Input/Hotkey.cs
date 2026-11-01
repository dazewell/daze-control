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

/// <summary>
/// A global shortcut as written in config.yaml and shown in the editor (ported from OSD D-020..D-022): "Win+Alt+A". <paramref name="Key"/> is a
/// virtual-key code: A–Z, 0–9 or F1–F24. At least one of Win/Ctrl/Alt is required, so a hotkey never
/// swallows plain typing.
/// </summary>
public sealed record Hotkey(HotkeyModifiers Modifiers, uint Key)
{
    private static readonly (string Name, HotkeyModifiers Flag)[] ModifierNames =
    [
        ("Win", HotkeyModifiers.Win),
        ("Ctrl", HotkeyModifiers.Ctrl),
        ("Alt", HotkeyModifiers.Alt),
        ("Shift", HotkeyModifiers.Shift),
    ];

    /// <summary>Case and spaces don't matter; "Control" and "Windows" are accepted too.</summary>
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
        var valid = (modifiers & (HotkeyModifiers.Win | HotkeyModifiers.Ctrl | HotkeyModifiers.Alt)) != 0
            && (key is >= 0x41 and <= 0x5A or >= 0x30 and <= 0x39 or >= 0x70 and <= 0x87); // A–Z, 0–9, F1–F24
        hotkey = valid ? new Hotkey(modifiers, key) : null;
        return valid;
    }

    /// <summary>The held modifiers alone, for the recorder box while keys are going down: "Win+Alt+".</summary>
    public static string Prefix(HotkeyModifiers modifiers) =>
        string.Concat(ModifierNames.Where(m => modifiers.HasFlag(m.Flag)).Select(m => m.Name + "+"));

    private static uint? KeyCode(string name)
    {
        name = name.ToUpperInvariant();
        if (name.Length == 1 && (char.IsAsciiLetterUpper(name[0]) || char.IsAsciiDigit(name[0])))
            return name[0]; // VK_A–VK_Z and VK_0–VK_9 are the ASCII codes
        if (name.Length > 1 && name[0] == 'F' && int.TryParse(name.AsSpan(1), out var n) && n is >= 1 and <= 24
            && name[1] != '0')
            return (uint)(0x70 + n - 1); // VK_F1
        return null;
    }

    /// <summary>Canonical form, modifiers in Windows' order: "Win+Ctrl+Alt+Shift+A".</summary>
    public override string ToString()
    {
        var key = Key is >= 0x70 and <= 0x87 ? "F" + (Key - 0x70 + 1) : ((char)Key).ToString();
        return string.Join('+', ModifierNames.Where(m => Modifiers.HasFlag(m.Flag)).Select(m => m.Name).Append(key));
    }
}
