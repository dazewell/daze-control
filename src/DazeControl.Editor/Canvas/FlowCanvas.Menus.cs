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
    // ---- Context menus (right-click, long-press) ----

    /// <summary>"Run from this step" (T27.4): set by the window, which owns the run overlay; null hides the command.</summary>
    public Action<string>? RunFrom { get; set; }

    /// <summary>Whether a run can start now (the engine runs, no run or request is out).</summary>
    public Func<bool>? CanRunFrom { get; set; }

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (_rightPan is { } pan && !pan.TakeMenu())
        {
            e.Handled = true; // the release of a right-drag pan (D-069); handled also stops the ContextRequested after it
            return;
        }
        if (_viewModel is null || !_viewModel.CanEdit)
            return;
        var position = e.GetPosition(Host);
        var touch = e.PointerDeviceType == PointerDeviceType.Touch;
        if (_viewModel.HitTest(C(position)) is { } node)
        {
            if (!node.IsSelected)
                _viewModel.Select(node.Id);
            NodeMenu(position).ShowAt(Host, position);
            e.Handled = true;
        }
        else if (_viewModel.HitTestEdge(C(position), touch ? 20 : 12) is { } edge)
        {
            if (!edge.IsSelected)
                _viewModel.SelectEdge(edge); // the accent shows which edge the menu is for
            EdgeMenu(edge, position).ShowAt(Host, position);
            e.Handled = true;
        }
        else if (_viewModel.CanPaste)
        {
            var menu = new MenuFlyout();
            menu.Items.Add(Item("Add a step…", "", () => ShowQuickAdd(position, null, null), "/"));
            menu.Items.Add(Item("Paste", "", () => { _viewModel.ClearSelection(); _viewModel.Paste(); }, "Ctrl+V"));
            menu.ShowAt(Host, position);
            e.Handled = true;
        }
    }

    private static MenuFlyoutItem Item(string text, string glyph, Action click, string? shortcut = null, bool enabled = true)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph }, IsEnabled = enabled };
        if (shortcut is not null)
            item.KeyboardAcceleratorTextOverride = shortcut;
        item.Click += (_, _) => click();
        return item;
    }

    /// <summary>Every command on the selection (the Outline view shows the same menu, UX §5.2).</summary>
    public MenuFlyout NodeMenu(Windows.Foundation.Point? at = null)
    {
        var menu = new MenuFlyout();
        if (_viewModel is not { } vm)
            return menu;
        var editable = vm.CanEdit;
        var selected = vm.SelectedIds.ToList();
        var anchor = selected.Count > 0 ? vm.Find(selected[0]) : null;
        menu.Items.Add(Item("Add a step after…", "", () => ShowQuickAdd(at, selected, null), "/", editable));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Copy", "", () => vm.Copy(), "Ctrl+C"));
        menu.Items.Add(Item("Paste after", "", vm.Paste, "Ctrl+V", editable && vm.CanPaste));
        menu.Items.Add(Item("Duplicate", "", vm.Duplicate, "Ctrl+D", editable));
        menu.Items.Add(new MenuFlyoutSeparator());
        if (selected.Count > 1)
            menu.Items.Add(Item("Run together", "", () => ShowRefusalAt(vm.GroupSelection(), anchor), "Ctrl+G", editable));
        if (selected.Any(id => vm.Find(id)?.Kind == CanvasNodeKind.Card))
            menu.Items.Add(Item("Ungroup", "", () => ShowRefusalAt(vm.UngroupSelection(), anchor), "Ctrl+Shift+G", editable));
        if (selected.Count == 1 && anchor is not null && anchor.Kind != CanvasNodeKind.Card)
        {
            var before = new MenuFlyoutSubItem { Text = "Run before…", Icon = new FontIcon { Glyph = "" }, IsEnabled = editable };
            foreach (var candidate in vm.RunBeforeCandidates(anchor.Id))
                before.Items.Add(Item(candidate.Title, "", () => ShowRefusalAt(vm.RunBefore(anchor.Id, candidate.Id), anchor)));
            before.IsEnabled = editable && before.Items.Count > 0;
            menu.Items.Add(before);
            menu.Items.Add(Item("Move up", "", () => vm.MoveInColumn(anchor.Id, -1), "Alt+Up", editable));
            menu.Items.Add(Item("Move down", "", () => vm.MoveInColumn(anchor.Id, 1), "Alt+Down", editable));
        }
        if (selected.Count == 1 && anchor is not null && RunFrom is { } runFrom)
            menu.Items.Add(Item("Run from this step", "", () => runFrom(anchor.Id), enabled: CanRunFrom?.Invoke() == true));
        if (vm.SelectedStepType is { } stepType)
            menu.Items.Add(Item($"Select all {ProviderCatalog.Find(stepType)?.Title ?? stepType} steps", "", () => vm.SelectAllOfType(stepType)));
        menu.Items.Add(Item("Delete", "", vm.DeleteSelection, "Del", editable));
        return menu;
    }

    private void ShowRefusalAt(ConnectRefusal refusal, CanvasNodeViewModel? at)
    {
        if (at is not null)
            ShowRefusal(refusal, at);
        else if (FlowCanvasViewModel.Explain(refusal) is { } why)
            ShowTip(why, null);
    }

    /// <summary>An edit went through but changed more than it shows (D-058, D-059): said beside the node once laid out.</summary>
    private void OnNoted(object? sender, CanvasNote note) => DispatcherQueue.TryEnqueue(() =>
    {
        if (!Shown)
            ShowTip(note.Text, FallbackTarget, title: "");
        else if (_views.TryGetValue(note.At, out var view))
            ShowTip(note.Text, view, title: "");
    });

    private void ShowTip(string text, FrameworkElement? target, string title = "Can't connect these")
    {
        RefusalTip.Title = title;
        RefusalTip.Subtitle = text;
        RefusalTip.Target = target;
        RefusalTip.IsOpen = true;
    }

    private MenuFlyout EdgeMenu(CanvasEdgeViewModel edge, Windows.Foundation.Point at)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(Item("Add a step here…", "", () => ShowQuickAdd(at, null, edge)));
        menu.Items.Add(new MenuFlyoutSeparator());
        void Add(string text, EdgeWhen when)
        {
            var item = new ToggleMenuFlyoutItem { Text = text, IsChecked = edge.When == when };
            item.Click += (_, _) => _viewModel?.SetWhen(edge, when);
            menu.Items.Add(item);
        }
        Add("When it works", EdgeWhen.Success);
        Add("Only if this fails", EdgeWhen.Failure);
        Add("Whatever happens", EdgeWhen.Always);
        menu.Items.Add(new MenuFlyoutSeparator());
        var remove = new MenuFlyoutItem { Text = "Remove connection", Icon = new FontIcon { Glyph = "" } };
        remove.Click += (_, _) => _viewModel?.Disconnect(edge);
        menu.Items.Add(remove);
        return menu;
    }
}
