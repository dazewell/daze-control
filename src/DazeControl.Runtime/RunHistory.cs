using System.Text.Json;
using System.Text.Json.Serialization;
using DazeControl.Core.Runs;

namespace DazeControl.Runtime;

/// <summary>
/// The last <see cref="Capacity"/> runs, in memory and as JSON lines in <c>history.jsonl</c> (FEATURES F5). Fail-soft:
/// a file that can't be read or written only costs the persisted part (<see cref="LoadError"/> and
/// <see cref="WriteFailed"/> tell the log); a corrupt line is skipped. The file is cut back to the last <see cref="Capacity"/> lines once it holds
/// twice that many. Thread-safe.
/// </summary>
public sealed class RunHistory
{
    public const int Capacity = 100;

    private readonly Lock _lock = new();
    private readonly string? _path;
    private readonly List<RunRecord> _records = [];
    private int _linesInFile;
    private bool _unterminated;

    /// <param name="path">The history file; null keeps history in memory only.</param>
    public RunHistory(string? path)
    {
        _path = path;
        if (path is null)
            return;
        try
        {
            if (!File.Exists(path))
                return;
            var text = File.ReadAllText(path);
            _unterminated = text.Length > 0 && !text.EndsWith('\n'); // a crash cut the last record short
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            _linesInFile = lines.Length;
            _records.AddRange(lines.Select(TryParse).OfType<RunRecord>().TakeLast(Capacity));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LoadError = e;
        }
    }

    /// <summary>Why the file couldn't be read at start, for the log; history then starts empty.</summary>
    public Exception? LoadError { get; }

    /// <summary>Raised (on the caller's thread) when the file can't be read or written.</summary>
    public event Action<Exception>? WriteFailed;

    /// <summary>Oldest first.</summary>
    public IReadOnlyList<RunRecord> Recent
    {
        get
        {
            lock (_lock)
                return [.. _records];
        }
    }

    public void Add(RunRecord record)
    {
        lock (_lock)
        {
            _records.Add(record);
            if (_records.Count > Capacity)
                _records.RemoveAt(0);
            if (_path is null)
                return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
                if (_linesInFile + 1 >= 2 * Capacity)
                {
                    // Rewrite atomically with what's in memory (D-004's write pattern): a crash leaves old or new.
                    var temp = _path + ".tmp";
                    File.WriteAllLines(temp, _records.Select(Serialize));
                    File.Move(temp, _path, overwrite: true);
                    _linesInFile = _records.Count;
                }
                else
                {
                    // Never glue a record onto a cut-off one: both would be lost at the next start.
                    File.AppendAllText(_path, (_unterminated ? "\n" : "") + Serialize(record) + "\n");
                    _unterminated = false;
                    _linesInFile++;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                WriteFailed?.Invoke(e);
            }
        }
    }

    private static string Serialize(RunRecord record) => JsonSerializer.Serialize(record, HistoryJson.Default.RunRecord);

    private static RunRecord? TryParse(string line)
    {
        try
        {
            return JsonSerializer.Deserialize(line, HistoryJson.Default.RunRecord);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Source-generated JSON for the history file: no reflection under Native AOT.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RunRecord))]
internal sealed partial class HistoryJson : JsonSerializerContext;
