using DazeControl.Core.Layout;

namespace DazeControl.Editor.ViewModels.Canvas;

/// <summary>
/// A uniform grid over content DIP (D-119 a): each entry is listed in every cell its rect touches, so a hit test
/// looks only at the entries near the point instead of every one. Rebuilt whole by <see cref="CanvasScene.Apply"/>
/// (flows are 5–100 steps: a rebuild is microseconds); no work at all while idle.
/// </summary>
internal sealed class SpatialGrid
{
    /// <summary>About one pill wide: a point query reads one cell, a 12–20 DIP band at most four.</summary>
    public const double DefaultCell = 256;

    /// <summary>A rect spanning more cells than this (a corrupt or huge coordinate) goes to a list every query reads.</summary>
    private const long MaxCells = 4096;

    private readonly double _cell;
    private readonly Dictionary<long, List<int>> _cells = [];
    private readonly List<int> _everywhere = [];
    private int[] _seen = [];
    private int _stamp;

    public SpatialGrid(double cell = DefaultCell) => _cell = cell;

    /// <summary>Empties the grid for up to <paramref name="count"/> entries (indices 0 … count-1).</summary>
    public void Reset(int count)
    {
        _cells.Clear();
        _everywhere.Clear();
        if (_seen.Length < count)
            _seen = new int[count];
        else
            Array.Clear(_seen);
        _stamp = 0;
    }

    public void Add(int index, Rect r)
    {
        if (!double.IsFinite(r.X) || !double.IsFinite(r.Y) || !double.IsFinite(r.Right) || !double.IsFinite(r.Bottom))
        {
            _everywhere.Add(index);
            return;
        }
        var (x0, y0, x1, y1) = Cells(r);
        if (MoreThan(x1 - x0 + 1, y1 - y0 + 1, MaxCells))
        {
            _everywhere.Add(index);
            return;
        }
        for (var x = x0; x <= x1; x++)
        {
            for (var y = y0; y <= y1; y++)
            {
                var key = Key(x, y);
                if (!_cells.TryGetValue(key, out var list))
                    _cells[key] = list = [];
                list.Add(index);
            }
        }
    }

    /// <summary>
    /// Every entry listed in a cell <paramref name="area"/> touches, each once, into <paramref name="into"/> (cleared
    /// first; in no particular order). A superset of the entries whose rect touches the area.
    /// </summary>
    public void Query(Rect area, List<int> into)
    {
        into.Clear();
        if (++_stamp == int.MaxValue)
        {
            Array.Clear(_seen);
            _stamp = 1;
        }
        foreach (var index in _everywhere)
            Take(index, into);
        if (!double.IsFinite(area.X) || !double.IsFinite(area.Y) || !double.IsFinite(area.Right) || !double.IsFinite(area.Bottom))
            return;
        var (x0, y0, x1, y1) = Cells(area);
        if (MoreThan(x1 - x0 + 1, y1 - y0 + 1, _cells.Count))
        {
            // A query wider than the grid: walk the cells that exist instead of the empty ones.
            foreach (var (key, list) in _cells)
            {
                var (x, y) = ((int)(key >> 32), (int)key);
                if (x >= x0 && x <= x1 && y >= y0 && y <= y1)
                    foreach (var index in list)
                        Take(index, into);
            }
            return;
        }
        for (var x = x0; x <= x1; x++)
        {
            for (var y = y0; y <= y1; y++)
            {
                if (_cells.TryGetValue(Key(x, y), out var list))
                    foreach (var index in list)
                        Take(index, into);
            }
        }
    }

    /// <summary>width × height &gt; limit without the multiplication (two clamped spans can reach 2³², their product overflows).</summary>
    private static bool MoreThan(long width, long height, long limit) => width > limit || height > limit || width > limit / Math.Max(height, 1);

    private void Take(int index, List<int> into)
    {
        if (_seen[index] == _stamp)
            return;
        _seen[index] = _stamp;
        into.Add(index);
    }

    // Inclusive on both ends, like Rect.Contains: a point on a cell line is in the cell to its right/below, and a rect
    // whose right edge lies on that line is listed there too.
    private (long X0, long Y0, long X1, long Y1) Cells(Rect r) =>
        (Cell(r.X), Cell(r.Y), Cell(r.Right), Cell(r.Bottom));

    private long Cell(double v) => (long)Math.Clamp(Math.Floor(v / _cell), int.MinValue, int.MaxValue);

    private static long Key(long x, long y) => (x << 32) | (uint)(int)y;
}
