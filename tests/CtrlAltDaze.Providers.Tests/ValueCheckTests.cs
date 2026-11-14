using CtrlAltDaze.Core.Model;
using CtrlAltDaze.Core.Runs;
using CtrlAltDaze.Core.Yaml;
using CtrlAltDaze.Providers.Basic;
using CtrlAltDaze.Runtime;
using CtrlAltDaze.Runtime.Providers;

namespace CtrlAltDaze.Providers.Tests;

/// <summary>T24.3 (D-137): the Check value step, per comparison, and in a run with values from earlier steps.</summary>
public sealed class ValueCheckTests
{
    private static async Task<(bool Holds, string Result)> Check(string value, string op, string to = "", string? ignoreCase = null)
    {
        var with = new List<(string, string)> { ("value", value), ("is", op), ("to", to) };
        if (ignoreCase is not null)
            with.Add(("ignoreCase", ignoreCase));
        var context = Steps.Context("value.check", [.. with]);
        var holds = true;
        try
        {
            await new ValueCheckProvider().ExecuteAsync(context, TestContext.Current.CancellationToken);
        }
        catch (Exception e) when (e is StepFailedException or CheckNotMetException)
        {
            holds = false;
            Assert.Equal(e.Message, context.Values["result"].Text); // a failure link can say why
        }
        return (holds, context.Values["result"].Text);
    }

    [Theory]
    [InlineData("Home", "is", "home", null, true)]
    [InlineData("Home", "is", "home", "false", false)]
    [InlineData("Home", "isNot", "home", null, false)]
    [InlineData("Home", "isNot", "Work", null, true)]
    [InlineData("5", "is", "5.0", null, true)]       // two numbers: as numbers
    [InlineData("5", "isNot", "5.0", null, false)]
    [InlineData("1e2", "is", "100", null, true)]
    [InlineData("5", "is", "five", null, false)]      // a number and text: as text
    [InlineData("  Home  ", "is", "Home", null, true)] // spaces at the ends don't count
    [InlineData("", "is", "", null, true)]
    [InlineData("Wi-Fi Home", "contains", "FI HO", null, true)]
    [InlineData("Wi-Fi Home", "contains", "fi H", null, true)]
    [InlineData("Wi-Fi Home", "contains", "fi h", "false", false)]
    [InlineData("Wi-Fi Home", "doesNotContain", "Work", null, true)]
    [InlineData("Wi-Fi Home", "doesNotContain", "home", null, false)]
    [InlineData("Wi-Fi Home", "startsWith", "wi-fi", null, true)]
    [InlineData("Wi-Fi Home", "startsWith", "Home", null, false)]
    [InlineData("Wi-Fi Home", "endsWith", "HOME", null, true)]
    [InlineData("Wi-Fi Home", "endsWith", "HOME", "false", false)]
    [InlineData("", "isEmpty", "", null, true)]
    [InlineData("   ", "isEmpty", "", null, true)]
    [InlineData("x", "isEmpty", "", null, false)]
    [InlineData("x", "isNotEmpty", "", null, true)]
    [InlineData("", "isNotEmpty", "", null, false)]
    [InlineData("5", "atLeast", "3", null, true)]
    [InlineData("3", "atLeast", "3", null, true)]
    [InlineData("2.5", "atLeast", "3", null, false)]
    [InlineData("-1", "below", "0", null, true)]
    [InlineData("3", "below", "3", null, false)]
    [InlineData("10", "atLeast", "9", null, true)]    // not text order
    public async Task EachComparison(string value, string op, string to, string? ignoreCase, bool expected)
    {
        var (holds, result) = await Check(value, op, to, ignoreCase);

        Assert.Equal(expected, holds);
        Assert.False(string.IsNullOrWhiteSpace(result));
    }

    [Theory]
    [InlineData("abc", "atLeast", "3")]
    [InlineData("3", "below", "abc")]
    [InlineData("", "atLeast", "3")]                  // a missing value is not zero
    [InlineData("NaN", "atLeast", "3")]
    [InlineData("Infinity", "below", "3")]
    [InlineData("3,5", "atLeast", "3")]               // invariant: a comma is not a decimal point
    [InlineData("5", "contains", "")]                 // nothing to look for
    [InlineData("5", "startsWith", "  ")]
    [InlineData("5", "sideways", "5")]
    public async Task ARefusedComparisonFailsWithASentence(string value, string op, string to)
    {
        var (holds, result) = await Check(value, op, to);

        Assert.False(holds);
        Assert.NotEmpty(result);
    }

    [Fact]
    public async Task ANonNumberSaysSo()
    {
        var (_, result) = await Check("abc", "atLeast", "3");

        Assert.Equal("\"abc\" isn't a number, so it can't be compared with at least.", result);
    }

    [Fact]
    public async Task LongValuesAreCutInTheSentence()
    {
        var (holds, result) = await Check(new string('a', 500), "is", "b");

        Assert.False(holds);
        Assert.True(result.Length < 200);
    }

    private sealed class Fake(string id, Action<StepContext> run) : IStepProvider
    {
        public string Id => id;

        public Task ExecuteAsync(StepContext context, CancellationToken cancellationToken)
        {
            run(context);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task InARunTheFailureLinkIsTheElseAndAMissingValueFollowsTheFallbackRules()
    {
        const string yaml = """
            flows:
              f:
                nodes:
                  - { id: q, step: prompt.ask, with: { question: Q } }
                  - { id: skipped, step: process.run, onFailure: skip }
                  - { id: yes, step: value.check, with: { value: "{q.answer}", is: is, to: focus }, after: [q] }
                  - { id: no, step: value.check, with: { value: "{q.answer}", is: is, to: relax }, after: [q], onFailure: skip }
                  - { id: other, step: wait, after: [{ id: no, when: failure }] }
                  - { id: fallback, step: value.check, with: { value: "{skipped.exitCode|none}", is: is, to: none }, after: [{ id: skipped, when: always }] }
                  - { id: empty, step: value.check, with: { value: "{skipped.exitCode}", is: isEmpty }, after: [{ id: skipped, when: always }] }
                  - { id: number, step: value.check, with: { value: "{skipped.exitCode}", is: atLeast, to: "0" }, after: [{ id: skipped, when: always }], onFailure: skip }
            """;
        var load = ConfigYaml.Load(yaml);
        Assert.Empty(load.Problems);
        var runner = new FlowRunner(
        [
            new Fake("prompt.ask", c => c.SetValue("answer", "Focus")),
            new Fake("process.run", _ => throw new StepFailedException("no")),
            new Fake("wait", _ => { }),
            new ValueCheckProvider(),
        ], TimeProvider.System);

        var result = await runner.RunAsync(load.Config!.Flows[0], load.Config.Library, TestContext.Current.CancellationToken);

        NodeOutcome Outcome(string id) => result.Nodes.Single(n => n.NodeId == id).Outcome;
        Assert.Equal(NodeOutcome.Succeeded, Outcome("yes"));
        Assert.Equal(NodeOutcome.Failed, Outcome("no"));
        Assert.Equal(NodeOutcome.Succeeded, Outcome("other")); // the failure link ran: the "else"
        Assert.Equal(NodeOutcome.Succeeded, Outcome("fallback"));
        Assert.Equal(NodeOutcome.Succeeded, Outcome("empty"));
        Assert.Equal(NodeOutcome.Failed, Outcome("number"));
        Assert.Contains("isn't a number", result.Nodes.Single(n => n.NodeId == "number").Error, StringComparison.Ordinal);
    }
}
