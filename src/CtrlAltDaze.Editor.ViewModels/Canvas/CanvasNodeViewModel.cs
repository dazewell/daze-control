using CommunityToolkit.Mvvm.ComponentModel;
using CtrlAltDaze.Core.Catalog;
using CtrlAltDaze.Core.Layout;
using CtrlAltDaze.Core.Model;

namespace CtrlAltDaze.Editor.ViewModels.Canvas;

public enum CanvasNodeKind
{
    Step,

    /// <summary>A step inside a card.</summary>
    Member,
    Card,
}

/// <summary>How a step shows during or after a run (UX §5.3 run states: shape plus glyph, never colour alone).</summary>
public enum NodeRunState
{
    /// <summary>No run on the canvas.</summary>
    None,

    /// <summary>Empty ring.</summary>
    Pending,

    /// <summary>Indeterminate ProgressRing.</summary>
    Running,

    /// <summary>E73E, success fill.</summary>
    Succeeded,

    /// <summary>E711, critical fill and border.</summary>
    Failed,

    /// <summary>A may-fail step that failed: E7BA, caution (not red).</summary>
    FailedMayFail,

    /// <summary>Dashed ring, E893, disabled text. Also a step a stopped run never reached.</summary>
    Skipped,
}

/// <param name="Text">Replaces the summary: "1.2 s", "Skipped", "Didn't work: …".</param>
/// <param name="Order">Dry run: 1 for the first step to start; 0 = no number.</param>
/// <param name="ConditionSkipped">An "Only if" step that was skipped (D-081): Succeeded, drawn with a dashed ring.</param>
public sealed record NodeRunView(NodeRunState State, string Text = "", int Order = 0, bool ConditionSkipped = false);

/// <summary>A chip on a pill (UX §5.3): optional Segoe Fluent glyph + short text.</summary>
public sealed record Badge(string Glyph, string Text, bool Inherited = false)
{
    public string Name => Text;
}

/// <summary>One pill or card on the canvas. Updated in place on every edit so the control keeps its element.</summary>
public sealed partial class CanvasNodeViewModel : ObservableObject
{
    public const int MaxBadges = 2;

    internal CanvasNodeViewModel(string id) => Id = id;

    public string Id { get; }

    [ObservableProperty]
    public partial CanvasNodeKind Kind { get; private set; }

    /// <summary>The card a member sits in.</summary>
    [ObservableProperty]
    public partial string? CardId { get; private set; }

    [ObservableProperty]
    public partial string Title { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShownSummary))]
    public partial string Summary { get; private set; } = "";

    /// <summary>The provider glyph as a character (cards: E8FD).</summary>
    [ObservableProperty]
    public partial string Glyph { get; private set; } = "";

    /// <summary>"Run the next steps anyway" policy: dashed border + "Failure ignored" chip (UX §5.3).</summary>
    [ObservableProperty]
    public partial bool MayFail { get; private set; }

    [ObservableProperty]
    public partial bool HasProblem { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<Badge> Badges { get; private set; } = [];

    /// <summary>All badges in one chip (glyphs fall back to Segoe Fluent Icons): keeps the pill at ≤ 12 elements (UX §5.8).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBadges))]
    public partial string BadgeText { get; private set; } = "";

    public bool HasBadges => BadgeText.Length > 0;

    /// <summary>Every chip shows a value the step inherits from its type's default (shown dimmer, DESIGN A5).</summary>
    [ObservableProperty]
    public partial bool BadgesInherited { get; private set; }

    /// <summary>Cards: "2 steps".</summary>
    [ObservableProperty]
    public partial string Count { get; private set; } = "";

    [ObservableProperty]
    public partial double X { get; private set; }

    [ObservableProperty]
    public partial double Y { get; private set; }

    [ObservableProperty]
    public partial double Width { get; private set; }

    [ObservableProperty]
    public partial double Height { get; private set; }

    [ObservableProperty]
    public partial int Column { get; private set; }

    [ObservableProperty]
    public partial int Row { get; private set; }

    [ObservableProperty]
    public partial bool IsSelected { get; internal set; }

    /// <summary>The canvas's roving focus (keyboard) is here.</summary>
    [ObservableProperty]
    public partial bool IsFocused { get; internal set; }

    /// <summary>Faded while the inspector previews a failure policy: what wouldn't run (UX §5.4).</summary>
    [ObservableProperty]
    public partial bool IsDimmed { get; internal set; }

    /// <summary>A step turned off (<c>enabled: false</c>, D-148): drawn dimmed, says "Turned off".</summary>
    [ObservableProperty]
    public partial bool IsOff { get; private set; }

    /// <summary>Its state in the run on the canvas (<see cref="NodeRunState.None"/> while editing).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInRun))]
    public partial NodeRunState RunState { get; private set; }

    public bool IsInRun => RunState != NodeRunState.None;

    /// <summary>Succeeded by skipping: dashed ring and a "Skipped: …" caption, nothing red or cautionary (DESIGN B6).</summary>
    [ObservableProperty]
    public partial bool RunConditionSkipped { get; private set; }

    /// <summary>During a run it replaces the summary (a finished step shows its duration).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShownSummary))]
    public partial string RunText { get; private set; } = "";

    public string ShownSummary => RunText.Length > 0 ? RunText : Summary;

    /// <summary>Dry run start order ("1", "2"…); empty = none.</summary>
    [ObservableProperty]
    public partial string OrderText { get; private set; } = "";

    internal void ShowRun(NodeRunView? run)
    {
        RunState = run?.State ?? NodeRunState.None;
        RunText = run?.Text ?? "";
        RunConditionSkipped = run?.ConditionSkipped == true;
        OrderText = run is { Order: > 0 } ? run.Order.ToString(System.Globalization.CultureInfo.CurrentCulture) : "";
        AutomationName = NameWithRun();
    }

    /// <summary>Screen readers hear the run too: "Connect, Technics. Bluetooth. Step 1. Done in 1.2 s."</summary>
    private string NameWithRun()
    {
        var said = new List<string>();
        if (OrderText.Length > 0)
            said.Add($"Step {OrderText}");
        if (RunState != NodeRunState.None)
            said.Add(RunConditionSkipped ? RunText : RunWords(RunState, RunText));
        return said.Count == 0 ? _name : $"{_name} {string.Join(". ", said)}.";
    }

    private static string RunWords(NodeRunState state, string text) => state switch
    {
        NodeRunState.Pending => "Waiting",
        NodeRunState.Running => "Running",
        NodeRunState.Succeeded => text.Length > 0 ? $"Done in {text}" : "Would work",
        NodeRunState.FailedMayFail => $"Didn't work, the flow goes on. {text}".TrimEnd(),
        NodeRunState.Failed => $"Didn't work. {text}".TrimEnd(),
        _ => text.Length > 0 ? text : "Skipped",
    };

    private string _name = "";

    /// <summary>Screen-reader name, e.g. "Set default output, Technics. Audio. Waits for Connect Technics. Failure ignored."</summary>
    [ObservableProperty]
    public partial string AutomationName { get; private set; } = "";

    public Rect Bounds => new(X, Y, Width, Height);

    internal void Place(NodeBox box)
    {
        X = box.Bounds.X;
        Y = box.Bounds.Y;
        Width = box.Bounds.Width;
        Height = box.Bounds.Height;
        Column = box.Column;
        Row = box.Row;
        CardId = box.Card;
        Kind = box.Card is not null ? CanvasNodeKind.Member : Kind == CanvasNodeKind.Card ? CanvasNodeKind.Card : CanvasNodeKind.Step;
    }

    internal void Update(Node node, Configuration config, bool hasProblem)
    {
        HasProblem = hasProblem;
        // A step shows its effective policy, retries and timeout (own > its type's default > built-in), dimmer when
        // inherited, and only what differs from the built-in (DESIGN A5).
        StepSettings? effective = null;
        ProviderDescriptor? stepDescriptor = null;
        if (node is StepNode own)
        {
            stepDescriptor = ProviderCatalog.Find(own.Step);
            effective = StepSettings.Resolve(own, stepDescriptor, config.Settings.StepDefaults.GetValueOrDefault(own.Step));
        }
        var policy = effective?.OnFailure ?? node.OnFailure;
        var policyInherited = node.OnFailure is null;
        MayFail = policy == FailurePolicy.Ignore;
        IsOff = node is StepNode { Enabled: false };
        var badges = new List<Badge>();
        if (IsOff)
            badges.Add(new Badge("", "Turned off"));
        if (MayFail)
            badges.Add(new Badge("", "Failure ignored", policyInherited));
        else if (policy == FailurePolicy.Skip)
            badges.Add(new Badge("", "Skip", policyInherited));
        if (node is GroupNode card)
        {
            Kind = CanvasNodeKind.Card;
            Title = card.Name ?? "Run together";
            Summary = "";
            Glyph = "";
            Count = card.Members.Count == 1 ? "1 step" : $"{card.Members.Count} steps";
        }
        else if (node is StepNode step)
        {
            if (Kind == CanvasNodeKind.Card)
                Kind = CanvasNodeKind.Step;
            var descriptor = ProviderCatalog.Find(step.Step);
            var called = step.Step == ProviderCatalog.RunFlowId && Core.Validation.ConfigValidator.CalledFlow(step) is { Length: > 0 } id
                ? config.Flows.FirstOrDefault(f => f.Id == id)
                : null;
            Title = step.Name ?? (called is not null ? "Run " + called.Name : descriptor?.Title ?? step.Step);
            Summary = called is not null ? CallSummary(step, called, config) : Summarize(step, descriptor, config);
            // Run a flow shows the called flow's own icon (DESIGN §6).
            Glyph = called is not null ? FlowGlyph.Decode(FlowGlyph.CodeOf(called)) : FlowItemViewModel.DecodeGlyph(descriptor?.Glyph ?? "E9CE");
            Count = "";
            if (effective!.Only is { } only && only != StepNode.OnlyAlways)
                badges.Add(new Badge("", "Only if…", step.Only is null));
            if (effective!.RetryCount > 0)
                badges.Add(new Badge("", $"×{effective.RetryCount}", step.RetryCount is null));
            if (stepDescriptor is not null && effective.Timeout != stepDescriptor.DefaultTimeout)
                badges.Add(new Badge("", Duration.Format(effective.Timeout) ?? "", step.Timeout is null));
        }
        var allInherited = badges.Count > 0 && badges.All(b => b.Inherited);
        if (badges.Count > MaxBadges)
            badges = [.. badges.Take(MaxBadges), new Badge("", $"+{badges.Count - MaxBadges}")];
        if (!badges.SequenceEqual(Badges))
            Badges = badges; // a new list every Sync: set only when it says something else
        BadgesInherited = allInherited;
        BadgeText = string.Join("   ", badges.Select(b => b.Glyph.Length > 0 ? $"{b.Glyph} {b.Text}" : b.Text));

        var head = new List<string> { Summary.Length == 0 ? Title : $"{Title}, {Summary}" };
        if (node is StepNode s && ProviderCatalog.Find(s.Step) is { } d)
            head.Add(Area(d.Area));
        if (node is GroupNode)
            head.Add($"Runs together, {Count}");
        var tail = new List<string>();
        if (IsOff)
            tail.Add("Turned off");
        if (node is StepNode onlyStep && effective?.Only is { } cond && cond != StepNode.OnlyAlways)
            tail.Add($"Only if {OnlyLabel(onlyStep.Step, cond)}");
        if (MayFail)
            tail.Add("Failure ignored");
        if (hasProblem)
            tail.Add("Needs fixing");
        (_nameHead, _nameTail) = (head, tail);
        ApplyName();
    }

    /// <summary>"Waits for Connect Technics · Failure ignored · Needs fixing": the Outline row's second line (UX §5.2); empty when none apply.</summary>
    internal string OutlineDetail
    {
        get
        {
            var parts = new List<string>();
            if (_waitsFor.Length > 0)
                parts.Add($"Waits for {_waitsFor}");
            parts.AddRange(_nameTail);
            return string.Join(" · ", parts);
        }
    }

    private List<string> _nameHead = [];
    private List<string> _nameTail = [];
    private string _waitsFor = "";

    /// <summary>The steps this one waits for ("Connect Technics, Wait"), said in its name; set once every title is known.</summary>
    internal void SetWaitsFor(string waitsFor)
    {
        if (waitsFor == _waitsFor)
            return;
        _waitsFor = waitsFor;
        ApplyName();
    }

    /// <summary>Builds the name from its parts and sets it with the run words, so an unchanged name raises nothing.</summary>
    private void ApplyName()
    {
        var parts = new List<string>(_nameHead);
        if (_waitsFor.Length > 0)
            parts.Add($"Waits for {_waitsFor}");
        parts.AddRange(_nameTail);
        _name = string.Join(". ", parts) + ".";
        AutomationName = NameWithRun();
    }

    /// <summary>The chosen condition in the user's words ("it isn't connected"), else the id.</summary>
    internal static string OnlyLabel(string stepId, string id)
    {
        var label = ProviderCatalog.Find(stepId)?.Conditions.FirstOrDefault(c => c.Id == id)?.Label ?? id;
        return label.Length > 0 ? char.ToLowerInvariant(label[0]) + label[1..] : label;
    }

    /// <summary>"Technics · Default output": the library items, flows and values the step works with, at most two.</summary>
    internal static string Summarize(StepNode step, ProviderDescriptor? descriptor, Configuration config)
    {
        if (descriptor is null)
            return "Unknown step";
        var values = new List<string>();
        foreach (var spec in descriptor.Params)
        {
            if (!step.With.TryGetValue(spec.Name, out var value) || value is not ScalarParam { Text: { Length: > 0 } text })
                continue;
            // A text that takes values shows the titles ("Connected to Device at Time"), never ids (D-129).
            if (StepValues.Takes(spec) && OwnerOf(step, config) is { } owner)
                text = StepValues.Describe(owner, text, marks: false);
            values.Add(spec.Kind switch
            {
                // "$output" shows as the input's name, like the picker (D-064).
                // "$result:pick" shows as "Result of Pick device" (D-117), "$result:set.previous" as
                // "Previous default from Set default device" (D-130).
                ParamKind.LibraryRef when PickResults.StepIn(value) is not null =>
                    OwnerOf(step, config) is { } flow ? PickResults.Describe(flow, text) ?? text : text,
                ParamKind.LibraryRef when FlowInputs.NameIn(value) is { } input =>
                    OwnerOf(step, config)?.Inputs.GetValueOrDefault(input)?.Label ?? text,
                ParamKind.LibraryRef => LibraryName(config.Library, spec.Library, text),
                _ when spec.RefersTo == ConfigRef.Flow => config.Flows.FirstOrDefault(f => f.Id == text.Trim())?.Name ?? text,
                ParamKind.MultilineText => text.Split('\n')[0].Trim(),
                ParamKind.Choice => spec.CaptionOf(text),
                _ => text,
            });
            if (values.Count == 2)
                break;
        }
        return values.Count == 0 ? Area(descriptor.Area) : Captions.Join(values);
    }

    /// <summary>
    /// A call's pill (UX §5.4): "Doesn't wait" in start mode, else the devices it passes ("Sony", a caller's input by
    /// its name), else "Its defaults" when the called flow takes inputs, else "Flows".
    /// </summary>
    internal static string CallSummary(StepNode step, Flow called, Configuration config)
    {
        if (step.With.TryGetValue("wait", out var wait) && wait is ScalarParam { Text: var text } && bool.TryParse(text.Trim(), out var waits) && !waits)
            return "Doesn't wait";
        var names = FlowInputs.Given(step)
            .Where(e => called.Inputs.ContainsKey(e.Key))
            .Select(e => FlowInputs.NameIn(new ScalarParam(e.Value)) is { } own
                ? OwnerOf(step, config)?.Inputs.GetValueOrDefault(own)?.Label ?? e.Value
                : called.Inputs[e.Key].Kind is { } kind ? Inspector.LibraryDevices.NameOf(config.Library, kind, e.Value)
                : StepValues.Describe(OwnerOf(step, config) ?? called, e.Value, marks: false))
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToList();
        return names.Count > 0 ? Captions.Join(names) : called.Inputs.Count > 0 ? "Its defaults" : Area("flow");
    }

    /// <summary>The flow the step is in (its inputs name <c>$name</c> values).</summary>
    /// <remarks>By reference: steps are records, and two flows may hold equal ones.</remarks>
    private static Flow? OwnerOf(StepNode step, Configuration config) =>
        config.Flows.FirstOrDefault(f => f.Nodes.Any(n => ReferenceEquals(n, step))) ?? config.Flows.FirstOrDefault(f => f.Nodes.Contains(step));

    private static string LibraryName(Library library, LibraryKind? kind, string key) => kind switch
    {
        LibraryKind.Bluetooth or LibraryKind.Audio when library.Bound.FirstOrDefault(d => d.Key == key) is { } bound => bound.Name ?? key,
        LibraryKind.Bluetooth => library.Bluetooth.FirstOrDefault(d => d.Key == key)?.Name ?? key,
        LibraryKind.Audio => library.Audio.FirstOrDefault(d => d.Key == key)?.Match ?? key,
        _ => key,
    };

    /// <summary>The eleven themed palette groups, in palette order (D-102 choice 3). Display only: step ids and areas are unchanged.</summary>
    public static IReadOnlyList<string> Groups { get; } = ["Audio", "Bluetooth", "Displays", "Android", "Programs", "Services", "Files", "Web", "Network", "This PC", "Flow"];

    /// <summary>The palette group of a step area ("script" and "process" are both Programs; "wait", "notify", "flag", "flow", "menu" are Flow).</summary>
    internal static string Group(string area) => area switch
    {
        "audio" => "Audio",
        "bluetooth" => "Bluetooth",
        "display" => "Displays",
        "android" => "Android",
        "process" or "script" => "Programs",
        "service" => "Services",
        "file" => "Files",
        "http" => "Web",
        "network" => "Network",
        "power" or "pc" or "time" => "This PC",
        _ => "Flow",
    };

    internal static string Area(string area) => area switch
    {
        "audio" => "Audio",
        "bluetooth" => "Bluetooth",
        "display" => "Displays",
        "process" => "Programs",
        "script" => "Scripts",
        "http" => "Web",
        "android" => "Android",
        "service" => "Services",
        "wait" => "Wait",
        "notify" => "Message",
        "flag" => "Flags",
        "file" => "Files",
        "flow" => "Flows",
        "menu" => "Menu",
        "prompt" => "Questions",
        "network" => "Network",
        "power" => "Power",
        "pc" => "This PC",
        "time" => "Time",
        "text" => "Text",
        "value" => "Value",
        _ => area,
    };
}
