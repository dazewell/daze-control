using CommunityToolkit.Mvvm.ComponentModel;
using DazeControl.Core.Catalog;
using DazeControl.Core.Layout;
using DazeControl.Core.Model;

namespace DazeControl.Editor.ViewModels.Canvas;

public enum CanvasNodeKind
{
    Step,

    /// <summary>A step inside a card.</summary>
    Member,
    Card,
}

/// <summary>A chip on a pill (UX §5.3): optional Segoe Fluent glyph + short text.</summary>
public sealed record Badge(string Glyph, string Text)
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
    public partial string Summary { get; private set; } = "";

    /// <summary>The provider glyph as a character (cards: E8FD).</summary>
    [ObservableProperty]
    public partial string Glyph { get; private set; } = "";

    /// <summary>"Keep going" policy: dashed border + "May fail" chip (UX §5.3).</summary>
    [ObservableProperty]
    public partial bool MayFail { get; private set; }

    [ObservableProperty]
    public partial bool HasProblem { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<Badge> Badges { get; private set; } = [];

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

    /// <summary>Screen-reader name, e.g. "Set default output, Technics. Audio. Waits for Connect Technics. May fail."</summary>
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

    internal void Update(Node node, Library library, bool hasProblem, string waitsFor)
    {
        HasProblem = hasProblem;
        MayFail = node.OnFailure == FailurePolicy.Ignore;
        var badges = new List<Badge>();
        if (MayFail)
            badges.Add(new Badge("", "May fail"));
        else if (node.OnFailure == FailurePolicy.Skip)
            badges.Add(new Badge("", "Skip what depends on it"));
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
            Title = step.Name ?? descriptor?.Title ?? step.Step;
            Summary = Summarize(step, descriptor, library);
            Glyph = FlowItemViewModel.DecodeGlyph(descriptor?.Glyph ?? "E9CE");
            Count = "";
            if (step.Retry is { Count: > 0 } retry)
                badges.Add(new Badge("", $"×{retry.Count}"));
            if (step.Timeout is { } timeout)
                badges.Add(new Badge("", Duration.Format(timeout) ?? ""));
        }
        if (badges.Count > MaxBadges)
            badges = [.. badges.Take(MaxBadges), new Badge("", $"+{badges.Count - MaxBadges}")];
        Badges = badges;

        var parts = new List<string> { Summary.Length == 0 ? Title : $"{Title}, {Summary}" };
        if (node is StepNode s && ProviderCatalog.Find(s.Step) is { } d)
            parts.Add(Area(d.Area));
        if (node is GroupNode)
            parts.Add($"Runs together, {Count}");
        if (waitsFor.Length > 0)
            parts.Add($"Waits for {waitsFor}");
        if (MayFail)
            parts.Add("May fail");
        if (hasProblem)
            parts.Add("Needs fixing");
        AutomationName = string.Join(". ", parts) + ".";
    }

    /// <summary>"Technics · Default output": the library items and values the step works with, at most two.</summary>
    internal static string Summarize(StepNode step, ProviderDescriptor? descriptor, Library library)
    {
        if (descriptor is null)
            return "Unknown step";
        var values = new List<string>();
        foreach (var spec in descriptor.Params)
        {
            if (!step.With.TryGetValue(spec.Name, out var value) || value is not ScalarParam { Text: { Length: > 0 } text })
                continue;
            values.Add(spec.Kind switch
            {
                ParamKind.LibraryRef => LibraryName(library, spec.Library, text),
                ParamKind.MultilineText => text.Split('\n')[0].Trim(),
                _ => text,
            });
            if (values.Count == 2)
                break;
        }
        return values.Count == 0 ? Area(descriptor.Area) : string.Join(" · ", values);
    }

    private static string LibraryName(Library library, LibraryKind? kind, string key) => kind switch
    {
        LibraryKind.Bluetooth => library.Bluetooth.FirstOrDefault(d => d.Key == key)?.Name ?? key,
        LibraryKind.Audio => library.Audio.FirstOrDefault(d => d.Key == key)?.Match ?? key,
        _ => key,
    };

    internal static string Area(string area) => area switch
    {
        "audio" => "Audio",
        "bluetooth" => "Bluetooth",
        "display" => "Displays",
        "process" => "Programs",
        "script" => "Scripts",
        "http" => "Web",
        "firetv" => "Fire TV",
        "service" => "Services",
        "wait" => "Wait",
        "notify" => "Message",
        "flag" => "Flags",
        _ => area,
    };
}
