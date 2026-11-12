using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.ComponentModel;
using DazeControl.Core.Catalog;
using DazeControl.Core.Layout;
using DazeControl.Core.Model;
using DazeControl.Editor.ViewModels.Canvas;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Core;
using CorePoint = DazeControl.Core.Layout.Point;
using XamlCanvas = Microsoft.UI.Xaml.Controls.Canvas;

namespace DazeControl.Editor.Canvas;

public sealed partial class FlowCanvas
{
    /// <summary>Only for keyboard focus, so a mouse click doesn't draw a ring too (system focus visual rule).</summary>
    private void UpdateFocusRing()
    {
        if (_viewModel?.FocusedId is { } id && FocusState == FocusState.Keyboard && _viewModel.Find(id) is { } node)
        {
            var radius = node.Kind == CanvasNodeKind.Card ? 15 : 31;
            FocusRing.RadiusX = FocusRing.RadiusY = radius;
            var bounds = BoundsOf(node); // follows a live drag
            FocusRing.Width = bounds.Width + 6;
            FocusRing.Height = (_views.TryGetValue(id, out var view) && view.ActualHeight > 0 ? view.ActualHeight : bounds.Height) + 6;
            XamlCanvas.SetLeft(FocusRing, bounds.X - 3);
            XamlCanvas.SetTop(FocusRing, bounds.Y - 3);
            FocusRing.Visibility = Visibility.Visible;
        }
        else
        {
            FocusRing.Visibility = Visibility.Collapsed;
        }
    }

    // ---- Keyboard (UX §5.2): one tab stop, roving focus ----

    /// <summary>
    /// The edit shortcuts the canvas and the Outline view share (UX §5.2): Ctrl+G, Ctrl+Shift+G, Ctrl+Z/Y, Ctrl+C/V/D,
    /// Del and "/". False when <paramref name="key"/> isn't one of them.
    /// </summary>
    public bool RunCommandKey(VirtualKey key)
    {
        if (_viewModel is not { } vm)
            return false;
        var ctrl = CtrlDown();
        switch (key)
        {
            case VirtualKey.G when ctrl && vm.CanEdit:
                if (ShiftDown())
                {
                    if (vm.SelectedIds.FirstOrDefault() is { } card && vm.Find(card) is { } at)
                        ShowRefusal(vm.UngroupSelection(), at);
                }
                else if (vm.SelectedIds.FirstOrDefault() is { } first && vm.Find(first) is { } anchor)
                    ShowRefusal(vm.GroupSelection(), anchor);
                break;
            case VirtualKey.Z when ctrl && ShiftDown():
            case VirtualKey.Y when ctrl:
                vm.Redo();
                break;
            case VirtualKey.Z when ctrl:
                vm.Undo();
                break;
            case VirtualKey.C when ctrl:
                vm.Copy();
                break;
            case VirtualKey.V when ctrl:
                vm.Paste();
                break;
            case VirtualKey.D when ctrl:
                vm.Duplicate();
                break;
            case VirtualKey.Delete or VirtualKey.Back when vm.CanEdit:
                vm.DeleteSelection();
                break;
            case (VirtualKey)191 or VirtualKey.Divide when vm.CanEdit: // "/" (VK_OEM_2)
                ShowQuickAdd(null, [.. vm.SelectedIds], null);
                break;
            default:
                return false;
        }
        return true;
    }

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (_viewModel is null)
            return;
        var ctrl = CtrlDown();
        var alt = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(CoreVirtualKeyStates.Down);
        e.Handled = true;
        _autoFit = false;
        if (RunCommandKey(e.Key))
        {
            UpdateFocusRing();
            RevealFocused();
            return;
        }
        switch (e.Key)
        {
            case VirtualKey.Up or VirtualKey.Down when alt && _viewModel.FocusedId is { } id && _viewModel.CanEdit:
                _viewModel.MoveInColumn(id, e.Key == VirtualKey.Up ? -1 : 1);
                break;
            case VirtualKey.Left:
                _viewModel.MoveFocus(FocusMove.Left);
                break;
            case VirtualKey.Right:
                _viewModel.MoveFocus(FocusMove.Right);
                break;
            case VirtualKey.Up:
                _viewModel.MoveFocus(FocusMove.Up);
                break;
            case VirtualKey.Down:
                _viewModel.MoveFocus(FocusMove.Down);
                break;
            case VirtualKey.A when ctrl:
                _viewModel.SelectAll();
                break;
            case VirtualKey.Number0 or VirtualKey.NumberPad0 when ctrl:
                Fit();
                break;
            case VirtualKey.Number1 or VirtualKey.NumberPad1 when ctrl:
                ZoomToActualSize();
                break;
            case VirtualKey.Escape:
                _viewModel.ClearSelection();
                break;
            case VirtualKey.Space:
                _spaceDown = true;
                break;
            default:
                e.Handled = false;
                base.OnKeyDown(e);
                return;
        }
        UpdateFocusRing();
        if (_viewModel.FocusedId is { } focused && _viewModel.Find(focused) is { } node)
            BringIntoView(node);
    }

    protected override void OnKeyUp(KeyRoutedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Key == VirtualKey.Space)
            _spaceDown = false;
        base.OnKeyUp(e);
    }

    /// <summary>Focus stays on the canvas (one tab stop), so tell screen readers which step the arrows reached.</summary>
    private void AnnounceFocus()
    {
        if (FocusState == FocusState.Unfocused || _viewModel?.FocusedId is not { } id || _viewModel.Find(id) is not { } node)
            return;
        FrameworkElementAutomationPeer.FromElement(this)?.RaiseNotificationEvent(
            AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.MostRecent, node.AutomationName, "canvas-focus");
    }

    /// <summary>After an add or paste: scroll to the new step once it has its slot.</summary>
    private void RevealFocused() => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
    {
        if (Shown && _viewModel?.FocusedId is { } id && _viewModel.Find(id) is { } node)
            BringIntoView(node);
    });

    private void BringIntoView(CanvasNodeViewModel node) => Reveal(node.Bounds);
}
