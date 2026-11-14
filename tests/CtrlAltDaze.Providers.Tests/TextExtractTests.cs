using System.Diagnostics;
using System.Net;
using System.Text;
using CtrlAltDaze.Core.Catalog;
using CtrlAltDaze.Core.Model;
using CtrlAltDaze.Core.Runs;
using CtrlAltDaze.Core.Yaml;
using CtrlAltDaze.Providers.Basic;
using CtrlAltDaze.Providers.Http;
using CtrlAltDaze.Runtime;
using CtrlAltDaze.Runtime.Providers;

namespace CtrlAltDaze.Providers.Tests;

/// <summary>T25.2 (D-142): Get from text, per way of taking, with malicious inputs, and in a run after a web request.</summary>
public sealed class TextExtractTests
{
    private static async Task<StepContext> Run(string by, string text, string find = "", string until = "")
    {
        var context = Steps.Context("text.extract", ("text", text), ("by", by), ("find", find), ("until", until));
        await new TextExtractProvider().ExecuteAsync(context, TestContext.Current.CancellationToken);
        return context;
    }

    private static async Task<string> Fails(string by, string text, string find = "", string until = "")
    {
        var e = await Assert.ThrowsAsync<StepFailedException>(() => Run(by, text, find, until));
        return e.Message;
    }

    private const string Json = """{"data":{"items":[{"name":"First","n":3},{"name":"Second","n":4.5}],"ok":true,"none":null},"a.b":{"c":1},"x":"é\n"}""";

    [Theory]
    [InlineData("data.items[0].name", "First")]
    [InlineData("data.items[1].name", "Second")]
    [InlineData("$.data.items[1].name", "Second")]
    [InlineData("$data", null)] // "$data" is not a path
    [InlineData("  data.ok  ", "true")]
    [InlineData("data.items[1].n", "4.5")]
    [InlineData("[\"a.b\"].c", "1")]
    [InlineData("x", "é\n")]
    public async Task JsonFieldsAreTaken(string path, string? expected)
    {
        if (expected is null)
        {
            Assert.NotEmpty(await Fails("json", Json, path));
            return;
        }
        var context = await Run("json", Json, path);
        Assert.Equal(expected, context.Values["text"].Text);
    }

    [Fact]
    public async Task ANumberIsGivenAsTextAndNumber()
    {
        var context = await Run("json", Json, "data.items[1].n");

        Assert.Equal("4.5", context.Values["number"].Text);
        Assert.Equal(ValueKind.Number, context.Values["number"].Kind);
    }

    [Fact]
    public async Task ATextThatIsNotANumberGivesNoNumber()
    {
        var context = await Run("json", Json, "data.items[0].name");

        Assert.False(context.Values.ContainsKey("number"));
    }

    [Theory]
    [InlineData("data.items[2].name")]
    [InlineData("data.missing")]
    [InlineData("data.items.name")]   // an array has no name
    [InlineData("data[0]")]           // an object has no index
    [InlineData("data.none")]         // null is nothing to take
    [InlineData("data.items[0].name.deeper")]
    public async Task APathThatIsNotThereFailsWithASentence(string path)
    {
        var message = await Fails("json", Json, path);

        Assert.DoesNotContain("First", message, StringComparison.Ordinal);
        Assert.Contains(path.Split('.')[0], message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"a\":")]
    [InlineData("{\"a\":1} trailing")]
    public async Task TextThatIsNotJsonFailsWithoutRepeatingIt(string text)
    {
        var message = await Fails("json", text, "a");

        Assert.DoesNotContain("trailing", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AByteOrderMarkAndSpacesBeforeJsonAreFine()
    {
        var context = await Run("json", "\uFEFF  {\"a\":\"b\"} \n", "a");

        Assert.Equal("b", context.Values["text"].Text);
    }

    [Fact]
    public async Task ATwentyKilobyteJsonIsRead()
    {
        var items = string.Join(",", Enumerable.Range(0, 800).Select(i => $"{{\"id\":{i},\"name\":\"item number {i}\"}}"));
        var big = $"{{\"items\":[{items}],\"last\":\"done\"}}";
        Assert.InRange(big.Length, 20_000, TextExtract.MaxText);

        var context = await Run("json", big, "items[799].name");

        Assert.Equal("item number 799", context.Values["text"].Text);
    }

    [Fact]
    public async Task DeeplyNestedJsonIsRefusedQuickly()
    {
        var deep = new string('[', 20_000) + new string(']', 20_000);
        var clock = Stopwatch.StartNew();

        var message = await Fails("json", deep, "[0]");

        Assert.Contains("valid JSON", message, StringComparison.Ordinal);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task JsonJustWithinTheDepthLimitIsRead()
    {
        var ok = string.Concat(Enumerable.Repeat("{\"a\":", 30)) + "7" + new string('}', 30);

        var context = await Run("json", ok, string.Join(".", Enumerable.Repeat("a", 30)));

        Assert.Equal("7", context.Values["text"].Text);
    }

    [Fact]
    public async Task ATextOverSixtyFourKilobytesFailsNotCuts()
    {
        var message = await Fails("pattern", new string('a', TextExtract.MaxText + 1), "a");

        Assert.Contains("64 KB", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATextOfExactlyTheLimitIsRead()
    {
        var context = await Run("line", new string('a', TextExtract.MaxText), "1");

        Assert.Equal(TextExtract.MaxText, context.Values["text"].Text.Length);
    }

    [Theory]
    [InlineData(@"Battery: (\d+)%", "Battery: 87% left", "87")]
    [InlineData(@"\d+", "a 12 b 34", "12")]           // the first match; no group = the whole match
    [InlineData(@"(\w+)=(\w+)", "k=v j=w", "k")]      // the first group
    [InlineData(@"v(\d{2,3})", "v1234", "123")]       // a quantifier is not a reference
    [InlineData(@"(?<n>\d+)", "x 5", "5")]
    [InlineData(@"(\d*)x", "x", "")]                  // an empty group is still a match
    public async Task APatternGivesItsFirstGroupOrTheMatch(string pattern, string text, string expected)
    {
        var context = await Run("pattern", text, pattern);

        Assert.Equal(expected, context.Values["text"].Text);
    }

    [Fact]
    public async Task APatternThatDoesNotMatchFailsWithASentence()
    {
        var message = await Fails("pattern", "no digits", @"(\d+)");

        Assert.Equal("The pattern doesn't match the text.", message);
    }

    [Fact]
    public async Task AGroupThatTookPartInNothingFails()
    {
        var message = await Fails("pattern", "b", "(a)?b");

        Assert.Contains("first group", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("(")]
    [InlineData("[a-")]
    [InlineData("(?<n>a)\\k<m>")]
    public async Task ABrokenPatternFailsWithASentence(string pattern)
    {
        var message = await Fails("pattern", "abc", pattern);

        Assert.Contains("valid pattern", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACatastrophicPatternStopsInsteadOfRunningForever()
    {
        var text = new string('a', 60_000) + "!";
        var clock = Stopwatch.StartNew();

        var message = await Fails("pattern", text, "^(a+)+$");

        Assert.Contains("too long", message, StringComparison.Ordinal);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
    }

    [Fact]
    public async Task ANestedQuantifierOnShortTextAlsoStops()
    {
        var clock = Stopwatch.StartNew();

        await Fails("pattern", new string('a', 40) + "!", "^(a|aa)+$");

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task APatternOverTheLengthLimitIsRefused()
    {
        var message = await Fails("pattern", "a", new string('a', TextExtract.MaxPattern + 1));

        Assert.Contains("longer than", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALargeAllMatchingTextIsFast()
    {
        var clock = Stopwatch.StartNew();

        var context = await Run("pattern", new string('a', TextExtract.MaxText), "(a+)");

        Assert.Equal(TextExtract.MaxText, context.Values["text"].Text.Length);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("<b>Hi</b> and <b>Bye</b>", "<b>", "</b>", "Hi")]
    [InlineData("key=value;", "key=", ";", "value")]
    [InlineData("key=value;", "key=", "", "value;")]          // empty after: to the end
    [InlineData("key=value;", "", ";", "key=value")]          // empty before: from the start
    [InlineData("a [x] b", " [", "] ", "x")]
    public async Task WhatIsBetweenTwoTextsIsTaken(string text, string before, string after, string expected)
    {
        var context = await Run("between", text, before, after);

        Assert.Equal(expected, context.Values["text"].Text);
    }

    [Fact]
    public async Task BetweenCountsUpperAndLowerCaseAndNeedsTheSecondTextAfterTheFirst()
    {
        Assert.Contains("before the part", await Fails("between", "start x end", "START", "end"), StringComparison.Ordinal);
        Assert.Contains("after the part", await Fails("between", "end x start", "start", "end"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BetweenWithNeitherTextFails() =>
        Assert.NotEmpty(await Fails("between", "abc", "", "  "));

    [Theory]
    [InlineData("one\ntwo\r\nthree", "1", "one")]
    [InlineData("one\ntwo\r\nthree", "2", "two")]
    [InlineData("one\ntwo\r\nthree", "3", "three")]
    [InlineData("one\n\nthree", "2", "")]                 // an empty line is a line
    [InlineData("one\n", "2", "")]                        // the empty line after the last break
    [InlineData(" 2 ", "1", " 2 ")]                       // the line keeps its spaces; the number is trimmed
    public async Task OneLineIsTaken(string text, string number, string expected)
    {
        var context = await Run("line", text, number);

        Assert.Equal(expected, context.Values["text"].Text);
    }

    [Theory]
    [InlineData("one\ntwo", "3")]
    [InlineData("one", "2")]
    [InlineData("one", "0")]
    [InlineData("one", "-1")]
    [InlineData("one", "two")]
    [InlineData("one", "1.5")]
    [InlineData("one", "")]
    [InlineData("one", "99999999999")]
    public async Task ALineThatIsNotThereFailsWithASentence(string text, string number) =>
        Assert.NotEmpty(await Fails("line", text, number));

    [Fact]
    public async Task ManyLinesAreFast()
    {
        var text = string.Join("\n", Enumerable.Range(1, 9_000).Select(i => "l" + i));
        Assert.True(text.Length <= TextExtract.MaxText);

        var context = await Run("line", text, "9000");

        Assert.Equal("l9000", context.Values["text"].Text);
    }

    [Fact]
    public async Task ANumberPartIsAlsoANumberAndSpacesAreTrimmedForIt()
    {
        var context = await Run("between", "x= 42 ;", "x=", ";");

        Assert.Equal("42", context.Values["number"].Text.Trim());
    }

    [Fact]
    public async Task AnUnknownWayFailsAndASummaryNeverHoldsTheText()
    {
        Assert.NotEmpty(await Fails("sideways", "x", "y"));
        var context = await Run("between", "pw=secret;", "pw=", ";");

        Assert.DoesNotContain("secret", context.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACancelledRunStopsAtOnce()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var context = Steps.Context("text.extract", ("text", "a"), ("by", "line"), ("find", "1"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TextExtractProvider().ExecuteAsync(context, cancelled.Token));
    }

    // ---- In a run: a web answer is kept long only for a Get from text ----

    private sealed class Answer(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) });
    }

    private static async Task<(RunResult Result, string Yaml)> RunFlow(string body, string tail)
    {
        var yaml = "flows:\n  f:\n    nodes:\n      - { id: web, step: http.request, with: { url: 'https://example.test/x' } }\n" + tail;
        var load = ConfigYaml.Load(yaml);
        Assert.Empty(load.Problems);
        using var http = new HttpRequestProvider(new Answer(body), new NoCredentials());
        var runner = new FlowRunner([http, new TextExtractProvider(), new ValueCheckProvider()], TimeProvider.System);
        return (await runner.RunAsync(load.Config!.Flows[0], load.Config.Library, TestContext.Current.CancellationToken), yaml);
    }

    private sealed class NoCredentials : ICredentialStore
    {
        public Credential? Read(string name) => null;
    }

    private static string BigJson()
    {
        var items = string.Join(",", Enumerable.Range(0, 800).Select(i => $"{{\"id\":{i},\"name\":\"item number {i}\"}}"));
        return $"{{\"items\":[{items}],\"last\":\"done\"}}";
    }

    [Fact]
    public async Task AWebAnswerOfTwentyKilobytesIsReadByGetFromText()
    {
        var body = BigJson();
        Assert.True(body.Length > 20_000);

        var (result, _) = await RunFlow(body, """
                  - { id: get, step: text.extract, with: { text: "{web.body}", by: json, find: last }, after: [web] }
                  - { id: is, step: value.check, with: { value: "{get.text}", is: is, to: done }, after: [get] }
            """);

        Assert.All(result.Nodes, n => Assert.Equal(NodeOutcome.Succeeded, n.Outcome));
    }

    [Fact]
    public async Task WithoutAGetFromTextTheAnswerStaysAtOneThousandCharacters()
    {
        var (result, _) = await RunFlow(BigJson(), """
                  - { id: is, step: value.check, with: { value: "{web.body}", is: contains, to: "item number 799" }, after: [web], onFailure: skip }
            """);

        Assert.Equal(NodeOutcome.Failed, result.Nodes.Single(n => n.NodeId == "is").Outcome);
    }

    [Fact]
    public async Task AnAnswerOverTheLimitMakesGetFromTextFailAndTheFailureLinkIsTheElse()
    {
        var (result, _) = await RunFlow("{\"a\":\"" + new string('x', 70_000) + "\"}", """
                  - { id: get, step: text.extract, with: { text: "{web.body}", by: json, find: a }, after: [web] }
                  - { id: other, step: value.check, with: { value: x, is: is, to: x }, after: [{ id: get, when: failure }] }
            """);

        Assert.Equal(NodeOutcome.Failed, result.Nodes.Single(n => n.NodeId == "get").Outcome);
        Assert.Contains("64 KB", result.Nodes.Single(n => n.NodeId == "get").Error, StringComparison.Ordinal);
        Assert.Equal(NodeOutcome.Succeeded, result.Nodes.Single(n => n.NodeId == "other").Outcome);
    }

    [Fact]
    public async Task APartOfASensitiveAnswerNeverReachesTheErrorOrSummary()
    {
        var (result, _) = await RunFlow("{\"token\":\"abc123secret\"}", """
                  - { id: get, step: text.extract, with: { text: "{web.body}", by: json, find: token }, after: [web] }
                  - { id: is, step: value.check, with: { value: "{get.text}", is: is, to: other }, after: [get] }
            """);

        var check = result.Nodes.Single(n => n.NodeId == "is");
        Assert.Equal(NodeOutcome.Failed, check.Outcome);
        Assert.DoesNotContain("abc123secret", check.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123secret", result.Nodes.Single(n => n.NodeId == "get").Summary ?? "", StringComparison.Ordinal);
    }
}
