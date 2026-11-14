using CtrlAltDaze.Core.Model;

namespace CtrlAltDaze.Core.Runs;

/// <summary>One step that ran, on its lane.</summary>
/// <param name="Notches">Start of each try after the first (retries).</param>
public sealed record TimelineBar(string NodeId, int Lane, TimeSpan Start, TimeSpan End, NodeOutcome Outcome,
    IReadOnlyList<TimeSpan> Notches);

/// <summary>A card drawn as a bracket over its members' lanes (always next to each other).</summary>
public sealed record TimelineBracket(string CardId, int FirstLane, int LastLane, TimeSpan Start, TimeSpan End);

/// <summary>
/// The Runs page's timeline (UX §5.5): one row per lane, steps that overlap in time on separate lanes. Steps that
/// never started (skipped, cancelled before they ran) have no bar.
/// </summary>
/// <param name="Length">The run's duration, or the last bar's end if that is later.</param>
public sealed record RunTimeline(IReadOnlyList<TimelineBar> Bars, IReadOnlyList<TimelineBracket> Brackets, int Lanes,
    TimeSpan Length)
{
    /// <summary>
    /// Greedy interval packing: units (a step outside a card, or a whole card) in start order, each on the lowest
    /// lane(s) free at its start. A card takes as many neighbouring lanes as it has members that ran, so its bracket
    /// covers only its own members. A lane is free again at the instant its last bar ends.
    /// </summary>
    /// <param name="run">The run; nodes recorded before 0.15 have no start and all pack at zero.</param>
    /// <param name="flow">The flow, for its cards; null (flow gone) draws every step on its own, without brackets.</param>
    public static RunTimeline Build(RunRecord run, Flow? flow)
    {
        ArgumentNullException.ThrowIfNull(run);
        var ran = run.Nodes.Where(n => n.Attempts > 0).ToList();
        var cards = flow?.Nodes.OfType<GroupNode>().ToList() ?? [];
        var inCard = cards.SelectMany(c => c.Members.Select(m => (Member: m, Card: c.Id)))
            .ToDictionary(x => x.Member, x => x.Card, StringComparer.Ordinal);

        var units = new List<(string? Card, List<NodeResult> Members)>();
        foreach (var node in ran)
        {
            if (inCard.TryGetValue(node.NodeId, out var card))
            {
                if (units.FindIndex(u => u.Card == card) is var i and >= 0)
                    units[i].Members.Add(node);
                else
                    units.Add((card, [node]));
            }
            else
            {
                units.Add((null, [node]));
            }
        }
        static TimeSpan StartOf(List<NodeResult> members) => members.Min(m => m.Start);
        static TimeSpan EndOf(List<NodeResult> members) => members.Max(m => m.Start + m.Duration);

        var laneEnds = new List<TimeSpan>(); // when each lane is free again
        var bars = new List<TimelineBar>();
        var brackets = new List<TimelineBracket>();
        // Stable: equal starts keep the flow's order (and start order, which follows it within a wave).
        foreach (var unit in units.OrderBy(u => StartOf(u.Members)).ThenBy(u => u.Members.Min(m => m.Order)))
        {
            var start = StartOf(unit.Members);
            var width = unit.Members.Count;
            var lane = 0;
            while (!Enumerable.Range(lane, width).All(l => l >= laneEnds.Count || laneEnds[l] <= start))
                lane++;
            var members = unit.Members.OrderBy(m => m.Order).ToList();
            for (var i = 0; i < members.Count; i++)
            {
                var m = members[i];
                var end = m.Start + m.Duration;
                while (laneEnds.Count <= lane + i)
                    laneEnds.Add(TimeSpan.Zero);
                // A card holds all its lanes until its last member ends, so nothing lands inside the bracket.
                laneEnds[lane + i] = unit.Card is null ? end : EndOf(unit.Members);
                bars.Add(new TimelineBar(m.NodeId, lane + i, m.Start, end, m.Outcome, m.Retries ?? []));
            }
            if (unit.Card is { } cardId)
                brackets.Add(new TimelineBracket(cardId, lane, lane + width - 1, start, EndOf(unit.Members)));
        }
        var length = bars.Count == 0 ? run.Duration : new[] { run.Duration, bars.Max(b => b.End) }.Max();
        return new RunTimeline(bars, brackets, laneEnds.Count, length);
    }
}
