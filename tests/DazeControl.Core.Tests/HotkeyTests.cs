using DazeControl.Core.Input;
using DazeControl.Core.Model;
using DazeControl.Core.Yaml;

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
    [InlineData("[")]              // punctuation alone would take typing
    [InlineData("Shift+\\")]
    [InlineData("Shift+Num1")]     // with Num Lock on, Windows turns it into End
    [InlineData("Ctrl+Shift+Num1")]
    [InlineData("Num10")]
    [InlineData("Num")]
    [InlineData("Win+Numpad")]
    [InlineData("Shift+Num*")]
    [InlineData("Shift+Num+")]
    [InlineData("Ctrl+Shift+Num/")]
    [InlineData("Win+Num")]
    [InlineData("Num++ +")]
    [InlineData("NumEnter")]       // RegisterHotKey can't tell it from Enter (D-085)
    [InlineData("Win+#")]          // not a key of its own on a US layout
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

    // D-071: every new key, both directions (text -> VK -> canonical text).
    [Theory]
    [InlineData("Win+[", HotkeyModifiers.Win, 0xDBu)]
    [InlineData("Win+]", HotkeyModifiers.Win, 0xDDu)]
    [InlineData("Win+\\", HotkeyModifiers.Win, 0xDCu)]
    [InlineData("Ctrl+Alt+;", HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 0xBAu)]
    [InlineData("Win+'", HotkeyModifiers.Win, 0xDEu)]
    [InlineData("Win+,", HotkeyModifiers.Win, 0xBCu)]
    [InlineData("Win+.", HotkeyModifiers.Win, 0xBEu)]
    [InlineData("Win+/", HotkeyModifiers.Win, 0xBFu)]
    [InlineData("Win+`", HotkeyModifiers.Win, 0xC0u)]
    [InlineData("Win+-", HotkeyModifiers.Win, 0xBDu)]
    [InlineData("Win+=", HotkeyModifiers.Win, 0xBBu)]
    [InlineData("Win+Shift+[", HotkeyModifiers.Win | HotkeyModifiers.Shift, 0xDBu)]
    [InlineData("Num0", HotkeyModifiers.None, 0x60u)]
    [InlineData("Num5", HotkeyModifiers.None, 0x65u)]
    [InlineData("Num9", HotkeyModifiers.None, 0x69u)]
    [InlineData("Ctrl+Num1", HotkeyModifiers.Ctrl, 0x61u)]
    [InlineData("Win+Num1", HotkeyModifiers.Win, 0x61u)]
    [InlineData("Win+Ctrl+Alt+Num9", HotkeyModifiers.Win | HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 0x69u)]
    public void ParsesAndFormatsNewKeys(string text, HotkeyModifiers modifiers, uint key)
    {
        Assert.True(Hotkey.TryParse(text, out var hotkey));
        Assert.Equal(new Hotkey(modifiers, key), hotkey);
        Assert.Equal(text, hotkey.ToString());
    }

    [Theory]
    [InlineData("Numpad1", "Num1")]
    [InlineData("alt+NUMPAD7", "Alt+Num7")]
    [InlineData("num3", "Num3")]
    [InlineData("ctrl + ]", "Ctrl+]")]
    [InlineData("Numpad/", "Num/")]
    [InlineData("NumDivide", "Num/")]
    [InlineData("num divide", "Num/")]
    [InlineData("Num*", "Num*")]
    [InlineData("Numpad Multiply", "Num*")]
    [InlineData("NumStar", "Num*")]
    [InlineData("Num+", "Num+")]
    [InlineData("Win+Num++", "Win+Num+")]
    [InlineData("ctrl + numpad +", "Ctrl+Num+")]
    [InlineData("NumPlus", "Num+")]
    [InlineData("Num-", "Num-")]
    [InlineData("Alt+NumSubtract", "Alt+Num-")]
    [InlineData("Num.", "Num.")]
    [InlineData("Ctrl+NumDecimal", "Ctrl+Num.")]
    [InlineData("NumPeriod", "Num.")]
    public void FormatsNumberPadAndPunctuationCanonically(string text, string canonical)
    {
        Assert.True(Hotkey.TryParse(text, out var hotkey));
        Assert.Equal(canonical, hotkey.ToString());
    }

    [Theory]
    [InlineData(HotkeyModifiers.Win, 0xDCu, HotkeyRejection.None)]                          // Win+\
    [InlineData(HotkeyModifiers.None, 0x65u, HotkeyRejection.None)]                         // Num5 alone
    [InlineData(HotkeyModifiers.Ctrl, 0x61u, HotkeyRejection.None)]                         // Ctrl+Num1
    [InlineData(HotkeyModifiers.None, 0x41u, HotkeyRejection.NeedsModifier)]                // A
    [InlineData(HotkeyModifiers.Shift, 0xDBu, HotkeyRejection.NeedsModifier)]               // Shift+[
    [InlineData(HotkeyModifiers.Win, 0x20u, HotkeyRejection.NotAKey)]                       // Space
    [InlineData(HotkeyModifiers.None, 0x20u, HotkeyRejection.NotAKey)]
    [InlineData(HotkeyModifiers.Shift, 0x61u, HotkeyRejection.ShiftWithNumberPad)]
    [InlineData(HotkeyModifiers.None, 0x6Au, HotkeyRejection.None)]                         // Num* alone
    [InlineData(HotkeyModifiers.None, 0x6Fu, HotkeyRejection.None)]                         // Num/ alone
    [InlineData(HotkeyModifiers.Win, 0x6Bu, HotkeyRejection.None)]                          // Win+Num+
    [InlineData(HotkeyModifiers.Shift, 0x6Du, HotkeyRejection.ShiftWithNumberPad)]
    [InlineData(HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, 0x6Eu, HotkeyRejection.ShiftWithNumberPad)]
    [InlineData(HotkeyModifiers.None, 0x6Cu, HotkeyRejection.NotAKey)]                     // VK_SEPARATOR
    [InlineData(HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, 0x61u, HotkeyRejection.ShiftWithNumberPad)]
    public void ChecksWhyAKeyIsRefused(HotkeyModifiers modifiers, uint key, HotkeyRejection expected)
    {
        Assert.Equal(expected, Hotkey.Check(modifiers, key));
        Assert.Equal(expected == HotkeyRejection.None, Hotkey.TryCreate(modifiers, key, out _));
    }

    [Theory]
    [InlineData("Win+\\")]
    [InlineData("Win+[")]
    [InlineData("Win+]")]
    [InlineData("Ctrl+Num1")]
    [InlineData("Num5")]
    [InlineData("Num*")]
    [InlineData("Num+")]
    [InlineData("Win+Num-")]
    [InlineData("Num.")]
    [InlineData("Ctrl+Num/")]
    [InlineData("Win+'")]
    [InlineData("Win+,")]
    [InlineData("Win+`")]
    public void RoundTripsThroughYaml(string text)
    {
        var config = new Configuration
        {
            Flows = [new Flow("f", "F", [new StepNode("s", "wait")]) { Hotkeys = [text] }],
        };

        var yaml = ConfigYaml.Save(config);
        var load = ConfigYaml.Load(yaml);

        var flow = Assert.Single(Assert.IsType<Configuration>(load.Config).Flows);
        Assert.Equal([text], flow.Hotkeys);
        Assert.True(Hotkey.TryParse(flow.Hotkeys[0], out var hotkey));
        Assert.Equal(text, hotkey.ToString());
    }

    [Fact]
    public void ReadsHandWrittenPunctuationYaml()
    {
        var load = ConfigYaml.Load("""
            version: 1
            flows:
              f:
                name: F
                hotkeys:
                  - Win+[
                  - Win+\
                  - Ctrl+Num1
                  - Num5
                nodes:
                  - { id: s, step: wait }
            """);
        var flow = Assert.Single(Assert.IsType<Configuration>(load.Config).Flows);
        Assert.Equal(["Win+[", "Win+\\", "Ctrl+Num1", "Num5"], flow.Hotkeys);
    }

    [Theory]
    [InlineData("Win+E", true)]
    [InlineData("Win+L", true)]
    [InlineData("Win+1", true)]
    [InlineData("Win+.", true)]
    [InlineData("Win+Shift+S", true)]
    [InlineData("Win+[", false)]       // the user's AutoHotkey, not Windows (D-073)
    [InlineData("Win+\\", false)]
    [InlineData("Ctrl+E", false)]
    [InlineData("Win+Alt+Z", false)]
    public void KnowsWindowsOwnShortcuts(string text, bool windows)
    {
        Assert.True(Hotkey.TryParse(text, out var hotkey));
        Assert.Equal(windows, WindowsShortcuts.Contains(hotkey));
    }
}
