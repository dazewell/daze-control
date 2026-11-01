using DazeControl.Core.Model;

namespace DazeControl.Core.Catalog;

/// <summary>
/// A step type (FEATURES F3). <see cref="Id"/> is "area.operation" (or just "area"), the <c>step:</c> value in YAML.
/// <see cref="Glyph"/> is a Segoe Fluent Icons code point (UX 5.3). <see cref="Resources"/> are lock names the
/// runner holds while the step runs (T2.3); "{param}" is replaced by that param's value, so two different
/// Bluetooth devices can connect at once while two steps on the same one can't.
/// </summary>
public sealed record ProviderDescriptor(string Id, string Title, string Glyph, TimeSpan DefaultTimeout, ValueList<ParamSpec> Params)
{
    public ValueList<string> Resources { get; init; } = [];

    /// <summary>"bluetooth" for "bluetooth.connect", "wait" for "wait"; groups the palette.</summary>
    public string Area => Id.Split('.')[0];

    public ParamSpec? FindParam(string name) => Params.FirstOrDefault(p => p.Name == name);
}
