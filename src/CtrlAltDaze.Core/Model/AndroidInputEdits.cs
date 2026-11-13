namespace CtrlAltDaze.Core.Model;

public enum InputEditRefusal
{
    None,
    Empty,
    Taken,
    Missing,

    /// <summary>A flow's <c>android.input</c> step picks it: removing it would break that step.</summary>
    InUse,

    /// <summary>A move other than up or down.</summary>
    BadMove,
}

/// <summary>
/// Library › Android › Inputs (L1, D-078): add, rename (the steps that pick it follow), remove (refused while used)
/// and set the D-pad moves of an Android device's inputs. Pure, so it is tested here.
/// </summary>
public static class AndroidInputEdits
{
    public const string InputStep = "android.input", DeviceParam = "device", InputParam = "input";

    public const string Up = "up", Down = "down";

    /// <summary>"input", then "input-2", "input-3"… whichever is free on this device.</summary>
    public static string NewName(AndroidDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        const string stem = "input";
        if (!device.Inputs.ContainsKey(stem))
            return stem;
        for (var n = 2; ; n++)
        {
            if (!device.Inputs.ContainsKey($"{stem}-{n}"))
                return $"{stem}-{n}";
        }
    }

    /// <summary>A new input with no moves (the one the TV's input list opens on), last in the list.</summary>
    public static (Configuration Config, InputEditRefusal Refusal) Add(Configuration config, string deviceKey, string name)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(name);
        name = name.Trim();
        if (Find(config, deviceKey) is not { } device)
            return (config, InputEditRefusal.Missing);
        if (name.Length == 0)
            return (config, InputEditRefusal.Empty);
        if (device.Inputs.ContainsKey(name))
            return (config, InputEditRefusal.Taken);
        return (With(config, device with { Inputs = new([.. device.Inputs, KeyValuePair.Create(name, new ValueList<string>([]))]) }),
            InputEditRefusal.None);
    }

    /// <summary>A new name in the same place; every <c>android.input</c> step that picked the old one follows (D-046).</summary>
    public static (Configuration Config, InputEditRefusal Refusal) Rename(Configuration config, string deviceKey, string name, string newName)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(newName);
        newName = newName.Trim();
        if (Find(config, deviceKey) is not { } device || !device.Inputs.ContainsKey(name))
            return (config, InputEditRefusal.Missing);
        if (newName.Length == 0)
            return (config, InputEditRefusal.Empty);
        if (newName == name)
            return (config, InputEditRefusal.None);
        if (device.Inputs.ContainsKey(newName))
            return (config, InputEditRefusal.Taken);

        var renamed = With(config, device with
        {
            Inputs = new(device.Inputs.Select(i => i.Key == name ? KeyValuePair.Create(newName, i.Value) : i)),
        });
        var flows = renamed.Flows.Select(f => Uses(f, deviceKey, name)
            ? f with { Nodes = [.. f.Nodes.Select(n => n is StepNode s && Picks(f, s, deviceKey, name) ? Renamed(s, newName) : n)] }
            : f);
        return (renamed with { Flows = [.. flows] }, InputEditRefusal.None);
    }

    /// <summary>Refused while a flow picks it (<see cref="InputEditRefusal.InUse"/>): that step would fail.</summary>
    public static (Configuration Config, InputEditRefusal Refusal) Remove(Configuration config, string deviceKey, string name)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (Find(config, deviceKey) is not { } device || !device.Inputs.ContainsKey(name))
            return (config, InputEditRefusal.Missing);
        if (config.Flows.Any(f => Uses(f, deviceKey, name)))
            return (config, InputEditRefusal.InUse);
        return (With(config, device with { Inputs = new(device.Inputs.Where(i => i.Key != name)) }), InputEditRefusal.None);
    }

    /// <summary>The D-pad moves, in order, from where the input list opens to this input; each is up or down.</summary>
    public static (Configuration Config, InputEditRefusal Refusal) SetMoves(Configuration config, string deviceKey, string name, IEnumerable<string> moves)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(moves);
        var list = moves.ToList();
        if (Find(config, deviceKey) is not { } device || !device.Inputs.ContainsKey(name))
            return (config, InputEditRefusal.Missing);
        if (list.Any(m => m is not (Up or Down)))
            return (config, InputEditRefusal.BadMove);
        return (With(config, device with
        {
            Inputs = new(device.Inputs.Select(i => i.Key == name ? KeyValuePair.Create(name, new ValueList<string>(list)) : i)),
        }), InputEditRefusal.None);
    }

    /// <summary>The flows with a step that picks this input, in file order.</summary>
    public static IReadOnlyList<Flow> Users(Configuration config, string deviceKey, string name)
    {
        ArgumentNullException.ThrowIfNull(config);
        return [.. config.Flows.Where(f => Uses(f, deviceKey, name))];
    }

    public static bool Uses(Flow flow, string deviceKey, string name)
    {
        ArgumentNullException.ThrowIfNull(flow);
        return flow.Nodes.OfType<StepNode>().Any(s => Picks(flow, s, deviceKey, name));
    }

    /// <summary>
    /// An <c>android.input</c> step picks this input when its input is the name and its device is this one: by key,
    /// or by one of the flow's inputs (<c>$tv</c>) whose default is this device (the device it runs with unless a
    /// call passes another).
    /// </summary>
    public static bool Picks(Flow flow, StepNode step, string deviceKey, string name)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(step);
        if (step.Step != InputStep
            || !step.With.TryGetValue(InputParam, out var input) || input is not ScalarParam { Text: var text } || text.Trim() != name
            || !step.With.TryGetValue(DeviceParam, out var device))
            return false;
        if (FlowInputs.NameIn(device) is { } own)
            return flow.Inputs.TryGetValue(own, out var flowInput) && flowInput.Kind == LibraryKind.Android && flowInput.Default == deviceKey;
        return device is ScalarParam { Text: var key } && key == deviceKey;
    }

    private static StepNode Renamed(StepNode step, string newName) => step with
    {
        With = new(step.With.Select(e => e.Key == InputParam ? KeyValuePair.Create(e.Key, (ParamValue)newName) : e)),
    };

    private static AndroidDevice? Find(Configuration config, string deviceKey) =>
        config.Library.Android.FirstOrDefault(d => d.Key == deviceKey);

    private static Configuration With(Configuration config, AndroidDevice device) => config with
    {
        Library = config.Library with { Android = [.. config.Library.Android.Select(d => d.Key == device.Key ? device : d)] },
    };
}
