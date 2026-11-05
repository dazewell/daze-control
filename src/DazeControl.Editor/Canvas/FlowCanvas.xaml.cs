using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.ComponentModel;
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

/// <summary>
/// Draws a <see cref="FlowCanvasViewModel"/> and turns pointer/keyboard input into its calls (D-013). No logic
/// here that a test would want: layout, hit-testing, selection and edits live in the view model.
/// Pointer capture only, never OLE drag-drop inside the canvas (UX §5.8 #7).
/// </summary>
public sealed partial class FlowCanvas : UserControl
{
    private const double DragThreshold = 4;

    private readonly Dictionary<string, NodeView> _views = new(StringComparer.Ordinal);
    private readonly TextBlock _measurer = new();
    private FlowCanvasViewModel? _viewModel;
    private bool _fitPending;
    private bool _autoFit;
    private double _padX, _padY; // Wrapper's padding per side now (content DIP)
    private double _padZoom = 1; // the zoom that padding was set for
    private CanvasView? _pendingView; // asked of the ScrollViewer, not reported back by ViewChanged yet
    private int _viewSeq; // bumped by every view change of ours and every press: a queued retry checks it's still current

    private enum Gesture { None, Press, DragNode, Marquee, Pan, Connect, RightPan }

    private Gesture _gesture;
    private uint _pointerId;
    private Windows.Foundation.Point _pressContent;
    private Windows.Foundation.Point _pressScreen;
    private double _panStartX, _panStartY;
    private RightDragPan? _rightPan;
    private NodeView? _dragged;
    private CanvasNodeViewModel? _connectFrom;
    private CanvasEdgeViewModel? _controlsEdge;
    private const double EdgeControlsWidth = 56;
    private const double EdgeControlsHeight = 24;
    private const double EdgeControlsLift = 10;
    private bool _spaceDown;
    private double _portRadius = FlowCanvasViewModel.PortRadius;

    public FlowCanvas()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => RefreshAll();
        GotFocus += (_, _) => { _viewModel?.EnsureFocus(); UpdateFocusRing(); };
        LostFocus += (_, _) => { _spaceDown = false; UpdateFocusRing(); }; // Space released elsewhere never reaches OnKeyUp
        SizeChanged += (_, _) => { if (_fitPending) Fit(); };
        Scroller.SizeChanged += (_, e) =>
        {
            if (_fitPending || e.PreviousSize.Width <= 0)
                return;
            // New pan room for the new viewport: refit while the user hasn't moved the view, else the top left stays.
            DispatcherQueue.TryEnqueue(() => { if (_autoFit) Fit(); else ZoomAround(CurrentView.Zoom, 0, 0); });
        };
        // Pinch and touchpad zoom are the ScrollViewer's own (no ChangeView of ours): new pan room once they end,
        // keeping the middle of the window still.
        Scroller.DirectManipulationCompleted += (_, _) =>
        {
            _pendingView = null;
            if (Math.Abs(Scroller.ZoomFactor - _padZoom) > 1e-4)
                ZoomAround(Scroller.ZoomFactor, Scroller.ViewportWidth / 2, Scroller.ViewportHeight / 2);
        };
        Wrapper.PointerWheelChanged += OnWheel;
        // The whole viewport takes gestures (a small flow leaves blank canvas around Host); positions are in Host's
        // coordinates. Scroll bars handle their own presses, so they never start a marquee.
        Root.PointerPressed += OnPointerPressed;
        Root.PointerMoved += OnPointerMoved;
        Root.PointerReleased += OnPointerReleased;
        Root.PointerCaptureLost += (_, _) => EndGesture();
        Root.PointerExited += (_, _) =>
        {
            if (_gesture == Gesture.RightPan && _rightPan is { IsPanning: false })
                EndGesture(); // armed without capture: a release outside would never reach Root
            if (_gesture != Gesture.None)
                return;
            ShowPorts(null);
            if (_viewModel?.HoveredEdge is not null)
            {
                _viewModel.HoveredEdge = null;
                ProtectedCursor = null;
                DrawEdges();
            }
        };
        Root.RightTapped += OnRightTapped;
        Root.DoubleTapped += OnDoubleTapped;
        Root.AllowDrop = true; // palette drags (OLE between controls; never for gestures inside the canvas, UX §5.8 #7)
        Root.DragOver += OnPaletteDragOver;
        Root.Drop += OnPaletteDrop;
        Root.AddHandler(PointerWheelChangedEvent, new PointerEventHandler((_, _) => _autoFit = false), handledEventsToo: true);
    }

    public FlowCanvasViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (_viewModel == value)
                return;
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged -= OnViewModelChanged;
                _viewModel.Nodes.CollectionChanged -= OnNodesChanged;
                _viewModel.Noted -= OnNoted;
            }
            _viewModel = value;
            NodeLayer.Children.Clear();
            foreach (var view in _views.Values)
                view.Detach();
            _views.Clear();
            if (_viewModel is null)
                return;
            _viewModel.PropertyChanged += OnViewModelChanged;
            _viewModel.Nodes.CollectionChanged += OnNodesChanged;
            _viewModel.Noted += OnNoted;
            SyncNodes();
            DrawEdges();
            UpdateExtent();
            _fitPending = _autoFit = true;
            Fit();
        }
    }

    // ---- View commands ----

    /// <summary>Ctrl+0.</summary>
    public void Fit()
    {
        if (_viewModel is null || Scroller.ViewportWidth <= 0)
            return;
        _fitPending = false;
        ApplyView(_viewModel.FitView(Scroller.ViewportWidth, Scroller.ViewportHeight));
    }

    /// <summary>Ctrl+1.</summary>
    public void ZoomToActualSize()
    {
        _autoFit = false;
        ZoomAround(1, Scroller.ViewportWidth / 2, Scroller.ViewportHeight / 2);
    }

    public void ZoomBy(double factor)
    {
        _autoFit = false;
        ZoomAround(CurrentView.Zoom * factor, Scroller.ViewportWidth / 2, Scroller.ViewportHeight / 2);
    }

    private CanvasView CurrentView =>
        _pendingView ?? new CanvasView(Scroller.HorizontalOffset, Scroller.VerticalOffset, Scroller.ZoomFactor);

    /// <summary>Zooms keeping the flow point at viewport position (x, y) still (D-074).</summary>
    private void ZoomAround(double zoom, double x, double y)
    {
        if (_viewModel is null || Scroller.ViewportWidth <= 0)
            return;
        ApplyView(CanvasViewport.Reanchor(CurrentView, _padX, _padY, CanvasViewport.Clamp(zoom), x, y,
            _viewModel.ExtentWidth, _viewModel.ExtentHeight, Scroller.ViewportWidth, Scroller.ViewportHeight));
    }

    /// <summary>Sets the pan room for the view's zoom, then the view itself.</summary>
    private void ApplyView(CanvasView view)
    {
        var padX = CanvasViewport.Pad(Scroller.ViewportWidth, view.Zoom);
        var padY = CanvasViewport.Pad(Scroller.ViewportHeight, view.Zoom);
        _padZoom = view.Zoom;
        var repadded = padX != _padX || padY != _padY;
        if (repadded)
        {
            (_padX, _padY) = (padX, padY);
            Wrapper.Padding = new Thickness(padX, padY, padX, padY);
            Scroller.UpdateLayout(); // the new extent, so ChangeView doesn't clamp to the old one
        }
        _pendingView = view;
        var seq = ++_viewSeq;
        Scroller.ChangeView(view.OffsetX, view.OffsetY, (float)view.Zoom, disableAnimation: true);
        {
            // Inside a layout pass (SizeChanged, first open) UpdateLayout doesn't run and the view above may be clamped
            // to the old extent: ask again once layout is done, unless a newer view or a press came first.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (seq == _viewSeq && (Math.Abs(Scroller.ZoomFactor - view.Zoom) > 1e-4
                    || Math.Abs(Scroller.HorizontalOffset - view.OffsetX) > 0.5 || Math.Abs(Scroller.VerticalOffset - view.OffsetY) > 0.5))
                    Scroller.ChangeView(view.OffsetX, view.OffsetY, (float)view.Zoom, disableAnimation: true);
            });
        }
    }

    /// <summary>
    /// Mouse wheel and Ctrl+wheel zoom at the pointer, Shift+wheel scrolls sideways (the ScrollViewer would scroll it
    /// vertically); a touchpad and a tilt wheel stay the ScrollViewer's (D-074).
    /// </summary>
    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        if (_viewModel is null || e.Pointer.PointerDeviceType != PointerDeviceType.Mouse)
            return;
        var point = e.GetCurrentPoint(Scroller);
        if (point.Properties.IsHorizontalMouseWheel)
            return;
        _autoFit = false;
        var delta = point.Properties.MouseWheelDelta;
        var view = CurrentView;
        if (ShiftDown())
        {
            var x = Math.Clamp(view.OffsetX - delta * CanvasViewport.SideScrollPerNotch / 120, 0, Scroller.ScrollableWidth);
            _pendingView = view with { OffsetX = x };
            _viewSeq++;
            Scroller.ChangeView(x, null, null, disableAnimation: true);
        }
        else
        {
            ZoomAround(view.Zoom * CanvasViewport.WheelFactor(delta), point.Position.X, point.Position.Y);
        }
        e.Handled = true;
    }

    private void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_viewModel is null || e.IsIntermediate)
            return;
        _pendingView = null;
        var details = _viewModel.ShowDetails;
        _viewModel.Zoom = Scroller.ZoomFactor;
        if (details != _viewModel.ShowDetails)
        {
            // Hiding details shrinks the pills, which would re-fit above the threshold and flicker: fit once.
            _autoFit = false;
            RefreshAll();
        }
    }

    // ---- Sync from the view model ----

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(FlowCanvasViewModel.ExtentWidth) or nameof(FlowCanvasViewModel.ExtentHeight) or nameof(FlowCanvasViewModel.IsEmpty):
                UpdateExtent();
                // Measured pill sizes arrive after the first fit: keep fitting until the user moves the view.
                if (_autoFit)
                    DispatcherQueue.TryEnqueue(Fit);
                break;
            case nameof(FlowCanvasViewModel.FocusedId):
                UpdateFocusRing();
                AnnounceFocus();
                break;
            case nameof(FlowCanvasViewModel.SelectedIds) or nameof(FlowCanvasViewModel.CanEdit) or nameof(FlowCanvasViewModel.IsShowingRun):
                DrawEdges();
                if (_gesture == Gesture.None)
                    ShowPorts(null); // the dots follow the selection until the pointer hovers again
                break;
            case nameof(FlowCanvasViewModel.Arrangement):
                SyncNodes(); // kinds are final now (a node can turn into a card without a collection change)
                DrawEdges();
                UpdateFocusRing();
                if (_gesture == Gesture.None)
                    ShowPorts(null); // nodes moved (reorder, Tidy up): stale dots would not match OutPortAt
                break;
        }
    }

    private void OnNodesChanged(object? sender, NotifyCollectionChangedEventArgs e) => SyncNodes();

    private void UpdateExtent()
    {
        if (_viewModel is null)
            return;
        Host.Width = _viewModel.ExtentWidth;
        Host.Height = _viewModel.ExtentHeight;
        EmptyState.Visibility = _viewModel.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// One view per node; members draw above cards by ZIndex. Views are never moved within the layer: a removed and
    /// re-added element can miss its node's changes (X-004), which left pills drawn where they no longer were.
    /// </summary>
    private void SyncNodes()
    {
        if (_viewModel is null)
            return;
        var live = _viewModel.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _views.Keys.Where(id => !live.Contains(id)).ToList())
            RemoveView(id);
        foreach (var node in _viewModel.Nodes)
        {
            if (_views.TryGetValue(node.Id, out var view) && view.IsCard != (node.Kind == CanvasNodeKind.Card))
            {
                RemoveView(node.Id);
                view = null;
            }
            if (view is null)
            {
                view = node.Kind == CanvasNodeKind.Card ? new CardView(node) : new PillView(node);
                if (view is PillView pill)
                    pill.SizeChanged += (_, _) => Report(pill);
                _views[node.Id] = view;
                NodeLayer.Children.Add(view);
            }
            XamlCanvas.SetZIndex(view, node.Kind == CanvasNodeKind.Member ? 1 : 0);
            view.Refresh(_viewModel.ShowDetails); // places it: the layout may have moved it without a change event reaching it
        }
    }

    private void RemoveView(string id)
    {
        var view = _views[id];
        view.Detach();
        NodeLayer.Children.Remove(view);
        _views.Remove(id);
    }

    private void Report(PillView pill)
    {
        if (_viewModel is null || pill.ActualHeight <= 0)
            return;
        var width = pill.Node.Kind == CanvasNodeKind.Member ? pill.Node.Width : pill.DesiredPillWidth(_measurer);
        // Defer: re-layout from inside SizeChanged would reposition the element mid-pass.
        DispatcherQueue.TryEnqueue(() => _viewModel?.ReportSize(pill.Node.Id, width, pill.ActualHeight));
    }

    private void RefreshAll()
    {
        if (_viewModel is null)
            return;
        foreach (var view in _views.Values)
            view.Refresh(_viewModel.ShowDetails);
        DrawEdges();
    }

    private void DrawEdges()
    {
        if (_viewModel is null)
            return;
        var curves = new Dictionary<Microsoft.UI.Xaml.Shapes.Path, PathGeometry>
        {
            [NormalEdges] = new(), [MayFailEdges] = new(), [AlwaysEdges] = new(), [IfFailsEdges] = new(), [SelectedEdges] = new(),
            [NormalArrows] = new(), [IfFailsArrows] = new(), [SelectedArrows] = new(),
        };
        ChipLayer.Children.Clear();
        var controlled = EdgeWithControls();
        foreach (var edge in _viewModel.Edges)
        {
            var c = edge.Curve;
            // During a run an edge turns accent once its source finished; hover/selected is 2 px accent (UX §5.3).
            var accent = edge.IsSelected || edge.IsHovered || edge.IsLit;
            var path = accent ? SelectedEdges : edge.Style switch
            {
                EdgeStyle.IntoMayFail => MayFailEdges,
                EdgeStyle.IfFails => IfFailsEdges,
                EdgeStyle.Always => AlwaysEdges,
                _ => NormalEdges,
            };
            var figure = new PathFigure { StartPoint = P(c.Start), IsClosed = false, IsFilled = false };
            if (c.IsOrthogonal)
            {
                // Orthogonal with rounded corners: across a row break (D-060), or Lines › Right angles (D-067).
                foreach (var (lineTo, corner, curveTo) in c.RoundedSegments())
                {
                    figure.Segments.Add(new LineSegment { Point = P(lineTo) });
                    if (corner is { } k)
                        figure.Segments.Add(new QuadraticBezierSegment { Point1 = P(k), Point2 = P(curveTo) });
                }
            }
            else
            {
                figure.Segments.Add(new BezierSegment { Point1 = P(c.Control1), Point2 = P(c.Control2), Point3 = P(c.End) });
            }
            curves[path].Figures.Add(figure);

            var arrows = accent ? SelectedArrows : edge.Style == EdgeStyle.IfFails ? IfFailsArrows : NormalArrows;
            var arrow = new PathFigure { StartPoint = P(c.Tip), IsClosed = true, IsFilled = true };
            arrow.Segments.Add(new LineSegment { Point = new(c.End.X, c.End.Y - 3.5) });
            arrow.Segments.Add(new LineSegment { Point = new(c.End.X, c.End.Y + 3.5) });
            curves[arrows].Figures.Add(arrow);

            if (edge.Chip is { } chip)
                ChipLayer.Children.Add(Chip(chip, c.Midpoint, edge.Style == EdgeStyle.IfFails));
        }
        ShowEdgeControls(controlled);
        SelectedEdges.Stroke = SelectedArrows.Fill = Theme.Selection;
        foreach (var (path, geometry) in curves)
            path.Data = geometry;
    }

    /// <summary>The edge that gets "+"/"×" at its midpoint: the hovered one, else the one edge picked by itself.</summary>
    private CanvasEdgeViewModel? EdgeWithControls()
    {
        if (_viewModel is not { CanEdit: true, IsShowingRun: false } vm)
            return null;
        return vm.HoveredEdge ?? (vm.SelectedEdges is [var only] ? only : null);
    }

    /// <summary>"+" inserts a step on the edge, "×" removes it (UX §5.3).</summary>
    private void ShowEdgeControls(CanvasEdgeViewModel? edge)
    {
        _controlsEdge = edge;
        if (edge is null)
        {
            EdgeControls.Visibility = Visibility.Collapsed;
            return;
        }
        var mid = edge.Curve.Midpoint;
        if (edge.Curve.IsWrapped)
        {
            // The row gap is too low to lift them above the lane (D-060): centre them on the lane, right of the chip.
            var lane = edge.Curve.Corners;
            XamlCanvas.SetLeft(EdgeControls, (mid.X + lane[2].X) / 2 - EdgeControlsWidth / 2);
            XamlCanvas.SetTop(EdgeControls, mid.Y - EdgeControlsHeight / 2);
            EdgeControls.Visibility = Visibility.Visible;
            return;
        }
        XamlCanvas.SetLeft(EdgeControls, mid.X - EdgeControlsWidth / 2);
        // Above the line, so a click on the line itself still selects the edge.
        XamlCanvas.SetTop(EdgeControls, mid.Y - EdgeControlsLift - EdgeControlsHeight);
        EdgeControls.Visibility = Visibility.Visible;
    }

    private void OnInsertOnEdge(object sender, RoutedEventArgs e)
    {
        if (_controlsEdge is { } edge)
            ShowQuickAdd(P(edge.Curve.Midpoint), null, edge);
    }

    private void OnRemoveEdge(object sender, RoutedEventArgs e)
    {
        if (_controlsEdge is { } edge)
            _viewModel?.Disconnect(edge);
    }

    /// <summary>Hover follows the pointer within the 12-DIP band; over the "+"/"×" pair it stays on their edge.</summary>
    private void HoverEdge(Windows.Foundation.Point position)
    {
        if (_viewModel is null)
            return;
        CanvasEdgeViewModel? edge;
        if (EdgeControls.Visibility == Visibility.Visible && _controlsEdge is { } shown && OverEdgeControls(position))
            edge = shown;
        else
            edge = _viewModel.HitTest(C(position)) is null ? _viewModel.HitTestEdge(C(position)) : null;
        if (ReferenceEquals(edge, _viewModel.HoveredEdge))
            return;
        _viewModel.HoveredEdge = edge;
        ProtectedCursor = edge is null ? null : InputSystemCursor.Create(InputSystemCursorShape.Hand);
        DrawEdges();
    }

    private bool OverEdgeControls(Windows.Foundation.Point position)
    {
        double left = XamlCanvas.GetLeft(EdgeControls), top = XamlCanvas.GetTop(EdgeControls);
        // Includes the gap down to the line, so moving from the line up to the pair keeps the hover.
        return position.X >= left - 4 && position.X <= left + EdgeControlsWidth + 4 && position.Y >= top - 4 && position.Y <= top + EdgeControlsHeight + EdgeControlsLift + 4;
    }

    private static Border Chip(string text, CorePoint at, bool critical)
    {
        var chip = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 1, 8, 2),
            BorderThickness = new Thickness(1),
            Background = Theme.Brush("SolidBackgroundFillColorTertiaryBrush"), // opaque: the edge runs under it
            BorderBrush = Theme.Brush(critical ? "SystemFillColorCriticalBrush" : "CardStrokeColorDefaultBrush"),
            Child = new TextBlock { Text = text, FontSize = 11, Foreground = Theme.Brush(critical ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush") },
        };
        chip.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        XamlCanvas.SetLeft(chip, at.X - chip.DesiredSize.Width / 2);
        XamlCanvas.SetTop(chip, at.Y - chip.DesiredSize.Height / 2);
        return chip;
    }

    private static Windows.Foundation.Point P(CorePoint p) => new(p.X, p.Y);

    private static CorePoint C(Windows.Foundation.Point p) => new(p.X, p.Y);

    /// <summary>Only for keyboard focus, so a mouse click doesn't draw a ring too (system focus visual rule).</summary>
    private void UpdateFocusRing()
    {
        if (_viewModel?.FocusedId is { } id && FocusState == FocusState.Keyboard && _viewModel.Find(id) is { } node)
        {
            var radius = node.Kind == CanvasNodeKind.Card ? 15 : 31;
            FocusRing.RadiusX = FocusRing.RadiusY = radius;
            FocusRing.Width = node.Width + 6;
            FocusRing.Height = (_views.TryGetValue(id, out var view) && view.ActualHeight > 0 ? view.ActualHeight : node.Height) + 6;
            XamlCanvas.SetLeft(FocusRing, node.X - 3);
            XamlCanvas.SetTop(FocusRing, node.Y - 3);
            FocusRing.Visibility = Visibility.Visible;
        }
        else
        {
            FocusRing.Visibility = Visibility.Collapsed;
        }
    }

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
                    foreach (var view in DragGroup())
                        XamlCanvas.SetZIndex(view, view == _dragged ? 1 : 2);
                }
                break;
            case Gesture.DragNode:
                foreach (var view in DragGroup())
                    view.RenderTransform = new TranslateTransform { X = dx, Y = dy };
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
                ResetDragged();
                if (target is not null && zone != DropZone.None)
                    ShowRefusal(_viewModel.Drop(node.Id, target.Id, zone), target);
                else if (node.CardId is { } card && _viewModel.Find(card) is { } own && !own.Bounds.Contains(C(position)))
                    ShowRefusal(_viewModel.TakeOutOfCard(node.Id), own);
                else
                    _viewModel.MoveToRow(node.Id, _viewModel.RowAt(node.Id, node.Bounds.CenterY + (position.Y - _pressContent.Y)));
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

    private void ResetDragged()
    {
        foreach (var view in DragGroup())
        {
            view.RenderTransform = null;
            XamlCanvas.SetZIndex(view, 0);
        }
    }

    /// <summary>The dragged view and, for a card, its members (separate siblings in NodeLayer).</summary>
    private IEnumerable<NodeView> DragGroup()
    {
        if (_dragged is null)
            yield break;
        yield return _dragged;
        foreach (var view in _views.Values)
        {
            if (view.Node.CardId == _dragged.Node.Id)
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
        Dot(InPortDot, FlowCanvasViewModel.InPort(hovered));
        Dot(OutPortDot, FlowCanvasViewModel.OutPort(hovered));
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
        var start = FlowCanvasViewModel.OutPort(_connectFrom!);
        var curve = DazeControl.Core.Layout.EdgeCurve.Between(start, C(position));
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

    /// <summary>Where tips and the quick-add search open while the canvas is hidden (the Outline view).</summary>
    public FrameworkElement? FallbackTarget { get; set; }

    private bool Shown => Visibility == Visibility.Visible && ActualWidth > 0;

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
        var position = at ?? (vm.FocusedId is { } id && vm.Find(id) is { } node ? P(FlowCanvasViewModel.OutPort(node)) : default);
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

    // ---- Drag a pill onto another: run together / run after (UX §5.2) ----

    /// <summary>The pill or card under the pointer other than the dragged one (its own card counts only from outside).</summary>
    private (CanvasNodeViewModel? Target, DropZone Zone) DropAt(Windows.Foundation.Point position)
    {
        var dragged = _dragged?.Node;
        if (_viewModel is null || dragged is null || dragged.Kind == CanvasNodeKind.Card)
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

    // ---- Context menus (right-click, long-press) ----

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

    private void BringIntoView(CanvasNodeViewModel node)
    {
        if (_views.TryGetValue(node.Id, out var view))
            view.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true });
    }
}
