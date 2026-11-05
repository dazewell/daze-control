namespace DazeControl.Runtime.Menus;

/// <summary>What one open menu shows (DESIGN §2.1, §3.2).</summary>
/// <param name="Title">The menu's title (the flow's name unless the step sets one).</param>
/// <param name="Items">The choices, in menu order; flows that aren't there anymore are already dropped.</param>
/// <param name="Layout">List or grid.</param>
/// <param name="Large">"For a TV across the room".</param>
/// <param name="CloseAfter">The close timer: time with no input before the menu cancels itself.</param>
/// <param name="LastPickedFlowId">The flow last picked from this menu (engine memory), focused first.</param>
public sealed record MenuRequest(string Title, IReadOnlyList<MenuItem> Items, MenuLayout Layout, bool Large, TimeSpan CloseAfter,
    string? LastPickedFlowId);

/// <summary>
/// The seam to the window (DESIGN §3.2): shows one menu and says how it ended. Never activates anything; owns the
/// close timer (a <see cref="MenuModel"/>), so running out of time is a <see cref="MenuCloseReason.TimedOut"/> answer,
/// not a failure. Cancelling <c>cancellationToken</c> (step timeout, flow stopped) closes the menu and throws.
/// </summary>
public interface IMenuPresenter
{
    Task<MenuOutcome> ShowAsync(MenuRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// One menu on screen engine-wide: the gate is held only around <see cref="IMenuPresenter.ShowAsync"/> and released
/// before the picked flow starts or is awaited, so a <c>wait</c> parent's child can show its own menu (DESIGN §3.2).
/// Waiting for the gate counts toward the step's timeout (the token).
/// </summary>
/// <param name="inner">The window.</param>
public sealed class GatedMenuPresenter(IMenuPresenter inner) : IMenuPresenter, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<MenuOutcome> ShowAsync(MenuRequest request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await inner.ShowAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
