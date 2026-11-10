using System.Text.RegularExpressions;

namespace DazeControl.Editor.ViewModels.Tests;

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
        var editor = Path.Combine(RepoRoot(), "src", "DazeControl.Editor");
        var xaml = Directory.EnumerateFiles(editor, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToDictionary(f => f, File.ReadAllText);
        var generic = GenericXaml();
        Assert.True(generic is not null, "WinUI generic.xaml not found in the NuGet cache; restore the editor first.");
        var known = new HashSet<string>(Keys().Matches(File.ReadAllText(generic!)).Select(m => m.Groups[1].Value));
        known.UnionWith(xaml.Values.SelectMany(t => Keys().Matches(t)).Select(m => m.Groups[1].Value));

        var missing = xaml
            .SelectMany(kv => BasedOn().Matches(kv.Value).Select(m => (File: Path.GetFileName(kv.Key), Key: m.Groups[1].Value)))
            .Where(r => !known.Contains(r.Key))
            .Select(r => $"{r.File}: {r.Key}")
            .ToList();
        Assert.Empty(missing);
    }

    private static string? GenericXaml()
    {
        var root = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
                   ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        var winui = Path.Combine(root, "microsoft.windowsappsdk.winui");
        if (!Directory.Exists(winui)) return null;
        return Directory.EnumerateDirectories(winui)
            .OrderByDescending(d => Version.TryParse(Path.GetFileName(d).Split('-')[0], out var v) ? v : new Version())
            .Select(d => Path.Combine(d, "lib", "native", "Microsoft.UI", "Themes", "generic.xaml"))
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
