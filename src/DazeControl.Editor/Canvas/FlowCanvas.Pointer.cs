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
        _pressedSinceGlide = true;
        var props = point.Properties;
        Focus(FocusState.Pointer);
        UpdateFocusRing(); // the ring is for keyboard focus only
        _autoFit = false;
        _pointerId = e.Pointer.PointerId;
        _pressContent = point.Position;
        _pressScreen = e.GetCurrentPoint(this).Position;
        _panStart = _camera;
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
            _gestureProbe.Begin("connect");
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
            return; // one-finger pan: the camera's touch manipulation (FlowCanvas.Camera.cs)
        }
        else if (props.IsLeftButtonPressed)
        {
            _gesture = Gesture.Marquee; // mouse and pen only (UX §5.8 #4)
            _viewModel.BeginMarquee();
            _gestureProbe.Begin("marquee");
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
        _gestureScreen = e.GetCurrentPoint(Viewport).Position;
        double dx = position.X - _pressContent.X, dy = position.Y - _pressContent.Y;
        switch (_gesture)
        {
            case Gesture.DragNode or Gesture.Connect or Gesture.Marquee:
                // Coalesced: a mouse can report several moves per frame; only the latest is drawn (D-119 b).
                _pendingMove = position;
                OnFrames(true);
                UpdateEdgePan();
                break;
            case Gesture.RightPan:
                var started = _rightPan!.IsPanning;
                if (!_rightPan.Move(screen.X, screen.Y))
                    break;
                if (!started)
                    Root.CapturePointer(e.Pointer);
                ApplyCamera(_panStart.PanBy(screen.X - _pressScreen.X, screen.Y - _pressScreen.Y));
                break;
            case Gesture.Pan:
                ApplyCamera(_panStart.PanBy(screen.X - _pressScreen.X, screen.Y - _pressScreen.Y));
                break;
            case Gesture.Press when Math.Abs(dx) > DragThreshold || Math.Abs(dy) > DragThreshold:
                if (_dragged is not null && _viewModel.CanEdit)
                {
                    _gesture = Gesture.DragNode;
                    if (_animated.Count > 0)
                        FinishMotion(); // grabbed mid-glide: direct manipulation is 1:1, from where it is laid out
                    _gestureProbe.Begin("drag");
                    _dragViews = [.. DragGroup()];
                    foreach (var view in _dragViews)
                        XamlCanvas.SetZIndex(view, view.Node.CardId is null ? 1 : 2); // members above their card
                    // A free move previews what the drop commits: lines between steps that stay put are drawn once
                    // now, from the frozen arrangement (a first drag drops wrap lanes here, not on release).
                    if (_dragged.Node.CardId is null && _viewModel.BeginMove(DragIds()) is not null)
                        DrawEdges(); // the scene holds the preview (T21.6)
                    _pendingMove = position;
                    OnFrames(true);
                    UpdateEdgePan();
                }
                break;
        }
    }

    /// <summary>Asks for the per-frame update for a gesture, or stops asking (nothing runs per frame while idle).</summary>
    private void OnFrames(bool on)
    {
        _gestureFrames = on;
        UpdateFrames();
    }

    /// <summary>
    /// Auto-pan (T21.9): runs on the frame clock while the gesture's pointer is in the viewport's edge band, stops when
    /// it leaves the band, the camera can't go further, or the gesture ends (idle = zero).
    /// </summary>
    private void UpdateEdgePan()
    {
        var velocity = Camera.EdgeVelocity(_gestureScreen.X, _gestureScreen.Y, ViewportWidth, ViewportHeight);
        SetEdgeFrames(_gesture is Gesture.DragNode or Gesture.Connect or Gesture.Marquee && velocity != (0, 0));
    }

    private void SetEdgeFrames(bool on)
    {
        if (on == _edgeFrames)
            return;
        _edgeFrames = on;
        _edgeLast = null; // the first frame only takes the time: a pan never covers the wait before it
        UpdateFrames();
    }

    /// <summary>
    /// One auto-pan frame: the camera pans by speed × frame time, and the pointer, still on screen, is now over another
    /// world point, so the dragged steps, the connect line or the marquee follow it this same frame.
    /// </summary>
    private void EdgeFrame(TimeSpan now)
    {
        var last = _edgeLast;
        _edgeLast = now;
        if (last is not { } before || now <= before)
            return;
        var velocity = Camera.EdgeVelocity(_gestureScreen.X, _gestureScreen.Y, ViewportWidth, ViewportHeight);
        var was = _camera;
        ApplyCamera(_camera.EdgePan(velocity, (now - before).TotalSeconds));
        if (_camera == was)
        {
            SetEdgeFrames(false); // out of the band, or at the end of the pan room: the next pointer move asks again
            return;
        }
        var world = _camera.ToWorld(C(_gestureScreen));
        _pendingMove = new Windows.Foundation.Point(world.X, world.Y);
    }

    /// <summary>Escape mid-gesture: nothing is committed, a marquee gives back the selection it started from.</summary>
    private void CancelGesture()
    {
        Root.ReleasePointerCaptures();
        EndGesture();
    }

    /// <summary>Draws the latest pointer position of a drag, connect or marquee (from <see cref="OnFrame"/>).</summary>
    private void GestureFrame(Windows.Foundation.Point position)
    {
        switch (_gesture)
        {
            case Gesture.DragNode:
                DragFrame(position);
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
                if (rect.Width >= DragThreshold || rect.Height >= DragThreshold)
                    _viewModel?.MarqueeTo(rect, additive: CtrlDown()); // the selection shows live (T21.9)
                break;
        }
    }

    /// <summary>
    /// One drag frame: a free move shows the offset MoveBy will commit (snapped lead, padding clamp) with its lines,
    /// chips, arrowheads, ports, focus ring and "+/×" (D-120); a member reorders in its card, so it follows the pointer as is.
    /// </summary>
    private void DragFrame(Windows.Foundation.Point position)
    {
        double dx = position.X - _pressContent.X, dy = position.Y - _pressContent.Y;
        if (_viewModel?.Scene is not { Drag: { } preview } scene)
        {
            Translate(new CorePoint(dx, dy));
        }
        else
        {
            var offset = preview.Offset(dx, dy);
            if (scene.DragTo(offset)) // the snap holds most frames still: nothing to redraw
            {
                Translate(offset);
                DrawLiveEdges();
                ShowEdgeControls(EdgeWithControls());
                if (_dragged is not null && (InPortDot.Visibility == Visibility.Visible || OutPortDot.Visibility == Visibility.Visible))
                    ShowPorts(_dragged.Node);
                UpdateFocusRing();
            }
        }
        PreviewDrop(position);
    }

    private void Translate(CorePoint offset)
    {
        var shift = new System.Numerics.Vector3((float)offset.X, (float)offset.Y, 0);
        foreach (var view in _dragViews)
            view.Translation = shift;
    }

    /// <summary>Where a node is drawn now: mid-glide, its live rect during a drag, else its committed bounds.</summary>
    private DazeControl.Core.Layout.Rect BoundsOf(CanvasNodeViewModel node) =>
        _viewModel?.Scene is { } scene && scene.Find(node.Id) is not null ? scene.ShownBounds(node.Id) : node.Bounds;

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
                _viewModel.ReleaseMove(); // a layout during the drag left the dragged steps alone; the commit may move them
                // Commit first: the views take their new place in this same tick and EndGesture then clears the
                // preview offset and redraws the lines as committed, so nothing is drawn in between (D-120).
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
                    if (CtrlDown())
                    {
                        _viewModel.CancelMarquee(); // a Ctrl+click on blank canvas changes nothing
                    }
                    else
                    {
                        _viewModel.EndMarquee();
                        _viewModel.ClearSelection();
                    }
                }
                else
                {
                    _viewModel.MarqueeTo(rect, additive: CtrlDown());
                    _viewModel.EndMarquee();
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
            if (!_animated.Contains(view.Node.Id)) // a dropped step the layout moved on glides from where it was dropped
                view.Translation = System.Numerics.Vector3.Zero;
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
        _portsNode = hovered;
        if (hovered is null || !_viewModel.CanEdit)
        {
            InPortDot.Visibility = OutPortDot.Visibility = Visibility.Collapsed;
            return;
        }
        var bounds = BoundsOf(hovered); // follows a live drag
        Dot(InPortDot, FlowPlacement.Port(bounds, _viewModel.InSide));
        Dot(OutPortDot, FlowPlacement.Port(bounds, _viewModel.OutSide));
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
        var from = _connectFrom!;
        var start = _viewModel!.OutPortOf(from);
        var target = ConnectTarget(position);
        var refused = target is not null && target.Id != from.Id
            && _viewModel.CanConnect(from.Id, target.Id) is not (ConnectRefusal.None or ConnectRefusal.Exists);
        // Over a step it can join, the line snaps to that step's in-port: it shows the line the release makes (T21.9).
        var end = target is not null && target.Id != from.Id && !refused
            ? FlowPlacement.Port(BoundsOf(target), _viewModel.InSide)
            : C(position);
        var curve = DazeControl.Core.Layout.EdgeCurve.Between(start, end, _viewModel.OutSide, _viewModel.InSide);
        var figure = new PathFigure { StartPoint = P(curve.Start), IsFilled = false };
        figure.Segments.Add(new BezierSegment { Point1 = P(curve.Control1), Point2 = P(curve.Control2), Point3 = P(curve.Tip) });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        ConnectPreview.Data = geometry;
        ConnectPreview.Stroke = Theme.Selection;
        ConnectPreview.Visibility = Visibility.Visible;

        if (target is null || target.Id == _connectFrom!.Id)
        {
            DropTarget.Visibility = Visibility.Collapsed;
            ProtectedCursor = null;
            return;
        }
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
        _edgeFrames = false;
        _edgeLast = null;
        OnFrames(false);
        _pendingMove = null;
        if (_gesture == Gesture.Marquee)
            _viewModel?.CancelMarquee(); // capture lost or Escape; a release already ended it
        if (_gesture is Gesture.DragNode or Gesture.Connect or Gesture.Marquee)
            _gestureProbe.End();
        if (_viewModel is not null && (_viewModel.Scene.IsDragging || _gesture == Gesture.DragNode))
        {
            // After the commit (or none: no move, a refused drop, capture lost) every line is drawn as it now is.
            _viewModel.EndMove();
            ClearLiveEdges();
            DrawEdges();
            UpdateFocusRing();
            if (_dragged is not null && _viewModel?.Find(_dragged.Node.Id) is { } node
                && (InPortDot.Visibility == Visibility.Visible || OutPortDot.Visibility == Visibility.Visible))
                ShowPorts(node);
        }
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
