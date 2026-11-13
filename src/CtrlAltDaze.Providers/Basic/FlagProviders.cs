using System.Collections.Concurrent;
using CtrlAltDaze.Runtime.Providers;

namespace CtrlAltDaze.Providers.Basic;

/// <summary>
/// Named on/off flags shared by all flows while the engine runs (D-029: in memory, unset = off). Names ignore case,
/// like the rest of the user-facing names.
/// </summary>
public sealed class FlagStore
{
    private readonly ConcurrentDictionary<string, bool> _flags = new(StringComparer.OrdinalIgnoreCase);

    public bool Get(string name) => _flags.TryGetValue(name, out var value) && value;

    public void Set(string name, bool value) => _flags[name] = value;
}

/// <summary><c>flag.set</c>: turns a flag on or off.</summary>
public sealed class FlagSetProvider(FlagStore flags) : IStepProvider
{
    public string Id => "flag.set";

    /// <summary>The store this sets, for steps that remember an answer in a flag (<c>prompt.ask</c>).</summary>
    public FlagStore Flags => flags;

    public Task ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        flags.Set(context.RequiredText("name"), context.Boolean("value", fallback: true));
        return Task.CompletedTask;
    }
}

/// <summary><c>flag.check</c>: fails when the flag isn't as expected, so a <c>when: failure</c> branch can follow.</summary>
public sealed class FlagCheckProvider(FlagStore flags) : IStepProvider
{
    public string Id => "flag.check";

    public Task ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        var name = context.RequiredText("name");
        var expected = context.Boolean("value", fallback: true);
        context.SetValue("result", $"Flag \"{name}\" is {(flags.Get(name) ? "on" : "off")}");
        return flags.Get(name) == expected
            ? Task.CompletedTask
            : Task.FromException(new CheckNotMetException($"Flag \"{name}\" is {(expected ? "off" : "on")}."));
    }
}
