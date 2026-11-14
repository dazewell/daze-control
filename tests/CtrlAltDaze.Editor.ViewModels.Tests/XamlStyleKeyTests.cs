using System.Text.Json;
using System.Text.RegularExpressions;

namespace CtrlAltDaze.Editor.ViewModels.Tests;

/// <summary>
/// A <c>BasedOn="{StaticResource X}"</c> whose key exists nowhere crashes the editor when the page first loads
/// (D-113's <c>DefaultNumberBoxStyle</c> took the app down on opening a flow). Every key must be defined in the
/// editor's own XAML or in WinUI's generic.xaml.
/// </summary>
public sealed partial class XamlStyleKeyTests
{
    [Fact]
    public void EveryBasedOnKeyExists()
    {
        var editor = Path.Combine(RepoRoot(), "src", "CtrlAltDaze.Editor");
        var xaml = Directory.EnumerateFiles(editor, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToDictionary(f => f, File.ReadAllText);
        var generic = GenericXaml(editor);
        Assert.True(generic is not null, "WinUI generic.xaml not found from the editor's restore assets; restore the editor first.");
        var known = new HashSet<string>(Keys().Matches(File.ReadAllText(generic!)).Select(m => m.Groups[1].Value));
        known.UnionWith(xaml.Values.SelectMany(t => Keys().Matches(t)).Select(m => m.Groups[1].Value));

        var missing = xaml
            .SelectMany(kv => BasedOn().Matches(kv.Value).Select(m => (File: Path.GetFileName(kv.Key), Key: m.Groups[1].Value)))
            .Where(r => !known.Contains(r.Key))
            .Select(r => $"{r.File}: {r.Key}")
            .ToList();
        Assert.Empty(missing);
    }

    /// <summary>
    /// A Style for DropDownButton based on DefaultDropDownButtonStyle makes the editor exit at start with a native XAML
    /// exception and no log line (X-013), though the key exists. Nothing may style DropDownButton.
    /// </summary>
    [Fact]
    public void NoStyleTargetsDropDownButton()
    {
        var editor = Path.Combine(RepoRoot(), "src", "CtrlAltDaze.Editor");
        var offenders = Directory.EnumerateFiles(editor, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadAllText(f).Contains("TargetType=\"DropDownButton\"", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();
        Assert.Empty(offenders);
    }

    /// <summary>
    /// The generic.xaml the editor builds against: the WinUI package (a dependency of Microsoft.WindowsAppSDK) at the
    /// version and in the package folder the editor's own restore recorded, not whatever else sits in the cache.
    /// </summary>
    private static string? GenericXaml(string editor)
    {
        var assets = Path.Combine(editor, "obj", "project.assets.json");
        if (!File.Exists(assets))
            return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(assets));
        var library = doc.RootElement.GetProperty("libraries").EnumerateObject()
            .FirstOrDefault(p => p.Name.StartsWith("Microsoft.WindowsAppSDK.WinUI/", StringComparison.OrdinalIgnoreCase));
        if (library.Value.ValueKind != JsonValueKind.Object || !library.Value.TryGetProperty("path", out var path))
            return null;
        return doc.RootElement.GetProperty("packageFolders").EnumerateObject()
            .Select(folder => Path.Combine(folder.Name, path.GetString()!, "lib", "native", "Microsoft.UI", "Themes", "generic.xaml"))
            .FirstOrDefault(File.Exists);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Packages.props"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    [GeneratedRegex(@"BasedOn=""\{StaticResource (\w+)\}""")]
    private static partial Regex BasedOn();

    [GeneratedRegex(@"x:Key=""(\w+)""")]
    private static partial Regex Keys();
}
