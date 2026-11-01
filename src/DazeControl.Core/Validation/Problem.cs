namespace DazeControl.Core.Validation;

public enum Severity
{
    /// <summary>The flow (or, with no flow, the file) can't run as written.</summary>
    Error,

    /// <summary>Runs, but probably not as meant.</summary>
    Warning,
}

/// <summary>
/// One thing wrong with config.yaml, for the editor's Problems list, <c>dctl validate</c> and the log.
/// <paramref name="Path"/> names the YAML node: "flows.connect-technics.nodes.bt.timeout" (nodes by id, or
/// "nodes[2]" when the id is missing). <paramref name="FlowId"/> is set when only that flow is affected (D-021);
/// null means settings, library or the whole file.
/// </summary>
public sealed record Problem(Severity Severity, string Path, string Message, string? FlowId = null)
{
    public override string ToString() => $"{Severity.ToString().ToLowerInvariant()}: {Path}: {Message}";
}
