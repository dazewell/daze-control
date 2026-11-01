using DazeControl.Core.Model;
using DazeControl.Ipc;

namespace DazeControl.Runtime.Control;

/// <summary>
/// Read-only questions about this PC's devices, for <c>dctl devices</c>, <c>dctl capture-display-layout</c> and the
/// editor's pickers. Failures are <see cref="Providers.StepFailedException"/>s in plain words.
/// </summary>
public interface IDeviceQueries
{
    /// <summary>The kinds <see cref="ListAsync"/> knows, in the order help shows them.</summary>
    IReadOnlyList<string> Kinds { get; }

    Task<IReadOnlyList<DeviceInfo>> ListAsync(string kind, CancellationToken cancellationToken);

    /// <summary>The screens as they are now; changes nothing.</summary>
    Task<DisplayLayout> CaptureDisplayLayoutAsync(string key, CancellationToken cancellationToken);
}
