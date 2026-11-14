using System.Diagnostics;
using System.Numerics;
using CtrlAltDaze.Editor.ViewModels.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace CtrlAltDaze.Editor.Canvas;

/// <summary>
/// Layout motion (D-119 d, T21.8). A re-layout places every view at its new rect at once (so UIA, hit tests and the
/// scene agree on the final place) and the scene's glide is shown on top, FLIP style: each animated view gets a
/// <see cref="UIElement.Translation"/> from where it is laid out to where the glide is, the lines touching it go to
/// the live set, and one <see cref="CompositionTarget.Rendering"/> handler (the gesture's, subscribed only while
/// something runs) advances the glide from the frame clock. Nothing runs when idle.
/// </summary>
public sealed partial class FlowCanvas
{
    private readonly HashSet<string> _animated = new(StringComparer.Ordinal); // views the glide moves or fades
    private bool _motionFrames;
    private readonly FrameProbe _gestureProbe = new(), _glideProbe = new();

    /// <summary>After a re-layout: shows the scene's new glide from its first frame, or ends the one shown.</summary>
    private void BeginMotion()
    {
        if (_viewModel is null)
            return;
        var scene = _viewModel.Scene;
        if (!scene.IsMoving)
        {
            if (_animated.Count > 0 || _motionFrames)
                FinishMotion();
            return;
        }
        var now = scene.Animated.ToHashSet(StringComparer.Ordinal);
        foreach (var id in _animated.Where(id => !now.Contains(id)).ToList())
            Rest(id); // a retarget left it where it is laid out
        _animated.Clear();
        _animated.UnionWith(now);
        ApplyMotionFrame();
        _motionFrames = true;
        UpdateFrames();
        _glideProbe.Begin("layout glide");
    }

    private void MotionFrame(TimeSpan now)
    {
        if (_viewModel?.Scene.Advance(now) == true)
            ApplyMotionFrame();
        else
            FinishMotion();
    }

    /// <summary>Every animated view, its lines, the focus ring, the dots and "+/×" at the glide's current frame.</summary>
    private void ApplyMotionFrame()
    {
        if (_viewModel is null)
            return;
        var scene = _viewModel.Scene;
        foreach (var id in _animated)
        {
            if (!_views.TryGetValue(id, out var view) || scene.Find(id) is not { } item)
                continue;
            var (shown, laid) = (scene.ShownBounds(id), item.Bounds);
            view.Translation = new Vector3((float)(shown.X - laid.X), (float)(shown.Y - laid.Y), 0);
            if (item.Kind == SceneKind.Card)
                (view.Width, view.Height) = (shown.Width, shown.Height);
            var scale = (float)scene.ShownScale(id);
            view.CenterPoint = new Vector3((float)(laid.Width / 2), (float)(laid.Height / 2), 0);
            view.Scale = new Vector3(scale, scale, 1);
            view.Opacity = view.RestOpacity * scene.ShownOpacity(id);
        }
        DrawLiveEdges();
        ShowEdgeControls(EdgeWithControls());
        UpdateFocusRing();
        if (_portsNode is not null && (InPortDot.Visibility == Visibility.Visible || OutPortDot.Visibility == Visibility.Visible))
            ShowPorts(_portsNode);
    }

    /// <summary>The glide ended (or was cut short): every view, line and marker as laid out.</summary>
    private void FinishMotion()
    {
        _viewModel?.Scene.Settle();
        foreach (var id in _animated)
            Rest(id);
        _animated.Clear();
        _motionFrames = false;
        UpdateFrames();
        _glideProbe.End();
        if (_viewModel is null)
            return;
        ClearLiveEdges();
        DrawEdges(); // the static set again, and a running drag's live set
        UpdateFocusRing();
        ReanchorTip();
        if (_portsNode is not null && (InPortDot.Visibility == Visibility.Visible || OutPortDot.Visibility == Visibility.Visible))
            ShowPorts(_portsNode);
    }

    private void Rest(string id)
    {
        if (!_views.TryGetValue(id, out var view))
            return;
        if (!_dragViews.Contains(view))
            view.Translation = Vector3.Zero; // a dragged view keeps its drag offset
        view.Scale = Vector3.One;
        view.Place(); // size and opacity as laid out
    }

    /// <summary>Subscribes the per-frame handler while a gesture or a glide needs it; removes it otherwise (idle = zero).</summary>
    private void UpdateFrames()
    {
        var on = _gestureFrames || _motionFrames || _edgeFrames;
        if (on == _onFrames)
            return;
        _onFrames = on;
        if (on)
            CompositionTarget.Rendering += OnFrame;
        else
            CompositionTarget.Rendering -= OnFrame;
    }

    /// <summary>
    /// The one frame clock: the glide advances first (a drag that grabbed a gliding step ends it), then auto-pan moves
    /// the camera, then the latest pointer position of a drag, connect or marquee is drawn: once per frame, whatever
    /// the move rate.
    /// </summary>
    private void OnFrame(object? sender, object e)
    {
        var probing = _gestureProbe.On || _glideProbe.On;
        var started = probing ? Stopwatch.GetTimestamp() : 0;
        var time = e is RenderingEventArgs r ? r.RenderingTime : Stopwatch.GetElapsedTime(0);
        if (_motionFrames)
            MotionFrame(time);
        if (_edgeFrames)
            EdgeFrame(time);
        if (_viewModel is not null && _pendingMove is { } position)
        {
            _pendingMove = null;
            GestureFrame(position);
        }
        if (probing)
        {
            var work = Stopwatch.GetElapsedTime(started);
            _gestureProbe.Frame(work, _viewModel?.Nodes.Count ?? 0);
            _glideProbe.Frame(work, _viewModel?.Nodes.Count ?? 0);
        }
    }

    /// <summary>
    /// <c>--canvas-probe</c> (Debug builds, T21.8): for each drag, connect, marquee or glide, the worst and mean time
    /// spent in the frame handler and between frames, as one line in logs\editor.log.
    /// </summary>
    private sealed class FrameProbe
    {
        private string? _what;
        private int _frames, _boxes;
        private double _workWorst, _workSum, _gapWorst, _gapSum;
        private long _last;

        public bool On => _what is not null;

        public void Begin(string what)
        {
            if (!Program.CanvasProbe || _what is not null)
                return;
            (_what, _frames, _workWorst, _workSum, _gapWorst, _gapSum, _last) = (what, 0, 0, 0, 0, 0, 0);
        }

        public void Frame(TimeSpan work, int boxes)
        {
            if (_what is null)
                return;
            var now = Stopwatch.GetTimestamp();
            var ms = work.TotalMilliseconds;
            (_frames, _boxes, _workSum, _workWorst) = (_frames + 1, boxes, _workSum + ms, Math.Max(_workWorst, ms));
            if (_last != 0)
            {
                var gap = Stopwatch.GetElapsedTime(_last, now).TotalMilliseconds;
                (_gapSum, _gapWorst) = (_gapSum + gap, Math.Max(_gapWorst, gap));
            }
            _last = now;
        }

        public void End()
        {
            if (_what is null)
                return;
            if (_frames > 0)
            {
                App.LogLine(FormattableString.Invariant(
                    $"canvas probe: {_what}, {_boxes} boxes, {_frames} frames, work worst {_workWorst:0.00} ms mean {_workSum / _frames:0.00} ms, frame gap worst {_gapWorst:0.0} ms mean {(_frames > 1 ? _gapSum / (_frames - 1) : 0):0.0} ms"));
            }
            _what = null;
        }
    }
}
