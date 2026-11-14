using CommunityToolkit.Mvvm.ComponentModel;
using CtrlAltDaze.Core.Catalog;
using CtrlAltDaze.Core.Model;

namespace CtrlAltDaze.Editor.ViewModels.Inspector;

/// <summary>One row of "Values it gives" (D-129, DESIGN §5.4): what the step gives, who uses it, and the text to paste into a box.</summary>
public sealed partial class GivenValueViewModel : ObservableObject
{
    internal GivenValueViewModel(string stepName, ValueSpec spec, int usedBy)
    {
        Title = spec.Title;
        Help = spec.Help ?? "";
        UsedBy = usedBy switch { 0 => "Not used yet", 1 => "Used by 1 step", _ => $"Used by {usedBy} steps" };
        CopyText = $"{{{stepName}: {spec.Title}}}";
    }

    public string Title { get; }

    public string Help { get; }

    public bool HasHelp => Help.Length > 0;

    /// <summary>"Used by 2 steps".</summary>
    public string UsedBy { get; }

    /// <summary>What "Copy" puts on the clipboard: the display form, which a text box that takes values turns into the value.</summary>
    public string CopyText { get; }

    /// <summary>"Copied" replaces the "Used by" line until the list is read again; the slot keeps its height.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Note))]
    public partial bool IsCopied { get; set; }

    public string Note => IsCopied ? "Copied" : UsedBy;

    /// <summary>Narrator: "Device. The Library name of the device it connected. Used by 2 steps. Press to copy."</summary>
    public string AutomationName => string.Join(". ", new[] { Title, Help, UsedBy }.Where(t => t.Length > 0)) + ". Press to copy it.";
}

public sealed partial class InspectorViewModel
{
    /// <summary>The values the selected step gives, for "Values it gives"; empty for a step that gives none.</summary>
    public IReadOnlyList<GivenValueViewModel> GivenValues { get; private set; } = [];

    public bool HasGivenValues => GivenValues.Count > 0;

    private void LoadGivenValues(StepNode? step)
    {
        var next = step is null || Flow is not { } flow ? [] : GivenValuesOf(flow, step, flows: CalledFlowOf);
        if (GivenValues.Count == 0 && next.Count == 0)
            return;
        GivenValues = next;
        OnPropertyChanged(nameof(GivenValues));
        OnPropertyChanged(nameof(HasGivenValues));
    }

    /// <summary>
    /// The step's values with how many steps use each. A step's <c>error</c> is listed only when a step waits for its failure or
    /// something already uses it, like the Insert list.
    /// </summary>
    internal static List<GivenValueViewModel> GivenValuesOf(Flow flow, StepNode step, IReadOnlyList<ProviderDescriptor>? catalog = null,
        Func<string, Flow?>? flows = null)
    {
        catalog ??= ProviderCatalog.All;
        if (catalog.FirstOrDefault(d => d.Id == step.Step) is not { } descriptor)
            return [];
        var uses = StepValues.Uses(flow, catalog).Where(u => u.Ref.Source == ValueSource.Step && u.Ref.Name == step.Id).ToList();
        var failureEdge = flow.Nodes.Any(n => n.After.Any(e => e.From == step.Id && e.When == EdgeWhen.Failure));
        var shown = ValueText.LabelOf(flow, step.Id, catalog);
        return [.. StepValues.Of(flow, step, descriptor, flows, catalog)
            .Where(v => v.Name != StepValues.ErrorName || failureEdge || uses.Any(u => u.Ref.Value == v.Name))
            .Select(v => new GivenValueViewModel(shown, v, uses.Where(u => u.Ref.Value == v.Name).Select(u => u.Consumer.Id).Distinct().Count()))];
    }
}
