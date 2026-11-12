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
using Shape = Microsoft.UI.Xaml.Shapes.Path;
using XamlCanvas = Microsoft.UI.Xaml.Controls.Canvas;

namespace DazeControl.Editor.Canvas;

public sealed partial class FlowCanvas
{
    /// <summary>One set of edge paths: one per style, whatever the edge count (D-037).</summary>
    private sealed record EdgePaths(Shape Normal, Shape MayFail, Shape Always, Shape IfFails, Shape Selected,
        Shape NormalArrows, Shape IfFailsArrows, Shape SelectedArrows)
    {
        public IEnumerable<Shape> All => [Normal, MayFail, Always, IfFails, Selected, NormalArrows, IfFailsArrows, SelectedArrows];
    }

    private EdgePaths? _staticPaths, _livePaths;

    private EdgePaths StaticPaths => _staticPaths ??= new(NormalEdges, MayFailEdges, AlwaysEdges, IfFailsEdges, SelectedEdges, NormalArrows, IfFailsArrows, SelectedArrows);

    private EdgePaths LivePaths => _livePaths ??= new(LiveNormalEdges, LiveMayFailEdges, LiveAlwaysEdges, LiveIfFailsEdges, LiveSelectedEdges, LiveNormalArrows, LiveIfFailsArrows, LiveSelectedArrows);

    /// <summary>
    /// Every edge into the static set; during a live drag only the lines that don't touch the moved steps, from the
    /// frozen arrangement (drawn once at drag start), while the touching ones go to the live set (D-119 b).
    /// </summary>
    private void DrawEdges()
    {
        if (_viewModel is null)
            return;
        _drawnEdgeVersion = _viewModel.EdgeVersion;
        var edges = _viewModel.Scene.Drag is { } preview
            ? _viewModel.Edges.Where(e => !preview.Touching.Contains((e.From, e.To))).Select(e => (e, preview.StaticCurve(e.From, e.To) ?? e.Curve))
            : _viewModel.Edges.Select(e => (e, e.Curve));
        Draw(StaticPaths, edges, ChipLayer, _chips);
        DrawLiveEdges();
        ShowEdgeControls(EdgeWithControls());
    }

    /// <summary>
    /// After a re-layout or a selection change: the edge figures only when a line was added, removed, re-routed or
    /// restyled since they were drawn (T21.6); the "+"/"×" pair always follows.
    /// </summary>
    private void RedrawEdges()
    {
        if (_viewModel is null)
            return;
        if (_viewModel.EdgeVersion != _drawnEdgeVersion || _viewModel.Scene.IsDragging)
            DrawEdges();
        else
            ShowEdgeControls(EdgeWithControls());
    }

    /// <summary>The lines touching the dragged steps at the current frame, their arrowheads and chips (chips kept per edge).</summary>
    private void DrawLiveEdges()
    {
        if (_viewModel is null || _viewModel.Scene.LiveCurves is not { Count: > 0 } curves)
            return;
        var live = new List<(CanvasEdgeViewModel, DazeControl.Core.Layout.EdgeCurve)>(curves.Count);
        foreach (var edge in _viewModel.Edges)
        {
            if (curves.TryGetValue((edge.From, edge.To), out var curve))
                live.Add((edge, curve));
        }
        Draw(LivePaths, live, LiveChipLayer, _liveChips);
    }

    /// <summary>Empties the live set when a drag ends.</summary>
    private void ClearLiveEdges()
    {
        foreach (var path in LivePaths.All)
            path.Data = null;
        LiveChipLayer.Children.Clear();
        _liveChips.Clear();
    }

    /// <summary>Drops every kept chip and the live set (a new view model or theme): the next draw makes them again.</summary>
    private void ForgetChips()
    {
        ClearLiveEdges();
        ChipLayer.Children.Clear();
        _chips.Clear();
        _drawnEdgeVersion = -1;
    }

    /// <summary>The curve an edge is drawn with now: live while it follows a drag, frozen while a drag runs, else committed.</summary>
    private DazeControl.Core.Layout.EdgeCurve CurveOf(CanvasEdgeViewModel edge) =>
        _viewModel?.Scene.LiveCurves.TryGetValue((edge.From, edge.To), out var live) == true ? live
        : _viewModel?.Scene.Drag?.StaticCurve(edge.From, edge.To) ?? edge.Curve;

    /// <summary>
    /// One set of paths from <paramref name="edges"/>. Chips are kept per edge in <paramref name="keep"/>: a drawn
    /// edge's chip only moves (made once, or again when its text or colour changed), an edge not drawn loses its chip,
    /// so hover and selection restyle lines without recreating chips (T21.6).
    /// </summary>
    private static void Draw(EdgePaths paths, IEnumerable<(CanvasEdgeViewModel Edge, DazeControl.Core.Layout.EdgeCurve Curve)> edges,
        XamlCanvas chips, Dictionary<(string, string), Border> keep)
    {
        var curves = paths.All.ToDictionary(p => p, _ => new PathGeometry());
        var drawn = new HashSet<(string, string)>();
        foreach (var (edge, c) in edges)
        {
            // During a run an edge turns accent once its source finished; hover/selected is 2 px accent (UX §5.3).
            var accent = edge.IsSelected || edge.IsHovered || edge.IsLit;
            var path = accent ? paths.Selected : edge.Style switch
            {
                EdgeStyle.IntoMayFail => paths.MayFail,
                EdgeStyle.IfFails => paths.IfFails,
                EdgeStyle.Always => paths.Always,
                _ => paths.Normal,
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

            var arrows = accent ? paths.SelectedArrows : edge.Style == EdgeStyle.IfFails ? paths.IfFailsArrows : paths.NormalArrows;
            curves[arrows].Figures.Add(Arrowhead(c));

            if (edge.Chip is not { } text)
                continue;
            var key = (edge.From, edge.To);
            var look = (text, edge.Style == EdgeStyle.IfFails);
            drawn.Add(key);
            if (keep.TryGetValue(key, out var chip) && chip.Tag is ValueTuple<string, bool> was && was == look)
            {
                PlaceChip(chip, c.Midpoint); // a kept chip only moves: no new element per frame or per hover
                continue;
            }
            if (chip is not null)
                chips.Children.Remove(chip);
            chips.Children.Add(keep[key] = Chip(text, c.Midpoint, look.Item2));
            keep[key].Tag = look;
        }
        foreach (var gone in keep.Keys.Where(k => !drawn.Contains(k)).ToList())
        {
            chips.Children.Remove(keep[gone]);
            keep.Remove(gone);
        }
        paths.Selected.Stroke = paths.SelectedArrows.Fill = Theme.Selection;
        foreach (var (path, geometry) in curves)
            path.Data = geometry;
    }

    /// <summary>A 6-DIP arrowhead from the line's end to its tip, square to the way the line comes in (D-115).</summary>
    private static PathFigure Arrowhead(DazeControl.Core.Layout.EdgeCurve c)
    {
        double dx = c.Tip.X - c.End.X, dy = c.Tip.Y - c.End.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        (dx, dy) = length == 0 ? (1, 0) : (dx / length, dy / length);
        var arrow = new PathFigure { StartPoint = P(c.Tip), IsClosed = true, IsFilled = true };
        arrow.Segments.Add(new LineSegment { Point = new(c.End.X - dy * 3.5, c.End.Y + dx * 3.5) });
        arrow.Segments.Add(new LineSegment { Point = new(c.End.X + dy * 3.5, c.End.Y - dx * 3.5) });
        return arrow;
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
        var curve = CurveOf(edge);
        var mid = curve.Midpoint;
        if (curve.IsWrapped)
        {
            // The row gap is too low to lift them above the lane (D-060): centre them on the lane, right of the chip.
            var lane = curve.Corners;
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
        PlaceChip(chip, at);
        return chip;
    }

    private static void PlaceChip(Border chip, CorePoint at)
    {
        XamlCanvas.SetLeft(chip, at.X - chip.DesiredSize.Width / 2);
        XamlCanvas.SetTop(chip, at.Y - chip.DesiredSize.Height / 2);
    }

    private static Windows.Foundation.Point P(CorePoint p) => new(p.X, p.Y);

    private static CorePoint C(Windows.Foundation.Point p) => new(p.X, p.Y);
}
