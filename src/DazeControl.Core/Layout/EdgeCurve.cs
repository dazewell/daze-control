namespace DazeControl.Core.Layout;

/// <summary>
/// A horizontal cubic Bézier from an output port to just before an input port, where a 6-DIP arrowhead takes
/// over (UX §5.3). Hit-testing is the distance to the curve (UX §5.8 #2). A wrapped edge (<see cref="IsWrapped"/>)
/// is orthogonal instead: right, down to the row gap, back left to a lane before the next row, down, right into the
/// port (D-060). The same shape leads from a block into the line under it, whose target may sit far below the gap
/// (D-066). With Lines › Right angles (D-067) every other edge is orthogonal too: right to the gap's middle, along it
/// to the target's height, right into the port (<see cref="BendX"/>).
/// </summary>
public readonly record struct EdgeCurve(Point Start, Point Control1, Point Control2, Point End, Point Tip)
{
    public const double ArrowLength = 6;

    /// <summary>A wrapped edge's step out of the port and the lane's distance before the next row (D-060).</summary>
    public const double WrapStep = 16;

    public const double CornerRadius = 8;

    private const int Samples = 32;

    /// <summary>The gap line a wrapped edge runs back along; null for a curve.</summary>
    public double? WrapLaneY { get; init; }

    public bool IsWrapped => WrapLaneY is not null;

    /// <summary>A right-angled edge's vertical run (D-067); null for a curve or a wrapped edge.</summary>
    public double? BendX { get; init; }

    /// <summary>Drawn as straight runs with rounded corners (<see cref="RoundedSegments"/>), not a Bézier.</summary>
    public bool IsOrthogonal => WrapLaneY is not null || BendX is not null;

    /// <summary>The drawn shape of a laid-out edge for the flow's Lines choice.</summary>
    public static EdgeCurve For(EdgeRoute route, LayoutLines lines)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (route.WrapLaneY is { } lane)
            return Wrapped(route.Start, route.End, lane);
        if (route.IsFloating)
            return Floating(route.Start, route.End, route.Leaves, route.Enters); // a curve whatever Lines says (D-115)
        return lines == LayoutLines.RightAngles ? RightAngled(route.Start, route.End) : Between(route.Start, route.End);
    }

    /// <summary>
    /// Right, down or up in the middle of the gap, right into the port. A target too close or behind (a cycle in a
    /// half-built flow) bends one <see cref="WrapStep"/> out of the port.
    /// </summary>
    public static EdgeCurve RightAngled(Point start, Point tip)
    {
        var end = new Point(tip.X - ArrowLength, tip.Y);
        var bend = end.X - start.X >= 2 * WrapStep ? (start.X + end.X) / 2 : start.X + WrapStep;
        return new EdgeCurve(start, start, end, end, tip) { BendX = bend };
    }

    public static EdgeCurve Wrapped(Point start, Point tip, double laneY)
    {
        var end = new Point(tip.X - ArrowLength, tip.Y);
        return new EdgeCurve(start, start, end, end, tip) { WrapLaneY = laneY };
    }

    /// <summary>A wrapped edge's polyline from <see cref="Start"/> to <see cref="End"/> (corners before rounding).</summary>
    public IReadOnlyList<Point> Corners
    {
        get
        {
            if (BendX is { } bend)
                return [Start, new(bend, Start.Y), new(bend, End.Y), End];
            if (WrapLaneY is not { } lane)
                return [Start, End];
            var outX = Start.X + WrapStep;
            var laneX = Tip.X - WrapStep;
            return [Start, new(outX, Start.Y), new(outX, lane), new(laneX, lane), new(laneX, End.Y), End];
        }
    }

    /// <summary>
    /// An orthogonal edge as straight runs and rounded corners: each item is a line end and, when it rounds a corner,
    /// the corner (a quadratic control point) and where the curve ends.
    /// </summary>
    public IEnumerable<(Point LineTo, Point? Corner, Point CurveTo)> RoundedSegments()
    {
        var p = Corners;
        for (var i = 1; i < p.Count - 1; i++)
        {
            var r = Math.Min(CornerRadius, Math.Min(Length(p[i - 1], p[i]), Length(p[i], p[i + 1])) / 2);
            var before = Toward(p[i], p[i - 1], r);
            var after = Toward(p[i], p[i + 1], r);
            yield return (before, p[i], after);
        }
        yield return (p[^1], null, p[^1]);
    }

    /// <summary>
    /// A curve that leaves <paramref name="start"/> straight out of one side and points into another (D-115); the
    /// arrowhead lies along the entering side's normal.
    /// </summary>
    public static EdgeCurve Floating(Point start, Point tip, PortSide leaves, PortSide enters)
    {
        var (ox, oy) = Outward(leaves);
        var (ix, iy) = Outward(enters);
        var end = new Point(tip.X + ix * ArrowLength, tip.Y + iy * ArrowLength);
        var sideways = leaves is PortSide.Right or PortSide.Left;
        var reach = Math.Max(32, (sideways ? Math.Abs(end.X - start.X) : Math.Abs(end.Y - start.Y)) / 2);
        return new EdgeCurve(start, new Point(start.X + ox * reach, start.Y + oy * reach), new Point(end.X + ix * reach, end.Y + iy * reach), end, tip);
    }

    private static (double X, double Y) Outward(PortSide side) => side switch
    {
        PortSide.Left => (-1, 0),
        PortSide.Top => (0, -1),
        PortSide.Bottom => (0, 1),
        _ => (1, 0),
    };

    public static EdgeCurve Between(Point start, Point tip)
    {
        var end = new Point(tip.X - ArrowLength, tip.Y);
        // Backwards edges (a cycle in a half-built flow) still leave right and enter left.
        var reach = Math.Max(32, Math.Abs(end.X - start.X) / 2);
        return new EdgeCurve(start, new Point(start.X + reach, start.Y), new Point(end.X - reach, end.Y), end, tip);
    }

    /// <summary>The curve's middle; a wrapped edge's is the return lane's middle, so chips stay in the row gap.</summary>
    /// <remarks>A right-angled edge's is its vertical run's middle.</remarks>
    public Point Midpoint => WrapLaneY is { } lane ? new((Start.X + WrapStep + Tip.X - WrapStep) / 2, lane)
        : BendX is { } bend ? new(bend, (Start.Y + End.Y) / 2)
        : At(0.5);

    public Point At(double t)
    {
        if (IsOrthogonal)
            return AlongCorners(t);
        var u = 1 - t;
        double B(double a, double b, double c, double d) => u * u * u * a + 3 * u * u * t * b + 3 * u * t * t * c + t * t * t * d;
        return new Point(B(Start.X, Control1.X, Control2.X, End.X), B(Start.Y, Control1.Y, Control2.Y, End.Y));
    }

    /// <summary>Approximate (sampled) shortest distance from <paramref name="p"/> to the curve or the arrowhead.</summary>
    public double DistanceTo(Point p)
    {
        var best = Distance(p, End, Tip);
        if (IsOrthogonal)
        {
            var corners = Corners;
            for (var i = 1; i < corners.Count; i++)
                best = Math.Min(best, Distance(p, corners[i - 1], corners[i]));
            return best;
        }
        var previous = Start;
        for (var i = 1; i <= Samples; i++)
        {
            var next = At((double)i / Samples);
            best = Math.Min(best, Distance(p, previous, next));
            previous = next;
        }
        return best;
    }

    private Point AlongCorners(double t)
    {
        var corners = Corners;
        var total = 0.0;
        for (var i = 1; i < corners.Count; i++)
            total += Length(corners[i - 1], corners[i]);
        var left = Math.Clamp(t, 0, 1) * total;
        for (var i = 1; i < corners.Count; i++)
        {
            var length = Length(corners[i - 1], corners[i]);
            if (left <= length)
                return Toward(corners[i - 1], corners[i], left);
            left -= length;
        }
        return corners[^1];
    }

    private static double Length(Point a, Point b) => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));

    private static Point Toward(Point from, Point to, double distance)
    {
        var length = Length(from, to);
        return length == 0 ? from : new(from.X + (to.X - from.X) * distance / length, from.Y + (to.Y - from.Y) * distance / length);
    }

    private static double Distance(Point p, Point a, Point b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        var lengthSquared = dx * dx + dy * dy;
        var t = lengthSquared == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthSquared, 0, 1);
        double x = a.X + t * dx - p.X, y = a.Y + t * dy - p.Y;
        return Math.Sqrt(x * x + y * y);
    }
}
