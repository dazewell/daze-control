using System.Diagnostics;
using DazeControl.Core.Model;
using DazeControl.Core.Validation;
using DazeControl.Core.Yaml;

namespace DazeControl.Core.Tests;

/// <summary>T26.5: <c>file.*</c> triggers: reading, the folder and filter rules, the validator.</summary>
public class FileTriggerTests
{
    private const string Head = "version: 1\nflows:\n  sort:\n    nodes: []\n    triggers:\n";

    [Fact]
    public void AFileEventRoundTripsWithItsFolderAndFilter()
    {
        var load = ConfigYaml.Load(Head + "      - { on: file.created, path: '%USERPROFILE%\\Downloads', filter: ' *.pdf ' }\n      - { on: file.deleted, path: 'C:\\Temp' }\n");

        Assert.Empty(load.Problems);
        Assert.Equal([new EventTrigger("file.created") { Path = "%USERPROFILE%\\Downloads", Filter = "*.pdf" }, new EventTrigger("file.deleted") { Path = "C:\\Temp" }],
            load.Config!.Flows[0].Triggers);
        Assert.Equal(load.Config, ConfigYaml.Load(ConfigYaml.Save(load.Config)).Config);
    }

    [Theory]
    [InlineData("{ on: file.created }", "path needs the folder")]
    [InlineData("{ on: file.changed, path: 'Downloads' }", "full folder name")]
    [InlineData("{ on: file.changed, path: 'C:\\Temp\\..\\Windows' }", "can't use ..")]
    [InlineData("{ on: file.changed, path: '%NO_SUCH_VARIABLE_T265%\\x' }", "isn't set")]
    [InlineData("{ on: file.changed, path: 'C:\\Temp', filter: 'a\\b' }", "not a path")]
    [InlineData("{ on: session.lock, path: 'C:\\Temp' }", "only go with file.created")]
    [InlineData("{ on: session.lock, filter: '*.pdf' }", "only go with file.created")]
    public void AWrongFileTriggerIsAProblemOnThatTriggerOnly(string trigger, string expected)
    {
        var load = ConfigYaml.Load(Head + $"      - {trigger}\n      - {{ on: session.unlock }}\n");

        Assert.Contains(load.Problems, p => p.Message.Contains(expected, StringComparison.Ordinal));
        Assert.Equal([new EventTrigger("session.unlock")], load.Config!.Flows[0].Triggers);
    }

    [Fact]
    public void AFolderIsMadeFullWithVariablesExpandedAndNoTrailingSeparator()
    {
        Environment.SetEnvironmentVariable("DAZE_T265_ROOT", @"C:\Daze");
        try
        {
            Assert.Equal(@"C:\Daze\in", TriggerText.ResolveFolder(@" %DAZE_T265_ROOT%\in\ ", out var problem));
            Assert.Null(problem);
            Assert.Equal(@"C:\a\b", TriggerText.ResolveFolder(@"C:\a/b/", out _));
            Assert.Null(TriggerText.ResolveFolder(@"\\?\C:\x", out problem));
            Assert.Contains("device", problem, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DAZE_T265_ROOT", null);
        }
    }

    [Theory]
    [InlineData("*.pdf", "report.PDF", true)]
    [InlineData("*.pdf", "report.pdf.part", false)]
    [InlineData("a?c*", "abc", true)]
    [InlineData("a?c*", "ac", false)]
    [InlineData("*", "anything", true)]
    [InlineData("**x**", "abxcd", true)]
    [InlineData("", "anything", true)]
    [InlineData("*.tar.gz", "x.tar.gz", true)]
    [InlineData("report-*", "Report-2026", true)]
    public void AFilterIsASimpleWildcardOnTheFileName(string filter, string name, bool expected) =>
        Assert.Equal(expected, TriggerText.FilterMatches(filter, name));

    [Fact]
    public void AnAdversarialFilterCannotMakeTheMatchRunAway()
    {
        var filter = string.Concat(Enumerable.Repeat("a*", 60)) + "b"; // exponential for a backtracking regex
        var name = new string('a', 250);

        var watch = Stopwatch.StartNew();
        Assert.False(TriggerText.FilterMatches(filter, name));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"took {watch.Elapsed}");
    }

    [Fact]
    public void ATooLongFilterIsRefused() =>
        Assert.Contains("at most", TriggerText.FileFilterProblem("file.created", @"C:\Temp", new string('a', TriggerText.MaxFilterLength + 1)), StringComparison.Ordinal);

    [Fact]
    public void TheValidatorWarnsAboutAFolderThatIsNotThereAndAboutWhatTheRuntimeSkips()
    {
        var config = ConfigYaml.Load(Head + "      - { on: file.created, path: 'C:\\Gone' }\n      - { on: file.created, path: 'C:\\Here' }\n      - { on: file.created, path: '\\\\nas\\share' }\n").Config!;
        var flow = config.Flows[0] with { Triggers = [.. config.Flows[0].Triggers, new EventTrigger("file.changed"), new EventTrigger("file.changed") { Path = @"C:\a\..\b" }] };

        var problems = ConfigValidator.Validate(config with { Flows = [flow] }, folderExists: f => f == @"C:\Here");

        Assert.Contains(problems, p => p.Path.EndsWith("triggers[0].path", StringComparison.Ordinal) && p.Message.Contains(@"C:\Gone isn't there", StringComparison.Ordinal) && p.Severity == Severity.Warning);
        Assert.DoesNotContain(problems, p => p.Path.EndsWith("triggers[1].path", StringComparison.Ordinal) || p.Path.EndsWith("triggers[2].path", StringComparison.Ordinal)); // there; a share isn't asked
        Assert.Contains(problems, p => p.Path.EndsWith("triggers[3]", StringComparison.Ordinal) && p.Message.Contains("path needs", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Path.EndsWith("triggers[4]", StringComparison.Ordinal) && p.Message.Contains("..", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryFileEventHasAHistoryLabel() =>
        Assert.All(TriggerText.Events.Where(TriggerText.IsFileEvent), e => Assert.NotEqual(e, TriggerText.EventLabel(e)));
}
