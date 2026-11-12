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
    // ---- Pointer ----

    private static bool CtrlDown() =>
        InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);

    private static bool ShiftDown() =>
        InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_viewModel is null || _gesture != Gesture.None)
            return;
        var point = e.GetCurrentPoint(Host);
        _rightPan = null; // a swallow no RightTapped consumed must not eat this press's menu
        _viewSeq++; // a pan may start: a queued view retry must not snap it back
        var props = point.Properties;
        Focus(FocusState.Pointer);
        UpdateFocusRing(); // the ring is for keyboard focus only
        _autoFit = false;
        _pointerId = e.Pointer.PointerId;
        _pressContent = point.Position;
        _pressScreen = e.GetCurrentPoint(this).Position;
        _panStartX = Scroller.HorizontalOffset;
        _panStartY = Scroller.VerticalOffset;
        var touch = e.Pointer.PointerDeviceType == PointerDeviceType.Touch;
        _portRadius = touch ? 20 : FlowCanvasViewModel.PortRadius; // 40-DIP touch target (UX §5.3)

        if (props.IsMiddleButtonPressed || (_spaceDown && props.IsLeftButtonPressed))
        {
            _gesture = Gesture.Pan;
        }
        else if (props.IsRightButtonPressed)
        {
            if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse)
            {
                // Armed only: a still click falls through to RightTapped (the menus); capture waits for the pan (D-069).
                _rightPan = NewRightPan();
                _rightPan.Arm(_pressScreen.X, _pressScreen.Y);
                _gesture = Gesture.RightPan;
            }
            return;
        }
        else if (_viewModel.CanEdit && _viewModel.OutPortAt(C(point.Position), _portRadius) is { } source)
        {
            _connectFrom = source;
            _gesture = Gesture.Connect;
        }
        else if (props.IsLeftButtonPressed && _viewModel.HitTest(C(point.Position)) is { } node)
        {
            if (CtrlDown())
                _viewModel.Toggle(node.Id);
            else if (!node.IsSelected)
                _viewModel.Select(node.Id);
            _dragged = _views.GetValueOrDefault(node.Id);
            _gesture = Gesture.Press;
        }
        else if ((props.IsLeftButtonPressed || touch) && _viewModel.HitTestEdge(C(point.Position), touch ? 20 : 12) is { } edge)
        {
            _viewModel.SelectEdge(edge, additive: CtrlDown()); // UX §5.8 #2; Del removes it, right-click has its menu
            e.Handled = true;
            return;
        }
        else if (touch)
        {
            _viewModel.ClearSelection();
            return; // one-finger pan: the ScrollViewer's own manipulation
        }
        else if (props.IsLeftButtonPressed)
        {
            _gesture = Gesture.Marquee; // mouse and pen only (UX §5.8 #4)
        }
        if (_gesture != Gesture.None)
        {
            Root.CapturePointer(e.Pointer);
            e.Handled = true;
        }
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_viewModel is null)
            return;
        if (_gesture == Gesture.None)
        {
            var at = e.GetCurrentPoint(Host).Position;
            ShowPorts(_viewModel.HitTest(C(at)));
            if (e.Pointer.PointerDeviceType != PointerDeviceType.Touch)
                HoverEdge(at);
            return;
        }
        if (e.Pointer.PointerId != _pointerId)
            return;
        var position = e.GetCurrentPoint(Host).Position;
        var screen = e.GetCurrentPoint(this).Position;
        double dx = position.X - _pressContent.X, dy = position.Y - _pressContent.Y;
        switch (_gesture)
        {
            case Gesture.RightPan:
                var started = _rightPan!.IsPanning;
                if (!_rightPan.Move(screen.X, screen.Y))
                    break;
                if (!started)
                    Root.CapturePointer(e.Pointer);
                Scroller.ChangeView(_panStartX - (screen.X - _pressScreen.X), _panStartY - (screen.Y - _pressScreen.Y), null, disableAnimation: true);
                break;
            case Gesture.Pan:
                Scroller.ChangeView(_panStartX - (screen.X - _pressScreen.X), _panStartY - (screen.Y - _pressScreen.Y), null, disableAnimation: true);
                break;
            case Gesture.Press when Math.Abs(dx) > DragThreshold || Math.Abs(dy) > DragThreshold:
                if (_dragged is not null && _viewModel.CanEdit)
                {
                    _gesture = Gesture.DragNode;
                    _dragViews = [.. DragGroup()];
                    foreach (var view in _dragViews)
                        XamlCanvas.SetZIndex(view, view.Node.CardId is null ? 1 : 2); // members above their card
                }
                break;
            case Gesture.DragNode:
                // A free move shows the offset MoveBy will commit (snapped lead, padding clamp), so the drop moves
                // nothing (D-120); a member reorders in its card, so it follows the pointer as is.
                var offset = _dragged!.Node.CardId is null ? _viewModel.SnappedOffset(DragIds(), dx, dy) : new CorePoint(dx, dy);
                foreach (var view in _dragViews)
                {
                    if (view.RenderTransform is TranslateTransform shift)
                        (shift.X, shift.Y) = (offset.X, offset.Y);
                    else
                        view.RenderTransform = new TranslateTransform { X = offset.X, Y = offset.Y };
                }
                PreviewDrop(position);
                break;
            case Gesture.Connect:
                PreviewConnect(position);
                break;
            case Gesture.Marquee:
                var rect = DazeControl.Core.Layout.Rect.FromCorners(C(_pressContent), C(position));
                XamlCanvas.SetLeft(Marquee, rect.X);
                XamlCanvas.SetTop(Marquee, rect.Y);
                Marquee.Width = rect.Width;
                Marquee.Height = rect.Height;
                Marquee.Visibility = Visibility.Visible;
                break;
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_viewModel is null || e.Pointer.PointerId != _pointerId)
            return;
        if (_gesture == Gesture.RightPan)
        {
            var panned = _rightPan!.IsPanning;
            _rightPan.Release();
            EndGesture();
            if (panned)
            {
                Root.ReleasePointerCapture(e.Pointer);
                e.Handled = true;
            }
            return; // unhandled after a still click, so RightTapped still comes
        }
        var position = e.GetCurrentPoint(Host).Position;
        switch (_gesture)
        {
            case Gesture.Press when _dragged is not null && !CtrlDown():
                _viewModel.Select(_dragged.Node.Id); // a plain click on one of several selected narrows to it
                break;
            case Gesture.Connect when ConnectTarget(position) is { } end && end.Id != _connectFrom!.Id: // a click on a port is no drag
                ShowRefusal(_viewModel.Connect(_connectFrom!.Id, end.Id), end);
                break;
            case Gesture.DragNode:
                var node = _dragged!.Node;
                var (target, zone) = DropAt(position);
                var moving = DragIds();
                // Commit first: the views take their new place in this same tick and EndGesture then clears the
                // preview offset, so nothing is drawn in between (D-120).
                if (target is not null && zone != DropZone.None)
                    ShowRefusal(_viewModel.Drop(node.Id, target.Id, zone), target);
                else if (node.CardId is { } card && _viewModel.Find(card) is { } own && !own.Bounds.Contains(C(position)))
                    ShowRefusal(_viewModel.TakeOutOfCard(node.Id), own);
                else if (node.CardId is not null)
                    _viewModel.MoveToRow(node.Id, _viewModel.RowAt(node.Id, node.Bounds.CenterY + (position.Y - _pressContent.Y)));
                else
                    _viewModel.MoveBy(moving, position.X - _pressContent.X, position.Y - _pressContent.Y); // D-115
                break;
            case Gesture.Marquee:
                var rect = DazeControl.Core.Layout.Rect.FromCorners(C(_pressContent), C(position));
                if (rect.Width < DragThreshold && rect.Height < DragThreshold)
                {
                    if (!CtrlDown())
                        _viewModel.ClearSelection();
                }
                else
                {
                    _viewModel.SelectIn(rect, additive: CtrlDown());
                }
                break;
        }
        Root.ReleasePointerCapture(e.Pointer);
        EndGesture();
        e.Handled = true;
    }

    /// <summary>Clears the drag preview on the views it moved (captured at drag start: a commit may change the group).</summary>
    private void ResetDragged()
    {
        foreach (var view in _dragViews)
        {
            view.RenderTransform = null;
            XamlCanvas.SetZIndex(view, view.Node.Kind == CanvasNodeKind.Member ? 1 : 0); // as SyncNodes sets it
        }
        _dragViews = [];
    }

    /// <summary>
    /// What a drag carries: a member alone (it reorders in its card), else the dragged step or card and, when it is
    /// part of the selection, every other selected top-level step or card (D-115).
    /// </summary>
    private List<string> DragIds()
    {
        if (_dragged is null || _viewModel is null)
            return [];
        var node = _dragged.Node;
        if (node.CardId is not null || !node.IsSelected)
            return [node.Id];
        return [node.Id, .. _viewModel.SelectedIds.Where(id => id != node.Id && _viewModel.Find(id) is { CardId: null })];
    }

    /// <summary>The dragged views and, for a card, its members (separate siblings in NodeLayer).</summary>
    private IEnumerable<NodeView> DragGroup()
    {
        var ids = DragIds();
        foreach (var view in _views.Values)
        {
            if (ids.Contains(view.Node.Id) || (view.Node.CardId is { } card && ids.Contains(card)))
                yield return view;
        }
    }

    // ---- Ports and connect ----

    /// <summary>Ports on hover, on the card for a member (UX §5.3); always on the single selected node.</summary>
    private void ShowPorts(CanvasNodeViewModel? hovered)
    {
        if (_viewModel is null)
            return;
        hovered ??= _viewModel.SelectedIds.Count == 1 ? _viewModel.Find(_viewModel.SelectedIds.First()) : null;
        if (hovered?.CardId is { } card)
            hovered = _viewModel.Find(card);
        if (hovered is null || !_viewModel.CanEdit)
        {
            InPortDot.Visibility = OutPortDot.Visibility = Visibility.Collapsed;
            return;
        }
        Dot(InPortDot, _viewModel.InPortOf(hovered));
        Dot(OutPortDot, _viewModel.OutPortOf(hovered));
    }

    private static void Dot(Microsoft.UI.Xaml.Shapes.Ellipse dot, CorePoint at)
    {
        dot.Fill = Theme.Brush("CardBackgroundFillColorDefaultBrush");
        dot.Stroke = Theme.Selection;
        XamlCanvas.SetLeft(dot, at.X - dot.Width / 2);
        XamlCanvas.SetTop(dot, at.Y - dot.Height / 2);
        dot.Visibility = Visibility.Visible;
    }

    private CanvasNodeViewModel? ConnectTarget(Windows.Foundation.Point position) =>
        _viewModel?.ConnectTargetAt(C(position), _portRadius);

    private void PreviewConnect(Windows.Foundation.Point position)
    {
        var start = _viewModel!.OutPortOf(_connectFrom!);
        var curve = DazeControl.Core.Layout.EdgeCurve.Between(start, C(position), _viewModel.OutSide, _viewModel.InSide);
        var figure = new PathFigure { StartPoint = P(curve.Start), IsFilled = false };
        figure.Segments.Add(new BezierSegment { Point1 = P(curve.Control1), Point2 = P(curve.Control2), Point3 = P(curve.Tip) });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        ConnectPreview.Data = geometry;
        ConnectPreview.Stroke = Theme.Selection;
        ConnectPreview.Visibility = Visibility.Visible;

        var target = ConnectTarget(position);
        if (target is null || target.Id == _connectFrom!.Id)
        {
            DropTarget.Visibility = Visibility.Collapsed;
            ProtectedCursor = null;
            return;
        }
        var refused = _viewModel!.CanConnect(_connectFrom.Id, target.Id) is not (ConnectRefusal.None or ConnectRefusal.Exists);
        DropTarget.Stroke = refused ? Theme.Brush("SystemFillColorCriticalBrush") : Theme.Selection;
        DropTarget.StrokeDashArray = null;
        DropTarget.RadiusX = DropTarget.RadiusY = target.Kind == CanvasNodeKind.Card ? 14 : 30;
        DropTarget.Width = target.Width + 4;
        DropTarget.Height = (_views.TryGetValue(target.Id, out var view) && view.ActualHeight > 0 ? view.ActualHeight : target.Height) + 4;
        XamlCanvas.SetLeft(DropTarget, target.X - 2);
        XamlCanvas.SetTop(DropTarget, target.Y - 2);
        DropTarget.Visibility = Visibility.Visible;
        ProtectedCursor = refused ? InputSystemCursor.Create(InputSystemCursorShape.UniversalNo) : null;
    }

    /// <summary>The §5.7 #3/#4 teaching tip at <paramref name="at"/>; nothing for a plain no-op.</summary>
    private void ShowRefusal(ConnectRefusal refusal, CanvasNodeViewModel at)
    {
        if (FlowCanvasViewModel.Explain(refusal) is not { } why)
            return;
        if (!Shown)
            ShowTip(why, FallbackTarget);
        else if (_views.TryGetValue(at.Id, out var view))
            ShowTip(why, view);
    }

    // ---- Drag a pill onto another: run together / run after (UX §5.2) ----

    /// <summary>The pill or card under the pointer other than the dragged one (its own card counts only from outside).</summary>
    private (CanvasNodeViewModel? Target, DropZone Zone) DropAt(Windows.Foundation.Point position)
    {
        var dragged = _dragged?.Node;
        if (_viewModel is null || dragged is null || dragged.Kind == CanvasNodeKind.Card || DragIds().Count > 1)
            return (null, DropZone.None);
        var target = _viewModel.Nodes
            .Where(n => n.Id != dragged.Id && n.Id != dragged.CardId && !(n.CardId is not null && n.CardId == dragged.CardId) && n.Bounds.Contains(C(position)))
            .OrderBy(n => n.Kind == CanvasNodeKind.Card ? 1 : 0).FirstOrDefault();
        return target is null ? (null, DropZone.None) : (target, FlowCanvasViewModel.ZoneAt(target, C(position)));
    }

    private void PreviewDrop(Windows.Foundation.Point position)
    {
        var (target, zone) = DropAt(position);
        if (target is null || zone == DropZone.None)
        {
            DropTarget.Visibility = DropLabel.Visibility = Visibility.Collapsed;
            return;
        }
        ShowDropZone(target, zone);
    }

    /// <summary>The dashed zone outline and its label over <paramref name="target"/>: a step drag and a library drag (D-120).</summary>
    private void ShowDropZone(CanvasNodeViewModel target, DropZone zone)
    {
        var height = _views.TryGetValue(target.Id, out var view) && view.ActualHeight > 0 ? view.ActualHeight : target.Height;
        var x = zone == DropZone.RunAfter ? target.X + target.Width * 2 / 3 : target.X;
        var third = zone is DropZone.RunAfter or DropZone.RunBefore;
        DropTarget.Stroke = Theme.Selection;
        DropTarget.StrokeDashArray = Theme.Dashes(2, 2);
        DropTarget.RadiusX = DropTarget.RadiusY = target.Kind == CanvasNodeKind.Card ? 14 : 30;
        DropTarget.Width = (third ? target.Width / 3 : target.Width) + 4;
        DropTarget.Height = height + 4;
        XamlCanvas.SetLeft(DropTarget, x - 2);
        XamlCanvas.SetTop(DropTarget, target.Y - 2);
        DropTarget.Visibility = Visibility.Visible;
        DropLabelText.Text = zone switch { DropZone.RunAfter => "Run after", DropZone.RunBefore => "Run before", _ => "Drop to run together" };
        XamlCanvas.SetLeft(DropLabel, x);
        XamlCanvas.SetTop(DropLabel, target.Y - 28);
        DropLabel.Visibility = Visibility.Visible;
    }

    /// <summary>A right-drag gesture with the system drag rectangle for this window's DPI, in DIP (D-069).</summary>
    private RightDragPan NewRightPan()
    {
        var dpi = (uint)Math.Round((XamlRoot?.RasterizationScale ?? 1) * 96);
        var (width, height) = RightDragPan.DragRectDip(GetSystemMetricsForDpi(SmCxDrag, dpi), GetSystemMetricsForDpi(SmCyDrag, dpi), dpi);
        return new RightDragPan(width, height);
    }

    private const int SmCxDrag = 68, SmCyDrag = 69;

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetricsForDpi(int nIndex, uint dpi);

    private void EndGesture()
    {
        ConnectPreview.Visibility = DropTarget.Visibility = DropLabel.Visibility = Visibility.Collapsed;
        ProtectedCursor = null;
        _connectFrom = null;
        ResetDragged();
        _dragged = null;
        _rightPan?.Cancel();
        _gesture = Gesture.None;
        Marquee.Visibility = Visibility.Collapsed;
    }
}
