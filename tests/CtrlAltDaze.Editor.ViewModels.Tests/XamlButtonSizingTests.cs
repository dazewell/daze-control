using System.Text.RegularExpressions;

namespace CtrlAltDaze.Editor.ViewModels.Tests;

/// <summary>
/// One size logic for every button (D-121): a button's height, width and padding come from a shared style in App.xaml
/// (IconButton, PrimaryButton, ChipToggleButton…), never from the button itself, so neighbours always line up.
/// </summary>
public sealed partial class XamlButtonSizingTests
{
    [Fact]
    public void NoButtonSetsItsOwnSize()
    {
        var editor = Path.Combine(RepoRoot(), "src", "CtrlAltDaze.Editor");
        var offenders = Directory.EnumerateFiles(editor, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(f => ButtonTag().Matches(File.ReadAllText(f))
                .Where(m => Sizing().IsMatch(m.Value))
                .Select(m => $"{Path.GetFileName(f)}: {Regex.Replace(m.Value, @"\s+", " ")[..Math.Min(110, Regex.Replace(m.Value, @"\s+", " ").Length)]}"))
            .ToList();
        Assert.Empty(offenders);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Packages.props"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    [GeneratedRegex(@"<(?:Button|ToggleButton|DropDownButton|HyperlinkButton|SplitButton)\b(?:[^>""]|""[^""]*"")*>")]
    private static partial Regex ButtonTag();

    [GeneratedRegex(@"\s(?:Width|Height|MinWidth|MinHeight|Padding)=""")]
    private static partial Regex Sizing();
}
