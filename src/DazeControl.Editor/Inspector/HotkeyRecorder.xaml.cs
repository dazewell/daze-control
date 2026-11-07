using DazeControl.Core.Input;
using DazeControl.Editor.ViewModels.Inspector;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace DazeControl.Editor.Inspector;

/// <summary>
/// Ported from the OSD (OSD D-021, D-022). A read-only box that records the chord pressed once the user starts it (click, Enter or Space; focus alone does not), with reset and turn-off buttons
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

    private bool _recording; // started by the user (click, Enter or Space), ended by Esc, a recorded chord or leaving the box
    private bool _recorded;  // since recording began: show the stored value, not the prompt, once keys are up
    private bool _chordPending; // Win, Ctrl or Alt went down and no other key came yet (H7)

    public HotkeyRecorder()
    {
        InitializeComponent();
        // Focus alone never records (it also arrives from a re-created control or tabbing past): a click does.
        Box.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => StartRecording()), true);
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

    /// <summary>Keys were pressed that make no shortcut, and why (UX §5.7 H1–H4, H7; D-073).</summary>
    public event EventHandler<HotkeyRejection>? Rejected;

    /// <summary>True while the box is recording (started by click, Enter or Space; ends on Esc, a recorded chord or leaving the box): registered shortcuts must not fire then (D-021).</summary>
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
        if (!_recording)
        {
            // Enter or Space on the focused box starts recording; every other key keeps its normal job (Tab, arrows).
            if (e.Key is VirtualKey.Enter or VirtualKey.Space)
            {
                e.Handled = true;
                StartRecording();
            }
            return;
        }
        if (!IsModifier(e.Key))
            _chordPending = false; // a key arrived, whatever it does (Tab, Escape, Backspace too): not H7
        if (e.Key == VirtualKey.Tab)
            return; // keep keyboard navigation out of the box
        e.Handled = true;
        if (e.Key == VirtualKey.Back || e.Key == VirtualKey.Delete && !IsNumberPadNavigation(e)) // pad Delete = Num. with Num Lock off
        {
            Recorded?.Invoke(this, "");
            StopRecording();
            return;
        }
        if (e.Key == VirtualKey.Escape)
        {
            StopRecording();
            return;
        }
        var modifiers = HeldModifiers();
        if (IsModifier(e.Key))
        {
            _chordPending |= (modifiers & ~HotkeyModifiers.Shift) != HotkeyModifiers.None;
            Box.Text = Core.Input.Hotkey.Prefix(modifiers);
            return;
        }
        if (IsNumberPadNavigation(e))
        {
            Rejected?.Invoke(this, HotkeyRejection.NumLockOff);
            ShowRecorded();
        }
        else if (Core.Input.Hotkey.TryCreate(modifiers, (uint)e.Key, out var hotkey))
        {
            Recorded?.Invoke(this, hotkey.ToString());
            StopRecording();
        }
        else
        {
            Rejected?.Invoke(this, Core.Input.Hotkey.Check(modifiers, (uint)e.Key)); // TryCreate is Check == None
            ShowRecorded();
        }
    }

    private void OnKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (!_recording)
            return;
        e.Handled = true;
        if (HeldModifiers() != HotkeyModifiers.None)
            return;
        if (_chordPending)
        {
            // Win/Ctrl/Alt went down and up and no key reached us: another app's RegisterHotKey took it (H7).
            _chordPending = false;
            Rejected?.Invoke(this, HotkeyRejection.NothingCameThrough);
        }
        Box.Text = _recorded ? Hotkey : RecordingPrompt;
    }

    /// <summary>Reads <paramref name="text"/> out at once (Narrator), e.g. why a key was refused.</summary>
    public void Announce(string text) =>
        (FrameworkElementAutomationPeer.FromElement(Box) ?? FrameworkElementAutomationPeer.CreatePeerForElement(Box))?.RaiseNotificationEvent(AutomationNotificationKind.Other,
            AutomationNotificationProcessing.ImportantMostRecent, text, "HotkeyRecorderNote");

    // With Num Lock off the pad sends Insert, End, Down, Clear (Num5)… as non-extended keys; the separate keys are
    // extended (D-073).
    private static bool IsNumberPadNavigation(KeyRoutedEventArgs e) => !e.KeyStatus.IsExtendedKey && e.Key is VirtualKey.Insert
        or VirtualKey.End or VirtualKey.Down or VirtualKey.PageDown or VirtualKey.Left or VirtualKey.Clear
        or VirtualKey.Delete or VirtualKey.Right or VirtualKey.Home or VirtualKey.Up or VirtualKey.PageUp;

    private void StartRecording()
    {
        if (_recording)
            return;
        _recording = true;
        _recorded = false;
        _chordPending = false;
        RecordingChanged?.Invoke(this, true);
        Box.Focus(FocusState.Pointer);
        Box.Text = RecordingPrompt;
    }

    private void StopRecording()
    {
        var was = _recording;
        _recording = false;
        _recorded = false;
        _chordPending = false;
        Box.Text = Hotkey;
        if (was)
            RecordingChanged?.Invoke(this, false);
    }

    private void OnGotFocus(object sender, RoutedEventArgs e)
    {
        if (!_recording)
            Box.Text = Hotkey;
    }

    private void OnLostFocus(object sender, RoutedEventArgs e) => StopRecording();

    /// <summary>Typed text was refused; the owner shows why (same place as <see cref="Rejected"/>).</summary>
    public event EventHandler<string>? TypedRefused;

    private void OnTypeLink(object sender, RoutedEventArgs e)
    {
        Box.Visibility = Visibility.Collapsed;
        TypeLink.Visibility = Visibility.Collapsed;
        TypeBox.Visibility = Visibility.Visible;
        TypeNote.Visibility = Visibility.Visible;
        TypeBox.Text = Hotkey;
        TypeBox.Focus(FocusState.Programmatic);
        TypeBox.SelectAll();
    }

    private void EndTyping(bool refocusLink)
    {
        TypeBox.Visibility = Visibility.Collapsed;
        TypeNote.Visibility = Visibility.Collapsed;
        Box.Visibility = Visibility.Visible;
        TypeLink.Visibility = Visibility.Visible;
        if (refocusLink)
            TypeLink.Focus(FocusState.Programmatic);
    }

    private void OnTypeKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            EndTyping(true);
        }
        else if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            if (TypeBox.Text.Trim().Length == 0)
            {
                EndTyping(true);
                return;
            }
            if (ShortcutTexts.TryParseTyped(TypeBox.Text, out var hotkey, out var error))
            {
                EndTyping(true);
                Recorded?.Invoke(this, hotkey);
                ShowRecorded();
            }
            else
            {
                TypedRefused?.Invoke(this, error!);
                Announce(error!);
            }
        }
    }

    private void OnTypeLostFocus(object sender, RoutedEventArgs e)
    {
        if (TypeBox.Visibility == Visibility.Visible)
            EndTyping(false);
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
