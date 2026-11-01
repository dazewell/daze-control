using DazeControl.Core.Catalog;
using DazeControl.Core.Model;
using DazeControl.Runtime.Providers;

namespace DazeControl.Providers.Tests;

/// <summary>Builds a <see cref="StepContext"/> for one step of the catalog with the given params.</summary>
internal static class Steps
{
    public static StepContext Context(string step, params (string Name, string Value)[] with)
    {
        var node = new StepNode("s1", step)
        {
            With = new ValueDictionary<ParamValue>(with.Select(p => KeyValuePair.Create(p.Name, (ParamValue)p.Value))),
        };
        var flow = new Flow("f1", "Test flow", [node]);
        return new StepContext(flow, node, ProviderCatalog.Find(step)!, Library.Empty, 1);
    }

    /// <summary>A context whose <paramref name="name"/> param is a list, as validated YAML gives it.</summary>
    public static StepContext WithList(this StepContext context, string name, params string[] items)
    {
        var node = (StepNode)context.Node;
        var with = node.With.Where(p => p.Key != name)
            .Append(KeyValuePair.Create(name, (ParamValue)new ListParam(new ValueList<ParamValue>(items.Select(i => (ParamValue)i)))));
        return context with { Node = node with { With = new ValueDictionary<ParamValue>(with) } };
    }

    public static Task RunAsync(this IStepProvider provider, params (string Name, string Value)[] with) =>
        provider.ExecuteAsync(Context(provider.Id, with), TestContext.Current.CancellationToken);
}
