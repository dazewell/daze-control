using System.Globalization;
using System.Text.RegularExpressions;
using DazeControl.Core.Catalog;
using DazeControl.Core.Input;
using DazeControl.Core.Model;

namespace DazeControl.Core.Validation;

/// <summary>
/// Checks a mapped <see cref="Configuration"/> against FEATURES F1 and the provider catalog. Errors inside a flow
/// carry its id, so the host disables (or keeps the last valid version of) only that flow (D-021).
/// </summary>
public static partial class ConfigValidator
{
    public const int MaxRetries = 10; // the inspector's "Try again" range (UX 5.4)

    public static ValueList<Problem> Validate(Configuration config, IReadOnlyList<ProviderDescriptor>? catalog = null)
    {
        var problems = new List<Problem>();
        catalog ??= ProviderCatalog.All;
        foreach (var device in config.Library.Bluetooth)
        {
            if (!BluetoothAddress().IsMatch(device.Address))
                problems.Add(new(Severity.Error, $"library.bluetooth.{device.Key}.address",
                    $"\"{device.Address}\" isn't a Bluetooth address (like B8:20:8E:ED:48:FC)."));
        }
        var hotkeyOwners = new Dictionary<Hotkey, string>();
        foreach (var flow in config.Flows)
            new FlowChecker(flow, config.Library, catalog, problems).Check(hotkeyOwners);
        return new(problems);
    }

    /// <summary>Flows that must not run as written: an error carries their id.</summary>
    public static IReadOnlySet<string> FlowsWithErrors(IEnumerable<Problem> problems) =>
        problems.Where(p => p is { Severity: Severity.Error, FlowId: not null }).Select(p => p.FlowId!).ToHashSet();

    [GeneratedRegex("^[0-9A-Fa-f]{2}(:[0-9A-Fa-f]{2}){5}$")]
    private static partial Regex BluetoothAddress();

    [GeneratedRegex("^[0-9A-Fa-f]{4,5}$")]
    private static partial Regex GlyphCode();

    private sealed class FlowChecker(Flow flow, Library library, IReadOnlyList<ProviderDescriptor> catalog, List<Problem> problems)
    {
        private readonly string _path = $"flows.{flow.Id}";
        private readonly Dictionary<string, Node> _nodes = [];
        private readonly Dictionary<string, string> _cardOf = []; // member id → card id

        public void Check(Dictionary<Hotkey, string> hotkeyOwners)
        {
            CheckSettings(hotkeyOwners);
            if (flow.Nodes.Count == 0)
                Add(Severity.Warning, _path + ".nodes", "This flow has no steps yet.");
            foreach (var node in flow.Nodes)
            {
                if (!_nodes.TryAdd(node.Id, node))
                    Error(NodePath(node), $"Another step or card is already called \"{node.Id}\".");
            }
            foreach (var card in flow.Nodes.OfType<GroupNode>())
                CheckCard(card);
            foreach (var node in flow.Nodes)
            {
                CheckEdges(node);
                if (node is StepNode step)
                    CheckStep(step);
            }
            CheckCycles();
        }

        private void CheckSettings(Dictionary<Hotkey, string> hotkeyOwners)
        {
            if (flow.Glyph is { } glyph && !GlyphCode().IsMatch(glyph))
                Add(Severity.Warning, _path + ".glyph", $"\"{glyph}\" isn't a Segoe Fluent Icons code (like E702); the step's icon is used.");
            foreach (var text in flow.Hotkeys)
            {
                if (!Hotkey.TryParse(text, out var hotkey))
                    Error(_path + ".hotkeys", $"\"{text}\" isn't a shortcut: use Win, Ctrl or Alt plus A–Z, 0–9 or F1–F24 (like Alt+Z).");
                else if (!flow.Enabled)
                    continue; // a turned-off flow doesn't hold its hotkeys
                else if (hotkeyOwners.TryGetValue(hotkey, out var owner))
                    Error(_path + ".hotkeys", owner == flow.Id ? $"{hotkey} is listed twice." : $"{hotkey} already runs flow \"{owner}\".");
                else
                    hotkeyOwners[hotkey] = flow.Id;
            }
        }

        private void CheckCard(GroupNode card)
        {
            var path = NodePath(card);
            if (card.Members.Count < 2)
                Error(path + ".group", "A card holds at least two steps; use a plain step instead.");
            foreach (var id in card.Members)
            {
                if (!_nodes.TryGetValue(id, out var member))
                    Error(path + ".group", $"No step called \"{id}\" in this flow.");
                else if (member is GroupNode)
                    Error(path + ".group", $"\"{id}\" is a card; cards can't hold cards.");
                else if (!_cardOf.TryAdd(id, card.Id))
                    Error(path + ".group", _cardOf[id] == card.Id ? $"\"{id}\" is listed twice." : $"\"{id}\" is already in card \"{_cardOf[id]}\".");
                else if (member.After.Count > 0)
                    Error(NodePath(member) + ".after", $"\"{id}\" is in card \"{card.Id}\"; connect the card instead.");
            }
        }

        private void CheckEdges(Node node)
        {
            var path = NodePath(node) + ".after";
            var seen = new HashSet<string>();
            foreach (var edge in node.After)
            {
                if (edge.From == node.Id)
                    Error(path, "A step can't wait for itself.");
                else if (!_nodes.ContainsKey(edge.From))
                    Error(path, $"No step or card called \"{edge.From}\" in this flow.");
                else if (_cardOf.TryGetValue(edge.From, out var card))
                    Error(path, $"\"{edge.From}\" is in card \"{card}\"; wait for the card instead.");
                else if (!seen.Add(edge.From))
                    Error(path, $"\"{edge.From}\" is listed twice.");
            }
        }

        private void CheckStep(StepNode step)
        {
            var path = NodePath(step);
            if (step.Timeout is { } timeout && timeout <= TimeSpan.Zero)
                Error(path + ".timeout", "Give the step more than 0 s.");
            if (step.Retry is { } retry && retry.Count is < 0 or > MaxRetries)
                Error(path + ".retry.count", $"Try again 0 to {MaxRetries} times.");
            var descriptor = catalog.FirstOrDefault(d => d.Id == step.Step);
            if (descriptor is null)
            {
                Error(path + ".step", $"\"{step.Step}\" isn't a known step type.");
                return;
            }
            foreach (var (name, value) in step.With)
            {
                if (descriptor.FindParam(name) is { } spec)
                    CheckParam(spec, value, $"{path}.with.{name}");
                else
                    Error($"{path}.with.{name}", $"{descriptor.Title} has no setting \"{name}\" ({Names(descriptor)}).");
            }
            foreach (var spec in descriptor.Params.Where(p => p.Required && !step.With.ContainsKey(p.Name)))
                Error(path + ".with", $"{spec.Title} is missing ({spec.Name}: …).");
        }

        private static string Names(ProviderDescriptor descriptor) =>
            descriptor.Params.Count == 0 ? "it takes none" : "it takes " + string.Join(", ", descriptor.Params.Select(p => p.Name));

        private void CheckParam(ParamSpec spec, ParamValue value, string path)
        {
            switch (spec.Kind)
            {
                case ParamKind.TextList:
                    if (value is not ListParam list)
                        Error(path, "Write a list: [a, b].");
                    else if (list.Items.Any(i => i is not ScalarParam))
                        Error(path, "List items must be plain values.");
                    return;
                case ParamKind.TextMap:
                    if (value is not MapParam map)
                        Error(path, "Write name: value pairs: { Accept: text/plain }.");
                    else if (map.Entries.Values.Any(v => v is not ScalarParam))
                        Error(path, "Values must be plain values.");
                    return;
            }
            if (value is not ScalarParam { Text: var text })
            {
                Error(path, "Write a single value here, not a list or mapping.");
                return;
            }
            if (spec.Required && string.IsNullOrWhiteSpace(text))
            {
                Error(path, $"{spec.Title} can't be empty.");
                return;
            }
            var message = spec.Kind switch
            {
                ParamKind.WholeNumber when !int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) =>
                    $"\"{text}\" isn't a whole number.",
                ParamKind.WholeNumber when int.Parse(text, CultureInfo.InvariantCulture) is var n && (n < spec.Min || n > spec.Max) =>
                    $"Use {spec.Min} to {spec.Max}.",
                ParamKind.Boolean when !bool.TryParse(text, out _) => $"\"{text}\" isn't true or false.",
                ParamKind.Duration when !Duration.TryParse(text, out _) => $"\"{text}\" isn't a duration (like 500ms, 20s, 1m30s).",
                ParamKind.Choice when !spec.Choices.Contains(text) => $"\"{text}\" isn't one of: {string.Join(", ", spec.Choices)}.",
                ParamKind.LibraryRef when text.Length > 0 && !library.Contains(spec.Library!.Value, text) =>
                    $"No {LibraryName(spec.Library!.Value)} called \"{text}\" in the library.",
                _ => null,
            };
            if (message is not null)
                Error(path, message);
        }

        private static string LibraryName(LibraryKind kind) => kind switch
        {
            LibraryKind.Audio => "audio device",
            LibraryKind.Bluetooth => "Bluetooth device",
            _ => kind.ToString(),
        };

        /// <summary>Reports each loop once, at the node where the search first closed it.</summary>
        private void CheckCycles()
        {
            var state = new Dictionary<string, bool>(); // false = on the current path, true = done
            var path = new List<string>();
            foreach (var node in flow.Nodes)
                Visit(node.Id);

            void Visit(string id)
            {
                if (state.TryGetValue(id, out var done))
                {
                    if (!done)
                    {
                        var loop = path[path.IndexOf(id)..].Append(id);
                        Error(NodePath(_nodes[id]) + ".after", $"Steps wait for each other in a loop: {string.Join(" → ", loop)}.");
                    }
                    return;
                }
                state[id] = false;
                path.Add(id);
                foreach (var edge in _nodes[id].After.Where(e => _nodes.ContainsKey(e.From) && e.From != id))
                    Visit(edge.From);
                path.RemoveAt(path.Count - 1);
                state[id] = true;
            }
        }

        private string NodePath(Node node) => $"{_path}.nodes.{node.Id}";

        private void Error(string path, string message) => Add(Severity.Error, path, message);

        private void Add(Severity severity, string path, string message) => problems.Add(new(severity, path, message, flow.Id));
    }
}
