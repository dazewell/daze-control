using DazeControl.Core.Input;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace DazeControl.Editor.Inspector;

/// <summary>
/// Ported from the OSD (OSD D-021, D-022). A read-only box that records the chord pressed while it has focus, with reset and turn-off buttons
/// inside. It never stores anything: it raises <see cref="Recorded"/> and shows whatever <see cref="Hotkey"/>
/// is afterwards, so the owner can refuse a chord (D-022) by leaving it unchanged.
/// </summary>
public sealed partial class HotkeyRecorder : UserControl
{
    private const string RecordingPrompt = "Press the keys";

    public static readonly DependencyProperty HotkeyProperty = DependencyProperty.Register(
        nameof(Hotkey), typeof(string), typeof(HotkeyRecorder), new PropertyMetadata("", (d, _) => ((HotkeyRecorder)d).OnStoredChanged()));

    public static readonly DependencyProperty DefaultHotkeyProperty = DependencyProperty.Register(
        nameof(DefaultHotkey), typeof(string), typeof(HotkeyRecorder), new PropertyMetadata("", (d, _) => ((HotkeyRecorder)d).OnStoredChanged()));

    public static readonly DependencyProperty AccessibleNameProperty = DependencyProperty.Register(
        nameof(AccessibleName), typeof(string), typeof(HotkeyRecorder),
        new PropertyMetadata("", (d, e) => AutomationProperties.SetName(((HotkeyRecorder)d).Box, (string)e.NewValue)));

    private bool _recording; // has focus
    private bool _recorded;  // since focus came: show the stored value, not the prompt, once keys are up

    public HotkeyRecorder()
    {
        InitializeComponent();
        OnStoredChanged();
    }

    /// <summary>The stored shortcut ("Win+Alt+A"); "" is off.</summary>
    public string Hotkey
    {
        get => (string)GetValue(HotkeyProperty);
        set => SetValue(HotkeyProperty, value);
    }

    /// <summary>What the reset button goes back to; "" hides it.</summary>
    public string DefaultHotkey
    {
        get => (string)GetValue(DefaultHotkeyProperty);
        set => SetValue(DefaultHotkeyProperty, value);
    }

    /// <summary>The box's UI Automation name, e.g. "Next output shortcut".</summary>
    public string AccessibleName
    {
        get => (string)GetValue(AccessibleNameProperty);
        set => SetValue(AccessibleNameProperty, value);
    }

    /// <summary>A valid chord was pressed, or reset (the default) / turn-off ("") was picked.</summary>
    public event EventHandler<string>? Recorded;

    /// <summary>Keys were pressed that make no shortcut.</summary>
    public event EventHandler? Rejected;

    /// <summary>True while the box has focus: registered shortcuts must not fire then (D-021).</summary>
    public event EventHandler<bool>? RecordingChanged;

    private void OnStoredChanged()
    {
        if (Box is null)
            return;
        if (!_recording || _recorded)
            Box.Text = Hotkey;
        ResetButton.Visibility = DefaultHotkey.Length > 0 && Hotkey != DefaultHotkey ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(ResetButton, "Reset to " + DefaultHotkey);
        ClearButton.Visibility = Hotkey.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // Records chords instead of taking text (D-021). Chords the shell keeps for itself never arrive here;
    // RegisterHotKey would refuse them anyway.
    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Tab)
            return; // keep keyboard navigation out of the box
        e.Handled = true;
        if (e.Key is VirtualKey.Back or VirtualKey.Delete)
        {
            Recorded?.Invoke(this, "");
            ShowRecorded();
            return;
        }
        if (e.Key == VirtualKey.Escape)
        {
            ShowRecorded();
            return;
        }
        var modifiers = HeldModifiers();
        if (IsModifier(e.Key))
        {
            Box.Text = Core.Input.Hotkey.Prefix(modifiers);
            return;
        }
        if (Core.Input.Hotkey.TryCreate(modifiers, (uint)e.Key, out var hotkey))
            Recorded?.Invoke(this, hotkey.ToString());
        else
            Rejected?.Invoke(this, EventArgs.Empty);
        ShowRecorded();
    }

    private void OnKeyUp(object sender, KeyRoutedEventArgs e)
    {
        e.Handled = true;
        if (HeldModifiers() == HotkeyModifiers.None)
            Box.Text = _recorded ? Hotkey : RecordingPrompt;
    }

    private void OnGotFocus(object sender, RoutedEventArgs e)
    {
        _recording = true;
        _recorded = false;
        RecordingChanged?.Invoke(this, true);
        Box.Text = RecordingPrompt;
    }

    private void OnLostFocus(object sender, RoutedEventArgs e)
    {
        _recording = false;
        RecordingChanged?.Invoke(this, false);
        Box.Text = Hotkey;
    }

    private void OnReset(object sender, RoutedEventArgs e) => Recorded?.Invoke(this, DefaultHotkey);

    private void OnClear(object sender, RoutedEventArgs e) => Recorded?.Invoke(this, "");

    // The binding doesn't refresh the box when the value is unchanged (same chord again, or refused).
    private void ShowRecorded()
    {
        _recorded = true;
        Box.Text = Hotkey;
    }

    private static bool IsModifier(VirtualKey key) => key is VirtualKey.Control or VirtualKey.LeftControl
        or VirtualKey.RightControl or VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu
        or VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift
        or VirtualKey.LeftWindows or VirtualKey.RightWindows;

    private static HotkeyModifiers HeldModifiers()
    {
        static bool Down(VirtualKey key) =>
            InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
        var modifiers = HotkeyModifiers.None;
        if (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows))
            modifiers |= HotkeyModifiers.Win;
        if (Down(VirtualKey.Control))
            modifiers |= HotkeyModifiers.Ctrl;
        if (Down(VirtualKey.Menu))
            modifiers |= HotkeyModifiers.Alt;
        if (Down(VirtualKey.Shift))
            modifiers |= HotkeyModifiers.Shift;
        return modifiers;
    }
}
