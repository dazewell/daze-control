using DazeControl.Core.Model;
using DazeControl.Runtime.Providers;

namespace DazeControl.Providers.Display;

public enum DisplayTopology
{
    Internal,
    Clone,
    Extend,
    External,
}

/// <summary>
/// One video output: <paramref name="Port"/> as scd named it ("HDMI-1", "DP-3"), the monitor's name.
/// <paramref name="DevicePath"/> is the monitor's, as a <see cref="DisplayLayout"/> lists it ("" when none).
/// </summary>
public sealed record DisplayOutput(string Port, string Monitor, bool Available, bool Active, string DevicePath = "");

/// <summary>The CCD API (QueryDisplayConfig / SetDisplayConfig) behind a seam (D-019).</summary>
public interface IDisplays
{
    IReadOnlyList<DisplayOutput> Outputs();

    /// <summary>The Win+P presets: Windows keeps each preset's layout in its own database.</summary>
    /// <remarks>The writes check <paramref name="cancellationToken"/> last thing before SetDisplayConfig: a stopped or
    /// timed-out step must not change the screens after the runner released the display lock.</remarks>
    void SetTopology(DisplayTopology topology, CancellationToken cancellationToken);

    /// <summary>Turns on exactly these outputs (by port or monitor name), the rest off; returns the names it didn't find.</summary>
    IReadOnlyList<string> UseOnly(IReadOnlyList<string> outputs, CancellationToken cancellationToken);

    /// <summary>The screens as they are now, for <c>display.layout</c>.</summary>
    DisplayLayout CaptureLayout(string key);

    void ApplyLayout(DisplayLayout layout, CancellationToken cancellationToken);

    /// <summary>Read-only (D-077): null when the screens on now are what <paramref name="layout"/> describes, else how they differ.</summary>
    string? LayoutDifferences(DisplayLayout layout);
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
        displays.SetTopology(topology, cancellationToken);
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
        var missing = displays.UseOnly(wanted, cancellationToken);
        return missing.Count == 0
            ? Task.CompletedTask
            : Task.FromException(new StepFailedException($"Not found: {string.Join(", ", missing)}. Connected: {Known(displays)}."));
    }

    private static string Known(IDisplays displays) =>
        string.Join(", ", displays.Outputs().Where(o => o.Available).Select(o => o.Monitor.Length > 0 ? $"{o.Port} ({o.Monitor})" : o.Port));
}

/// <summary><c>display.layout</c>: restores a captured layout (positions, resolutions, which screens are on).</summary>
public sealed class DisplayLayoutProvider(IDisplays displays) : IStepProvider
{
    public string Id => "display.layout";

    public Task ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        var key = context.RequiredText("layout");
        var layout = context.Library.Displays.FirstOrDefault(d => d.Key == key)
            ?? throw new StepFailedException(Library.Missing(LibraryKind.Display, key));
        displays.ApplyLayout(layout, cancellationToken);
        return Task.CompletedTask;
    }
}


/// <summary>
/// <c>display.check</c> (D-077, D-081): reads the screens and fails when they aren't (or, with <c>is: differs</c>, are) the
/// captured layout. Changes nothing, so it needs no lock beyond the display's own.
/// </summary>
public sealed class DisplayCheckProvider(IDisplays displays) : IStepProvider
{
    public string Id => "display.check";

    public Task ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        var key = context.RequiredText("layout");
        var layout = context.Library.Displays.FirstOrDefault(d => d.Key == key)
            ?? throw new StepFailedException(Library.Missing(LibraryKind.Display, key));
        cancellationToken.ThrowIfCancellationRequested();
        var difference = displays.LayoutDifferences(layout);
        var wantMatch = context.Text("is") != "differs";
        context.SetValue("result", difference ?? $"The screens match {layout.Key}");
        if (wantMatch && difference is not null)
            throw new CheckNotMetException(difference);
        if (!wantMatch && difference is null)
            throw new CheckNotMetException($"The screens already match {layout.Key}.");
        context.SetSummary(difference is null ? $"The screens match {layout.Key}" : difference);
        return Task.CompletedTask;
    }
}
