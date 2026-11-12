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
    // ---- Add a step: quick-add search, palette drop (UX §5.2) ----

    /// <summary>
    /// The search flyout at <paramref name="at"/> (content DIP; null = beside the focused step, else the middle of the
    /// view). The chosen step runs after <paramref name="after"/>, or goes on <paramref name="edge"/>.
    /// </summary>
    public void ShowQuickAdd(Windows.Foundation.Point? at, IReadOnlyList<string>? after, CanvasEdgeViewModel? edge)
    {
        if (_viewModel is not { CanEdit: true } vm)
            return;
        var box = new AutoSuggestBox
        {
            PlaceholderText = "Search steps",
            QueryIcon = new SymbolIcon(Symbol.Find),
            Width = 280,
            ItemsSource = StepPaletteViewModel.Search(""),
            UpdateTextOnSelect = false,
        };
        AutomationProperties.SetName(box, "Add a step");
        var flyout = new Flyout { Content = box, Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        box.TextChanged += (s, args) =>
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            {
                s.ItemsSource = StepPaletteViewModel.Search(s.Text);
                s.IsSuggestionListOpen = true;
            }
        };
        var done = false;
        void Choose(PaletteItem? item)
        {
            if (done || item is null)
                return;
            done = true;
            flyout.Hide();
            if (edge is not null)
                vm.InsertStep(item.Step, edge);
            else
                vm.AddStep(item.Step, after);
            if (Shown)
                Focus(FocusState.Programmatic);
            RevealFocused();
        }
        box.QuerySubmitted += (_, args) =>
            Choose(args.ChosenSuggestion as PaletteItem ?? (StepPaletteViewModel.Search(args.QueryText) is [var first, ..] ? first : null));
        flyout.Opened += (_, _) => { box.Focus(FocusState.Programmatic); box.IsSuggestionListOpen = true; };
        var position = at ?? (vm.FocusedId is { } id && vm.Find(id) is { } node ? P(vm.OutPortOf(node)) : default);
        if (!Shown && FallbackTarget is not null)
            flyout.ShowAt(FallbackTarget, new FlyoutShowOptions { Placement = FlyoutPlacementMode.Bottom });
        else if (at is null && vm.FocusedId is null)
            flyout.ShowAt(Root, new FlyoutShowOptions { Position = new(Root.ActualWidth / 2 - 140, Root.ActualHeight / 3) });
        else
            flyout.ShowAt(Host, new FlyoutShowOptions { Position = position });
    }

    /// <summary>Palette click or Enter: after the selection, else a first step.</summary>
    public void AddFromPalette(string step)
    {
        if (_viewModel is null)
            return;
        _viewModel.AddStep(step, [.. _viewModel.SelectedIds]);
        if (Shown)
            Focus(FocusState.Programmatic);
        RevealFocused();
    }

    /// <summary>Data format the palette drags: the step id as text with this prefix.</summary>
    public const string PaletteDragPrefix = "daze-step:";

    private static async Task<string?> DraggedStep(DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.Text))
            return null;
        var text = await e.DataView.GetTextAsync();
        return text.StartsWith(PaletteDragPrefix, StringComparison.Ordinal) ? text[PaletteDragPrefix.Length..] : null;
    }

    private void OnPaletteDragOver(object sender, DragEventArgs e)
    {
        if (_viewModel is not { CanEdit: true } || !e.DataView.Contains(StandardDataFormats.Text))
            return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        var target = _viewModel.HitTest(C(e.GetPosition(Host)));
        e.DragUIOverride.Caption = target is null ? "Add a step" : $"Run after {target.Title}";
    }

    /// <summary>Dropped on a step: runs after it (its card for a member); on empty canvas: a first step.</summary>
    private async void OnPaletteDrop(object sender, DragEventArgs e)
    {
        if (_viewModel is not { CanEdit: true } vm)
            return;
        var deferral = e.GetDeferral();
        try
        {
            var target = vm.HitTest(C(e.GetPosition(Host)));
            if (await DraggedStep(e) is { } step)
            {
                vm.AddStep(step, target is null ? null : [target.Id]);
                RevealFocused();
            }
        }
        finally
        {
            deferral.Complete();
        }
        if (Shown)
            Focus(FocusState.Programmatic);
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_viewModel is null)
            return;
        var position = e.GetPosition(Host);
        if (_viewModel.HitTest(C(position)) is null && _viewModel.HitTestEdge(C(position)) is null)
        {
            _viewModel.ClearSelection();
            ShowQuickAdd(position, null, null);
            e.Handled = true;
        }
    }

    private void OnAddFirstStep(object sender, RoutedEventArgs e) => ShowQuickAdd(null, null, null);
}
