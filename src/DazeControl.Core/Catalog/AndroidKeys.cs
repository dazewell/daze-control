using DazeControl.Core.Model;

namespace DazeControl.Core.Catalog;

/// <summary>
/// The Android keys the inspector suggests (UX §5.4), with the key code the provider sends for each. One table, so
/// a suggestion can't drift from what <c>android.keys</c> understands (D-050).
/// </summary>
public static class AndroidKeys
{
    private static readonly (string Name, string Code)[] Table =
    [
        ("home", "KEYCODE_HOME"), ("back", "KEYCODE_BACK"),
        ("dpad_up", "KEYCODE_DPAD_UP"), ("dpad_down", "KEYCODE_DPAD_DOWN"), ("dpad_left", "KEYCODE_DPAD_LEFT"),
        ("dpad_right", "KEYCODE_DPAD_RIGHT"), ("dpad_center", "KEYCODE_DPAD_CENTER"), ("enter", "KEYCODE_ENTER"),
        ("menu", "KEYCODE_MENU"), ("media_play_pause", "KEYCODE_MEDIA_PLAY_PAUSE"), ("media_next", "KEYCODE_MEDIA_NEXT"),
        ("media_previous", "KEYCODE_MEDIA_PREVIOUS"), ("volume_up", "KEYCODE_VOLUME_UP"),
        ("volume_down", "KEYCODE_VOLUME_DOWN"), ("volume_mute", "KEYCODE_VOLUME_MUTE"), ("power", "KEYCODE_POWER"),
        ("wakeup", "KEYCODE_WAKEUP"), ("sleep", "KEYCODE_SLEEP"), ("settings", "KEYCODE_SETTINGS"),
        ("tv_input", "KEYCODE_TV_INPUT"),
    ];

    public static ValueList<string> Names { get; } = [.. Table.Select(k => k.Name)];

    /// <summary>"home" → "KEYCODE_HOME"; null for a name not in the table.</summary>
    public static string? Code(string name) =>
        Table.FirstOrDefault(k => string.Equals(k.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)).Code;
}
