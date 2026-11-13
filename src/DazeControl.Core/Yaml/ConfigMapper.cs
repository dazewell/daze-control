using System.Globalization;
using DazeControl.Core.Catalog;
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
    /// <summary>A text input's <c>kind</c> (T23.7).</summary>
    public const string TextInputKind = "text";

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
            Popup = config.Settings.Popup == new PopupSettings() ? null : new PopupDocument
            {
                Position = EnumText(config.Settings.Popup.Position),
                Seconds = config.Settings.Popup.Seconds.ToString(CultureInfo.InvariantCulture),
            },
            StepDefaults = NullIfEmpty(config.Settings.StepDefaults.ToDictionary(d => d.Key, d => new StepDefaultDocument
            {
                Timeout = Duration.Format(d.Value.Timeout),
                Retry = RetryToDocument(d.Value.RetryCount, d.Value.RetryDelay),
                OnFailure = d.Value.OnFailure is { } policy ? EnumText(policy) : null,
                Only = d.Value.Only,
            })),
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
            Android = NullIfEmpty(config.Library.Android.ToDictionary(d => d.Key, d => new AndroidDocument
            {
                Address = d.Address,
                Adb = d.Adb,
                Inputs = d.Inputs.Count == 0 ? null : new ParamDocument(new MapParam(new(d.Inputs.Select(i =>
                    KeyValuePair.Create(i.Key, (ParamValue)new ListParam(new(i.Value.Select(m => (ParamValue)m)))))))),
            })),
            Bound = NullIfEmpty(config.Library.Bound.ToDictionary(d => d.Key, d => new BoundDocument
            {
                Bluetooth = d.Bluetooth,
                Audio = d.Audio,
                Name = d.Name,
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
        Triggers = flow.Triggers.Count == 0 ? null : [.. flow.Triggers.Select(TriggerMapper.ToDocument)],
        OnlyIf = flow.OnlyIf.Count == 0 ? null : [.. flow.OnlyIf.Select((c, i) => OnlyIfToDocument(c, i))],
        OnlyIfByHand = flow.OnlyIfByHand && flow.OnlyIf.Count > 0 ? "true" : null,
        WhenRunning = flow.WhenRunning == WhenRunning.Ignore ? null : EnumText(flow.WhenRunning),
        Popup = flow.Popup ? null : "false",
        Enabled = flow.Enabled ? null : "false",
        Inputs = flow.Inputs.Count == 0 ? null : flow.Inputs.ToDictionary(i => i.Key, i => new InputDocument
        {
            Kind = i.Value.Kind is { } kind ? EnumText(kind) : TextInputKind,
            Name = i.Value.Label == i.Key ? null : i.Value.Label,
            Default = i.Value.Default,
            Ask = i.Value.Ask ? "true" : null,
            Timeout = i.Value.Ask && i.Value.Timeout is { } timeout ? Duration.Format(timeout) : null,
            OnTimeout = i.Value.Ask && !string.IsNullOrEmpty(i.Value.OnTimeout) ? i.Value.OnTimeout : null,
        }),
        Outputs = flow.Outputs.Count == 0 ? null : flow.Outputs.ToDictionary(o => o.Key, o => o.Value),
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
            OnFailure = node.OnFailure is { } policy ? EnumText(policy) : null, // an explicit stop is kept (D-080)
        };
        switch (node)
        {
            case StepNode step:
                document.Step = step.Step;
                document.With = step.With.Count == 0 ? null : step.With.ToDictionary(p => p.Key, p => new ParamDocument(p.Value));
                document.Timeout = Duration.Format(step.Timeout);
                document.Retry = RetryToDocument(step.RetryCount, step.RetryDelay);
                document.Only = step.Only;
                document.Enabled = step.Enabled ? null : "false";
                break;
            case GroupNode group:
                document.Group = [.. group.Members];
                break;
        }
        return document;
    }

    /// <summary>An "Only run if" check: a step without edges; its id is left out while it is the one reading would give it.</summary>
    private static NodeDocument OnlyIfToDocument(StepNode check, int index)
    {
        var document = ToDocument(check);
        if (check.Id == OnlyIfId(index))
            document.Id = null;
        return document;
    }

    /// <summary>The id an "Only run if" check without one gets (T25.1): its place in the list.</summary>
    public static string OnlyIfId(int index) => $"if{index + 1}";

    private static RetryDocument? RetryToDocument(int? count, TimeSpan? delay) =>
        count is null && delay is null ? null : new RetryDocument
        {
            Count = count?.ToString(CultureInfo.InvariantCulture),
            Delay = Duration.Format(delay),
        };

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
            if (document is null)
                return new Settings();
            return new Settings { Popup = ReadPopup(document.Popup), StepDefaults = ReadStepDefaults(document.StepDefaults) };
        }

        private PopupSettings ReadPopup(PopupDocument? popup)
        {
            var defaults = new PopupSettings();
            if (popup is null)
                return defaults;
            var seconds = Int(popup.Seconds, "settings.popup.seconds") ?? defaults.Seconds;
            if (seconds is < 1 or > 60)
            {
                Error("settings.popup.seconds", "Use 1 to 60 seconds.");
                seconds = defaults.Seconds;
            }
            return new PopupSettings(Enum(popup.Position, "settings.popup.position", defaults.Position), seconds);
        }

        /// <summary>A4: a bad entry or field is a Problem and is dropped; a flow is never disabled by it.</summary>
        private ValueDictionary<StepDefault> ReadStepDefaults(Dictionary<string, StepDefaultDocument>? documents)
        {
            var entries = new List<KeyValuePair<string, StepDefault>>();
            foreach (var (id, document) in documents ?? [])
            {
                var path = "settings.stepDefaults." + id;
                if (ProviderCatalog.Find(id) is null)
                {
                    Error(path, $"\"{id}\" isn't a step type.");
                    continue;
                }
                if (document is null)
                {
                    Error(path, "Write the defaults as { timeout: 30s, retry: { count: 2 }, onFailure: skip }.");
                    continue;
                }
                var timeout = DurationOrNull(document.Timeout, path + ".timeout");
                if (timeout is { } t && (t <= TimeSpan.Zero || t > ConfigValidator.MaxDuration))
                {
                    Error(path + ".timeout", "Give up after more than 0 s and at most 24h.");
                    timeout = null;
                }
                int? count = null;
                TimeSpan? delay = null;
                if (document.Retry is { } retry)
                {
                    count = Int(retry.Count, path + ".retry.count");
                    if (count is < 0 or > ConfigValidator.MaxRetries)
                    {
                        Error(path + ".retry.count", $"Try again 0 to {ConfigValidator.MaxRetries} times.");
                        count = null;
                    }
                    delay = DurationOrNull(retry.Delay, path + ".retry.delay");
                    if (delay is { } d && (d < TimeSpan.Zero || d > ConfigValidator.MaxDuration))
                    {
                        Error(path + ".retry.delay", "Wait 0 s to 24h between tries.");
                        delay = null;
                    }
                }
                FailurePolicy? onFailure = TryEnum(document.OnFailure, path + ".onFailure", FailurePolicy.Stop, out var policy) && document.OnFailure is not null
                    ? policy : null;
                var only = string.IsNullOrWhiteSpace(document.Only) ? null : document.Only.Trim();
                if (only is not null && only != StepNode.OnlyAlways && ProviderCatalog.Find(id)!.Conditions.All(c => c.Id != only))
                {
                    var ids = ProviderCatalog.Find(id)!.Conditions.Select(c => c.Id).Append(StepNode.OnlyAlways);
                    Error(path + ".only", $"Use one of: {string.Join(", ", ids)}.");
                    only = null;
                }
                var entry = new StepDefault { Timeout = timeout, RetryCount = count, RetryDelay = delay, OnFailure = onFailure, Only = only };
                if (!entry.IsEmpty)
                    entries.Add(KeyValuePair.Create(id, entry));
            }
            return new(entries);
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
            if (document?.Firetv is not null)
                Error("library.firetv", "firetv: is now called android: (it works with any Android device); rename the section, its entries stay the same.");
            var android = new List<AndroidDevice>();
            foreach (var (key, item) in document?.Android ?? [])
            {
                var path = $"library.android.{key}";
                if (string.IsNullOrWhiteSpace(item?.Address))
                {
                    Error(path + ".address", "Android device address missing (like 192.168.1.50).");
                    continue;
                }
                var inputs = new List<KeyValuePair<string, ValueList<string>>>();
                var ok = true;
                IEnumerable<KeyValuePair<string, ParamValue>>? entries = item.Inputs?.ToValue() switch
                {
                    null => [],
                    MapParam map => map.Entries,
                    ScalarParam { Text: "" } => [],
                    _ => (IEnumerable<KeyValuePair<string, ParamValue>>?)null,
                };
                if (entries is null)
                {
                    Error(path + ".inputs", "Inputs are names with their moves, like  hdmi2: [down, down].");
                    continue;
                }
                foreach (var (input, value) in entries)
                {
                    // A lone move is a one-move list; anything else that isn't up or down rejects the entry.
                    var moves = value switch
                    {
                        ScalarParam scalar => [scalar.Text],
                        ListParam list => list.Items.Select(m => m is ScalarParam s ? s.Text : null).ToList(),
                        _ => new List<string?> { null },
                    };
                    var bad = moves.FindIndex(m => m is not ("up" or "down"));
                    if (bad >= 0)
                    {
                        Error($"{path}.inputs.{input}", moves[bad] is { Length: > 0 } text
                            ? $"\"{text}\" isn't a move: use up or down."
                            : "Each move is up or down.");
                        ok = false;
                    }
                    else
                    {
                        inputs.Add(KeyValuePair.Create(input, new ValueList<string>(moves!)));
                    }
                }
                if (ok)
                    android.Add(new AndroidDevice(key, item.Address, new(inputs), string.IsNullOrWhiteSpace(item.Adb) ? null : item.Adb));
            }
            var bound = new List<BoundDevice>();
            foreach (var (key, item) in document?.Bound ?? [])
            {
                var path = $"library.bound.{key}";
                if (string.IsNullOrWhiteSpace(item?.Bluetooth))
                    Error(path + ".bluetooth", "Say which Bluetooth device of your Library it binds (bluetooth: technics).");
                else if (string.IsNullOrWhiteSpace(item.Audio))
                    Error(path + ".audio", "Say which audio device of your Library it binds (audio: technics).");
                else
                    bound.Add(new BoundDevice(key, item.Bluetooth, item.Audio, string.IsNullOrWhiteSpace(item.Name) ? null : item.Name));
            }
            return new Library
            {
                Audio = new(audio), Bluetooth = new(bluetooth), Displays = new(displays), Android = new(android), Bound = new(bound),
            };
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
                Triggers = TriggerMapper.Read(document.Triggers, path + ".triggers", id, problems),
                OnlyIf = ReadOnlyIf(document.OnlyIf, path + ".onlyIf"),
                OnlyIfByHand = Bool(document.OnlyIfByHand, path + ".onlyIfByHand", false),
                WhenRunning = Enum(document.WhenRunning, path + ".whenRunning", WhenRunning.Ignore),
                Popup = Bool(document.Popup, path + ".popup", true),
                Enabled = Bool(document.Enabled, path + ".enabled", true),
                Inputs = ReadInputs(document.Inputs, path + ".inputs"),
                Outputs = new((document.Outputs ?? []).Select(o => KeyValuePair.Create(o.Key, o.Value ?? ""))),
            };
            if (document.Gamepad is { Count: > 0 })
                problems.Add(new Problem(Severity.Warning, path + ".gamepad", "Controller chords were removed (a controller now drives the menu step only): this setting is ignored and dropped on the next save.", id));
            _flowId = null;
            return flow;
        }

        /// <summary>
        /// "Only run if" checks (T25.1): each a step without edges or a policy; an id is optional (<see cref="OnlyIfId"/>).
        /// A bad entry is reported and dropped (fail-soft: the flow keeps the rest); the validator checks they are checks.
        /// </summary>
        private ValueList<StepNode> ReadOnlyIf(List<NodeDocument>? documents, string path)
        {
            List<StepNode> checks = [];
            for (var i = 0; i < (documents?.Count ?? 0); i++)
            {
                var document = documents![i];
                if (document is null)
                    continue;
                var id = string.IsNullOrWhiteSpace(document.Id) ? OnlyIfId(i) : document.Id.Trim();
                if (document.Group is not null || document.After is { Count: > 0 } || document.OnFailure is not null || document.Only is not null)
                {
                    Error($"{path}.{id}", "An \"Only run if\" entry is one check with its settings: no after, onFailure, only or card.");
                    continue;
                }
                if (checks.Exists(c => c.Id == id))
                {
                    Error($"{path}.{id}", $"Two \"Only run if\" checks have the id \"{id}\".");
                    continue;
                }
                document.Id = id;
                if (ReadNode(document, path, i) is StepNode step)
                    checks.Add(step);
            }
            return new(checks);
        }

        /// <summary>An input without a known kind is dropped (its <c>$name</c> uses then fail validation); the rest is checked there.</summary>
        private ValueDictionary<FlowInput> ReadInputs(Dictionary<string, InputDocument>? document, string path)
        {
            var inputs = new List<KeyValuePair<string, FlowInput>>();
            foreach (var (name, item) in document ?? [])
            {
                if (item?.Kind is null)
                    Error($"{path}.{name}.kind", "Say what kind of device it takes (kind: audio, bluetooth, bound, android or display), or kind: text.");
                else if (item.Kind.Trim() == TextInputKind)
                    inputs.Add(KeyValuePair.Create(name, new FlowInput(null, string.IsNullOrWhiteSpace(item.Name) ? name : item.Name,
                        item.Default ?? "", Bool(item.Ask, $"{path}.{name}.ask", false))));
                else if (TryEnum(item.Kind, $"{path}.{name}.kind", LibraryKind.Audio, out var kind))
                {
                    TimeSpan? timeout = null;
                    if (item.Timeout is { } written)
                    {
                        if (Duration.TryParse(written, out var span))
                            timeout = span;
                        else
                            Error($"{path}.{name}.timeout", $"\"{written}\" isn't a duration (like 30s, 2m).");
                    }
                    inputs.Add(KeyValuePair.Create(name, new FlowInput(kind, string.IsNullOrWhiteSpace(item.Name) ? name : item.Name,
                        item.Default?.Trim() ?? "", Bool(item.Ask, $"{path}.{name}.ask", false), timeout, item.OnTimeout?.Trim() ?? "")));
                }
            }
            return new(inputs);
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
            FailurePolicy? onFailure = TryEnum(document.OnFailure, path + ".onFailure", FailurePolicy.Stop, out var policy) && document.OnFailure is not null
                ? policy : null;

            if (document.Step is not null && document.Group is not null)
            {
                Error(path, "A node is either a step or a group, not both.");
                return null;
            }
            if (document.Group is { } members)
            {
                if (document.With is not null || document.Timeout is not null || document.Retry is not null || document.Only is not null
                    || document.Enabled is not null)
                    Error(path, "A card has no with, timeout, retry, only or enabled; set them on its members.");
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
                RetryCount = Int(document.Retry?.Count, path + ".retry.count"),
                RetryDelay = DurationOrNull(document.Retry?.Delay, path + ".retry.delay"),
                Only = string.IsNullOrWhiteSpace(document.Only) ? null : document.Only.Trim(),
                Enabled = Bool(document.Enabled, path + ".enabled", true),
            };
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
