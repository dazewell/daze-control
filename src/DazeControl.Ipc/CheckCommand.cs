using DazeControl.Core.Catalog;
using DazeControl.Core.Model;
using DazeControl.Core.Yaml;

namespace DazeControl.Ipc;

/// <summary>
/// <c>dctl check &lt;check&gt; [name=value...]</c> (D-118): one check step, sent as a "Test this step" request with the
/// Library of config.yaml, so audio and Bluetooth checks can name its devices.
/// </summary>
public static class CheckCommand
{
    public const string Flow = "dctl-check", Node = "check";

    /// <summary>The request, or null and why (plain words for the console).</summary>
    public static (PipeRequest? Request, string? Error) Request(IReadOnlyList<string> words, string? configYaml)
    {
        var step = words[0];
        if (!ProviderCatalog.Checks.Contains(step))
            return (null, $"{step} isn't a check. Checks: {string.Join(", ", ProviderCatalog.Checks.Order(StringComparer.Ordinal))}.");
        var descriptor = ProviderCatalog.Find(step)!;
        List<KeyValuePair<string, ParamValue>> with = [];
        foreach (var word in words.Skip(1))
        {
            var equals = word.IndexOf('=', StringComparison.Ordinal);
            var name = equals > 0 ? word[..equals] : "";
            if (descriptor.FindParam(name) is null)
                return (null, $"{step} takes {string.Join(", ", descriptor.Params.Select(p => p.Name + "=…"))}; not \"{word}\".");
            with.Add(KeyValuePair.Create(name, (ParamValue)new ScalarParam(word[(equals + 1)..])));
        }
        var config = Configuration.Empty;
        if (configYaml is not null)
        {
            var load = ConfigYaml.Load(configYaml);
            if (load.Config is null)
                return (null, $"Can't read config.yaml, so its Library isn't there to check against: {(load.Problems.Count > 0 ? load.Problems[0].Message : "unknown problem")}");
            config = load.Config;
        }
        var node = new StepNode(Node, step) { With = new ValueDictionary<ParamValue>(with) };
        var yaml = ConfigYaml.Save(new Configuration { Library = config.Library, Flows = [new Flow(Flow, "dctl check", [node])] });
        return (new PipeRequest { Op = Ops.TestStep, Flow = Flow, Node = Node, Config = yaml }, null);
    }
}
