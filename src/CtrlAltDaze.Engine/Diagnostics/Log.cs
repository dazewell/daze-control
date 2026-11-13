using System.Diagnostics;
using System.Globalization;
using CtrlAltDaze.Core;
using CtrlAltDaze.Runtime.Diagnostics;

namespace CtrlAltDaze.Engine.Diagnostics;

/// <summary>
/// The engine's rolling text log (D-016): <c>engine.log</c>, rotated daily, 7 days kept (<see cref="LogRotation"/>).
/// Thread-safe; never throws. Secrets never reach it (D-014): steps log names, never values.
/// </summary>
internal static class Log
{
    private static readonly Lock Gate = new();
    private static string? _directory;
    private static DateTime _day;

    public static string DefaultDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.FolderName, "logs");

    public static void Initialize(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            _directory = directory;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Log disabled, {directory} not writable: {e.Message}");
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception exception) => Write("ERROR", $"{message}: {exception}");

    private static void Write(string level, string message)
    {
        var now = DateTime.Now;
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.CurrentManagedThreadId}] {level} {message}");
        Debug.WriteLine(line);
        if (_directory is null)
            return;
        lock (Gate)
        {
            if (now.Date != _day)
            {
                LogRotation.Rotate(_directory, now);
                _day = now.Date;
            }
            try
            {
                File.AppendAllText(Path.Combine(_directory, LogRotation.FileName), line + Environment.NewLine);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Logging must never take the engine down.
            }
        }
    }
}
