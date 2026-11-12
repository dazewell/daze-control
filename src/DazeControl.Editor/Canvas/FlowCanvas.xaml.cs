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
    private List<NodeView> _dragViews = []; // moved by the drag preview
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

    /// <summary>Where tips and the quick-add search open while the canvas is hidden (the Outline view).</summary>
    public FrameworkElement? FallbackTarget { get; set; }

    private bool Shown => Visibility == Visibility.Visible && ActualWidth > 0;
}
