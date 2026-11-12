using DazeControl.Core.Model;
using DazeControl.Core.Validation;
using DazeControl.Core.Yaml;

namespace DazeControl.Core.Tests;

/// <summary>
/// Every place a Library key can be stored (step settings, checks, flow input defaults, values <c>flow.run</c> passes, a Pick
/// device's default) follows a rename, counts as "used" and is named by the validator once the item is gone.
/// Triggers, "Only if" conditions and step defaults hold no Library keys, so they are not covered.
/// </summary>
public class LibraryReferenceAuditTests
{
    private const string Ccd = "AQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    private const string Library = $$"""
        library:
          audio: { old-audio: { match: Technics } }
          bluetooth: { old-bt: { address: "B8:20:8E:ED:48:FC" } }
          bound: { old-bound: { bluetooth: old-bt, audio: old-audio } }
          displays: { old-layout: { monitors: [m], ccd: '{{Ccd}}' } }
          android: { old-tv: { address: 192.168.1.50, inputs: { pc: [down] } } }

        """;

    /// <summary>Kind, its key, a step setting, a check step, the pick word and the setting name each step uses.</summary>
    public static TheoryData<LibraryKind, string, string, string, string, string, string> Kinds => new()
    {
        { LibraryKind.Audio, "old-audio", "new-audio", "audio.setDefault", "audio.check", "audio", "device" },
        { LibraryKind.Bluetooth, "old-bt", "new-bt", "bluetooth.connect", "bluetooth.check", "bluetooth", "device" },
        { LibraryKind.Bound, "old-bound", "new-bound", "bluetooth.connect", "audio.check", "bound", "device" },
        { LibraryKind.Display, "old-layout", "new-layout", "display.layout", "display.check", "display", "layout" },
        { LibraryKind.Android, "old-tv", "new-tv", "android.wake", "android.check", "android", "device" },
    };

    private static Configuration Build(LibraryKind kind, string key, string step, string check, string pick, string setting)
    {
        var input = kind == LibraryKind.Bound ? "bound" : pick;
        var yaml = Library + $$"""
            flows:
              step:
                nodes: [ { id: n, step: {{step}}, with: { {{setting}}: {{key}} } } ]
              check:
                nodes: [ { id: n, step: {{check}}, with: { {{setting}}: {{key}} } } ]
              input:
                inputs: { dev: { kind: {{input}}, name: Device, default: {{key}} } }
                nodes: [ { id: n, step: {{step}}, with: { {{setting}}: $dev } } ]
              callee:
                inputs: { dev: { kind: {{input}}, name: Device, default: {{key}} } }
                nodes: [ { id: n, step: {{step}}, with: { {{setting}}: $dev } } ]
              call:
                nodes: [ { id: n, step: flow.run, with: { flow: callee, inputs: { dev: {{key}} } } } ]
              pick:
                nodes: [ { id: n, step: prompt.pick, with: { kind: {{pick}}, default: {{key}} } } ]
            """;
        var load = ConfigYaml.Load(yaml);
        Assert.Empty(load.Problems);
        return load.Config!;
    }

    private static readonly string[] Sites = ["step", "check", "input", "call", "pick"];

    [Theory, MemberData(nameof(Kinds))]
    public void RenameFollowsEveryStorageSiteAndUsesCountsThem(LibraryKind kind, string key, string newKey, string step, string check, string pick, string setting)
    {
        var config = Build(kind, key, step, check, pick, setting);
        Assert.DoesNotContain(ConfigValidator.Validate(config), p => p.Severity == Severity.Error);
        foreach (var site in Sites)
            Assert.True(LibraryEdits.Uses(config.Flows.First(f => f.Id == site), config, kind, key), $"{site} should use {key}");

        var (renamed, refusal) = LibraryEdits.Rename(config, kind, key, newKey);

        Assert.Equal(RenameRefusal.None, refusal);
        Assert.DoesNotContain(ConfigValidator.Validate(renamed), p => p.Severity == Severity.Error);
        Assert.DoesNotContain(key, ConfigYaml.Save(renamed), StringComparison.Ordinal);
        Assert.Contains(newKey, ConfigYaml.Save(renamed), StringComparison.Ordinal);
        foreach (var site in Sites)
        {
            var flow = renamed.Flows.First(f => f.Id == site);
            Assert.False(LibraryEdits.Uses(flow, renamed, kind, key), $"{site} still uses {key}");
            Assert.True(LibraryEdits.Uses(flow, renamed, kind, newKey), $"{site} lost {newKey}");
        }
    }

    [Theory, MemberData(nameof(Kinds))]
    public void ARemovedItemIsNamedByTheValidatorAtEveryStorageSite(LibraryKind kind, string key, string newKey, string step, string check, string pick, string setting)
    {
        _ = newKey;
        var config = Build(kind, key, step, check, pick, setting);
        var library = config.Library;
        library = kind switch
        {
            LibraryKind.Audio => library with { Audio = [] },
            LibraryKind.Bluetooth => library with { Bluetooth = [] },
            LibraryKind.Bound => library with { Bound = [] },
            LibraryKind.Display => library with { Displays = [] },
            _ => library with { Android = [] },
        };
        var problems = ConfigValidator.Validate(config with { Library = library });

        foreach (var site in Sites.Where(s => s != "call")) // the call's value is reported on the caller (below)
            Assert.Contains(problems, p => p.FlowId == site && p.Message.Contains(key, StringComparison.Ordinal));
        Assert.Contains(problems, p => p.FlowId == "call" && p.Message.Contains(key, StringComparison.Ordinal));
    }
}
