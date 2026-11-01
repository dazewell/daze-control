using System.Globalization;
using DazeControl.Core.Model;
using DazeControl.Core.Validation;

namespace DazeControl.Core.Yaml;

/// <summary>
/// <see cref="ConfigDocument"/> ⇄ <see cref="Configuration"/>. Reading never throws: what can't be read becomes
/// a <see cref="Problem"/> and a default (bad setting), a dropped item (bad library entry or node) or a dropped
/// edge. Problems inside a flow carry its id so only that flow is disabled (D-021). Writing omits defaults so
/// the editor's saves stay as short as hand-written files.
/// </summary>
public static class ConfigMapper
{
    public static Configuration ToModel(ConfigDocument document, ICollection<Problem> problems)
    {
        var reader = new Reader(problems);
        return reader.Read(document);
    }

    public static ConfigDocument ToDocument(Configuration config) => new()
    {
        Version = Configuration.CurrentVersion.ToString(CultureInfo.InvariantCulture),
        Settings = config.Settings == new Settings() ? null : new SettingsDocument
        {
            Popup = new PopupDocument
            {
                Position = EnumText(config.Settings.Popup.Position),
                Seconds = config.Settings.Popup.Seconds.ToString(CultureInfo.InvariantCulture),
            },
        },
        Library = config.Library == Library.Empty ? null : new LibraryDocument
        {
            Audio = NullIfEmpty(config.Library.Audio.ToDictionary(d => d.Key, d => new AudioDocument
            {
                Match = d.Match,
                Flow = d.Flow == AudioFlow.Output ? null : EnumText(d.Flow),
            })),
            Bluetooth = NullIfEmpty(config.Library.Bluetooth.ToDictionary(d => d.Key, d => new BluetoothDocument
            {
                Address = d.Address,
                Name = d.Name,
            })),
            Displays = NullIfEmpty(config.Library.Displays.ToDictionary(d => d.Key, d => new DisplayLayoutDocument
            {
                Monitors = [.. d.Monitors],
                Ccd = d.Data,
            })),
            Firetv = NullIfEmpty(config.Library.FireTv.ToDictionary(d => d.Key, d => new FireTvDocument
            {
                Address = d.Address,
                Adb = d.Adb,
                Inputs = NullIfEmpty(d.Inputs.ToDictionary(i => i.Key, i => i.Value.ToList())),
            })),
        },
        Flows = NullIfEmpty(config.Flows.ToDictionary(f => f.Id, ToDocument)),
    };

    private static FlowDocument ToDocument(Flow flow) => new()
    {
        Name = flow.Name == flow.Id ? null : flow.Name,
        Glyph = flow.Glyph,
        Tray = flow.Tray == new TraySettings() ? null : new TrayDocument
        {
            Show = flow.Tray.Show ? null : "false",
            Folder = flow.Tray.Folder,
            Separator = flow.Tray.Separator ? "true" : null,
        },
        Hotkeys = flow.Hotkeys.Count == 0 ? null : [.. flow.Hotkeys],
        WhenRunning = flow.WhenRunning == WhenRunning.Ignore ? null : EnumText(flow.WhenRunning),
        Popup = flow.Popup ? null : "false",
        Enabled = flow.Enabled ? null : "false",
        Nodes = [.. flow.Nodes.Select(ToDocument)],
    };

    private static NodeDocument ToDocument(Node node)
    {
        var document = new NodeDocument
        {
            Id = node.Id,
            Name = node.Name,
            After = node.After.Count == 0 ? null : [.. node.After.Select(e => new AfterDocument
            {
                Id = e.From,
                When = e.When == EdgeWhen.Success ? null : EnumText(e.When),
            })],
            OnFailure = node.OnFailure == FailurePolicy.Stop ? null : EnumText(node.OnFailure),
        };
        switch (node)
        {
            case StepNode step:
                document.Step = step.Step;
                document.With = step.With.Count == 0 ? null : step.With.ToDictionary(p => p.Key, p => new ParamDocument(p.Value));
                document.Timeout = Duration.Format(step.Timeout);
                document.Retry = step.Retry is { } retry ? new RetryDocument
                {
                    Count = retry.Count.ToString(CultureInfo.InvariantCulture),
                    Delay = retry.Delay == TimeSpan.Zero ? null : Duration.Format(retry.Delay),
                } : null;
                break;
            case GroupNode group:
                document.Group = [.. group.Members];
                break;
        }
        return document;
    }

    private static Dictionary<string, T>? NullIfEmpty<T>(Dictionary<string, T> map) => map.Count == 0 ? null : map;

    /// <summary>Enum values are written camelCase: "bottomCenter", "failure".</summary>
    internal static string EnumText<T>(T value) where T : struct, Enum
    {
        var text = value.ToString();
        return char.ToLowerInvariant(text[0]) + text[1..];
    }

    private sealed class Reader(ICollection<Problem> problems)
    {
        private string? _flowId;

        public Configuration Read(ConfigDocument document)
        {
            if (document.Version is { } version && version != Configuration.CurrentVersion.ToString(CultureInfo.InvariantCulture))
                Error("version", $"Version {version} isn't supported; this build reads version {Configuration.CurrentVersion}.");
            return new Configuration
            {
                Settings = ReadSettings(document.Settings),
                Library = ReadLibrary(document.Library),
                Flows = new((document.Flows ?? []).Select(f => ReadFlow(f.Key, f.Value))),
            };
        }

        private Settings ReadSettings(SettingsDocument? document)
        {
            var defaults = new PopupSettings();
            if (document?.Popup is not { } popup)
                return new Settings();
            var seconds = Int(popup.Seconds, "settings.popup.seconds") ?? defaults.Seconds;
            if (seconds is < 1 or > 60)
            {
                Error("settings.popup.seconds", "Use 1 to 60 seconds.");
                seconds = defaults.Seconds;
            }
            return new Settings
            {
                Popup = new PopupSettings(Enum(popup.Position, "settings.popup.position", defaults.Position), seconds),
            };
        }

        private Library ReadLibrary(LibraryDocument? document)
        {
            var audio = new List<AudioDevice>();
            foreach (var (key, item) in document?.Audio ?? [])
            {
                var path = $"library.audio.{key}";
                // A bad entry is dropped, not defaulted: its references then fail their own flows.
                if (string.IsNullOrWhiteSpace(item?.Match))
                    Error(path + ".match", "Say which device: part of its name as Windows shows it.");
                else if (TryEnum(item.Flow, path + ".flow", AudioFlow.Output, out var flow))
                    audio.Add(new AudioDevice(key, item.Match, flow));
            }
            var bluetooth = new List<BluetoothDevice>();
            foreach (var (key, item) in document?.Bluetooth ?? [])
            {
                if (string.IsNullOrWhiteSpace(item?.Address))
                    Error($"library.bluetooth.{key}.address", "Bluetooth address missing (like B8:20:8E:ED:48:FC).");
                else
                    bluetooth.Add(new BluetoothDevice(key, item.Address, item.Name));
            }
            var displays = new List<DisplayLayout>();
            foreach (var (key, item) in document?.Displays ?? [])
            {
                // A damaged layout is dropped like any bad entry, so its flows keep their last good version (D-021).
                if (string.IsNullOrWhiteSpace(item?.Ccd) || item.Monitors is not { Count: > 0 }
                    || new DisplayLayout(key, new(item.Monitors), item.Ccd) is not { } layout || !layout.IsWellFormed())
                    Error($"library.displays.{key}", "This layout is incomplete or damaged; capture it again.");
                else
                    displays.Add(layout);
            }
            var fireTv = new List<FireTvDevice>();
            foreach (var (key, item) in document?.Firetv ?? [])
            {
                var path = $"library.firetv.{key}";
                if (string.IsNullOrWhiteSpace(item?.Address))
                {
                    Error(path + ".address", "Fire TV address missing (like 192.168.1.50).");
                    continue;
                }
                var inputs = new List<KeyValuePair<string, ValueList<string>>>();
                var ok = true;
                foreach (var (input, moves) in item.Inputs ?? [])
                {
                    if ((moves ?? []).FirstOrDefault(m => m is not ("up" or "down")) is { } bad)
                    {
                        Error($"{path}.inputs.{input}", $"\"{bad}\" isn't a move: use up or down.");
                        ok = false;
                    }
                    else
                    {
                        inputs.Add(KeyValuePair.Create(input, new ValueList<string>(moves ?? [])));
                    }
                }
                if (ok)
                    fireTv.Add(new FireTvDevice(key, item.Address, new(inputs), string.IsNullOrWhiteSpace(item.Adb) ? null : item.Adb));
            }
            return new Library { Audio = new(audio), Bluetooth = new(bluetooth), Displays = new(displays), FireTv = new(fireTv) };
        }

        private Flow ReadFlow(string id, FlowDocument? document)
        {
            _flowId = id;
            document ??= new FlowDocument();
            var path = $"flows.{id}";
            var tray = new TraySettings
            {
                Show = Bool(document.Tray?.Show, path + ".tray.show", true),
                Folder = document.Tray?.Folder,
                Separator = Bool(document.Tray?.Separator, path + ".tray.separator", false),
            };
            var nodes = new List<Node>();
            for (var i = 0; i < (document.Nodes?.Count ?? 0); i++)
            {
                if (ReadNode(document.Nodes![i], $"{path}.nodes", i) is { } node)
                    nodes.Add(node);
            }
            var flow = new Flow(id, string.IsNullOrWhiteSpace(document.Name) ? id : document.Name, new(nodes))
            {
                Glyph = document.Glyph,
                Tray = tray,
                Hotkeys = new(document.Hotkeys ?? []),
                WhenRunning = Enum(document.WhenRunning, path + ".whenRunning", WhenRunning.Ignore),
                Popup = Bool(document.Popup, path + ".popup", true),
                Enabled = Bool(document.Enabled, path + ".enabled", true),
            };
            _flowId = null;
            return flow;
        }

        private Node? ReadNode(NodeDocument? document, string nodesPath, int index)
        {
            if (document is null || string.IsNullOrWhiteSpace(document.Id))
            {
                Error($"{nodesPath}[{index}].id", "Every step and card needs an id.");
                return null;
            }
            var path = $"{nodesPath}.{document.Id}";
            var after = new List<Edge>();
            foreach (var item in document.After ?? [])
            {
                if (item.Malformed is { } malformed)
                    Error(path + ".after", malformed);
                else if (string.IsNullOrWhiteSpace(item.Id))
                    Error(path + ".after", "An after entry needs the id of a step or card.");
                else
                    after.Add(new Edge(item.Id, Enum(item.When, path + ".after", EdgeWhen.Success)));
            }
            var onFailure = Enum(document.OnFailure, path + ".onFailure", FailurePolicy.Stop);

            if (document.Step is not null && document.Group is not null)
            {
                Error(path, "A node is either a step or a group, not both.");
                return null;
            }
            if (document.Group is { } members)
            {
                if (document.With is not null || document.Timeout is not null || document.Retry is not null)
                    Error(path, "A card has no with, timeout or retry; set them on its members.");
                return new GroupNode(document.Id, new(members)) { Name = document.Name, After = new(after), OnFailure = onFailure };
            }
            if (string.IsNullOrWhiteSpace(document.Step))
            {
                Error(path + ".step", "Say what this step does (step: bluetooth.connect, …) or make it a card (group: […]).");
                return null;
            }
            return new StepNode(document.Id, document.Step)
            {
                Name = document.Name,
                After = new(after),
                OnFailure = onFailure,
                With = new((document.With ?? []).Select(p => KeyValuePair.Create(p.Key, p.Value.ToValue()))),
                Timeout = DurationOrNull(document.Timeout, path + ".timeout"),
                Retry = ReadRetry(document.Retry, path + ".retry"),
            };
        }

        private RetryPolicy? ReadRetry(RetryDocument? document, string path)
        {
            if (document is null)
                return null;
            var count = Int(document.Count, path + ".count");
            if (count is null)
            {
                if (document.Count is null)
                    Error(path + ".count", "Say how many more tries (count: 3).");
                return null;
            }
            return new RetryPolicy(count.Value, DurationOrNull(document.Delay, path + ".delay") ?? TimeSpan.Zero);
        }

        private T Enum<T>(string? text, string path, T fallback) where T : struct, Enum =>
            TryEnum(text, path, fallback, out var value) ? value : fallback;

        private bool TryEnum<T>(string? text, string path, T fallback, out T value) where T : struct, Enum
        {
            value = fallback;
            if (text is null)
                return true;
            // Enum.TryParse also takes numbers and "a, b" lists; neither is meaningful in YAML.
            if (text.Length > 0 && char.IsAsciiLetter(text[0]) && !text.Contains(',', StringComparison.Ordinal)
                && System.Enum.TryParse(text, ignoreCase: true, out value))
                return true;
            value = fallback;
            var choices = string.Join(", ", System.Enum.GetValues<T>().Select(EnumText));
            Error(path, $"\"{text}\" isn't one of: {choices}.");
            return false;
        }

        private bool Bool(string? text, string path, bool fallback)
        {
            if (text is null)
                return fallback;
            if (bool.TryParse(text, out var value))
                return value;
            Error(path, $"\"{text}\" isn't true or false.");
            return fallback;
        }

        private int? Int(string? text, string path)
        {
            if (text is null)
                return null;
            if (int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
                return value;
            Error(path, $"\"{text}\" isn't a whole number.");
            return null;
        }

        private TimeSpan? DurationOrNull(string? text, string path)
        {
            if (text is null)
                return null;
            if (Duration.TryParse(text, out var value))
                return value;
            Error(path, $"\"{text}\" isn't a duration (like 500ms, 20s, 2m, 1m30s).");
            return null;
        }

        private void Error(string path, string message) => problems.Add(new Problem(Severity.Error, path, message, _flowId));
    }
}
