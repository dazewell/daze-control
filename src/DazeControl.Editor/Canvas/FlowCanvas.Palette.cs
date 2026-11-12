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

    /// <summary>The step or card a library drag is over at <paramref name="p"/> (a pill before its card) and the zone it would use.</summary>
    private (CanvasNodeViewModel? Target, DropZone Zone) PaletteTargetAt(CorePoint p)
    {
        var target = _viewModel?.Nodes.Where(n => n.Bounds.Contains(p))
            .OrderBy(n => n.Kind == CanvasNodeKind.Card ? 1 : 0).FirstOrDefault();
        return target is null ? (null, DropZone.None) : (target, FlowCanvasViewModel.ZoneAt(target, p));
    }

    /// <summary>
    /// Over a step: the step drag's zone highlight and label; elsewhere: a dashed outline where the new step will land
    /// (D-120). Positions are Host (content) DIP, so zoom and scroll need no maths here.
    /// </summary>
    private void OnPaletteDragOver(object sender, DragEventArgs e)
    {
        if (_viewModel is not { CanEdit: true } vm || !e.DataView.Contains(StandardDataFormats.Text))
            return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        var p = C(e.GetPosition(Host));
        var (target, zone) = PaletteTargetAt(p);
        if (target is not null && zone != DropZone.None)
        {
            LandingOutline.Visibility = Visibility.Collapsed;
            ShowDropZone(target, zone);
            e.DragUIOverride.Caption = zone switch
            {
                DropZone.RunAfter => $"Run after {target.Title}",
                DropZone.RunBefore => $"Run before {target.Title}",
                _ => $"Run together with {target.Title}",
            };
            return;
        }
        DropTarget.Visibility = DropLabel.Visibility = Visibility.Collapsed;
        var landing = vm.LandingAt(p, FlowCanvasViewModel.DefaultPillSize);
        LandingOutline.Stroke = Theme.Selection;
        LandingOutline.StrokeDashArray = Theme.Dashes(2, 2);
        LandingOutline.Width = landing.Width;
        LandingOutline.Height = landing.Height;
        XamlCanvas.SetLeft(LandingOutline, landing.X);
        XamlCanvas.SetTop(LandingOutline, landing.Y);
        LandingOutline.Visibility = Visibility.Visible;
        e.DragUIOverride.Caption = "Add a step here";
    }

    private void OnPaletteDragLeave(object sender, DragEventArgs e) => HidePaletteLanding();

    private void HidePaletteLanding() =>
        LandingOutline.Visibility = DropTarget.Visibility = DropLabel.Visibility = Visibility.Collapsed;

    /// <summary>Dropped on a step's zone: the step drag's edit; elsewhere: centred under the pointer, placed by hand (D-120).</summary>
    private async void OnPaletteDrop(object sender, DragEventArgs e)
    {
        HidePaletteLanding();
        if (_viewModel is not { CanEdit: true } vm)
            return;
        var deferral = e.GetDeferral();
        try
        {
            var p = C(e.GetPosition(Host)); // read before the await: the event args don't outlive it
            var (target, zone) = PaletteTargetAt(p);
            if (await DraggedStep(e) is { } step)
            {
                var (_, refusal) = vm.AddStepAt(step, p, target?.Id, zone);
                if (target is not null)
                    ShowRefusal(refusal, target);
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
