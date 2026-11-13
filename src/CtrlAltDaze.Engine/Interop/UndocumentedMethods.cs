using System.Runtime.InteropServices;

namespace CtrlAltDaze.Engine.Interop;

internal static partial class UndocumentedMethods
{
    // uxtheme ordinals (1903+), as used by Notepad++/Explorer for dark Win32 popup menus. Process-wide (OSD D-009).
    internal enum PreferredAppMode { Default, AllowDark, ForceDark, ForceLight }

    [LibraryImport("uxtheme.dll", EntryPoint = "#135")]
    internal static partial PreferredAppMode SetPreferredAppMode(PreferredAppMode mode);

    [LibraryImport("uxtheme.dll", EntryPoint = "#136")]
    internal static partial void FlushMenuThemes();
}
