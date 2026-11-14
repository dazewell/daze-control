namespace CtrlAltDaze.Runtime.Providers;

/// <summary>What a probe found: the fact's value and a sentence about it ("Technics is connected").</summary>
public readonly record struct FactReading(bool Value, string Text);

/// <summary>
/// A read-only question about the machine (D-081), e.g. <c>bluetooth.connected</c>. Used by a step's "Only if"
/// (<see cref="Core.Catalog.ConditionSpec.Fact"/>) and by check steps. Reads only, no side effect; runs only when a
/// step that uses it runs, from the same queries the pickers use, so nothing stays resident (idle = zero).
/// Throw (or time out) when the answer is unknown: the runner then runs the step anyway.
/// </summary>
public interface IFactProbe
{
    /// <summary>The fact id a <see cref="Core.Catalog.ConditionSpec"/> names.</summary>
    string Fact { get; }

    Task<FactReading> ReadAsync(StepContext context, CancellationToken cancellationToken);
}
