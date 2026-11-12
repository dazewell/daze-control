using DazeControl.Core.Layout;

namespace DazeControl.Editor.ViewModels.Canvas;

/// <summary>How a re-layout is shown (T21.8, D-119 d): moved boxes glide for <see cref="Reflow"/>, added ones fade in
/// and grow for <see cref="Appear"/>. Zero = at once (Windows "Animation effects" off, or a move that isn't layout's).</summary>
public sealed record SceneMotion(TimeSpan Reflow, TimeSpan Appear)
{
    public static SceneMotion Instant { get; } = new(TimeSpan.Zero, TimeSpan.Zero);

    /// <summary>The canvas tokens (UX §5.3): reflow 200 ms decelerate, add 150 ms; all zero with animations off.</summary>
    public static SceneMotion For(bool animationsEnabled) => new(
        MotionTokens.Duration(animationsEnabled, MotionTokens.ReflowMs),
        MotionTokens.Duration(animationsEnabled, MotionTokens.AppearMs));

    public bool IsInstant => Reflow <= TimeSpan.Zero && Appear <= TimeSpan.Zero;
}

/// <summary>Easing curves (pure).</summary>
public static class Easing
{
    /// <summary>
    /// Fluent's decelerate curve, cubic-bezier(0, 0, 0, 1) (WinUI's ControlFastOutSlowInKeySpline): fast out, slow in.
    /// With both x control points at 0, x(t) = t³ and y(t) = 3t² − 2t³, so it solves in closed form.
    /// </summary>
    public static double Decelerate(double x)
    {
        if (!(x > 0)) // NaN too
            return 0;
        if (x >= 1)
            return 1;
        var t = Math.Cbrt(x);
        return t * t * (3 - 2 * t);
    }

    public static double Lerp(double from, double to, double p) => from + (to - from) * p;

    public static Rect Lerp(Rect from, Rect to, double p) => p >= 1 ? to : new(
        Lerp(from.X, to.X, p), Lerp(from.Y, to.Y, p), Lerp(from.Width, to.Width, p), Lerp(from.Height, to.Height, p));
}

public sealed partial class CanvasScene
{
    /// <summary>A box gliding from where it was shown to where it is laid out; <see cref="Now"/> at the last frame.</summary>
    private sealed class Glide(Rect from, Rect to)
    {
        public Rect From { get; set; } = from;

        public Rect To { get; } = to;

        public Rect Now { get; set; } = from;
    }

    /// <summary>What was shown when a new arrangement came: where to glide from.</summary>
    private sealed record Shown(Dictionary<string, Rect> Rects, Dictionary<string, double> Opacity, Dictionary<(string, string), double> Lanes);

    private readonly Dictionary<string, Glide> _moving = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _appearing = new(StringComparer.Ordinal); // the opacity each starts from
    private readonly Dictionary<(string From, string To), double?> _edgeGlides = []; // the lane each starts from
    private readonly Dictionary<(string From, string To), EdgeCurve> _glideCurves = [];
    private SceneMotion _motion = SceneMotion.Instant;
    private TimeSpan? _start;
    private double _appearProgress = 1;

    /// <summary>A glide or fade is running: the canvas reads <see cref="ShownBounds"/>, <see cref="ShownOpacity"/> and the live curves each frame.</summary>
    public bool IsMoving => _moving.Count > 0 || _appearing.Count > 0 || _edgeGlides.Count > 0;

    /// <summary>The boxes gliding or fading in now.</summary>
    public IEnumerable<string> Animated => _moving.Keys.Concat(_appearing.Keys.Where(id => !_moving.ContainsKey(id)));

    /// <summary>How opaque a box is drawn now (1 unless it is fading in).</summary>
    public double ShownOpacity(string id) =>
        _appearing.TryGetValue(id, out var from) ? Easing.Lerp(from, 1, _appearProgress) : 1;

    /// <summary>How large a box is drawn now (it grows from <see cref="MotionTokens.AppearScale"/> as it fades in).</summary>
    public double ShownScale(string id) =>
        _appearing.ContainsKey(id) ? Easing.Lerp(MotionTokens.AppearScale, 1, ShownOpacity(id)) : 1;

    /// <summary>
    /// One frame of the glide at <paramref name="now"/> (any monotonic clock; the first frame after a re-layout starts
    /// it). Returns whether it still runs; when it ends everything is shown as laid out.
    /// </summary>
    public bool Advance(TimeSpan now)
    {
        if (!IsMoving)
            return false;
        _start ??= now;
        var elapsed = now - _start.Value;
        double reflow = Progress(elapsed, _motion.Reflow), appear = Progress(elapsed, _motion.Appear);
        if (reflow >= 1 && appear >= 1)
        {
            Settle();
            return false;
        }
        Sample(reflow, appear);
        return true;
    }

    /// <summary>
    /// The last layout also moved every box by (<paramref name="dx"/>, <paramref name="dy"/>) and the camera pans that
    /// back in the same tick (a library drop above or left of the flow moves its origin): that uniform shift isn't
    /// layout motion, so it comes out of every glide; boxes it alone moved stop gliding.
    /// </summary>
    public void ShiftBy(double dx, double dy)
    {
        if (!IsMoving || (dx == 0 && dy == 0))
            return;
        foreach (var (id, glide) in _moving.ToList())
        {
            glide.From = glide.From with { X = glide.From.X + dx, Y = glide.From.Y + dy };
            glide.Now = glide.Now with { X = glide.Now.X + dx, Y = glide.Now.Y + dy };
            if (glide.From == glide.To)
                _moving.Remove(id);
        }
        foreach (var edge in _edgeGlides.ToList())
        {
            var lane = edge.Value + dy;
            var laneMoves = lane is not null && _edges[_edgeAt[edge.Key]].Route.WrapLaneY != lane;
            if (laneMoves || _moving.ContainsKey(Owner(edge.Key.From)) || _moving.ContainsKey(Owner(edge.Key.To)))
                _edgeGlides[edge.Key] = lane;
            else
            {
                _edgeGlides.Remove(edge.Key);
                _glideCurves.Remove(edge.Key);
            }
        }
        if (!IsMoving)
            Settle();
        else if (_start is null)
            Sample(0, 0);
    }

    /// <summary>Ends any glide or fade at once: everything is shown as laid out.</summary>
    public void Settle()
    {
        _moving.Clear();
        _appearing.Clear();
        _edgeGlides.Clear();
        _glideCurves.Clear();
        _start = null;
        _appearProgress = 1;
    }

    private static double Progress(TimeSpan elapsed, TimeSpan duration) =>
        duration <= TimeSpan.Zero ? 1 : Easing.Decelerate(elapsed / duration);

    private Shown? SnapshotShown(SceneMotion? motion)
    {
        if (motion is null || motion.IsInstant || _items.Count == 0)
            return null;
        var rects = new Dictionary<string, Rect>(_items.Count, StringComparer.Ordinal);
        var opacity = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var item in _items)
        {
            rects[item.Id] = ShownBounds(item.Id);
            if (_appearing.ContainsKey(item.Id))
                opacity[item.Id] = ShownOpacity(item.Id);
        }
        var lanes = new Dictionary<(string, string), double>();
        foreach (var edge in _edges)
        {
            if (ShownCurve(edge.From, edge.To).WrapLaneY is { } lane)
                lanes[edge.Key] = lane;
        }
        return new Shown(rects, opacity, lanes);
    }

    /// <summary>
    /// After a re-layout: every box glides from where it was shown (mid-glide too: a new layout retargets from the
    /// current frame, never from the start) to where it is now; a box that wasn't there fades in, unless the scene was
    /// empty (opening a flow shows it at once). Steps and members keep their new size (their content set it); cards
    /// grow or shrink with the glide. Lines touching a gliding box glide with it, so they stay attached.
    /// </summary>
    private void Retarget(Shown? before, SceneMotion? motion)
    {
        Settle();
        _motion = motion ?? SceneMotion.Instant;
        if (before is null || _motion.IsInstant)
            return;
        var glides = _motion.Reflow > TimeSpan.Zero;
        var fades = _motion.Appear > TimeSpan.Zero;
        foreach (var item in _items)
        {
            if (before.Rects.TryGetValue(item.Id, out var was))
            {
                var to = item.Bounds;
                if (Frame is not null && Frame.Rects.ContainsKey(item.Id) && Drag?.FrozenBounds(item.Id) == to)
                    continue; // a layout mid-drag that didn't commit the move: the box stays under the pointer
                if (item.Kind != SceneKind.Card)
                    was = was with { Width = to.Width, Height = to.Height };
                if (glides && was != to)
                    _moving[item.Id] = new Glide(was, to);
                if (fades && before.Opacity.TryGetValue(item.Id, out var opacity) && opacity < 1)
                    _appearing[item.Id] = opacity; // was fading in: carries on from where it is
            }
            else if (fades)
            {
                _appearing[item.Id] = 0;
            }
        }
        if (glides)
        {
            foreach (var edge in _edges)
            {
                var lane = before.Lanes.TryGetValue(edge.Key, out var l) ? l : (double?)null;
                var laneMoves = lane is not null && edge.Route.WrapLaneY is { } now && now != lane;
                if (laneMoves || _moving.ContainsKey(Owner(edge.From)) || _moving.ContainsKey(Owner(edge.To)))
                    _edgeGlides[edge.Key] = lane;
            }
        }
        if (IsMoving)
            Sample(0, 0);
    }

    /// <summary>The box a line attaches to: a member's card, else the box itself.</summary>
    private string Owner(string id) => Find(id)?.Card ?? id;

    private void Sample(double reflow, double appear)
    {
        _appearProgress = appear;
        foreach (var glide in _moving.Values)
            glide.Now = Easing.Lerp(glide.From, glide.To, reflow);
        foreach (var (key, fromLane) in _edgeGlides)
        {
            var edge = _edges[_edgeAt[key]];
            var route = edge.Route;
            // Each end moves with its box: the laid-out port shifted by how far the box is from its place, so a line
            // never leaves its port; the lane glides from where it was shown.
            var start = Shifted(route.Start, Owner(route.From), route.StartSide);
            var end = Shifted(route.End, Owner(route.To), route.EndSide);
            var lane = route.WrapLaneY is { } toLane && fromLane is { } wasLane ? Easing.Lerp(wasLane, toLane, reflow) : route.WrapLaneY;
            _glideCurves[key] = EdgeCurve.For(route with { Start = start, End = end, WrapLaneY = lane }, Lines);
        }
    }

    private Point Shifted(Point port, string owner, PortSide side)
    {
        if (!_moving.TryGetValue(owner, out var glide))
            return port;
        Point now = FlowPlacement.Port(glide.Now, side), laid = FlowPlacement.Port(glide.To, side);
        return new Point(port.X + now.X - laid.X, port.Y + now.Y - laid.Y);
    }

    private Dictionary<(string From, string To), EdgeCurve> MergedLive()
    {
        var merged = new Dictionary<(string From, string To), EdgeCurve>(_live);
        foreach (var (key, curve) in _glideCurves)
            merged[key] = curve;
        return merged;
    }
}
