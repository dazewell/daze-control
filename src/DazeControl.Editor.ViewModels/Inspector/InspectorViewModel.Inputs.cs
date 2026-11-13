using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DazeControl.Core.Catalog;
using DazeControl.Core.Model;
using DazeControl.Core.Validation;

namespace DazeControl.Editor.ViewModels.Inspector;

// Flow inputs in the editor (D-064, UX §5.4): the Inputs section of the flow's settings, and "Run a flow"'s device
// pickers for the inputs of the flow it runs.
public sealed partial class InspectorViewModel
{
    /// <summary>UX microcopy #60.</summary>
    public const string AddInputHelp = "Lets another flow pick the device when it runs this one.";

    /// <summary>The group of the caller's own inputs in a call's input pickers.</summary>
    public const string FromThisFlowsInputs = "From this flow's inputs";

    public const string ChooseAFlowFirst = "Choose a flow first.";

    public ObservableCollection<FlowInputViewModel> Inputs { get; } = [];

    /// <summary>"Add an input" made a row (its key): the pane scrolls it into view and focuses its name box (F-009).</summary>
    public event Action<string>? InputAdded;

    /// <summary>"Bind devices in Library › Bound devices": the shell shows the Library page.</summary>
    public event Action? BoundDevicesRequested;

    internal void RequestBoundDevices() => BoundDevicesRequested?.Invoke();

    [ObservableProperty]
    public partial bool HasInputs { get; private set; }

    /// <summary>#61 after removing (or changing the kind of) an input steps used: "Steps that used Output now use Technics."</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInputTip))]
    public partial string? InputTip { get; private set; }

    public bool HasInputTip => InputTip is not null;

    /// <summary>The selected step runs a flow that exists: "Open this flow" can go there.</summary>
    [ObservableProperty]
    public partial string? CalledFlowId { get; private set; }

    public bool CanOpenCalledFlow => CalledFlowId is not null;

    /// <summary>"Open this flow" (UX §5.4): the shell leaves this flow with the D-047 prompt and opens it.</summary>
    public event Action<string>? OpenFlowRequested;

    [RelayCommand]
    private void OpenCalledFlow()
    {
        if (CalledFlowId is { } id)
            OpenFlowRequested?.Invoke(id);
    }

    partial void OnCalledFlowIdChanged(string? value) => OnPropertyChanged(nameof(CanOpenCalledFlow));

    private void LoadInputs(Flow flow)
    {
        var names = flow.Inputs.Keys.ToList();
        if (!Inputs.Select(i => i.Name).SequenceEqual(names))
        {
            Inputs.Clear();
            foreach (var name in names)
                Inputs.Add(new FlowInputViewModel(this, name));
        }
        var library = _session.Current.Library;
        foreach (var row in Inputs)
        {
            var input = flow.Inputs[row.Name];
            row.Load(input);
            row.SetNoBoundDevices(input.Kind == LibraryKind.Bound && !LibraryDevices.Items(library, LibraryKind.Bound).Any());
            row.SetDevices(LibraryDevices.Options(input.Kind, library, _live.GetValueOrDefault(input.Kind), input.Default,
                required: true, canCapture: false), input.Default);
            var path = $"flows.{flow.Id}.inputs.{row.Name}";
            row.Error = _session.Problems.FirstOrDefault(p => p.Severity == Severity.Error
                && (p.Path == path || p.Path.StartsWith(path + ".", StringComparison.Ordinal)))?.Message;
        }
        HasInputs = Inputs.Count > 0;
    }

    /// <summary>"Add an input": an audio device with the Library's first one as its default.</summary>
    [RelayCommand]
    private void AddInput()
    {
        if (Flow is not { } flow)
            return;
        const LibraryKind kind = LibraryKind.Audio;
        var name = FlowInputs.NewName(flow, kind);
        var label = UniqueLabel(flow, FlowInputViewModel.KindNames[0]);
        var first = LibraryDevices.Items(_session.Current.Library, kind).Select(i => i.Key).FirstOrDefault() ?? "";
        InputTip = null;
        EditFlow(f => f with { Inputs = new([.. f.Inputs, KeyValuePair.Create(name, new FlowInput(kind, label, first))]) });
        _ = RefreshDevicesAsync(kind);
        InputAdded?.Invoke(name);
    }

    private static string UniqueLabel(Flow flow, string stem)
    {
        var label = stem;
        for (var n = 2; flow.Inputs.Values.Any(i => i.Label == label); n++)
            label = $"{stem} {n}";
        return label;
    }

    private void EditInput(string name, Func<Flow, FlowInput, Flow> change)
    {
        if (Flow is { } flow && flow.Inputs.TryGetValue(name, out var input))
            EditFlow(f => change(f, input));
    }

    private static Flow WithInput(Flow flow, string name, FlowInput input) =>
        flow with { Inputs = new(flow.Inputs.Select(i => i.Key == name ? KeyValuePair.Create(name, input) : i)) };

    internal void SetInputLabel(string name, string label)
    {
        if (label.Length == 0)
        {
            Load(); // a name is needed: back to the one it has
            return;
        }
        EditInput(name, (f, input) => input.Label == label ? f : WithInput(f, name, input with { Label = label }));
    }

    /// <summary>A new kind gets that kind's first device; steps that used the input keep its old default (#61).</summary>
    internal void SetInputKind(string name, LibraryKind kind)
    {
        if (Flow is not { } flow || !flow.Inputs.TryGetValue(name, out var input) || input.Kind == kind)
            return;
        InputTip = Tip(flow, name, input);
        var first = LibraryDevices.Items(_session.Current.Library, kind).Select(i => i.Key).FirstOrDefault() ?? "";
        var label = FlowInputViewModel.KindNames.Contains(input.Label)
            ? UniqueLabel(flow, FlowInputViewModel.KindNames[Array.IndexOf(FlowInputViewModel.Kinds, kind)])
            : input.Label;
        EditFlow(f => WithInput(FlowInputs.Detach(f, name), name, new FlowInput(kind, label, first)));
        _ = RefreshDevicesAsync(kind);
    }

    internal void SetInputAsk(string name, bool ask) =>
        EditInput(name, (f, input) => input.Ask == ask ? f : WithInput(f, name, input with { Ask = ask }));

    /// <summary>Clamped to the 5 s to 10 min a prompt may wait (the default, 30 s, is written as unset).</summary>
    internal void SetInputTimeout(string name, TimeSpan timeout)
    {
        var clamped = timeout < Prompts.MinTimeout ? Prompts.MinTimeout : timeout > Prompts.MaxTimeout ? Prompts.MaxTimeout : TimeSpan.FromSeconds(Math.Round(timeout.TotalSeconds));
        EditInput(name, (f, input) => input.Timeout == clamped || input.Timeout is null && clamped == Prompts.DefaultTimeout
            ? f
            : WithInput(f, name, input with { Timeout = clamped == Prompts.DefaultTimeout ? null : clamped }));
        if (Flow is { } flow && flow.Inputs.TryGetValue(name, out var now) && now.Timeout is null && clamped != Prompts.DefaultTimeout)
            Load();
    }

    internal void SetInputOnTimeout(string name, string onTimeout) =>
        EditInput(name, (f, input) => input.OnTimeout == onTimeout ? f : WithInput(f, name, input with { OnTimeout = onTimeout }));

    internal void SetInputDefault(string name, LibraryKind kind, DeviceOption option)
    {
        if (!CanEdit)
            return;
        string key;
        switch (option.Source)
        {
            case DeviceSource.Library or DeviceSource.Missing when option.Key is { } k:
                key = k;
                break;
            case DeviceSource.Windows when option.Device is { } device:
                (var library, key) = LibraryDevices.Add(_session.Current.Library, kind, device);
                _session.Update(c => c with { Library = library });
                break;
            default:
                return;
        }
        EditInput(name, (f, input) => input.Default == key ? f : WithInput(f, name, input with { Default = key }));
    }

    /// <summary>Removes the input; steps that used it get its default in the same undo step (#61).</summary>
    internal void RemoveInput(string name)
    {
        if (Flow is not { } flow || !flow.Inputs.ContainsKey(name))
            return;
        InputTip = Tip(flow, name, flow.Inputs[name]);
        EditFlow(f => FlowInputs.Detach(f, name) with { Inputs = new(f.Inputs.Where(i => i.Key != name)) });
    }

    private string? Tip(Flow flow, string name, FlowInput input) => FlowInputs.IsUsed(flow, name)
        ? $"Steps that used {input.Label} now use {LibraryDevices.NameOf(_session.Current.Library, input.Kind, input.Default)}."
        : null;

    // ---- "Run a flow": the called flow's inputs ----

    private void LoadCallInputs(ParamFieldViewModel field, StepNode step)
    {
        var target = ConfigValidator.CalledFlow(step);
        var called = target is { Length: > 0 } ? _session.Current.Flows.FirstOrDefault(f => f.Id == target) : null;
        CalledFlowId = called?.Id;
        var given = FlowInputs.Given(step);
        if (field.SetCallInputs(called?.Inputs.ToList() ?? [], (name, input) => new CallInputViewModel(name, input, OnCallInputPicked)))
        {
            foreach (var kind in field.CallInputs.Select(r => r.Input.Kind).Distinct())
                _ = RefreshDevicesAsync(kind);
        }
        var library = _session.Current.Library;
        var path = $"flows.{FlowId}.nodes.{step.Id}.with.{field.Name}.";
        var errors = _session.Problems.Where(p => p.Severity == Severity.Error && p.Path.StartsWith(path, StringComparison.Ordinal)).ToList();
        foreach (var row in field.CallInputs)
        {
            var current = given.GetValueOrDefault(row.Name);
            var kind = row.Input.Kind;
            List<DeviceOption> options =
            [
                new(DeviceSource.None, null, "Its default: " + LibraryDevices.NameOf(library, kind, row.Input.Default), "", LibraryDevices.Glyph(kind)),
                .. LibraryDevices.Options(kind, library, _live.GetValueOrDefault(kind), current, required: true, canCapture: false,
                    Flow?.Inputs, FromThisFlowsInputs, DevicesBefore(kind)),
            ];
            row.SetDevices(options, current);
            row.Error = errors.FirstOrDefault(p => p.Path == path + row.Name)?.Message;
        }
        // An input passed that the called flow doesn't have (anymore): said under the field, there's no picker for it.
        if (field.Error is null && errors.FirstOrDefault(p => field.CallInputs.All(r => path + r.Name != p.Path)) is { } stale)
            field.SetError(stale.Message);
        field.CallInputsHint = called is null ? ChooseAFlowFirst : called.Inputs.Count == 0 ? $"{called.Name} takes no inputs." : "";
    }

    private void OnCallInputPicked(CallInputViewModel row, DeviceOption option)
    {
        if (Node is not StepNode step || Fields.FirstOrDefault(f => f.IsCallInputs) is not { } field || !CanEdit)
            return;
        string? value;
        switch (option.Source)
        {
            case DeviceSource.None:
                value = null;
                break;
            case DeviceSource.Library or DeviceSource.Missing or DeviceSource.Input when option.Key is { } key:
                value = key;
                break;
            case DeviceSource.Windows when option.Device is { } device:
                (var library, value) = LibraryDevices.Add(_session.Current.Library, row.Input.Kind, device);
                _session.Update(c => c with { Library = library });
                step = Node as StepNode ?? step;
                break;
            default:
                return;
        }
        var entries = step.With.TryGetValue(FlowInputs.Param, out var old) && old is MapParam map ? map.Entries.ToList() : [];
        var at = entries.FindIndex(e => e.Key == row.Name);
        if (value is null && at >= 0)
            entries.RemoveAt(at);
        else if (value is not null && at >= 0)
            entries[at] = KeyValuePair.Create(row.Name, (ParamValue)value);
        else if (value is not null)
            entries.Add(KeyValuePair.Create(row.Name, (ParamValue)value));
        WriteParam(field, entries.Count == 0 ? null : new MapParam(new(entries)));
    }

    /// <summary>Another flow picked: inputs the new one doesn't take, or takes as another kind, are dropped with it.</summary>
    private static StepNode KeepPassedInputs(StepNode step, StepNode before, Configuration config)
    {
        if (!step.With.TryGetValue(FlowInputs.Param, out var value) || value is not MapParam map)
            return step;
        var called = ConfigValidator.CalledFlow(step) is { } id ? config.Flows.FirstOrDefault(f => f.Id == id) : null;
        var old = ConfigValidator.CalledFlow(before) is { } was ? config.Flows.FirstOrDefault(f => f.Id == was) : null;
        // Same name and same kind: a device of another kind (or the caller's input of another kind) would be wrong.
        var kept = map.Entries.Where(e => called is not null && called.Inputs.TryGetValue(e.Key, out var input)
            && (old?.Inputs.TryGetValue(e.Key, out var prior) != true || prior!.Kind == input.Kind)).ToList();
        return kept.Count == map.Entries.Count ? step
            : step with
            {
                With = kept.Count == 0
                    ? new(step.With.Where(e => e.Key != FlowInputs.Param))
                    : new(step.With.Select(e => e.Key == FlowInputs.Param ? KeyValuePair.Create(e.Key, (ParamValue)new MapParam(new(kept))) : e)),
            };
    }

    /// <summary>The flow picker's caption for a flow with inputs: "Takes an output and a Bluetooth device".</summary>
    internal static string Takes(Flow flow)
    {
        static string Article(string label)
        {
            var word = label.Split(' ')[0];
            var proper = word.Length > 1 && char.IsUpper(word[1]) || word is "Bluetooth" or "Android" or "Windows";
            var text = proper ? label : char.ToLowerInvariant(label[0]) + label[1..];
            return ("aeiou".Contains(char.ToLowerInvariant(text[0]), StringComparison.Ordinal) ? "an " : "a ") + text;
        }
        var parts = flow.Inputs.Values.Where(i => i.Label.Length > 0).Select(i => Article(i.Label)).ToList();
        return parts.Count switch
        {
            0 => "",
            1 => "Takes " + parts[0],
            _ => "Takes " + string.Join(", ", parts[..^1]) + " and " + parts[^1],
        };
    }
}
