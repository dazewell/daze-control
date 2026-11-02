using System.Collections.Specialized;
using System.ComponentModel;
using DazeControl.Editor.ViewModels.Canvas;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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

    private enum Gesture { None, Press, DragNode, Marquee, Pan }

    private Gesture _gesture;
    private uint _pointerId;
    private Windows.Foundation.Point _pressContent;
    private Windows.Foundation.Point _pressScreen;
    private double _panStartX, _panStartY;
    private NodeView? _dragged;
    private bool _spaceDown;

    public FlowCanvas()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => RefreshAll();
        GotFocus += (_, _) => { _viewModel?.EnsureFocus(); UpdateFocusRing(); };
        LostFocus += (_, _) => { _spaceDown = false; UpdateFocusRing(); }; // Space released elsewhere never reaches OnKeyUp
        SizeChanged += (_, _) => { if (_fitPending) Fit(); };
        // The whole viewport takes gestures (a small flow leaves blank canvas around Host); positions are in Host's
        // coordinates. Scroll bars handle their own presses, so they never start a marquee.
        Root.PointerPressed += OnPointerPressed;
        Root.PointerMoved += OnPointerMoved;
        Root.PointerReleased += OnPointerReleased;
        Root.PointerCaptureLost += (_, _) => EndGesture();
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
            }
            _viewModel = value;
            NodeLayer.Children.Clear();
            _views.Clear();
            if (_viewModel is null)
                return;
            _viewModel.PropertyChanged += OnViewModelChanged;
            _viewModel.Nodes.CollectionChanged += OnNodesChanged;
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
        var (zoom, x, y) = _viewModel.FitView(Scroller.ViewportWidth, Scroller.ViewportHeight);
        Scroller.ChangeView(x, y, (float)zoom, disableAnimation: true);
    }

    /// <summary>Ctrl+1.</summary>
    public void ZoomToActualSize()
    {
        _autoFit = false;
        Scroller.ChangeView(null, null, 1f);
    }

    public void ZoomBy(double factor)
    {
        _autoFit = false;
        Scroller.ChangeView(null, null, (float)Math.Clamp(Scroller.ZoomFactor * factor, FlowCanvasViewModel.MinZoom, FlowCanvasViewModel.MaxZoom));
    }

    private void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_viewModel is null || e.IsIntermediate)
            return;
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
            case nameof(FlowCanvasViewModel.SelectedIds):
                DrawEdges();
                break;
            case nameof(FlowCanvasViewModel.Arrangement):
                SyncNodes(); // kinds are final now (a node can turn into a card without a collection change)
                DrawEdges();
                UpdateFocusRing();
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

    /// <summary>Cards first so members draw above them (the view model's order).</summary>
    private void SyncNodes()
    {
        if (_viewModel is null)
            return;
        var live = _viewModel.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _views.Keys.Where(id => !live.Contains(id)).ToList())
        {
            NodeLayer.Children.Remove(_views[id]);
            _views.Remove(id);
        }
        for (var i = 0; i < _viewModel.Nodes.Count; i++)
        {
            var node = _viewModel.Nodes[i];
            if (_views.TryGetValue(node.Id, out var view) && view.IsCard != (node.Kind == CanvasNodeKind.Card))
            {
                NodeLayer.Children.Remove(view);
                _views.Remove(node.Id);
                view = null;
            }
            if (view is null)
            {
                view = node.Kind == CanvasNodeKind.Card ? new CardView(node) : new PillView(node);
                if (view is PillView pill)
                    pill.SizeChanged += (_, _) => Report(pill);
                view.Refresh(_viewModel.ShowDetails);
                _views[node.Id] = view;
            }
            var at = NodeLayer.Children.IndexOf(view);
            if (at != i)
            {
                if (at >= 0)
                    NodeLayer.Children.RemoveAt(at);
                NodeLayer.Children.Insert(Math.Min(i, NodeLayer.Children.Count), view);
            }
        }
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
        foreach (var edge in _viewModel.Edges)
        {
            var c = edge.Curve;
            var path = edge.IsSelected ? SelectedEdges : edge.Style switch
            {
                EdgeStyle.IntoMayFail => MayFailEdges,
                EdgeStyle.IfFails => IfFailsEdges,
                EdgeStyle.Always => AlwaysEdges,
                _ => NormalEdges,
            };
            var figure = new PathFigure { StartPoint = P(c.Start), IsClosed = false, IsFilled = false };
            figure.Segments.Add(new BezierSegment { Point1 = P(c.Control1), Point2 = P(c.Control2), Point3 = P(c.End) });
            curves[path].Figures.Add(figure);

            var arrows = edge.IsSelected ? SelectedArrows : edge.Style == EdgeStyle.IfFails ? IfFailsArrows : NormalArrows;
            var arrow = new PathFigure { StartPoint = P(c.Tip), IsClosed = true, IsFilled = true };
            arrow.Segments.Add(new LineSegment { Point = new(c.End.X, c.End.Y - 3.5) });
            arrow.Segments.Add(new LineSegment { Point = new(c.End.X, c.End.Y + 3.5) });
            curves[arrows].Figures.Add(arrow);

            if (edge.Chip is { } chip)
                ChipLayer.Children.Add(Chip(chip, c.Midpoint, edge.Style == EdgeStyle.IfFails));
        }
        SelectedEdges.Stroke = SelectedArrows.Fill = Theme.Selection;
        foreach (var (path, geometry) in curves)
            path.Data = geometry;
    }

    private static Border Chip(string text, CorePoint at, bool critical)
    {
        var chip = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 1, 8, 2),
            BorderThickness = new Thickness(1),
            Background = Theme.Brush("CardBackgroundFillColorDefaultBrush"),
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

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_viewModel is null || _gesture != Gesture.None)
            return;
        var point = e.GetCurrentPoint(Host);
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

        if (props.IsMiddleButtonPressed || (_spaceDown && props.IsLeftButtonPressed))
        {
            _gesture = Gesture.Pan;
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
        if (_viewModel is null || _gesture == Gesture.None || e.Pointer.PointerId != _pointerId)
            return;
        var position = e.GetCurrentPoint(Host).Position;
        var screen = e.GetCurrentPoint(this).Position;
        double dx = position.X - _pressContent.X, dy = position.Y - _pressContent.Y;
        switch (_gesture)
        {
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
        var position = e.GetCurrentPoint(Host).Position;
        switch (_gesture)
        {
            case Gesture.Press when _dragged is not null && !CtrlDown():
                _viewModel.Select(_dragged.Node.Id); // a plain click on one of several selected narrows to it
                break;
            case Gesture.DragNode:
                var id = _dragged!.Node.Id;
                var row = _viewModel.RowAt(id, _dragged.Node.Bounds.CenterY + (position.Y - _pressContent.Y));
                ResetDragged();
                _viewModel.MoveToRow(id, row);
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

    private void EndGesture()
    {
        ResetDragged();
        _dragged = null;
        _gesture = Gesture.None;
        Marquee.Visibility = Visibility.Collapsed;
    }

    // ---- Keyboard (UX §5.2): one tab stop, roving focus ----

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (_viewModel is null)
            return;
        var ctrl = CtrlDown();
        var alt = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(CoreVirtualKeyStates.Down);
        e.Handled = true;
        _autoFit = false;
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

    private void BringIntoView(CanvasNodeViewModel node)
    {
        if (_views.TryGetValue(node.Id, out var view))
            view.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true });
    }
}
