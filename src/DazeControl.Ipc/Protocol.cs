namespace DazeControl.Ipc;

/// <summary>Named-pipe protocol constants (D-011).</summary>
public static class Protocol
{
    public const int Version = 1;

    /// <summary>Pipe name for a user; the SID keeps sessions of different users apart.</summary>
    public static string PipeName(string userSid) => $"DazeControl-{userSid}";
}
