namespace DazeControl.Editor.ViewModels;

/// <summary>Joins the parts of a caption line ("Alt+Z · 4 steps · Done 2 h ago"): empty parts vanish with their separator (UX: no empty reserved gaps).</summary>
public static class Captions
{
    public const string Separator = " · ";

    /// <summary>The non-blank parts (trimmed of nothing: spaces inside stay), joined by " · "; "" when none is left.</summary>
    public static string Join(params string?[] parts) => Join((IEnumerable<string?>)parts);

    public static string Join(IEnumerable<string?> parts) =>
        string.Join(Separator, parts.Where(p => !string.IsNullOrWhiteSpace(p)));
}
