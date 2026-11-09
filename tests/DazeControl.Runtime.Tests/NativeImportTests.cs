using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace DazeControl.Runtime.Tests;

/// <summary>
/// Smoke test for the engine's hand-written P/Invokes: every <c>[LibraryImport("x.dll")]</c> must name a function the
/// DLL really exports on this Windows. NetworkSource shipped calling an entry point that doesn't exist (found only in
/// the user's engine.log); this catches that kind of typo without loading the engine.
/// </summary>
public sealed partial class NativeImportTests
{
    [GeneratedRegex(@"\[LibraryImport\(""(?<dll>[^""]+)""(?<args>[^\]]*)\]\s*(?:\[[^\]]*\]\s*)*[^;{(]*?\s(?<name>\w+)\(", RegexOptions.Singleline)]
    private static partial Regex Import();

    [GeneratedRegex(@"EntryPoint\s*=\s*""(?<ep>[^""]+)""")]
    private static partial Regex EntryPoint();

    [Fact]
    public void EveryEngineImportExistsInItsDll()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !Directory.Exists(Path.Combine(root, "src", "DazeControl.Engine")))
            root = Path.GetDirectoryName(root);
        Assert.NotNull(root);

        var missing = new List<string>();
        var checkedCount = 0;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src", "DazeControl.Engine"), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            foreach (Match m in Import().Matches(File.ReadAllText(file)))
            {
                var dll = m.Groups["dll"].Value;
                var ep = EntryPoint().Match(m.Groups["args"].Value);
                var name = ep.Success ? ep.Groups["ep"].Value : m.Groups["name"].Value;
                checkedCount++;
                if (name.StartsWith('#'))
                    continue; // by ordinal (undocumented uxtheme exports): not checkable by name
                if (!NativeLibrary.TryLoad(dll, out var lib) || !NativeLibrary.TryGetExport(lib, name, out _))
                    missing.Add($"{dll}!{name} ({Path.GetFileName(file)})");
            }
        }
        Assert.True(checkedCount > 10, "the import scan found almost nothing: the pattern is broken");
        Assert.True(missing.Count == 0, "Missing native entry points: " + string.Join(", ", missing));
    }
}
