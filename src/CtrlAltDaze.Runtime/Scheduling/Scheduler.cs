using CtrlAltDaze.Core.Model;
using CtrlAltDaze.Core.Runs;

namespace CtrlAltDaze.Runtime.Scheduling;

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
/// incoming edges have fired; when one can never fire the unit is Skipped, and that propagates. Exception (D-112): edges
/// from a branch that simply wasn't taken (a handled failure, a NotNeeded fallback) are alternatives, so a unit after
/// both a check and its fallback runs once if at least one fires. Which edges a
/// finished unit fires:
/// <list type="bullet">
/// <item>Succeeded: success, always.</item>
/// <item>FailedIgnored (policy ignore, "may fail"): success, failure, always.</item>
/// <item>Failed: failure, always. With an outgoing failure edge the failure is handled (D-024); without one,
/// policy stop ends the run and skip just lets the edges die.</item>
/// <item>Skipped, NotNeeded: always.</item>
/// </list>
/// A fallback step (<see cref="FlowShape.Fallbacks"/>) that would be skipped ends NotNeeded instead (D-065). A
/// handled failure, and a skip that only follows from one (the branch its fallback replaced), don't make the run a
/// partial success.
/// A card starts all members together and finishes when all have; it fails when a member fails with stop or
/// skip, and then its own policy applies.
/// A step that is turned off (<see cref="StepNode.Enabled"/>, D-148) is never handed to the runner: when it would
/// start it ends Succeeded at once, so its success and always edges fire and its failure edges never do.
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
    private readonly IReadOnlySet<string> _fallbacks;
    private readonly IReadOnlySet<string> _withFallback;
    private readonly HashSet<string> _excused = []; // skipped only because a handled failure didn't fire success
    private readonly HashSet<string> _notRun = [];
    private readonly HashSet<string> _off = [];

    /// <param name="flow">A flow that passed <c>ConfigValidator</c>.</param>
    /// <param name="startAt">"Run from this step" (T27.4): the step (or card) to start at. Its unit and everything after it run;
    /// every other unit counts as done (Succeeded, <see cref="NotRun"/>) without running. Null = the whole flow.</param>
    public Scheduler(Flow flow, string? startAt = null)
    {
        Flow = flow;
        StartAt = startAt;
        _fallbacks = FlowShape.Fallbacks(flow);
        _withFallback = FlowShape.WithFallback(flow);
        foreach (var node in flow.Nodes)
        {
            _nodes[node.Id] = node;
            _outgoing[node.Id] = [];
            if (node is StepNode { Enabled: false })
                _off.Add(node.Id);
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

    /// <summary>The step a partial run starts at (T27.4); null = the whole flow.</summary>
    public string? StartAt { get; }

    /// <summary>Nodes (units and members) a partial run counts as done without running them (T27.4). Their outcome is Succeeded.</summary>
    public IReadOnlySet<string> NotRun => _notRun;

    /// <summary>A node failed with policy stop and no failure edge: the runner cancels what is still running.</summary>
    public bool IsStopped { get; private set; }

    public bool IsCancelled { get; private set; }

    public bool IsFinished => _outcomes.Count == _nodes.Count;

    /// <summary>The step is turned off: it ends Succeeded without running (D-148).</summary>
    public bool IsOff(string stepId) => _off.Contains(stepId);

    /// <summary>Steps started and not yet completed.</summary>
    public IReadOnlyCollection<string> Running => _running;

    /// <summary>Outcome per node id (steps, members and cards) so far.</summary>
    public IReadOnlyDictionary<string, NodeOutcome> Outcomes => _outcomes;

    /// <summary>Only meaningful once <see cref="IsFinished"/>.</summary>
    public FlowOutcome Result => Core.Runs.Outcomes.Summarize(
        _outcomes.Select(o => (o.Value, IsHandled(o.Key, o.Value) || _excused.Contains(o.Key))), IsCancelled, IsStopped,
        _partialCall);

    private bool _partialCall;

    /// <summary>A <c>flow.run</c> step is about to complete Succeeded but the flow it ran had a problem: the run ends
    /// PartiallySucceeded at best (DESIGN §2.2), without a failure edge or policy.</summary>
    public void MarkPartialCall() => _partialCall = true;

    private bool IsHandled(string id, NodeOutcome outcome) =>
        outcome is NodeOutcome.Failed or NodeOutcome.FailedIgnored && _withFallback.Contains(id);

    /// <summary>The steps to start first: units with no incoming edges (cards expanded to their members).</summary>
    public IReadOnlyList<string> Start()
    {
        if (StartAt is { } at)
            return StartFrom(at);
        foreach (var node in Flow.Nodes.Where(n => n.After.Count == 0 && !_cardOf.ContainsKey(n.Id)))
            StartUnit(node);
        return TakeReady();
    }

    /// <summary>
    /// A partial run (T27.4, D-149): the start unit runs whatever its incoming edges say; units after it run as they would after
    /// it; every other unit counts as Succeeded without running, so its edges fire as a success would: a failure link from it
    /// never fires and what only follows that link is skipped (a fallback: not needed). Steps on other branches don't run.
    /// </summary>
    private List<string> StartFrom(string at)
    {
        if (!_nodes.TryGetValue(at, out var named))
            throw new ArgumentException($"Step \"{at}\" isn't in the flow.", nameof(at));
        var first = _cardOf.TryGetValue(at, out var card) ? card : named;
        var kept = new HashSet<string> { first.Id };
        var queue = new Queue<string>([first.Id]);
        while (queue.Count > 0)
        {
            foreach (var (target, _) in _outgoing[queue.Dequeue()])
            {
                if (kept.Add(target.Id))
                    queue.Enqueue(target.Id);
            }
        }
        var units = Flow.Nodes.Where(n => !_cardOf.ContainsKey(n.Id)).ToList();
        var before = units.Where(u => !kept.Contains(u.Id)).ToList();
        foreach (var unit in before)
        {
            _outcomes[unit.Id] = NodeOutcome.Succeeded;
            _notRun.Add(unit.Id);
            if (unit is GroupNode members)
            {
                foreach (var member in members.Members)
                {
                    _outcomes[member] = NodeOutcome.Succeeded;
                    _notRun.Add(member);
                }
            }
        }
        StartUnit(first);
        // Edges from the steps before fire now; the start unit's own incoming edges are ignored (it was asked for).
        foreach (var unit in before)
        {
            foreach (var (target, index) in _outgoing[unit.Id])
            {
                if (target.Id == first.Id)
                    continue;
                _fired[(target.Id, index)] = Fires(NodeOutcome.Succeeded, target.After[index].When);
                Evaluate(target);
            }
        }
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
        if (outcome == NodeOutcome.Failed && !handled && unit.OnFailure is null or FailurePolicy.Stop)
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
        var dead = Enumerable.Range(0, states.Count).Where(i => states[i] == false).ToList();
        // A dead edge from a real failure or a plain skip kills the unit at once (AND, D-003). Dead edges from a branch
        // that was just not taken (a handled failure, an unneeded fallback) are alternatives: once every edge has
        // settled the unit runs if any other edge fired, and is skipped only when none did (D-112).
        if (dead.Any(i => !IsUntakenBranch(target.After[i].From)))
            Skip(target);
        else if (!states.Contains(null))
        {
            if (dead.Count == states.Count)
                Skip(target);
            else
                StartUnit(target);
        }
    }

    private bool IsUntakenBranch(string from) =>
        _outcomes[from] == NodeOutcome.NotNeeded || _excused.Contains(from) || IsHandled(from, _outcomes[from]);

    private void Skip(Node unit)
    {
        var outcome = _fallbacks.Contains(unit.Id) ? NodeOutcome.NotNeeded : NodeOutcome.Skipped;
        // The edges that can't fire all come from a failure its fallback handled (or from a skip excused that way).
        var excused = outcome == NodeOutcome.Skipped && Enumerable.Range(0, unit.After.Count)
            .Where(i => _fired.TryGetValue((unit.Id, i), out var fired) && !fired)
            .All(i => _excused.Contains(unit.After[i].From) || IsHandled(unit.After[i].From, _outcomes[unit.After[i].From]));
        if (excused)
            _excused.Add(unit.Id);
        if (unit is GroupNode card)
        {
            foreach (var member in card.Members)
            {
                _outcomes[member] = outcome;
                if (excused)
                    _excused.Add(member);
            }
        }
        Finish(unit, outcome);
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
        if (_off.Contains(id))
        {
            // Done without running: as if it had succeeded at once (D-148). Its card waits for the other members.
            _outcomes[id] = NodeOutcome.Succeeded;
            if (_cardOf.TryGetValue(id, out var card))
                CompleteMemberOf(card);
            else
                Finish(_nodes[id], NodeOutcome.Succeeded);
            return;
        }
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
