namespace CtrlAltDaze.Core;

/// <summary>
/// The names before the rename to Ctrl Alt Daze (M29, D-152): where the user's existing setup lives. Only the
/// carry-over (<see cref="OldSetup"/>, the credential fallback, the old engine's exit) reads these.
/// </summary>
public static class OldName
{
    /// <summary>Folder under %APPDATA% / %LOCALAPPDATA%.</summary>
    public const string FolderName = "DazeControl";

    /// <summary>Velopack's install folder under %LOCALAPPDATA%.</summary>
    public const string InstallFolderName = "DazeControlApp";

    /// <summary>Velopack's stub in the install folder (named after the pack title) and the engine in <c>current</c>.</summary>
    public const string StubName = "Daze Control.exe";

    public const string EngineExe = "DazeControl.exe";

    public const string CredentialPrefix = "DazeControl:";

    public const string AutostartValueName = "DazeControl";

    public const string TrayWindowClass = "DazeControl.Tray";

    public static string EngineMutexName(string userSid) => $@"Global\DazeControl.Engine-{userSid}";

    public static string InstallFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), InstallFolderName);
}
