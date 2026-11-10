namespace DazeControl.Core.Layout;

/// <summary>
/// A horizontal cubic Bézier from an output port to just before an input port, where a 6-DIP arrowhead takes
/// over (UX §5.3). Hit-testing is the distance to the curve (UX §5.8 #2). A wrapped edge (<see cref="IsWrapped"/>)
/// is orthogonal instead: right, down to the row gap, back left to a lane before the next row, down, right into the
/// port (D-060). The same shape leads from a block into the line under it, whose target may sit far below the gap
/// (D-066). With Lines › Right angles (D-067) every other edge is orthogonal too: right to the gap's middle, along it
/// to the target's height, right into the port (<see cref="BendX"/>).
/// Lines between hand-placed steps or in another Arrange shape (D-115) leave and enter by any side
/// (<see cref="EdgeRoute.StartSide"/>, <see cref="EdgeRoute.EndSide"/>): a vertical run bends at <see cref="BendY"/>,
/// a line between a horizontal and a vertical side turns once at <see cref="Elbow"/>.
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

    /// <summary>A right-angled edge's horizontal run between a bottom and a top side (D-115).</summary>
    public double? BendY { get; init; }

    /// <summary>The one corner of a right-angled edge between a horizontal and a vertical side (D-115).</summary>
    public Point? Elbow { get; init; }

    /// <summary>Drawn as straight runs with rounded corners (<see cref="RoundedSegments"/>), not a Bézier.</summary>
    public bool IsOrthogonal => WrapLaneY is not null || BendX is not null || BendY is not null || Elbow is not null;

    /// <summary>The drawn shape of a laid-out edge for the flow's Lines choice.</summary>
    public static EdgeCurve For(EdgeRoute route, LayoutLines lines)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (route.WrapLaneY is { } lane)
            return Wrapped(route.Start, route.End, lane);
        return lines == LayoutLines.RightAngles
            ? RightAngled(route.Start, route.End, route.StartSide, route.EndSide)
            : Between(route.Start, route.End, route.StartSide, route.EndSide);
    }

    /// <summary>The outward unit direction of a side.</summary>
    public static Point Normal(PortSide side) => side switch
    {
        PortSide.Left => new(-1, 0),
        PortSide.Bottom => new(0, 1),
        PortSide.Top => new(0, -1),
        _ => new(1, 0),
    };

    private static bool IsHorizontal(PortSide side) => side is PortSide.Left or PortSide.Right;

    /// <summary>Where the line ends and the arrowhead starts: one arrow length out of <paramref name="tip"/>'s side.</summary>
    private static Point ArrowBase(Point tip, PortSide side)
    {
        var n = Normal(side);
        return new(tip.X + n.X * ArrowLength, tip.Y + n.Y * ArrowLength);
    }

    /// <summary>Right-angled between any two sides (D-115): Z-shaped across the gap between parallel sides, an L otherwise.</summary>
    public static EdgeCurve RightAngled(Point start, Point tip, PortSide startSide, PortSide endSide)
    {
        if (startSide == PortSide.Right && endSide == PortSide.Left)
            return RightAngled(start, tip);
        var end = ArrowBase(tip, endSide);
        var n = Normal(startSide);
        if (IsHorizontal(startSide) != IsHorizontal(endSide))
        {
            var elbow = IsHorizontal(startSide) ? new Point(end.X, start.Y) : new Point(start.X, end.Y);
            return new EdgeCurve(start, start, end, end, tip) { Elbow = elbow };
        }
        // Parallel sides: bend halfway across the gap, or one step out of the port when the target is too close or behind.
        if (IsHorizontal(startSide))
        {
            var ahead = (end.X - start.X) * n.X;
            return new EdgeCurve(start, start, end, end, tip) { BendX = ahead >= 2 * WrapStep ? (start.X + end.X) / 2 : start.X + n.X * WrapStep };
        }
        var down = (end.Y - start.Y) * n.Y;
        return new EdgeCurve(start, start, end, end, tip) { BendY = down >= 2 * WrapStep ? (start.Y + end.Y) / 2 : start.Y + n.Y * WrapStep };
    }

    /// <summary>A Bézier leaving <paramref name="startSide"/> and entering <paramref name="endSide"/> straight on (D-115).</summary>
    public static EdgeCurve Between(Point start, Point tip, PortSide startSide, PortSide endSide)
    {
        if (startSide == PortSide.Right && endSide == PortSide.Left)
            return Between(start, tip);
        var end = ArrowBase(tip, endSide);
        double dx = Math.Abs(end.X - start.X), dy = Math.Abs(end.Y - start.Y);
        var span = IsHorizontal(startSide) && IsHorizontal(endSide) ? dx : !IsHorizontal(startSide) && !IsHorizontal(endSide) ? dy : Math.Max(dx, dy);
        var reach = Math.Max(32, span / 2);
        Point n1 = Normal(startSide), n2 = Normal(endSide);
        return new EdgeCurve(start, new Point(start.X + n1.X * reach, start.Y + n1.Y * reach),
            new Point(end.X + n2.X * reach, end.Y + n2.Y * reach), end, tip);
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
            if (BendY is { } bendY)
                return [Start, new(Start.X, bendY), new(End.X, bendY), End];
            if (Elbow is { } elbow)
                return [Start, elbow, End];
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
        : BendY is { } bendY ? new((Start.X + End.X) / 2, bendY)
        : Elbow is { } elbow ? elbow
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
