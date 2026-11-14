using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace CtrlAltDaze.Runtime.Tests;

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

    /// <summary>By name, or for an ordinal import ("#135", undocumented uxtheme exports) through <c>GetProcAddress(lib, MAKEINTRESOURCE(n))</c>.</summary>
    private static bool HasExport(nint lib, string name) =>
        name.StartsWith('#') && ushort.TryParse(name.AsSpan(1), out var ordinal)
            ? GetProcAddress(lib, ordinal) != 0
            : NativeLibrary.TryGetExport(lib, name, out _);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint GetProcAddress(nint module, nint ordinal);

    [Fact]
    public void EveryEngineImportExistsInItsDll()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !Directory.Exists(Path.Combine(root, "src", "CtrlAltDaze.Engine")))
            root = Path.GetDirectoryName(root);
        Assert.NotNull(root);

        var missing = new List<string>();
        var checkedCount = 0;
        var ordinals = 0;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src", "CtrlAltDaze.Engine"), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            foreach (Match m in Import().Matches(File.ReadAllText(file)))
            {
                var dll = m.Groups["dll"].Value;
                var ep = EntryPoint().Match(m.Groups["args"].Value);
                var name = ep.Success ? ep.Groups["ep"].Value : m.Groups["name"].Value;
                checkedCount++;
                if (name.StartsWith('#'))
                    ordinals++;
                if (!NativeLibrary.TryLoad(dll, out var lib) || !HasExport(lib, name))
                    missing.Add($"{dll}!{name} ({Path.GetFileName(file)})");
            }
        }
        Assert.True(ordinals >= 2, "the ordinal imports (uxtheme #135/#136) weren't scanned");
        Assert.True(checkedCount > 10, "the import scan found almost nothing: the pattern is broken");
        Assert.True(missing.Count == 0, "Missing native entry points: " + string.Join(", ", missing));
    }
}
