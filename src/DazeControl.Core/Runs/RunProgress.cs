using DazeControl.Core.Model;

namespace DazeControl.Core.Runs;

/// <summary>
/// The one "n of m" of a run, shared by the pop-up and the editor's run overlay (F-012, D-127): m is the flow's
/// step count, fixed when the run starts (the same number as the card's "17 steps"); n is the steps that finished,
/// with any outcome. Fallbacks count from the start and finish as "Not needed" when skipped, cards don't count
/// (their members do), so m never moves and n never goes back.
/// </summary>
public sealed class RunProgress
{
    private readonly HashSet<string> _steps;
    private readonly HashSet<string> _done = new(StringComparer.Ordinal);

    public RunProgress(Flow flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        _steps = new HashSet<string>(flow.Nodes.OfType<StepNode>().Where(s => s.Enabled).Select(s => s.Id), StringComparer.Ordinal);
    }

    /// <summary>m: the flow's steps that are on.</summary>
    public int Total => _steps.Count;

    /// <summary>n: steps that finished (a repeat event for the same step counts once).</summary>
    public int Done => _done.Count;

    /// <summary>The node is one of the counted steps.</summary>
    public bool Counts(string nodeId) => _steps.Contains(nodeId);

    /// <summary>A node finished: true when that moved n.</summary>
    public bool Finish(string nodeId) => _steps.Contains(nodeId) && _done.Add(nodeId);

    /// <summary>n / m as 0–1, plus partial progress of running steps (nested runs), never above 1.</summary>
    public double Fraction(double running = 0) => _steps.Count == 0 ? 0 : Math.Clamp((Done + running) / _steps.Count, 0, 1);

    /// <summary>"18 of 20".</summary>
    public string Text => $"{Done} of {Total}";
}
