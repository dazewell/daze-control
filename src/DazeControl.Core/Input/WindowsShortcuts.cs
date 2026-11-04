namespace DazeControl.Core.Input;

/// <summary>
/// Windows 11's own Win shortcuts that a shortcut key could clash with (D-073). <c>RegisterHotKey</c> fails with
/// the same 1409 whoever holds a chord (X-007), so a refused chord on this list is named "Windows uses…" (H5) and
/// any other one "Another app uses…" (H6). A missing entry only means the generic H6 text, never a wrong refusal.
/// </summary>
public static class WindowsShortcuts
{
    private static readonly HashSet<Hotkey> Known = Build();

    /// <summary>True when Windows itself uses <paramref name="hotkey"/>.</summary>
    public static bool Contains(Hotkey hotkey) => Known.Contains(hotkey);

    private static HashSet<Hotkey> Build()
    {
        var set = new HashSet<Hotkey>();
        void Add(string text)
        {
            if (!Hotkey.TryParse(text, out var hotkey))
                throw new InvalidOperationException($"Bad built-in shortcut \"{text}\".");
            set.Add(hotkey);
        }

        // Win + any letter: Windows 11 uses every one (Quick Settings, Copilot, Explorer, lock, Run, clipboard
        // history, Windows tip on J, Mixed Reality input on Y, Snap layouts…).
        foreach (var letter in "ABCDEFGHIJKLMNOPQRSTUVWXYZ")
            Add("Win+" + letter);
        // Win + a taskbar position, plain or with Shift/Ctrl/Alt/Ctrl+Shift.
        foreach (var digit in "0123456789")
        {
            Add("Win+" + digit);
            Add("Win+Shift+" + digit);
            Add("Win+Ctrl+" + digit);
            Add("Win+Alt+" + digit);
            Add("Win+Ctrl+Shift+" + digit);
        }
        foreach (var text in (ReadOnlySpan<string>)
        [
            "Win+.", "Win+;", "Win+,", "Win+=", "Win+-", "Win+/", // emoji, emoji, peek, Magnifier in/out, IME
            "Win+Shift+S", "Win+Shift+C", "Win+Shift+M", "Win+Shift+R", "Win+Shift+T", "Win+Shift+V",
            "Win+Ctrl+D", "Win+Ctrl+F4", "Win+Ctrl+C", "Win+Ctrl+F", "Win+Ctrl+N", "Win+Ctrl+O", "Win+Ctrl+Q",
            "Win+Ctrl+S", "Win+Ctrl+V", "Win+Ctrl+Shift+B",
            "Win+Alt+B", "Win+Alt+D", "Win+Alt+G", "Win+Alt+K", "Win+Alt+R",
            "Win+F1",
        ])
        {
            Add(text);
        }
        return set;
    }
}
