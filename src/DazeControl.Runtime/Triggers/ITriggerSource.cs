namespace DazeControl.Runtime.Triggers;

/// <summary>One thing that happened (M20): an event name from <c>TriggerText.Events</c>.</summary>
/// <param name="Name">The event name, e.g. <c>session.unlock</c>.</param>
/// <param name="Detail">Extra the source knows (on a network event: the Wi-Fi name, or the cable adapter's name), else null.
/// Debounce and cooldown are kept per detail, so one connection doesn't swallow another's event.</param>
/// <param name="Chain">Flow ids the event descends from (a <c>flow.finished</c> event: the ended run's chain and the flow itself); null = a person or the OS.</param>
/// <param name="Kind">On a network event, the connection's kind as <c>TriggerText.ViaText</c> says it ("wifi", "cable"); on a flow event, the
/// flow's id (<c>Detail</c> is its name); else null.</param>
public sealed record TriggerEvent(string Name, IReadOnlyList<string>? Chain = null, string? Detail = null, string? Kind = null);

/// <summary>
/// A push source of system events (DESIGN §3, D-107): session, power, … Lazy like the gamepad reader (D-086): the
/// <see cref="TriggerService"/> calls <see cref="Load"/> only while some flow subscribes to one of its
/// <see cref="Events"/> and <see cref="Unload"/> after the last one goes; before that it holds nothing (no handles, no
/// threads). No polling, no admin. <see cref="Raised"/> may come from any thread and must be quick.
/// </summary>
public interface ITriggerSource : IDisposable
{
    /// <summary>The event names this source can raise.</summary>
    IReadOnlyList<string> Events { get; }

    event Action<TriggerEvent>? Raised;

    /// <summary>Registers the notifications; a second call while started does nothing.</summary>
    void Load();

    /// <summary>Releases everything <see cref="Load"/> took; <see cref="Load"/> may be called again.</summary>
    void Unload();
}

/// <summary>
/// A machine condition a trigger's <c>only</c> names ("onBattery"), read when the trigger fires and only then (D-081
/// style: read-only, nothing resident).
/// </summary>
public interface ITriggerCondition
{
    /// <summary>The id from <c>TriggerText.Conditions</c>.</summary>
    string Id { get; }

    /// <summary>Throw when the answer is unknown: the trigger then fires anyway.</summary>
    Task<bool> HoldsAsync(CancellationToken cancellationToken);
}
