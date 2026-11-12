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

    private enum Gesture { None, Press, DragNode, Marquee, Pan, Connect, RightPan }

    private Gesture _gesture;
    private uint _pointerId;
    private Windows.Foundation.Point _pressContent;
    private Windows.Foundation.Point _pressScreen;
    private RightDragPan? _rightPan;
    private NodeView? _dragged;
    private List<NodeView> _dragViews = []; // moved by the drag preview
    private readonly Dictionary<(string, string), Border> _chips = []; // static set's chips, kept per edge (T21.6)
    private readonly Dictionary<(string, string), Border> _liveChips = [];
    private int _drawnEdgeVersion = -1; // the view model's EdgeVersion the static set was last drawn for
    private Windows.Foundation.Point? _pendingMove; // the latest pointer position, drawn on the next frame
    private bool _onFrames; // CompositionTarget.Rendering subscribed (for a gesture or a glide only: idle = zero)
    private bool _gestureFrames; // a drag, connect or marquee wants frames
    private bool _edgeFrames; // the pointer of a drag, connect or marquee is in the edge band: auto-pan (T21.9)
    private TimeSpan? _edgeLast; // the frame time auto-pan last panned at
    private Windows.Foundation.Point _gestureScreen; // the gesture's pointer in Viewport (screen) DIP
    private CanvasNodeViewModel? _portsNode; // whose dots are shown (they follow a glide)
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
        Viewport.SizeChanged += OnViewportSizeChanged;
        Root.PointerWheelChanged += OnWheel;
        // Touch pans and pinches through manipulations (mouse and pen ones are completed at once: their gestures are
        // the pointer handlers').
        Root.ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY | ManipulationModes.Scale
            | ManipulationModes.TranslateInertia;
        Root.ManipulationStarted += OnManipulationStarted;
        Root.ManipulationDelta += OnManipulationDelta;
        Root.ManipulationInertiaStarting += OnManipulationInertiaStarting;
        // The whole viewport takes gestures (a small flow leaves blank canvas around Host); positions are in Host's
        // coordinates, through the camera transform.
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
        Root.DragLeave += OnPaletteDragLeave;
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
                _viewModel.OriginShifted -= OnOriginShifted;
                _viewModel.Motion = null;
                if (_motionFrames || _animated.Count > 0)
                    FinishMotion();
            }
            _viewModel = value;
            _measuredSizes.Clear(); // the old flow's pills
            ForgetChips();
            NodeLayer.Children.Clear();
            foreach (var view in _views.Values)
                view.Detach();
            _views.Clear();
            if (_viewModel is null)
                return;
            _viewModel.PropertyChanged += OnViewModelChanged;
            _viewModel.Nodes.CollectionChanged += OnNodesChanged;
            _viewModel.Noted += OnNoted;
            _viewModel.OriginShifted += OnOriginShifted;
            // Layout glides only while the canvas is on screen and Windows "Animation effects" is on (UX §5.3).
            _viewModel.Motion = () => SceneMotion.For(Shown && Motion.Enabled);
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
                    DispatcherQueue.TryEnqueue(() => { if (_autoFit) Fit(); }); // a pan or drop since then wins
                break;
            case nameof(FlowCanvasViewModel.FocusedId):
                UpdateFocusRing();
                AnnounceFocus();
                break;
            case nameof(FlowCanvasViewModel.SelectedIds) or nameof(FlowCanvasViewModel.CanEdit) or nameof(FlowCanvasViewModel.IsShowingRun):
                RedrawEdges();
                if (_gesture == Gesture.None)
                    ShowPorts(null); // the dots follow the selection until the pointer hovers again
                break;
            case nameof(FlowCanvasViewModel.Arrangement):
                SyncNodes(_viewModel?.LastDiff); // kinds are final now (a node can turn into a card without a collection change)
                BeginMotion(); // before the lines: the gliding ones go to the live set (T21.8)
                RedrawEdges();
                UpdateFocusRing();
                if (_gesture == Gesture.None)
                    ShowPorts(null); // nodes moved (reorder, Tidy up): stale dots would not match OutPortAt
                break;
        }
    }

    // Mid-Sync: only add or drop views here; the Arrangement raised at its end refreshes what its diff names.
    private void OnNodesChanged(object? sender, NotifyCollectionChangedEventArgs e) => SyncNodes(SceneDiff.Empty);

    private void UpdateExtent()
    {
        if (_viewModel is null)
            return;
        Host.Width = _viewModel.ExtentWidth;
        Host.Height = _viewModel.ExtentHeight;
        if (!_autoFit && !_fitPending)
            ApplyCamera(_camera); // fewer steps: keep 48 DIP of them on screen, as the scroll extent used to
        EmptyState.Visibility = _viewModel.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// One view per node; members draw above cards by ZIndex. Views are never moved within the layer: a removed and
    /// re-added element can miss its node's changes (X-004), which left pills drawn where they no longer were.
    /// With a re-layout's <paramref name="diff"/> (T21.6) only what it names is touched: new boxes and changed content
    /// refresh, moved or resized boxes are placed, the rest are left alone (their own node events already updated them);
    /// without one (a new view model) every view refreshes.
    /// </summary>
    private void SyncNodes(SceneDiff? diff = null)
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
            if (diff is null)
                view.Refresh(_viewModel.ShowDetails);
        }
        if (diff is null)
            return;
        foreach (var id in diff.Added.Concat(diff.ContentChanged))
            if (_views.TryGetValue(id, out var view))
                view.Refresh(_viewModel.ShowDetails); // also places it
        foreach (var id in diff.Moved.Concat(diff.Resized))
            if (_views.TryGetValue(id, out var view))
                view.Place();
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
        // Defer: re-layout from inside SizeChanged would reposition the element mid-pass. Every pill measured in this
        // pass goes in one re-layout (F-036: a pasted 95 steps made 95 re-layouts, each redrawing every line).
        var first = _measuredSizes.Count == 0;
        _measuredSizes[pill.Node.Id] = (width, pill.ActualHeight);
        if (first)
            DispatcherQueue.TryEnqueue(FlushSizes);
    }

    private readonly Dictionary<string, (double Width, double Height)> _measuredSizes = new(StringComparer.Ordinal);

    private void FlushSizes()
    {
        var sizes = _measuredSizes.Select(s => (s.Key, s.Value.Width, s.Value.Height)).ToList();
        _measuredSizes.Clear();
        _viewModel?.ReportSizes(sizes);
    }

    private void RefreshAll()
    {
        if (_viewModel is null)
            return;
        foreach (var view in _views.Values)
            view.Refresh(_viewModel.ShowDetails);
        ForgetChips(); // their brushes are the old theme's
        DrawEdges();
    }

    /// <summary>Where tips and the quick-add search open while the canvas is hidden (the Outline view).</summary>
    public FrameworkElement? FallbackTarget { get; set; }

    private bool Shown => Visibility == Visibility.Visible && ActualWidth > 0;
}
