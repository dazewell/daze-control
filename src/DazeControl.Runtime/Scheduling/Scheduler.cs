using DazeControl.Core.Model;
using DazeControl.Core.Runs;

namespace DazeControl.Runtime.Scheduling;

/// <summary>How one attempt-sequence of a step ended, as the runner saw it (retries are the runner's business).</summary>
public enum StepResult
{
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>
/// The DAG semantics of one run (FEATURES F1, D-003, D-024), with no threads, time or providers: the runner calls
/// <see cref="Start"/>, runs the steps it returns, and reports each with <see cref="Complete"/>, which returns
/// the steps that became ready. Not thread-safe; the runner serialises calls.
/// </summary>
/// <remarks>
/// Units are steps outside cards and cards; edges connect units only (validated). A unit runs when all its
/// incoming edges have fired; when one can never fire the unit is Skipped, and that propagates. Which edges a
/// finished unit fires:
/// <list type="bullet">
/// <item>Succeeded: success, always.</item>
/// <item>FailedIgnored (policy ignore, "may fail"): success, failure, always.</item>
/// <item>Failed: failure, always. With an outgoing failure edge the failure is handled (D-024); without one,
/// policy stop ends the run and skip just lets the edges die.</item>
/// <item>Skipped: always.</item>
/// </list>
/// A card starts all members together and finishes when all have; it fails when a member fails with stop or
/// skip, and then its own policy applies.
/// </remarks>
public sealed class Scheduler
{
    private readonly Dictionary<string, Node> _nodes = [];
    private readonly Dictionary<string, GroupNode> _cardOf = [];
    private readonly Dictionary<string, List<(Node Target, int Index)>> _outgoing = [];
    private readonly Dictionary<(string Target, int Index), bool> _fired = []; // true = fired, false = can't fire
    private readonly Dictionary<string, NodeOutcome> _outcomes = [];
    private readonly HashSet<string> _running = [];
    private readonly List<string> _ready = [];

    /// <param name="flow">A flow that passed <c>ConfigValidator</c>.</param>
    public Scheduler(Flow flow)
    {
        Flow = flow;
        foreach (var node in flow.Nodes)
        {
            _nodes[node.Id] = node;
            _outgoing[node.Id] = [];
        }
        foreach (var card in flow.Nodes.OfType<GroupNode>())
        {
            foreach (var member in card.Members)
                _cardOf[member] = card;
        }
        foreach (var node in flow.Nodes)
        {
            for (var i = 0; i < node.After.Count; i++)
                _outgoing[node.After[i].From].Add((node, i));
        }
    }

    public Flow Flow { get; }

    /// <summary>A node failed with policy stop and no failure edge: the runner cancels what is still running.</summary>
    public bool IsStopped { get; private set; }

    public bool IsCancelled { get; private set; }

    public bool IsFinished => _outcomes.Count == _nodes.Count;

    /// <summary>Steps started and not yet completed.</summary>
    public IReadOnlyCollection<string> Running => _running;

    /// <summary>Outcome per node id (steps, members and cards) so far.</summary>
    public IReadOnlyDictionary<string, NodeOutcome> Outcomes => _outcomes;

    /// <summary>Only meaningful once <see cref="IsFinished"/>.</summary>
    public FlowOutcome Result => Core.Runs.Outcomes.Summarize(_outcomes.Values, IsCancelled, IsStopped);

    /// <summary>The steps to start first: units with no incoming edges (cards expanded to their members).</summary>
    public IReadOnlyList<string> Start()
    {
        foreach (var node in Flow.Nodes.Where(n => n.After.Count == 0 && !_cardOf.ContainsKey(n.Id)))
            StartUnit(node);
        return TakeReady();
    }

    /// <summary>Records a step's result; returns the steps that may start now (none once stopped or cancelled).</summary>
    public IReadOnlyList<string> Complete(string stepId, StepResult result)
    {
        if (!_running.Remove(stepId))
            throw new InvalidOperationException($"Step \"{stepId}\" isn't running.");
        var step = _nodes[stepId];
        var outcome = result switch
        {
            StepResult.Succeeded => NodeOutcome.Succeeded,
            StepResult.Cancelled => NodeOutcome.Cancelled,
            _ when step.OnFailure == FailurePolicy.Ignore => NodeOutcome.FailedIgnored,
            _ => NodeOutcome.Failed,
        };
        _outcomes[stepId] = outcome;
        if (_cardOf.TryGetValue(stepId, out var card))
            CompleteMemberOf(card);
        else
            Finish(step, outcome);
        return TakeReady();
    }

    /// <summary>Cancelled from outside: nothing new starts, pending nodes end Cancelled, running steps report later.</summary>
    public void Cancel()
    {
        IsCancelled = true;
        CancelPending();
    }

    private void CompleteMemberOf(GroupNode card)
    {
        if (_outcomes.ContainsKey(card.Id) || !card.Members.All(_outcomes.ContainsKey))
            return;
        if (IsStopped || IsCancelled)
        {
            _outcomes[card.Id] = NodeOutcome.Cancelled;
            return;
        }
        var failed = card.Members.Any(m => _outcomes[m] == NodeOutcome.Failed);
        var outcome = !failed ? NodeOutcome.Succeeded
            : card.OnFailure == FailurePolicy.Ignore ? NodeOutcome.FailedIgnored
            : NodeOutcome.Failed;
        Finish(card, outcome);
    }

    /// <summary>A unit has its final outcome (except Cancelled, which fires nothing).</summary>
    private void Finish(Node unit, NodeOutcome outcome)
    {
        _outcomes[unit.Id] = outcome;
        if (outcome == NodeOutcome.Cancelled || IsStopped || IsCancelled)
            return;
        var handled = _outgoing[unit.Id].Any(e => e.Target.After[e.Index].When == EdgeWhen.Failure);
        if (outcome == NodeOutcome.Failed && !handled && unit.OnFailure == FailurePolicy.Stop)
        {
            IsStopped = true;
            CancelPending();
            return;
        }
        foreach (var (target, index) in _outgoing[unit.Id])
        {
            _fired[(target.Id, index)] = Fires(outcome, target.After[index].When);
            Evaluate(target);
        }
    }

    private static bool Fires(NodeOutcome outcome, EdgeWhen when) => when switch
    {
        EdgeWhen.Always => true,
        EdgeWhen.Success => outcome is NodeOutcome.Succeeded or NodeOutcome.FailedIgnored,
        EdgeWhen.Failure => outcome is NodeOutcome.Failed or NodeOutcome.FailedIgnored,
        _ => false,
    };

    private void Evaluate(Node target)
    {
        if (_outcomes.ContainsKey(target.Id) || IsStopped || IsCancelled)
            return;
        var states = Enumerable.Range(0, target.After.Count)
            .Select(i => _fired.TryGetValue((target.Id, i), out var fired) ? fired : (bool?)null).ToList();
        if (states.Contains(false))
            Skip(target);
        else if (!states.Contains(null))
            StartUnit(target);
    }

    private void Skip(Node unit)
    {
        if (unit is GroupNode card)
        {
            foreach (var member in card.Members)
                _outcomes[member] = NodeOutcome.Skipped;
        }
        Finish(unit, NodeOutcome.Skipped);
    }

    private void StartUnit(Node unit)
    {
        if (unit is GroupNode card)
        {
            foreach (var member in card.Members)
                StartStep(member);
        }
        else
        {
            StartStep(unit.Id);
        }
    }

    private void StartStep(string id)
    {
        _running.Add(id);
        _ready.Add(id);
    }

    private void CancelPending()
    {
        foreach (var id in _ready)
            _running.Remove(id); // queued but never handed to the runner
        _ready.Clear();
        foreach (var node in Flow.Nodes)
        {
            if (!_outcomes.ContainsKey(node.Id) && !_running.Contains(node.Id) && !IsCardStillRunning(node))
                _outcomes[node.Id] = NodeOutcome.Cancelled;
        }
    }

    // A card whose members are running gets its outcome when they report (CompleteMemberOf).
    private bool IsCardStillRunning(Node node) => node is GroupNode card && card.Members.Any(_running.Contains);

    private List<string> TakeReady()
    {
        var ready = _ready.ToList();
        _ready.Clear();
        return ready;
    }
}
