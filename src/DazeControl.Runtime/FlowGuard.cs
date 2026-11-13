using System.Collections.Concurrent;
using DazeControl.Core.Catalog;
using DazeControl.Core.Model;
using DazeControl.Core.Runs;
using DazeControl.Core.Validation;
using DazeControl.Runtime.Control;

namespace DazeControl.Runtime;

/// <summary>
/// "Only run if" (T25.1, D-148): reads a flow's <see cref="Flow.OnlyIf"/> checks right before an automatic start (and a
/// start by hand when <see cref="Flow.OnlyIfByHand"/>). The checks run side by side on a quiet runner (no pop-up, no
/// history), each with its own timeout, retries and the run's cancellation; nothing runs at any other time (idle = zero).
/// A check that read its fact and found it not as asked holds the start back; a check that couldn't answer (an error, a
/// timeout) lets it start, as an "Only if" probe does (D-081). Never throws except for the caller's cancellation.
/// </summary>
/// <param name="runner">A runner without pop-up events (the engine's test-step runner).</param>
public sealed class FlowGuard(FlowRunner runner, Action<string>? log = null, TimeProvider? time = null)
{
    private readonly Action<string> _log = log ?? (_ => { });
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, HeldBackNote> _last = new(StringComparer.Ordinal);

    /// <summary>The latest held-back start of a flow (in memory, one per flow: the flow card's "Held back: …"); null = none since the engine started.</summary>
    public HeldBackNote? LastHeldBack(string flowId) => _last.TryGetValue(flowId, out var note) ? note : null;

    /// <summary>Whether this start must read the list: automatic starts always, a person's only when the flow says so.</summary>
    public static bool Applies(Flow flow, bool byHand) => flow.OnlyIf.Count > 0 && (!byHand || flow.OnlyIfByHand);

    /// <summary>Null = start; else why it was held back, in the first unmet check's words (list order), Sensitive masked by the runner.</summary>
    /// <param name="flow">The flow version about to start, with the step defaults applied.</param>
    /// <param name="trigger">What started it ("Every day at 08:00", "Tray"), for the log and the note.</param>
    public async Task<string?> HeldBackAsync(Flow flow, Library library, string trigger, CancellationToken cancellationToken)
    {
        var held = await ReadAsync(flow, library, cancellationToken).ConfigureAwait(false);
        if (held is null)
            return null;
        // One log line and one in-memory note per held-back start: no history row, no pop-up (a Wi-Fi blip must not flood Runs).
        _last[flow.Id] = new HeldBackNote(_time.GetUtcNow(), held, trigger);
        _log($"only run if: {flow.Id} ({trigger}): {held}");
        return held;
    }

    private async Task<string?> ReadAsync(Flow flow, Library library, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(flow);
        if (flow.OnlyIf.Count == 0)
            return null;
        RunResult result;
        try
        {
            // The flow's inputs take their defaults: an asked input is asked after the guard, only when the run starts.
            var checks = FlowInputs.Apply(ConfigValidator.GuardFlow(flow), new Dictionary<string, string>());
            result = await runner.RunAsync(checks, library, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // fail-soft: a guard that can't be read lets the run start (D-081)
        catch (Exception e)
#pragma warning restore CA1031
        {
            _log($"only run if: {flow.Id}: couldn't read the checks ({e.Message}); starting anyway");
            return null;
        }
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var check in flow.OnlyIf)
        {
            if (result.Nodes.FirstOrDefault(n => n.NodeId == check.Id) is not { Outcome: NodeOutcome.Failed } node)
                continue;
            if (node.NotMet)
                return Sentence(check, node.Error);
            // Not a "no": an error or a timeout. Unknown lets the run start (D-081); the log says which check.
            _log($"only run if: {flow.Id}: couldn't read {check.Id} ({node.Error}); starting anyway");
        }
        return null;
    }

    /// <summary>A start that "Only run if" held back.</summary>
    /// <param name="Sentence">"Held back: Wi-Fi isn't Home."</param>
    public sealed record HeldBackNote(DateTimeOffset At, string Sentence, string Trigger);

    /// <summary>"Held back: Wi-Fi isn't Home." The check's own sentence, else its title.</summary>
    private static string Sentence(StepNode check, string? error)
    {
        var text = string.IsNullOrWhiteSpace(error)
            ? $"{check.Name ?? ProviderCatalog.Find(check.Step)?.Title ?? check.Step} isn't met"
            : error.Trim().TrimEnd('.');
        return $"Held back: {text}.";
    }
}
