using DazeControl.Runtime.Providers;

namespace DazeControl.Providers.Display;

public enum DisplayTopology
{
    Internal,
    Clone,
    Extend,
    External,
}

/// <summary>One video output: <paramref name="Port"/> as scd named it ("HDMI-1", "DP-3"), the monitor's name.</summary>
public sealed record DisplayOutput(string Port, string Monitor, bool Available, bool Active);

/// <summary>The CCD API (QueryDisplayConfig / SetDisplayConfig) behind a seam (D-019).</summary>
public interface IDisplays
{
    IReadOnlyList<DisplayOutput> Outputs();

    /// <summary>The Win+P presets: Windows keeps each preset's layout in its own database.</summary>
    void SetTopology(DisplayTopology topology);

    /// <summary>Turns on exactly these outputs (by port or monitor name), the rest off; returns the names it didn't find.</summary>
    IReadOnlyList<string> UseOnly(IReadOnlyList<string> outputs);
}

/// <summary><c>display.topology</c>: PC screen only / duplicate / extend / second screen only.</summary>
public sealed class DisplayTopologyProvider(IDisplays displays) : IStepProvider
{
    public string Id => "display.topology";

    public Task ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        var topology = context.RequiredText("mode") switch
        {
            "internal" => DisplayTopology.Internal,
            "clone" => DisplayTopology.Clone,
            "extend" => DisplayTopology.Extend,
            "external" => DisplayTopology.External,
            var other => throw new StepFailedException($"\"{other}\" isn't a display mode."),
        };
        displays.SetTopology(topology);
        return Task.CompletedTask;
    }
}

/// <summary><c>display.outputs</c>: only the listed screens on (port scd's DisplayOutputs profiles).</summary>
public sealed class DisplayOutputsProvider(IDisplays displays) : IStepProvider
{
    public string Id => "display.outputs";

    public Task ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        var wanted = context.TextList("outputs");
        if (wanted.Count == 0)
            throw new StepFailedException("List at least one screen.");
        var missing = displays.UseOnly(wanted);
        return missing.Count == 0
            ? Task.CompletedTask
            : Task.FromException(new StepFailedException($"Not found: {string.Join(", ", missing)}. Connected: {Known(displays)}."));
    }

    private static string Known(IDisplays displays) =>
        string.Join(", ", displays.Outputs().Where(o => o.Available).Select(o => o.Monitor.Length > 0 ? $"{o.Port} ({o.Monitor})" : o.Port));
}
