using DazeControl.Runtime.Menus;
using DazeControl.Runtime.Providers;

namespace DazeControl.Runtime.Tests;

public sealed class MenuPlumbingTests : IDisposable
{
    private readonly ManualTime _time = new();

    public void Dispose() => _time.Dispose();

    private static MenuRequest Request() =>
        new("Couch", [new MenuItem("tv", "TV", MenuItemState.Ready)], MenuLayout.List, false, TimeSpan.FromSeconds(30), null);

    private sealed class Window : IMenuPresenter
    {
        public int Shown;

        public Task<MenuOutcome> ShowAsync(MenuRequest request, CancellationToken cancellationToken)
        {
            Shown++;
            return Task.FromResult(new MenuOutcome("tv", MenuCloseReason.Picked));
        }
    }

    [Fact]
    public async Task OverAFullScreenGameTheStepFailsWithTheMessageAndOneLogLineAndNothingIsShown()
    {
        var window = new Window();
        List<string> log = [];
        var guard = new FullScreenGuardPresenter(window, () => true, log.Add);

        var failure = await Assert.ThrowsAsync<StepFailedException>(() => guard.ShowAsync(Request(), CancellationToken.None));

        Assert.Equal(FullScreenGuardPresenter.Message, failure.Message);
        Assert.Equal(0, window.Shown);
        Assert.Single(log);
        Assert.Contains("Couch", log[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithNoFullScreenGameTheMenuShowsAndTheProbeIsAskedEveryTime()
    {
        var window = new Window();
        var asked = 0;
        var guard = new FullScreenGuardPresenter(window, () => { asked++; return false; }, _ => { });

        var first = await guard.ShowAsync(Request(), CancellationToken.None);
        await guard.ShowAsync(Request(), CancellationToken.None);

        Assert.Equal("tv", first.FlowId);
        Assert.Equal(2, window.Shown);
        Assert.Equal(2, asked);
    }

    [Fact]
    public void EveryMenuKeyIsRegisteredOnceAndNoneIsMissing()
    {
        var keys = MenuKeyMap.All;

        Assert.Equal(11 + 9, keys.Count);
        Assert.Equal(keys.Count, keys.Select(k => (k.Key, k.Modifiers)).Distinct().Count());
        Assert.Contains(keys, k => k.Key == 0x1B && k.Action.Kind == MenuKeyKind.Cancel);
        Assert.Contains(keys, k => k.Key == 0x09 && k.Modifiers == 4 && k.Action.Direction == MenuDirection.Previous);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9], keys.Where(k => k.Action.Kind == MenuKeyKind.Number).Select(k => k.Action.Number));
    }

    [Fact]
    public void KeysDriveTheModel()
    {
        MenuItem[] items = [new("a", "A", MenuItemState.Ready), new("b", "B", MenuItemState.Ready), new("c", "C", MenuItemState.Ready)];
        using var menu = new MenuModel(items, MenuLayout.List, TimeSpan.FromSeconds(30), _time);

        MenuKeyMap.Apply(menu, new(MenuKeyKind.Move, MenuDirection.Down));
        Assert.Equal(1, menu.Focus);
        MenuKeyMap.Apply(menu, new(MenuKeyKind.Activate));

        Assert.Equal("b", menu.Outcome?.FlowId);
    }

    [Fact]
    public void EscAndNumbersEndTheMenu()
    {
        MenuItem[] items = [new("a", "A", MenuItemState.Ready), new("b", "B", MenuItemState.Ready)];
        using var esc = new MenuModel(items, MenuLayout.List, TimeSpan.FromSeconds(30), _time);
        using var two = new MenuModel(items, MenuLayout.List, TimeSpan.FromSeconds(30), _time);

        MenuKeyMap.Apply(esc, new(MenuKeyKind.Cancel));
        MenuKeyMap.Apply(two, new(MenuKeyKind.Number, Number: 2));

        Assert.Equal(MenuCloseReason.Cancelled, esc.Outcome?.Reason);
        Assert.Equal("b", two.Outcome?.FlowId);
    }
}
