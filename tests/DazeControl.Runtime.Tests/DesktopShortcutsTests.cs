using DazeControl.Core.Model;
using DazeControl.Runtime.Lifecycle;

namespace DazeControl.Runtime.Tests;

public sealed class EngineArgumentsTests
{
    [Theory]
    [InlineData(new string[0], null, null)]
    [InlineData(new[] { @"C:\c.yaml" }, @"C:\c.yaml", null)]
    [InlineData(new[] { "--run", "tv" }, null, "tv")]
    [InlineData(new[] { "--run", "tv", @"C:\c.yaml" }, @"C:\c.yaml", "tv")]
    [InlineData(new[] { @"C:\c.yaml", "--run", "tv" }, @"C:\c.yaml", "tv")]
    [InlineData(new[] { "--run" }, null, null)]
    [InlineData(new[] { "--run", "" }, null, null)]
    [InlineData(new[] { "--run", "--odd id" }, null, "--odd id")]
    public void ParsesRunAndConfig(string[] args, string? config, string? run)
    {
        var parsed = EngineArguments.Parse(args);

        Assert.Equal(config, parsed.Config);
        Assert.Equal(run, parsed.RunFlowId);
    }

    [Theory]
    [InlineData("tv", null)]
    [InlineData("a b", @"C:\Users\me\My flows\c.yaml")]
    [InlineData("quote\"in", @"C:\trailing\")]
    [InlineData(@"back\\slash\", null)]
    [InlineData("--run", "x")]
    public void FormatRoundTripsThroughTheCommandLine(string flowId, string? config)
    {
        var parsed = EngineArguments.Parse(EngineArguments.Split(EngineArguments.Format(flowId, config)));

        Assert.Equal(flowId, parsed.RunFlowId);
        Assert.Equal(config, parsed.Config);
    }

    [Fact]
    public void SplitFollowsTheCRuntime()
    {
        Assert.Equal(["a", "b c", "d\"e", @"f\\g", @"h\"], EngineArguments.Split(@"a ""b c"" d\""e f\\g ""h\\"""));
    }
}

public sealed class ShortcutNamesTests
{
    [Theory]
    [InlineData("TV only", "TV only")]
    [InlineData("Headphones", "Headphones")]
    public void ASafeNameStaysAsItIs(string name, string expected) => Assert.Equal(expected, ShortcutNames.FileName(name, "id"));

    [Theory]
    [InlineData("TV / Hi-Fi", "TV _ Hi-Fi ")]
    [InlineData("a:b*c?", "a_b_c_ ")]
    [InlineData("CON", "_CON ")]
    [InlineData("nul.txt", "_nul.txt ")]
    [InlineData("com1", "_com1 ")]
    [InlineData("dots...", "dots ")]
    public void AnUnsafeNameIsMadeSafeAndHashed(string name, string prefix)
    {
        var safe = ShortcutNames.FileName(name, "id");

        Assert.StartsWith(prefix, safe, StringComparison.Ordinal);
        Assert.Matches(@" [0-9a-f]{6}$", safe);
        Assert.DoesNotContain(safe, c => Path.GetInvalidFileNameChars().Contains(c));
    }

    [Fact]
    public void ABlankNameFallsBackToTheId() => Assert.Equal("tv", ShortcutNames.FileName("   ", "tv"));

    [Fact]
    public void TwoNamesThatBecomeEqualStayApart() =>
        Assert.NotEqual(ShortcutNames.FileName("a/b", "one"), ShortcutNames.FileName("a\\b", "two"));

    [Fact]
    public void ALongNameIsCut() => Assert.True(ShortcutNames.FileName(new string('x', 300), "id").Length <= ShortcutNames.MaxLength + 7);
}

public sealed class DesktopShortcutsTests : IDisposable
{
    private const string Stub = @"C:\Users\me\AppData\Local\DazeControlApp\Daze Control.exe";
    private const string Exe = @"C:\Users\me\AppData\Local\DazeControlApp\current\DazeControl.exe";
    private const string DefaultConfig = @"C:\Users\me\AppData\Roaming\DazeControl\config.yaml";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "dc-shortcuts-" + Guid.NewGuid().ToString("N"));
    private readonly FakeLinks _links = new();
    private readonly FakeIcons _icons = new();

    private string Desktop => Path.Combine(_root, "Desktop");

    private string Icons => Path.Combine(_root, "shortcuts");

    public DesktopShortcutsTests() => Directory.CreateDirectory(Desktop);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private DesktopShortcuts Make(string? config = null) =>
        new(new ShortcutPlaces(Desktop, Icons, Stub, [Stub, Exe], config, DefaultConfig), _links, _icons);

    private static Flow Flow(string id, string name, string? glyph = null) => new(id, name, []) { Glyph = glyph };

    [Fact]
    public void CreateWritesALinkToTheStubWithTheIdAndTheGlyphIcon()
    {
        var made = Make().Create(Flow("tv", "TV only", "E7F4"));

        Assert.Equal(Path.Combine(Desktop, "TV only.lnk"), made.Path);
        var link = _links.Files[made.Path];
        Assert.Equal(Stub, link.Target);
        Assert.Equal("--run \"tv\"", link.Arguments);
        Assert.StartsWith(Path.Combine(Icons, "tv "), link.IconPath, StringComparison.Ordinal);
        Assert.Equal("\uE7F4", _icons.Written[link.IconPath!]);
    }

    [Fact]
    public void ANonDefaultConfigGoesIntoTheArguments()
    {
        var made = Make(@"D:\other.yaml").Create(Flow("tv", "TV"));

        var args = EngineArguments.Parse(EngineArguments.Split(_links.Files[made.Path].Arguments));
        Assert.Equal(@"D:\other.yaml", args.Config);
        Assert.Equal("tv", args.RunFlowId);
    }

    [Fact]
    public void TheScanMatchesOnlyOurTargetWithRun()
    {
        _links.Put(Path.Combine(Desktop, "ours.lnk"), new(Stub, "--run \"a\""));
        _links.Put(Path.Combine(Desktop, "ours exe.lnk"), new(Exe.ToUpperInvariant(), "--run \"b\""));
        _links.Put(Path.Combine(Desktop, "other app.lnk"), new(@"C:\Other\app.exe", "--run \"c\""));
        _links.Put(Path.Combine(Desktop, "plain engine.lnk"), new(Stub, ""));
        _links.Put(Path.Combine(Desktop, "broken.lnk"), null);

        Assert.Equal(["a", "b"], Make().List().Select(s => s.FlowId).Order());
    }

    [Fact]
    public void AnotherConfigsShortcutsAreNotListed()
    {
        _links.Put(Path.Combine(Desktop, "default.lnk"), new(Stub, "--run \"a\""));
        _links.Put(Path.Combine(Desktop, "default named.lnk"), new(Stub, $"--run \"b\" \"{DefaultConfig}\""));
        _links.Put(Path.Combine(Desktop, "other.lnk"), new(Stub, "--run \"c\" \"D:\\other.yaml\""));

        Assert.Equal(["a", "b"], Make().List().Select(s => s.FlowId).Order());
        Assert.Equal(["c"], Make(@"D:\other.yaml").List().Select(s => s.FlowId));
    }

    [Fact]
    public void CreateAfterARenameReplacesTheOldName()
    {
        var shortcuts = Make();
        var old = shortcuts.Create(Flow("tv", "TV"));

        var renamed = Flow("tv", "Living room");
        Assert.False(DesktopShortcuts.HasCurrentName(old, renamed));
        var made = shortcuts.Create(renamed);

        Assert.Equal(["Living room"], shortcuts.List().Select(s => s.Name));
        Assert.True(DesktopShortcuts.HasCurrentName(made, renamed));
    }

    [Fact]
    public void AFileOfTheSameNameIsNeverOverwritten()
    {
        var mine = Path.Combine(Desktop, "TV.lnk");
        _links.Put(mine, new(@"C:\Other\tv.exe", ""));

        var made = Make().Create(Flow("tv", "TV"));

        Assert.Equal(Path.Combine(Desktop, "TV (2).lnk"), made.Path);
        Assert.Equal(@"C:\Other\tv.exe", _links.Files[mine].Target);
        Assert.True(DesktopShortcuts.HasCurrentName(made, Flow("tv", "TV")));
    }

    [Fact]
    public void RemoveTakesOnlyThatFlowsShortcutsAndItsIcon()
    {
        var shortcuts = Make();
        shortcuts.Create(Flow("a", "A"));
        shortcuts.Create(Flow("b", "B"));

        Assert.Equal(1, shortcuts.Remove("a"));

        Assert.Equal(["b"], shortcuts.List().Select(s => s.FlowId));
        Assert.False(File.Exists(shortcuts.IconPath("a")));
        Assert.True(File.Exists(shortcuts.IconPath("b")));
    }

    [Fact]
    public void RemoveAllTakesEveryConfigsShortcutsAndTheIcons()
    {
        Make().Create(Flow("a", "A"));
        Make(@"D:\other.yaml").Create(Flow("b", "B"));
        _links.Put(Path.Combine(Desktop, "other app.lnk"), new(@"C:\Other\app.exe", "--run \"c\""));

        Assert.Equal(2, Make().RemoveAll());

        Assert.Equal(["other app.lnk"], _links.Files.Keys.Select(Path.GetFileName));
        Assert.False(Directory.Exists(Icons));
    }

    [Fact]
    public void AnIconThatFailsLeavesTheShortcutWithoutOne()
    {
        _icons.Fail = true;

        var made = Make().Create(Flow("tv", "TV"));

        Assert.Null(_links.Files[made.Path].IconPath);
    }

    [Fact]
    public void AnUnsafeIdGetsASafeIconNameButKeepsTheRealIdInTheArguments()
    {
        var made = Make().Create(Flow("tv/hifi", "TV"));

        var link = _links.Files[made.Path];
        Assert.Equal("tv/hifi", EngineArguments.Parse(EngineArguments.Split(link.Arguments)).RunFlowId);
        Assert.DoesNotContain('/', Path.GetFileName(link.IconPath!));
        Assert.Equal("tv/hifi", Make().List().Single().FlowId);
    }

    [Fact]
    public void CaseOnlyIdsAndOtherConfigsGetTheirOwnIcons()
    {
        var lower = Make().IconPath("tv");
        Assert.NotEqual(lower, Make().IconPath("TV"));
        Assert.NotEqual(lower, Make(@"D:\other.yaml").IconPath("tv"));
        Assert.Equal(lower, Make(DefaultConfig).IconPath("tv")); // the default config, named or not
    }

    [Fact]
    public void AFolderWithTheNameIsNeverUsed()
    {
        Directory.CreateDirectory(Path.Combine(Desktop, "TV.lnk"));

        Assert.Equal(Path.Combine(Desktop, "TV (2).lnk"), Make().Create(Flow("tv", "TV")).Path);
    }

    [Fact]
    public async Task ConcurrentCreatesOfTwoSameNamedFlowsDontOverwrite()
    {
        var shortcuts = Make();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => shortcuts.Create(Flow("f" + i, "TV")))));

        Assert.Equal(8, shortcuts.List().Select(s => s.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void RepairPutsBackAnIconAnUpdateReplacedAndKeepsTheName()
    {
        var shortcuts = Make();
        var made = shortcuts.Create(Flow("tv", "TV", "E7F4"));
        var icon = _links.Files[made.Path].IconPath!;
        // What Velopack's updater leaves: same target and arguments, the main exe's icon.
        _links.Put(made.Path, new(Stub, "--run \"tv\"", Exe));

        Assert.Equal(1, shortcuts.RepairIcons([Flow("tv", "Renamed", "E7F4")]));

        Assert.Equal(icon, _links.Files[made.Path].IconPath);
        Assert.Equal(["TV"], shortcuts.List().Select(s => s.Name));
    }

    [Fact]
    public void RepairRedrawsAMissingIconAndLeavesGoodOnesAlone()
    {
        var shortcuts = Make();
        var a = shortcuts.Create(Flow("a", "A"));
        shortcuts.Create(Flow("b", "B"));
        File.Delete(_links.Files[a.Path].IconPath!);
        _icons.Written.Clear();

        Assert.Equal(1, shortcuts.RepairIcons([Flow("a", "A"), Flow("b", "B")]));

        Assert.Equal([shortcuts.IconPath("a")], _icons.Written.Keys);
    }

    [Fact]
    public void RepairSkipsAShortcutWhoseFlowIsGone()
    {
        _links.Put(Path.Combine(Desktop, "old.lnk"), new(Stub, "--run \"gone\"", Exe));

        Assert.Equal(0, Make().RepairIcons([Flow("tv", "TV")]));
        Assert.Equal(Exe, _links.Files[Path.Combine(Desktop, "old.lnk")].IconPath);
    }

    /// <summary>Links as real (empty) files, so the Desktop scan and the name clash check see them.</summary>
    private sealed class FakeLinks : IShellLinkStore
    {
        public Dictionary<string, ShellLinkData> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        private readonly HashSet<string> _unreadable = new(StringComparer.OrdinalIgnoreCase);

        public void Put(string path, ShellLinkData? link)
        {
            File.WriteAllText(path, "");
            if (link is null)
                _unreadable.Add(path);
            else
                Files[path] = link;
        }

        public IEnumerable<string> Links(string directory) => Directory.EnumerateFiles(directory, "*.lnk");

        public ShellLinkData? Read(string path) => _unreadable.Contains(path) ? null : Files.GetValueOrDefault(path);

        public void Write(string path, ShellLinkData link) => Put(path, link);

        public void Delete(string path)
        {
            File.Delete(path);
            Files.Remove(path);
        }
    }

    private sealed class FakeIcons : IFlowIconRenderer
    {
        public Dictionary<string, string> Written { get; } = [];

        public bool Fail { get; set; }

        public void Write(string path, string glyph)
        {
            if (Fail)
                throw new IOException("no");
            File.WriteAllText(path, glyph);
            Written[path] = glyph;
        }
    }
}

public sealed class IconFileTests
{
    [Fact]
    public void WritesADirectoryAndBottomUpBmpEntries()
    {
        var pixels = new uint[4];
        pixels[0] = 0xFF112233; // top-left
        using var stream = new MemoryStream();

        IconFile.Write(stream, [new IconImage(2, pixels), new IconImage(256, new uint[256 * 256])]);

        var bytes = stream.ToArray();
        Assert.Equal([0, 0, 1, 0, 2, 0], bytes[..6]);
        Assert.Equal(2, bytes[6]); // first width
        Assert.Equal(0, bytes[6 + 16]); // 256 is written as 0
        var offset = BitConverter.ToInt32(bytes, 6 + 12);
        Assert.Equal(6 + 32, offset);
        Assert.Equal(4, BitConverter.ToInt32(bytes, offset + 8)); // height counts the mask
        // Bottom-up: the top-left pixel is the first of the second (last) row.
        var top = offset + 40 + 2 * 4;
        Assert.Equal(new byte[] { 0x33, 0x22, 0x11, 0xFF }, bytes[top..(top + 4)]);
        var second = BitConverter.ToInt32(bytes, 6 + 16 + 12);
        Assert.Equal(offset + BitConverter.ToInt32(bytes, 6 + 8), second);
        Assert.Equal(bytes.Length, second + BitConverter.ToInt32(bytes, 6 + 16 + 8));
    }
}
