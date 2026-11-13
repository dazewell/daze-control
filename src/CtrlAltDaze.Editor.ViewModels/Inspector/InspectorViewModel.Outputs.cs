using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CtrlAltDaze.Core.Catalog;
using CtrlAltDaze.Core.Model;
using CtrlAltDaze.Core.Validation;

namespace CtrlAltDaze.Editor.ViewModels.Inspector;

// Flow outputs in the editor (T23.7, DESIGN §9): the "Values it gives back" section of the flow's settings. A "Run a flow"
// step that waits for this flow gives them to its caller's later steps.
public sealed partial class InspectorViewModel
{
    public const string AddOutputHelp = "Another flow that runs this one and waits for it can use these values in its later steps.";

    public ObservableCollection<FlowOutputViewModel> Outputs { get; } = [];

    [ObservableProperty]
    public partial bool HasOutputs { get; private set; }

    /// <summary>After renaming or removing a value another flow uses: what those flows now show.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutputTip))]
    public partial string? OutputTip { get; private set; }

    public bool HasOutputTip => OutputTip is not null;

    /// <summary>"Add a value" made a row (its name): the pane focuses its name box.</summary>
    public event Action<string>? OutputAdded;

    /// <summary>A flow of the config by id: what a waiting call's outputs are read from (T23.7).</summary>
    private Flow? CalledFlowOf(string id) => _session.Current.Flows.FirstOrDefault(f => f.Id == id);

    private void LoadOutputs(Flow flow)
    {
        var names = flow.Outputs.Keys.ToList();
        if (!Outputs.Select(o => o.Name).SequenceEqual(names))
        {
            Outputs.Clear();
            foreach (var name in names)
                Outputs.Add(new FlowOutputViewModel(this, name));
        }
        foreach (var row in Outputs)
        {
            row.Load(ValueText.ToDisplay(flow, flow.Outputs[row.Name], flows: CalledFlowOf));
            var path = $"flows.{flow.Id}.outputs.{row.Name}";
            var problems = _session.Problems.Where(p => p.Path == path).ToList();
            row.Error = (problems.FirstOrDefault(p => p.Severity == Severity.Error) ?? problems.FirstOrDefault())?.Message;
        }
        HasOutputs = Outputs.Count > 0;
    }

    /// <summary>"Add a value": a new output named "value" (or "value2"), empty until the person writes what it gives.</summary>
    [RelayCommand]
    private void AddOutput()
    {
        if (Flow is not { } flow || flow.Outputs.Count >= FlowOutputs.MaxCount)
            return;
        var name = FlowOutputs.NewName(flow);
        OutputTip = null;
        EditFlow(f => f with { Outputs = new([.. f.Outputs, KeyValuePair.Create(name, "")]) });
        OutputAdded?.Invoke(name);
    }

    /// <summary>The groups of a row's Insert value list: every step's values, the inputs, the run.</summary>
    internal IReadOnlyList<ValueGroup> OutputChoices() =>
        Flow is { } flow ? ValueText.OutputChoices(flow, flows: CalledFlowOf) : [];

    /// <summary>The text as the box shows it, stored with references (<c>{bt.device}</c>).</summary>
    internal void SetOutputText(string name, string shown)
    {
        if (Flow is not { } flow || !flow.Outputs.TryGetValue(name, out var old))
            return;
        var stored = ValueText.FromDisplay(flow, shown, flows: CalledFlowOf);
        if (stored != old)
            EditFlow(f => f with { Outputs = new(f.Outputs.Select(o => o.Key == name ? KeyValuePair.Create(name, stored) : o)) });
    }

    /// <summary>
    /// A new name, in this flow only (one undo step). Flows that use the old one are another file's edit, so they aren't
    /// changed: they show the value's fallback, and the tip and their warnings say so.
    /// </summary>
    internal void SetOutputName(string name, string newName)
    {
        if (Flow is not { } flow || !flow.Outputs.ContainsKey(name) || newName == name)
            return;
        if (!FlowOutputs.IsName(newName) || newName == StepValues.ErrorName || flow.Outputs.ContainsKey(newName))
        {
            Load(); // back to the name it has; the row says why
            Outputs.FirstOrDefault(o => o.Name == name)?.SetNameProblem(flow.Outputs.ContainsKey(newName)
                ? $"There's already a value called \"{newName}\"."
                : "Use letters and digits, starting with a small letter (like device).");
            return;
        }
        OutputTip = UsersTip(flow, name);
        EditFlow(f => f with { Outputs = new(f.Outputs.Select(o => o.Key == name ? KeyValuePair.Create(newName, o.Value) : o)) });
    }

    internal void RemoveOutput(string name)
    {
        if (Flow is not { } flow || !flow.Outputs.ContainsKey(name))
            return;
        OutputTip = UsersTip(flow, name);
        EditFlow(f => f with { Outputs = new(f.Outputs.Where(o => o.Key != name)) });
    }

    /// <summary>"Morning uses it: it now shows its fallback there." when another flow reads this value; else null.</summary>
    private string? UsersTip(Flow flow, string name)
    {
        var users = FlowOutputs.Users(_session.Current, flow.Id, name).Select(f => f.Name).Distinct(StringComparer.Ordinal).ToList();
        return users.Count == 0 ? null
            : $"{Captions.Join(users)} {(users.Count == 1 ? "uses" : "use")} \"{name}\": it now shows what's written for no value there, until you change it.";
    }
}

/// <summary>One value a flow gives back (T23.7): its name (what callers write after the step's id) and the text it holds.</summary>
public sealed partial class FlowOutputViewModel : ObservableObject
{
    private readonly InspectorViewModel _owner;
    private bool _loading;

    internal FlowOutputViewModel(InspectorViewModel owner, string name)
    {
        _owner = owner;
        Name = name;
        EditName = name;
    }

    /// <summary>The name callers use: <c>{call.device}</c>.</summary>
    public string Name { get; }

    /// <summary>The name box; commits on leaving it.</summary>
    [ObservableProperty]
    public partial string EditName { get; set; }

    /// <summary>The text as shown ("{Connect Technics: Device}"); commits on leaving the box.</summary>
    [ObservableProperty]
    public partial string Text { get; set; } = "";

    /// <summary>The validator's problem for this value (an error, else a warning), or a refused name.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; internal set; }

    public bool HasError => Error is not null;

    public string NameBoxName => $"Name of the value {Name}";

    public string TextBoxName => $"What {Name} holds";

    public string RemoveName => $"Remove the value {Name}";

    public string InsertValueName => $"Insert a value into {Name}";

    public IReadOnlyList<ValueGroup> ValueGroups => _owner.OutputChoices();

    internal void Load(string shown)
    {
        _loading = true;
        try
        {
            EditName = Name;
            Text = shown;
        }
        finally
        {
            _loading = false;
        }
    }

    internal void SetNameProblem(string message) => Error = message;

    partial void OnEditNameChanged(string value)
    {
        if (!_loading)
            _owner.SetOutputName(Name, value.Trim());
    }

    partial void OnTextChanged(string value)
    {
        if (!_loading)
            _owner.SetOutputText(Name, value);
    }

    /// <summary>Puts a value's token at the caret; returns where the caret goes.</summary>
    public int InsertValue(ValueChoice choice, int selectionStart, int selectionLength)
    {
        ArgumentNullException.ThrowIfNull(choice);
        Text = ValueText.Insert(Text, selectionStart, selectionLength, choice.Display, out var caret);
        return caret;
    }

    public void Remove() => _owner.RemoveOutput(Name);
}
