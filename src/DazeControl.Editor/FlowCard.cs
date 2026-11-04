using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace DazeControl.Editor;

/// <summary>
/// A Flows card as one keyboard stop (D-070): Enter or Space opens the flow, and Narrator's Invoke does too. The On
/// switch and the Run button inside stay stops of their own.
/// </summary>
internal sealed partial class FlowCard : ContentControl
{
    public FlowCard()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
    }

    /// <summary>The card was activated from the keyboard or UI Automation.</summary>
    public event EventHandler? Invoked;

    internal void Invoke() => Invoked?.Invoke(this, EventArgs.Empty);

    /// <summary>Delete on the focused card (D-076).</summary>
    public event EventHandler? DeleteRequested;

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        // Only when the card itself has focus: Space on the switch or Enter on Run belong to them.
        if (ReferenceEquals(e.OriginalSource, this) && e.Key is VirtualKey.Enter or VirtualKey.Space)
        {
            e.Handled = true;
            Invoke();
            return;
        }
        if (ReferenceEquals(e.OriginalSource, this) && e.Key == VirtualKey.Delete)
        {
            e.Handled = true;
            DeleteRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        base.OnKeyDown(e);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FlowCardPeer(this);
}

internal sealed partial class FlowCardPeer(FlowCard owner) : FrameworkElementAutomationPeer(owner), IInvokeProvider
{
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;

    protected override string GetClassNameCore() => nameof(FlowCard);

    protected override object? GetPatternCore(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.Invoke ? this : base.GetPatternCore(patternInterface);

    public void Invoke() => owner.Invoke();
}
