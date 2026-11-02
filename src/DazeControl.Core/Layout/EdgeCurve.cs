namespace DazeControl.Core.Layout;

/// <summary>
/// A horizontal cubic Bézier from an output port to just before an input port, where a 6-DIP arrowhead takes
/// over (UX §5.3). Hit-testing is the distance to the curve (UX §5.8 #2).
/// </summary>
public readonly record struct EdgeCurve(Point Start, Point Control1, Point Control2, Point End, Point Tip)
{
    public const double ArrowLength = 6;

    private const int Samples = 32;

    public static EdgeCurve Between(Point start, Point tip)
    {
        var end = new Point(tip.X - ArrowLength, tip.Y);
        // Backwards edges (a cycle in a half-built flow) still leave right and enter left.
        var reach = Math.Max(32, Math.Abs(end.X - start.X) / 2);
        return new EdgeCurve(start, new Point(start.X + reach, start.Y), new Point(end.X - reach, end.Y), end, tip);
    }

    public Point Midpoint => At(0.5);

    public Point At(double t)
    {
        var u = 1 - t;
        double B(double a, double b, double c, double d) => u * u * u * a + 3 * u * u * t * b + 3 * u * t * t * c + t * t * t * d;
        return new Point(B(Start.X, Control1.X, Control2.X, End.X), B(Start.Y, Control1.Y, Control2.Y, End.Y));
    }

    /// <summary>Approximate (sampled) shortest distance from <paramref name="p"/> to the curve or the arrowhead.</summary>
    public double DistanceTo(Point p)
    {
        var best = Distance(p, End, Tip);
        var previous = Start;
        for (var i = 1; i <= Samples; i++)
        {
            var next = At((double)i / Samples);
            best = Math.Min(best, Distance(p, previous, next));
            previous = next;
        }
        return best;
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
