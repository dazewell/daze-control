using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct2D.Common;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.WindowsAndMessaging;

namespace DazeControl.Engine.Shell;

/// <summary>
/// The pop-up's colours and sizes, read from Windows on every show: theme, accent, text size and Animation effects
/// can all change while the engine runs. Values are the WinUI 3 theme resources the editor uses (UX §5.3); in a
/// contrast theme, the system colours on a solid <see cref="Background"/> instead of acrylic.
/// </summary>
internal sealed record PopupTheme(bool Light, D2D1_COLOR_F Text, D2D1_COLOR_F Secondary, D2D1_COLOR_F Track,
    D2D1_COLOR_F Accent, D2D1_COLOR_F Success, D2D1_COLOR_F Caution, D2D1_COLOR_F Critical, float TextScale,
    bool Animations)
{
    /// <summary>Transparent (the acrylic shows) except in a contrast theme.</summary>
    public D2D1_COLOR_F Background { get; init; }

    public bool HighContrast { get; init; }

    /// <summary>Text and glyph on an accent fill (TextOnAccentFillColorPrimary); in a contrast theme HighlightText.</summary>
    public D2D1_COLOR_F OnAccent { get; init; }

    /// <summary>The focused choice's fill in a contrast theme (the Highlight system colour); unused otherwise.</summary>
    public D2D1_COLOR_F Highlight { get; init; }

    private const string Personalize = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static PopupTheme Read()
    {
        if (ReadHighContrast())
        {
            // Run states stay apart by glyph (UX §5.3), so every status colour can be the window text.
            var text = SystemColour(SYS_COLOR_INDEX.COLOR_WINDOWTEXT);
            return new(false, text, text, SystemColour(SYS_COLOR_INDEX.COLOR_GRAYTEXT), SystemColour(SYS_COLOR_INDEX.COLOR_HOTLIGHT),
                text, text, text, ReadTextScale(), ReadAnimations())
            {
                Background = SystemColour(SYS_COLOR_INDEX.COLOR_WINDOW),
                HighContrast = true,
                OnAccent = SystemColour(SYS_COLOR_INDEX.COLOR_HIGHLIGHTTEXT),
                Highlight = SystemColour(SYS_COLOR_INDEX.COLOR_HIGHLIGHT),
            };
        }
        // The pop-up is shell chrome like the volume flyout: it follows the Windows mode, not the app mode.
        var light = Registry.GetValue(Personalize, "SystemUsesLightTheme", 0) is int value && value != 0;
        var accent = ReadAccent(light);
        return light
            ? new(true, Argb(0xE4000000), Argb(0x9E000000), Argb(0x29000000), accent, Argb(0xFF0F7B0F),
                Argb(0xFF9D5D00), Argb(0xFFC42B1C), ReadTextScale(), ReadAnimations()) { OnAccent = Argb(0xFFFFFFFF) }
            : new(false, Argb(0xFFFFFFFF), Argb(0xC5FFFFFF), Argb(0x37FFFFFF), accent, Argb(0xFF6CCB5F),
                Argb(0xFFFCE100), Argb(0xFFFF99A4), ReadTextScale(), ReadAnimations()) { OnAccent = Argb(0xFF000000) };
    }

    /// <summary>AccentFillColorDefault: SystemAccentColorDark1 in light mode, Light2 in dark (AccentPalette entries 4 / 1).</summary>
    private static D2D1_COLOR_F ReadAccent(bool light)
    {
        if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Accent",
                "AccentPalette", null) is byte[] { Length: >= 32 } palette)
        {
            var i = (light ? 4 : 1) * 4;
            return new D2D1_COLOR_F { r = palette[i] / 255f, g = palette[i + 1] / 255f, b = palette[i + 2] / 255f, a = 1 };
        }
        return light ? Argb(0xFF005FB8) : Argb(0xFF60CDFF);
    }

    private static unsafe bool ReadHighContrast()
    {
        var contrast = new HIGHCONTRASTW { cbSize = (uint)sizeof(HIGHCONTRASTW) };
        return PInvoke.SystemParametersInfo(SYSTEM_PARAMETERS_INFO_ACTION.SPI_GETHIGHCONTRAST, contrast.cbSize, &contrast, 0)
            && (contrast.dwFlags & HIGHCONTRASTW_FLAGS.HCF_HIGHCONTRASTON) != 0;
    }

    private static D2D1_COLOR_F SystemColour(SYS_COLOR_INDEX index)
    {
        var bgr = PInvoke.GetSysColor(index);
        return new D2D1_COLOR_F { r = (bgr & 0xFF) / 255f, g = ((bgr >> 8) & 0xFF) / 255f, b = ((bgr >> 16) & 0xFF) / 255f, a = 1 };
    }

    /// <summary>Settings › Accessibility › Text size, 1.0–2.25: the pop-up grows taller with it (UX §5.9).</summary>
    private static float ReadTextScale() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Accessibility", "TextScaleFactor", 100) is int percent
            ? Math.Clamp(percent, 100, 225) / 100f
            : 1f;

    /// <summary>Settings › Accessibility › Visual effects › Animation effects.</summary>
    private static unsafe bool ReadAnimations()
    {
        BOOL enabled = true;
        return !PInvoke.SystemParametersInfo(SYSTEM_PARAMETERS_INFO_ACTION.SPI_GETCLIENTAREAANIMATION, 0, &enabled, 0) || enabled;
    }

    private static D2D1_COLOR_F Argb(uint argb) => new()
    {
        a = (argb >> 24) / 255f,
        r = ((argb >> 16) & 0xFF) / 255f,
        g = ((argb >> 8) & 0xFF) / 255f,
        b = (argb & 0xFF) / 255f,
    };
}
