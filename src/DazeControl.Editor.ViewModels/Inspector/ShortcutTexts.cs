using DazeControl.Core.Input;

namespace DazeControl.Editor.ViewModels.Inspector;

/// <summary>What the shortcut recorder says under its box (UX §5.7 H1–H4, H7, H8; D-071, D-073).</summary>
public static class ShortcutTexts
{
    /// <summary>Why a pressed key wasn't taken; null for <see cref="HotkeyRejection.None"/>.</summary>
    public static string? Rejection(HotkeyRejection reason) => reason switch
    {
        HotkeyRejection.NotAKey =>
            "That key can't be a shortcut. Try a letter, a number, F1–F24, a key like [ or \\, or a number pad key.",
        HotkeyRejection.NeedsModifier => "Add Win, Ctrl or Alt, so the shortcut doesn't get in the way of your typing.",
        HotkeyRejection.NumLockOff => "Turn on Num Lock to use the number pad.",
        HotkeyRejection.ShiftWithNumberPad => "Shift doesn't work with number pad keys. Use Win, Ctrl or Alt instead.",
        HotkeyRejection.NothingCameThrough => "Nothing came through. Another app may be using those keys.",
        _ => null,
    };

    /// <summary>
    /// Typed instead of pressed (T14.12): the same <see cref="Hotkey.TryParse"/> the config uses, so a typed chord is as valid as a
    /// recorded one. <paramref name="hotkey"/> is the formatted chord; on false <paramref name="error"/> says why in plain words.
    /// </summary>
    public static bool TryParseTyped(string? text, out string hotkey, out string? error)
    {
        hotkey = "";
        error = null;
        if (Hotkey.TryParse(text, out var parsed))
        {
            hotkey = parsed.ToString();
            return true;
        }
        var parts = (text ?? "").Split('+', StringSplitOptions.TrimEntries);
        var hasShift = parts[..^1].Any(p => p.Equals("Shift", StringComparison.OrdinalIgnoreCase));
        if (hasShift && Hotkey.TryParse("Ctrl+" + string.Join('+', parts.Where(p => !p.Equals("Shift", StringComparison.OrdinalIgnoreCase))), out var noShift)
            && Hotkey.IsNumberPad(noShift.Key))
            error = Rejection(HotkeyRejection.ShiftWithNumberPad);
        else if (Hotkey.TryParse("Ctrl+" + text, out _))
            error = Rejection(HotkeyRejection.NeedsModifier);
        else
            error = "I couldn't read that. Write it like Ctrl+Alt+A or Numpad1: Win, Ctrl, Alt or Shift first, joined with +, then one key.";
        return false;
    }

    /// <summary>H8, for a number-pad key on its own ("Num1"), which takes that digit from typing; else null.</summary>
    public static string? NumLockTip(string hotkey) =>
        Hotkey.TryParse(hotkey, out var parsed) && parsed.Modifiers == HotkeyModifiers.None && Hotkey.IsNumberPad(parsed.Key)
            ? $"While Num Lock is on, {parsed.KeyName} runs this flow instead of typing {parsed.KeyName[^1]}. "
              + "Turn Num Lock off or pause shortcuts to type numbers."
            : null;
}
