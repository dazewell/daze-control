using DazeControl.Runtime.Triggers;
using DazeControl.Runtime.Providers;

namespace DazeControl.Runtime.Menus;

/// <summary>
/// Refuses to show a menu over a game in full screen (D-055, X-010): any window of ours knocks a DXGI full-screen game
/// (exclusive, or "exclusive" under Fullscreen Optimizations) out of full screen, so the menu says no before anything is
/// drawn or any key is taken. The probe is <c>SHQueryUserNotificationState == QUNS_RUNNING_D3D_FULL_SCREEN</c> in the
/// engine, a fake in tests. The step fails with UX §5.7 #32 (run history) and the engine logs one line; no pop-up beyond
/// the run's own failure.
/// </summary>
/// <param name="inner">The window.</param>
/// <param name="overFullScreenGame">True while a DXGI full-screen game has the screen.</param>
/// <param name="log">The engine log (one line per refusal).</param>
public sealed class FullScreenGuardPresenter(IMenuPresenter inner, Func<bool> overFullScreenGame, Action<string> log) : IMenuPresenter
{
    /// <summary>UX §5.7 #32.</summary>
    public const string Message = "The menu can't show over this full-screen game. Switch the game to borderless or windowed, then try again.";

    public Task<MenuOutcome> ShowAsync(MenuRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested(); // a stop that raced the probe keeps its own outcome
        if (overFullScreenGame())
        {
            log($"menu: \"{request.Title}\" not shown, a full-screen game has the screen (D-055)");
            throw new StepFailedException(Message);
        }
        return inner.ShowAsync(request, cancellationToken);
    }
}

/// <summary>What a key the open menu owns does (DESIGN §3.3, §4.3).</summary>
public enum MenuKeyKind { Move, Activate, Cancel, Number }

/// <param name="Kind">What it does.</param>
/// <param name="Direction">For <see cref="MenuKeyKind.Move"/>.</param>
/// <param name="Number">For <see cref="MenuKeyKind.Number"/>: 1–9.</param>
public readonly record struct MenuKeyAction(MenuKeyKind Kind, MenuDirection Direction = MenuDirection.Down, int Number = 0);

/// <summary>The keys a menu registers while it is open: Up Down Left Right Enter Space Esc Tab Shift+Tab Home End 1–9, and Num8 Num2 Num4 Num6 Num7 Num9 Num5 Num0.</summary>
public static class MenuKeyMap
{
    private const int Shift = 0x4; // MOD_SHIFT

    /// <summary>(virtual key, modifiers as RegisterHotKey's) and what each does.</summary>
    public static IReadOnlyList<(int Key, int Modifiers, MenuKeyAction Action)> All { get; } = Build();

    private static List<(int, int, MenuKeyAction)> Build()
    {
        List<(int, int, MenuKeyAction)> keys =
        [
            (0x26, 0, Move(MenuDirection.Up)), (0x28, 0, Move(MenuDirection.Down)),
            (0x25, 0, Move(MenuDirection.Left)), (0x27, 0, Move(MenuDirection.Right)),
            (0x09, 0, Move(MenuDirection.Next)), (0x09, Shift, Move(MenuDirection.Previous)),
            (0x24, 0, Move(MenuDirection.First)), (0x23, 0, Move(MenuDirection.Last)),
            (0x0D, 0, new(MenuKeyKind.Activate)), (0x20, 0, new(MenuKeyKind.Activate)),
            (0x1B, 0, new(MenuKeyKind.Cancel)),
            // The number pad as a d-pad, for a Steam layout that sends Num keys (Num Lock on; D-100): 8 2 4 6 move,
            // 7 / 9 previous / next, 5 and Num0 run / close. Num Enter is Enter already.
            (0x68, 0, Move(MenuDirection.Up)), (0x62, 0, Move(MenuDirection.Down)),
            (0x64, 0, Move(MenuDirection.Left)), (0x66, 0, Move(MenuDirection.Right)),
            (0x67, 0, Move(MenuDirection.Previous)), (0x69, 0, Move(MenuDirection.Next)),
            (0x65, 0, new(MenuKeyKind.Activate)), (0x60, 0, new(MenuKeyKind.Cancel)),
        ];
        for (var n = 1; n <= 9; n++)
            keys.Add((0x30 + n, 0, new(MenuKeyKind.Number, Number: n)));
        return keys;

        static MenuKeyAction Move(MenuDirection direction) => new(MenuKeyKind.Move, direction);
    }

    /// <summary>Feeds one key to the model; false when the menu dropped it.</summary>
    public static bool Apply(MenuModel model, MenuKeyAction action)
    {
        ArgumentNullException.ThrowIfNull(model);
        return action.Kind switch
        {
            MenuKeyKind.Move => model.KeyMove(action.Direction),
            MenuKeyKind.Activate => model.Activate(),
            MenuKeyKind.Cancel => model.Cancel(),
            _ => model.PickNumber(action.Number),
        };
    }
}

/// <summary>
/// Controller readings while a menu is open (T12.6 fills this with the real source, S1's backend; until then the
/// engine has none and the menu answers keys and mouse only). Readings arrive at about 60 Hz, on any thread.
/// </summary>
public interface IMenuPadFeed
{
    /// <summary>Starts delivering readings; disposing stops them (and the source with its last listener).</summary>
    IDisposable Start(Action<PadReading> onReading);
}
