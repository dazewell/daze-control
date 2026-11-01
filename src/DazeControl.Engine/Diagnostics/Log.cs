using System.Diagnostics;
using System.Globalization;
using DazeControl.Core;

namespace DazeControl.Engine.Diagnostics;

/// <summary>
/// Minimal append-only file log, so an agent can see what the tray engine did. Thread-safe; never throws.
/// T3.1 replaces it with the OSD's rotating log (OSD D-028, D-016).
/// </summary>
internal static class Log
{
    private static readonly Lock Gate = new();
    private static string? _path;

    public static string DefaultDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.FolderName, "logs");

    public static void Initialize(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            _path = Path.Combine(directory, "engine.log");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Log disabled, {directory} not writable: {e.Message}");
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception exception) => Write("ERROR", $"{message}: {exception}");

    private static void Write(string level, string message)
    {
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {level} {message}");
        Debug.WriteLine(line);
        if (_path is null)
            return;
        lock (Gate)
        {
            try
            {
                File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Logging must never take the engine down.
            }
        }
    }
}
