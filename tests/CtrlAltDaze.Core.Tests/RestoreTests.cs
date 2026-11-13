using System.Text;
using CtrlAltDaze.Core.Backup;
using CtrlAltDaze.Core.Yaml;

namespace CtrlAltDaze.Core.Tests;

public class RestoreTests
{
    private const string Dir = @"X:\backups";
    private static readonly RestoreTarget Target = new(@"X:\cfg\config.yaml", @"X:\cfg\editor.json", @"X:\cfg\auto");

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private static BackupService Svc(BackupTests.MemFiles f, string machine = "DAZE-PC") =>
        new(f, new FixedTime(new DateTimeOffset(2026, 11, 6, 21, 30, 0, TimeSpan.Zero)), "0.46.0", machine);

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    private static BackupArchive Make(BackupTests.MemFiles f, string config, byte[]? editor = null, string machine = "OTHER-PC")
    {
        var maker = Svc(f, machine);
        return maker.Open(maker.CreateIn(Dir, new BackupContents(B(config), editor), "manual").Path);
    }

    private const string Backup = """
        library:
          audio: { tv: { match: TV }, desk: { match: Desk } }
          bluetooth: { technics: { address: "B8:20:8E:ED:48:FC" } }
        flows:
          same: { name: Same, nodes: [{ id: a, step: notify, with: { text: x } }] }
          changed: { name: Changed, nodes: [{ id: a, step: notify, with: { text: new } }, { id: b, step: notify, with: { text: y } }] }
          fresh: { name: Fresh, nodes: [{ id: a, step: notify, with: { text: z } }] }
        """;

    private const string Current = """
        library:
          audio: { tv: { match: OldTV }, mine: { match: Mine } }
        flows:
          same: { name: Same, nodes: [{ id: a, step: notify, with: { text: x } }] }
          changed: { name: Changed, nodes: [{ id: a, step: notify, with: { text: old } }] }
          mine: { name: Mine, nodes: [{ id: a, step: notify, with: { text: m } }] }
        """;

    private static BackupTests.MemFiles WithCurrent(string text = Current)
    {
        var f = new BackupTests.MemFiles();
        f.Files[Target.ConfigPath] = B(text);
        return f;
    }

    [Fact]
    public void PreviewShowsTheMergeMatrix()
    {
        var f = WithCurrent();
        var p = Svc(f).Preview(Make(f, Backup), Target);

        Assert.True(p.CanReplace);
        Assert.True(p.CanAddMissing);
        Assert.Equal(ChangeState.Same, p.Items.Single(i => i is { Kind: "flow", Key: "same" }).State);
        var changed = p.Items.Single(i => i is { Kind: "flow", Key: "changed" });
        Assert.Equal(ChangeState.Changed, changed.State);
        Assert.Equal("1 step added, 1 step edited", changed.Summary);
        Assert.Equal(ChangeState.New, p.Items.Single(i => i is { Kind: "flow", Key: "fresh" }).State);
        Assert.Equal(ChangeState.OnlyHere, p.Items.Single(i => i is { Kind: "flow", Key: "mine" }).State);
        Assert.Equal(ChangeState.Changed, p.Items.Single(i => i is { Kind: "audio", Key: "tv" }).State);
        Assert.Equal(ChangeState.New, p.Items.Single(i => i is { Kind: "audio", Key: "desk" }).State);
        Assert.Equal(ChangeState.OnlyHere, p.Items.Single(i => i is { Kind: "audio", Key: "mine" }).State);
        Assert.Equal(1, p.Count("flow", ChangeState.New));
    }

    [Fact]
    public void ReplaceWritesTheBackupBytesAfterASafetyCopy()
    {
        var f = WithCurrent();
        var svc = Svc(f);
        var archive = Make(f, "# mine\r\n" + Backup);

        var outcome = svc.Restore(archive, RestoreMode.Replace, Target);

        Assert.Equal(archive.Config, f.Files[Target.ConfigPath]);
        var safety = svc.Open(outcome.SafetyPath!);
        Assert.Equal(B(Current), safety.Config);
        Assert.Equal("before-restore", safety.Manifest.Reason);
        Assert.StartsWith(Target.SafetyFolder, outcome.SafetyPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddMissingKeepsCurrentItemsAndAddsNewOnes()
    {
        var f = WithCurrent();
        var svc = Svc(f);

        var outcome = svc.Restore(Make(f, Backup), RestoreMode.AddMissing, Target);

        var cfg = ConfigYaml.Load(Encoding.UTF8.GetString(f.Files[Target.ConfigPath])).Config!;
        Assert.Equal(["same", "changed", "mine", "fresh"], cfg.Flows.Select(x => x.Id));
        Assert.Equal("old", ((Model.ScalarParam)((Model.StepNode)cfg.Flows[1].Nodes[0]).With["text"]).Text);
        Assert.Equal(["tv", "mine", "desk"], cfg.Library.Audio.Select(x => x.Key));
        Assert.Equal("OldTV", cfg.Library.Audio[0].Match);
        Assert.Single(cfg.Library.Bluetooth);
        Assert.Contains(outcome.KeptCurrent, k => k.Contains("\"changed\"", StringComparison.Ordinal));
        Assert.Contains(outcome.KeptCurrent, k => k.Contains("\"tv\"", StringComparison.Ordinal));
    }

    [Fact]
    public void UnreadableBackupConfigDisablesRestoreAndChangesNothing()
    {
        var f = WithCurrent();
        var svc = Svc(f);
        var archive = Make(f, "flows: [unclosed");

        var p = svc.Preview(archive, Target);
        Assert.False(p.CanReplace);
        Assert.False(p.CanAddMissing);
        Assert.StartsWith("config.yaml in this backup can't be read", p.Unreadable, StringComparison.Ordinal);
        var ex = Assert.Throws<RestoreException>(() => svc.Restore(archive, RestoreMode.Replace, Target));
        Assert.Equal(RestoreFailure.Unreadable, ex.Kind);
        Assert.Equal(B(Current), f.Files[Target.ConfigPath]);
    }

    [Fact]
    public void UnreadableCurrentConfigAllowsReplaceButNotAddMissing()
    {
        var f = WithCurrent("flows: [unclosed");
        var svc = Svc(f);
        var archive = Make(f, Backup);

        var p = svc.Preview(archive, Target);
        Assert.True(p.CanReplace);
        Assert.False(p.CanAddMissing);
        Assert.Equal(RestoreFailure.CurrentUnreadable, Assert.Throws<RestoreException>(() => svc.Restore(archive, RestoreMode.AddMissing, Target)).Kind);
        svc.Restore(archive, RestoreMode.Replace, Target);
        Assert.Equal(archive.Config, f.Files[Target.ConfigPath]);
    }

    [Fact]
    public void SecretLookingHeaderWarnsButStillRestores()
    {
        var f = WithCurrent();
        var svc = Svc(f);
        var archive = Make(f, """
            flows:
              hook: { nodes: [{ id: a, step: http.request, with: { url: "http://x", headers: { Authorization: "Bearer abcdefgh12345" } } }] }
            """);

        Assert.NotEmpty(svc.Preview(archive, Target).Warnings);
        svc.Restore(archive, RestoreMode.Replace, Target);
        Assert.Equal(archive.Config, f.Files[Target.ConfigPath]);
    }

    [Fact]
    public void NeedsAttentionListsMachineSpecificValuesCredentialsAndBrokenFlows()
    {
        var f = WithCurrent();
        var archive = Make(f, """
            library:
              bluetooth: { technics: { address: "B8:20:8E:ED:48:FC" } }
            flows:
              api: { nodes: [{ id: a, step: http.request, with: { url: "http://x", credential: gh } }] }
              prog: { nodes: [{ id: a, step: process.run, with: { path: "Z:\\nowhere\\app.exe" } }] }
              broken: { nodes: [{ id: a, step: bluetooth.connect, with: { device: nope } }] }
            """);

        var other = Svc(f).Preview(archive, Target, new PreviewEnvironment(["other"], _ => false)).NeedsAttention;
        Assert.Contains(other, x => x.Contains("Pair \"technics\"", StringComparison.Ordinal));
        Assert.Contains(other, x => x.Contains("credential \"gh\"", StringComparison.Ordinal));
        Assert.Contains(other, x => x.StartsWith("1 fixed path ", StringComparison.Ordinal));
        Assert.Contains(other, x => x.StartsWith("Flow \"broken\" won't run", StringComparison.Ordinal));

        var samePc = Svc(f, "OTHER-PC").Preview(archive, Target, new PreviewEnvironment(["GH"], _ => true)).NeedsAttention;
        Assert.DoesNotContain(samePc, x => x.Contains("Pair", StringComparison.Ordinal));
        Assert.DoesNotContain(samePc, x => x.Contains("credential", StringComparison.Ordinal));
        Assert.DoesNotContain(samePc, x => x.Contains("fixed path", StringComparison.Ordinal));
    }

    [Fact]
    public void SafetyCopyFailureStopsTheRestore()
    {
        var f = WithCurrent();
        var archive = Make(f, Backup);
        f.FailNewFiles = true;

        var ex = Assert.Throws<RestoreException>(() => Svc(f).Restore(archive, RestoreMode.Replace, Target));

        Assert.Equal(RestoreFailure.SafetyCopyFailed, ex.Kind);
        Assert.Equal(B(Current), f.Files[Target.ConfigPath]);
    }

    [Fact]
    public void FailedAtomicWriteLeavesTheOldConfigIntact()
    {
        var f = WithCurrent();
        var archive = Make(f, Backup);
        f.FailReplace = true;

        var ex = Assert.Throws<RestoreException>(() => Svc(f).Restore(archive, RestoreMode.Replace, Target));

        Assert.Equal(RestoreFailure.WriteFailed, ex.Kind);
        Assert.Equal(B(Current), f.Files[Target.ConfigPath]);
    }

    [Fact]
    public void NoCurrentConfigMeansNoSafetyCopyAndUndoRemovesTheRestoredFile()
    {
        var f = new BackupTests.MemFiles();
        var svc = Svc(f);
        var outcome = svc.Restore(Make(f, Backup), RestoreMode.Replace, Target);

        Assert.Null(outcome.SafetyPath);
        Assert.True(f.Files.ContainsKey(Target.ConfigPath));
        svc.Undo(outcome, Target);
        Assert.False(f.Files.ContainsKey(Target.ConfigPath));
    }

    [Fact]
    public void UndoPutsBackConfigAndEditorLayout()
    {
        var f = WithCurrent();
        f.Files[Target.EditorJsonPath!] = B("{\"window\":{\"x\":1},\"nav\":\"old\"}");
        var svc = Svc(f);
        var archive = Make(f, Backup, B("{\"window\":{\"x\":99},\"nav\":\"new\"}"));

        var outcome = svc.Restore(archive, RestoreMode.Replace, Target, includeEditorLayout: true);

        Assert.True(outcome.EditorLayoutRestored);
        var restored = Encoding.UTF8.GetString(f.Files[Target.EditorJsonPath!]);
        Assert.Contains("\"nav\":\"new\"", restored, StringComparison.Ordinal);
        Assert.Contains("\"x\":1", restored, StringComparison.Ordinal); // this PC's window kept
        Assert.DoesNotContain("99", restored, StringComparison.Ordinal);

        svc.Undo(outcome, Target);
        Assert.Equal(B(Current), f.Files[Target.ConfigPath]);
        Assert.Equal(B("{\"window\":{\"x\":1},\"nav\":\"old\"}"), f.Files[Target.EditorJsonPath!]);
    }

    [Fact]
    public void DamagedCurrentConfigRefusesAddMissing()
    {
        var f = WithCurrent("""
            library: { bluetooth: { x: { name: X } } }
            flows:
              keep: { nodes: [{ id: a, step: notify, with: { text: x } }] }
            """);
        var svc = Svc(f);
        var archive = Make(f, Backup);

        Assert.False(svc.Preview(archive, Target).CanAddMissing);
        Assert.Equal(RestoreFailure.CurrentUnreadable, Assert.Throws<RestoreException>(() => svc.Restore(archive, RestoreMode.AddMissing, Target)).Kind);
    }

    [Fact]
    public void UndoRestoresAnEditorFileThatHadNoConfigBesideIt()
    {
        var f = new BackupTests.MemFiles();
        f.Files[Target.EditorJsonPath!] = B("{\"nav\":\"old\"}");
        var svc = Svc(f);
        var archive = Make(f, Backup, B("{\"nav\":\"new\"}"));

        var outcome = svc.Restore(archive, RestoreMode.Replace, Target, includeEditorLayout: true);
        svc.Undo(outcome, Target);

        Assert.Equal(B("{\"nav\":\"old\"}"), f.Files[Target.EditorJsonPath!]);
    }

    [Fact]
    public void AddMissingSaysItRewritesTheFile()
    {
        var f = WithCurrent();
        var outcome = Svc(f).Restore(Make(f, Backup), RestoreMode.AddMissing, Target);
        Assert.Contains(outcome.Notes, n => n.Contains("rewrites config.yaml", StringComparison.Ordinal));
    }

    [Fact]
    public void EditorLayoutIsLeftAloneUnlessAsked()
    {
        var f = WithCurrent();
        f.Files[Target.EditorJsonPath!] = B("{\"nav\":\"old\"}");
        var archive = Make(f, Backup, B("{\"nav\":\"new\"}"));

        var outcome = Svc(f).Restore(archive, RestoreMode.Replace, Target);

        Assert.False(outcome.EditorLayoutRestored);
        Assert.Equal(B("{\"nav\":\"old\"}"), f.Files[Target.EditorJsonPath!]);
    }
}
