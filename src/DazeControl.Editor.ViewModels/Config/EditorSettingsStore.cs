using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DazeControl.Core;
using DazeControl.Core.Layout;

namespace DazeControl.Editor.ViewModels.Config;

/// <summary>How this PC shows the editor (D-068). Not configuration: the engine never reads it.</summary>
public sealed class EditorSettings
{
    /// <summary>The nav pane at full width; false = the 48-DIP icon strip (P9).</summary>
    public bool NavPaneOpen { get; set; } = true;

    /// <summary>Per-flow view choices by flow id (T13.6's layout choices, D-067).</summary>
    public Dictionary<string, FlowViewSettings> Flows { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The Flows list's view choices (D-087).</summary>
    public FlowListSettings FlowList { get; set; } = new();

    /// <summary>Where the window was last closed (D-098). Null = never saved: the default size.</summary>
    public WindowSettings? Window { get; set; }

    /// <summary>Keys a newer editor wrote: kept, so going back a version doesn't lose them.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Other { get; set; }
}

/// <summary>The Flows list on this PC. Groups start expanded; only the collapsed ones are named.</summary>
public sealed class FlowListSettings
{
    public List<string> CollapsedGroups { get; set; } = [];

    /// <summary>"Name" or "LastRun"; anything else (or missing) = Your order (D-088).</summary>
    public string? Sort { get; set; }

    /// <summary>"On", "Off", "NeedsFixing" or "NotInTray"; anything else (or missing) = All.</summary>
    public string? Filter { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Other { get; set; }
}

/// <summary>One flow's view choices on this PC: the Layout menu (D-067). Null = the default, not written.</summary>
public sealed class FlowViewSettings
{
    /// <summary>Wrap long chains after this many steps; 0 = Never.</summary>
    public int? WrapAfter { get; set; }

    public LayoutSpacing? Spacing { get; set; }

    public LayoutAlign? Align { get; set; }

    public LayoutLines? Lines { get; set; }

    [JsonIgnore]
    public bool IsDefault => WrapAfter is null && Spacing is null && Align is null && Lines is null && Other is not { Count: > 0 };

    /// <summary>The layout these choices give.</summary>
    public LayoutOptions ToOptions() => LayoutOptions.Of(WrapAfter ?? LayoutOptions.DefaultWrapAfter,
        Spacing ?? LayoutSpacing.Normal, Align ?? LayoutAlign.Centre, Lines ?? LayoutLines.Curved);

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Other { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(EditorSettings))]
internal sealed partial class EditorSettingsJson : JsonSerializerContext;

/// <summary>
/// <c>%LOCALAPPDATA%\DazeControl\editor.json</c> (D-068): read once at start (missing → defaults; unreadable or bad →
/// defaults and a log line), written atomically (temp file + rename) <see cref="SaveDelay"/> after the last change,
/// and on <see cref="Dispose"/>. A failed write is logged and stays pending.
/// </summary>
public sealed class EditorSettingsStore : IDisposable
{
    public static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(300);

    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly Lock _lock = new();
    private readonly Action<string> _log;
    private readonly ITimer _timer;
    private bool _dirty;

    public EditorSettingsStore(string path, TimeProvider? time = null, Action<string>? log = null)
    {
        Path = System.IO.Path.GetFullPath(path); // "editor.json" alone has no folder to create
        _log = log ?? (_ => { });
        Settings = Read();
        _timer = (time ?? TimeProvider.System).CreateTimer(_ => Flush(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public static string DefaultPath { get; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.FolderName, "editor.json");

    public string Path { get; }

    /// <summary>The current values. Read on the UI thread; change them only through <see cref="Update"/>.</summary>
    public EditorSettings Settings { get; }

    /// <summary>Changes the settings and writes them <see cref="SaveDelay"/> after the last change.</summary>
    public void Update(Action<EditorSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_lock)
        {
            change(Settings);
            _dirty = true;
        }
        _timer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>A saved rename (D-075): the flow's view choices follow its new id.</summary>
    public void RenameFlow(string oldId, string newId)
    {
        lock (_lock)
            if (!Settings.Flows.ContainsKey(oldId))
                return;
        Update(s =>
        {
            if (s.Flows.Remove(oldId, out var view))
                s.Flows[newId] = view;
        });
    }

    /// <summary>Writes a pending change now. Any thread; never throws for I/O.</summary>
    public void Flush()
    {
        lock (_lock)
        {
            if (!_dirty)
                return;
            try
            {
                var text = JsonSerializer.Serialize(Settings, EditorSettingsJson.Default.EditorSettings);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                var temp = Path + ".tmp";
                File.WriteAllText(temp, text, Utf8);
                File.Move(temp, Path, overwrite: true);
                _dirty = false;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log($"Couldn't save {Path}: {e.Message}");
            }
        }
    }

    private EditorSettings Read()
    {
        string text;
        try
        {
            text = File.ReadAllText(Path, Utf8);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return new EditorSettings();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log($"Couldn't read {Path}, using defaults: {e.Message}");
            return new EditorSettings();
        }
        try
        {
            var settings = JsonSerializer.Deserialize(text, EditorSettingsJson.Default.EditorSettings)
                ?? throw new JsonException("The file holds null.");
            settings.Flows = new Dictionary<string, FlowViewSettings>(settings.Flows ?? [], StringComparer.Ordinal);
            settings.FlowList ??= new FlowListSettings();
            settings.FlowList.CollapsedGroups ??= [];
            return settings;
        }
        catch (JsonException e)
        {
            _log($"{Path} isn't valid, using defaults: {e.Message}");
            return new EditorSettings();
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        Flush();
    }
}
