using DazeControl.Core.Input;

namespace DazeControl.Core.Tests;

public class HotkeyTests
{
    [Theory]
    [InlineData("Win+Alt+A", HotkeyModifiers.Win | HotkeyModifiers.Alt, 0x41u)]
    [InlineData(" alt + win + a ", HotkeyModifiers.Win | HotkeyModifiers.Alt, 0x41u)]
    [InlineData("Control+Alt+F11", HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 0x7Au)]
    [InlineData("Ctrl+Shift+F24", HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, 0x87u)]
    [InlineData("Windows+1", HotkeyModifiers.Win, 0x31u)]
    public void Parses(string text, HotkeyModifiers modifiers, uint key)
    {
        Assert.True(Hotkey.TryParse(text, out var hotkey));
        Assert.Equal(new Hotkey(modifiers, key), hotkey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("A")]              // no modifier
    [InlineData("Shift+A")]        // would swallow typing capitals
    [InlineData("Win+Alt")]        // no key
    [InlineData("Win+Win+A")]
    [InlineData("Win+Alt+Space")]
    [InlineData("Win+F25")]
    [InlineData("Win+F0")]
    [InlineData("Win+F01")]
    [InlineData("Hyper+A")]
    [InlineData("Win++A")]
    public void Rejects(string? text) => Assert.False(Hotkey.TryParse(text, out _));

    [Theory]
    [InlineData(HotkeyModifiers.Win | HotkeyModifiers.Alt, 0x41u, true)]
    [InlineData(HotkeyModifiers.Ctrl, 0x7Au, true)]          // F11
    [InlineData(HotkeyModifiers.Alt, 0x39u, true)]           // 9
    [InlineData(HotkeyModifiers.Shift, 0x41u, false)]        // only Shift
    [InlineData(HotkeyModifiers.None, 0x41u, false)]
    [InlineData(HotkeyModifiers.Win, 0x20u, false)]          // Space
    [InlineData(HotkeyModifiers.Win, 0x12u, false)]          // a modifier as the key
    [InlineData(HotkeyModifiers.Win, 0x88u, false)]          // past F24
    public void CreatesFromPressedKeys(HotkeyModifiers modifiers, uint key, bool valid) =>
        Assert.Equal(valid, Hotkey.TryCreate(modifiers, key, out _));

    [Fact]
    public void PrefixListsHeldModifiersInOrder() =>
        Assert.Equal("Win+Alt+Shift+", Hotkey.Prefix(HotkeyModifiers.Shift | HotkeyModifiers.Alt | HotkeyModifiers.Win));

    [Theory]
    [InlineData("alt+win+a", "Win+Alt+A")]
    [InlineData("Shift+Alt+Ctrl+Win+F1", "Win+Ctrl+Alt+Shift+F1")]
    [InlineData("Ctrl+Alt+f11", "Ctrl+Alt+F11")]
    [InlineData("Win+7", "Win+7")]
    public void FormatsCanonically(string text, string canonical)
    {
        Assert.True(Hotkey.TryParse(text, out var hotkey));
        Assert.Equal(canonical, hotkey.ToString());
    }
}
