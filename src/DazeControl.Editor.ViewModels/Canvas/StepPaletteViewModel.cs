using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DazeControl.Core.Catalog;

namespace DazeControl.Editor.ViewModels.Canvas;

/// <summary>One step type in the palette or the quick-add search (UX §5.1, §5.2).</summary>
public sealed record PaletteItem(string Step, string Title, string Glyph, string Area)
{
    /// <summary>"Connect, Bluetooth": titles repeat across areas ("Connect", "Disconnect").</summary>
    public string Name => $"{Title}, {Area}";

    /// <summary>A check step (D-081): the palette and search call it "Check", never "Test" (that runs a step).</summary>
    public bool IsCheck => Core.Runs.Outcomes.IsCheck(Step);

    public override string ToString() => Name;
}

public sealed record PaletteGroup(string Area, IReadOnlyList<PaletteItem> Items);

/// <summary>
/// The left palette: every step type grouped by area, filtered by <see cref="Query"/>. The quick-add flyout uses
/// <see cref="Search"/> for the same matching.
/// </summary>
public sealed partial class StepPaletteViewModel : ObservableObject
{
    public static IReadOnlyList<PaletteItem> All { get; } =
    [
        .. ProviderCatalog.All.Select(d => new PaletteItem(d.Id, d.Title, FlowItemViewModel.DecodeGlyph(d.Glyph), CanvasNodeViewModel.Area(d.Area))),
    ];

    public StepPaletteViewModel() => Filter();

    public ObservableCollection<PaletteGroup> Groups { get; } = [];

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    /// <summary>
    /// Items whose title, area or step id contain every word of <paramref name="query"/>, best first: a title starting
    /// with the query, then the rest in catalog order.
    /// </summary>
    public static IReadOnlyList<PaletteItem> Search(string? query)
    {
        var words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
            return All;
        return [.. All.Where(item => words.All(w => Matches(item, w)))
            .OrderBy(item => item.Title.StartsWith(words[0], StringComparison.OrdinalIgnoreCase) ? 0 : 1)];
    }

    private static bool Matches(PaletteItem item, string word) =>
        item.Title.Contains(word, StringComparison.OrdinalIgnoreCase)
        || item.Area.Contains(word, StringComparison.OrdinalIgnoreCase)
        || item.Step.Contains(word, StringComparison.OrdinalIgnoreCase)
        || item.IsCheck && "check".StartsWith(word, StringComparison.OrdinalIgnoreCase);

    partial void OnQueryChanged(string value) => Filter();

    private void Filter()
    {
        var words = Query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Groups.Clear();
        foreach (var group in All.Where(item => words.All(w => Matches(item, w))).GroupBy(item => item.Area))
            Groups.Add(new PaletteGroup(group.Key, [.. group]));
        IsEmpty = Groups.Count == 0;
    }
}
