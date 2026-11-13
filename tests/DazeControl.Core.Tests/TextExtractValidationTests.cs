using DazeControl.Core.Catalog;
using DazeControl.Core.Model;
using DazeControl.Core.Validation;
using DazeControl.Core.Yaml;

namespace DazeControl.Core.Tests;

/// <summary>T25.2 (D-144): the validator refuses what Get from text would always refuse, with the provider's own checks.</summary>
public class TextExtractValidationTests
{
    private static ValueList<Problem> Validate(string with)
    {
        var load = ConfigYaml.Load("flows:\n  f:\n    nodes:\n      - { id: w, step: http.request, with: { url: 'https://x.test' } }\n      - { id: g, step: text.extract, with: { text: '{w.body}', " + with + " }, after: [w] }\n");
        Assert.Empty(load.Problems);
        return ConfigValidator.Validate(load.Config!);
    }

    [Theory]
    [InlineData("by: json, find: 'data.items[0].name'")]
    [InlineData("find: data")]                                   // json is the default
    [InlineData("by: json, find: '$.a[\"b.c\"][2].d'")]
    [InlineData("by: json, find: ' a '")]
    [InlineData("by: pattern, find: '(\\d+)%'")]
    [InlineData("by: pattern, find: '\\d{3}'")]                  // a quantifier is not a reference
    [InlineData("by: pattern, find: '{w.status}'")]              // a reference: left to the run
    [InlineData("by: between, find: 'a', until: ''")]
    [InlineData("by: between, find: '', until: 'b'")]
    [InlineData("by: between, find: '{w.status}'")]
    [InlineData("by: line, find: '3'")]
    [InlineData("by: line, find: ' 3 '")]
    [InlineData("by: line, find: '{w.status|1}'")]
    public void OrdinarySettingsAreValid(string with) => Assert.Empty(Validate(with));

    [Theory]
    [InlineData("by: json")]
    [InlineData("by: json, find: '  '")]
    [InlineData("by: json, find: 'a..b'")]
    [InlineData("by: json, find: 'a.'")]
    [InlineData("by: json, find: 'a[x]'")]
    [InlineData("by: json, find: 'a[1'")]
    [InlineData("by: json, find: 'a[-1]'")]
    [InlineData("by: json, find: 'a.[0]'")]
    [InlineData("by: json, find: 'a]b'")]
    [InlineData("by: json, find: '$a'")]
    [InlineData("by: pattern, find: '('")]
    [InlineData("by: pattern, find: '[a-'")]
    [InlineData("by: pattern, find: ''")]
    [InlineData("by: between")]
    [InlineData("by: between, find: ' ', until: ' '")]
    [InlineData("by: line, find: '0'")]
    [InlineData("by: line, find: 'two'")]
    [InlineData("by: line, find: '1.5'")]
    [InlineData("by: line")]
    [InlineData("by: line, find: '{{1}'")]                       // an escaped brace is plain text "{1}"
    public void SettingsItAlwaysRefusesAreErrors(string with)
    {
        var problem = Assert.Single(Validate(with), p => p.Severity == Severity.Error);
        Assert.EndsWith("with.find", problem.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void ATooLongPatternOrPathIsAnError()
    {
        Assert.Contains(Validate($"by: pattern, find: '{new string('a', TextExtract.MaxPattern + 1)}'"), p => p.Severity == Severity.Error);
        Assert.Contains(Validate($"by: json, find: '{new string('a', TextExtract.MaxPath + 1)}'"), p => p.Severity == Severity.Error);
        Assert.Contains(Validate($"by: json, find: '{string.Join(".", Enumerable.Repeat("a", TextExtract.MaxPathParts + 1))}'"), p => p.Severity == Severity.Error);
    }

    [Fact]
    public void TheValidatorAndTheStepAgreeOnEverySetting()
    {
        string[] bys = ["json", "pattern", "between", "line", "sideways"];
        string[] finds = ["", " ", "a", "a.b", "a[0]", "(", "(a)", "0", "1", " 2 ", "a..b", "$.a", "{{1}"];
        string[] untils = ["", " ", "b"];
        foreach (var by in bys)
        {
            foreach (var find in finds)
            {
                foreach (var until in untils)
                {
                    var asValidated = TextExtract.Check(by, TextExtract.AsFilled(find), TextExtract.AsFilled(until));
                    var asRun = TextExtract.Extract(by, "{\"a\":{\"b\":[1]}}\nx", TextExtract.AsFilled(find), TextExtract.AsFilled(until));
                    // A setting the check refuses always fails the run with the same sentence; one it accepts never fails for a setting reason.
                    if (asValidated is not null)
                        Assert.Equal(asValidated, asRun.Failure);
                }
            }
        }
    }

    [Fact]
    public void ACalledFlowsAnswerIsRefusedAsTheText()
    {
        var load = ConfigYaml.Load("""
            flows:
              inner:
                name: Inner
                outputs:
                  said: 'x'
                nodes:
                  - { id: n, step: wait, with: { duration: 1s } }
              f:
                nodes:
                  - { id: call, step: flow.run, with: { flow: inner } }
                  - { id: g, step: text.extract, with: { text: '{call.said}', find: a }, after: [call] }
            """);
        Assert.Empty(load.Problems);

        var problem = Assert.Single(ConfigValidator.Validate(load.Config!), p => p.Severity == Severity.Error);

        Assert.EndsWith("nodes.g.with.text", problem.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void ItsValuesAreSensitiveWhenItsTextIs()
    {
        var load = ConfigYaml.Load("flows:\n  f:\n    nodes:\n      - { id: w, step: http.request, with: { url: 'https://x.test' } }\n      - { id: g, step: text.extract, with: { text: '{w.body}', find: a }, after: [w] }\n      - { id: h, step: text.extract, with: { text: 'plain', by: line, find: '1' } }\n      - { id: k, step: text.extract, with: { text: 'plain', find: '{w.body}' } }\n");
        var flow = load.Config!.Flows[0];
        bool Sensitive(string id, string value)
        {
            var step = (StepNode)flow.FindNode(id)!;
            return StepValues.Find(flow, step, ProviderCatalog.Find(step.Step)!, value)!.Sensitive;
        }

        Assert.True(Sensitive("g", "text"));
        Assert.True(Sensitive("g", "number"));
        Assert.False(Sensitive("g", "error"));
        Assert.False(Sensitive("h", "text"));
        Assert.True(Sensitive("k", "text"));
    }

    [Fact]
    public void OnlyTheValuesItsTextReadsAreKeptLong()
    {
        var load = ConfigYaml.Load("flows:\n  f:\n    nodes:\n      - { id: w, step: http.request, with: { url: 'https://x.test/{@flow}' } }\n      - { id: g, step: text.extract, with: { text: 'a {w.body} b', find: '{w.status}' }, after: [w] }\n");

        var longer = StepValues.LongValues(load.Config!.Flows[0]);

        Assert.Equal([("w", "body")], longer.ToArray());
    }

    [Fact]
    public void ItsPlainEditorWords()
    {
        var step = ProviderCatalog.Find(ProviderCatalog.ExtractId)!;
        var words = string.Join(" ", new[] { step.Title, step.Keywords }.Concat(step.Params.SelectMany(p => new[] { p.Title, p.Help }.Concat(p.Captions))).Concat(step.Values.SelectMany(v => new[] { v.Title, v.Help })));

        Assert.DoesNotContain("node", words, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DAG", words, StringComparison.Ordinal);
        Assert.DoesNotContain("OSD", words, StringComparison.Ordinal);
    }
}
