using System.Diagnostics;
using System.Globalization;

namespace DazeControl.Runtime.Diagnostics;

/// <summary>
/// Daily rotation of the engine log, ported from the OSD (OSD D-028, D-016): the first write of a new day renames
/// <c>engine.log</c> to <c>engine-yyyy-MM-dd.log</c> (the day it was last written) and deletes archives older than
/// <see cref="KeepDays"/> days. <c>engine.log</c> is always today's. One process writes it, so no mutex.
/// </summary>
public static class LogRotation
{
    public const string FileName = "engine.log";

    /// <summary>Days kept, today's <c>engine.log</c> included.</summary>
    public const int KeepDays = 7;

    private const string Prefix = "engine-";
    private const string ArchiveFormat = "yyyy-MM-dd";

    public static void Rotate(string directory, DateTime now)
    {
        var today = now.Date;
        var current = Path.Combine(directory, FileName);
        try
        {
            var lastDay = File.Exists(current) ? File.GetLastWriteTime(current).Date : today;
            if (lastDay < today)
                Archive(current, Path.Combine(directory, ArchiveName(lastDay)));
            foreach (var archive in Directory.GetFiles(directory, Prefix + "*.log"))
            {
                if (ArchiveDay(Path.GetFileName(archive)) is { } day && day <= today.AddDays(-KeepDays))
                    TryDelete(archive);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Held open without delete sharing (a tail in a terminal): keep appending, retry tomorrow.
            Debug.WriteLine($"Log rotation skipped: {e.Message}");
        }
    }

    internal static string ArchiveName(DateTime day) =>
        $"{Prefix}{day.ToString(ArchiveFormat, CultureInfo.InvariantCulture)}.log";

    private static DateTime? ArchiveDay(string fileName) =>
        DateTime.TryParseExact(fileName[Prefix.Length..^".log".Length], ArchiveFormat,
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;

    private static void Archive(string current, string archive)
    {
        if (!File.Exists(archive))
        {
            File.Move(current, archive);
            return;
        }
        // Same day archived before (clock turned back): keep both, in order. Move first, so a locked log fails
        // before anything is appended and a retry can't append it twice.
        var moved = current + ".rotating";
        File.Move(current, moved, overwrite: true);
        File.AppendAllText(archive, File.ReadAllText(moved));
        File.Delete(moved);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Old log not deleted: {path}: {e.Message}");
        }
    }
}
