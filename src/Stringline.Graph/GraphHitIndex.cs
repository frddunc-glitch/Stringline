using SkiaSharp;

namespace Stringline.Graph;

/// <summary>
/// A spatial grid over a scene's projected segments for one viewport (<c>05-…md</c> §7), so hovering tests only the
/// segments near the cursor. It answers exactly as <see cref="GraphHitTester.HitTest(GraphScene, GraphViewport, ScreenPoint, double)"/> does. Build one per scene and
/// viewport (<see cref="IsFor"/> says when it is stale); it is immutable once built, so any thread may use it.
/// </summary>
public sealed class GraphHitIndex
{
    /// <summary>Grid cell size in pixels.</summary>
    public const double CellSize = 16;

    private readonly List<(int Path, int Segment)>?[] _cells;
    private readonly double _left;
    private readonly double _top;
    private readonly int _columns;
    private readonly int _rows;

    private GraphHitIndex(GraphScene scene, GraphViewport viewport, double tolerance)
    {
        Scene = scene;
        Viewport = viewport;
        Tolerance = tolerance;
        _left = viewport.PlotLeft - tolerance;
        _top = viewport.PlotTop - tolerance;
        _columns = Math.Max(1, (int)Math.Ceiling((viewport.PlotWidth + (2 * tolerance)) / CellSize));
        _rows = Math.Max(1, (int)Math.Ceiling((viewport.PlotHeight + (2 * tolerance)) / CellSize));
        _cells = new List<(int, int)>?[_columns * _rows];
    }

    public GraphScene Scene { get; }

    public GraphViewport Viewport { get; }

    public double Tolerance { get; }

    /// <summary>Segments indexed (a segment counts once per cell it is near).</summary>
    public int Entries { get; private set; }

    /// <summary>True if this index answers for <paramref name="scene"/>'s paths seen through <paramref name="viewport"/>.</summary>
    public bool IsFor(GraphScene scene, GraphViewport viewport) =>
        scene is not null && ReferenceEquals(scene.Paths, Scene.Paths) && viewport == Viewport;

    public static GraphHitIndex Build(GraphScene scene, GraphViewport viewport, double tolerance = GraphHitTester.DefaultTolerance)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(viewport);
        ArgumentOutOfRangeException.ThrowIfNegative(tolerance);
        var index = new GraphHitIndex(scene, viewport, tolerance);
        index.Fill();
        return index;
    }

    /// <summary>The path under <paramref name="point"/>, as <see cref="GraphHitTester.HitTest(GraphScene, GraphViewport, ScreenPoint, double)"/> would find it.</summary>
    public GraphHit? HitTest(ScreenPoint point)
    {
        if (!Viewport.IsInPlot(point) || _cells[CellOf(point.X, point.Y)] is not { } candidates)
        {
            return null;
        }

        GraphHit? best = null;
        var bestScore = double.PositiveInfinity;
        foreach (var (p, i) in candidates)
        {
            var path = Scene.Paths[p];
            var v0 = path.Vertices[i];
            var v1 = path.Vertices[i + 1];
            var (distance, t) = GraphHitTester.DistanceToSegment(point, Viewport.ToScreen(v0.Time, v0.Axis), Viewport.ToScreen(v1.Time, v1.Axis));
            if (distance > Tolerance)
            {
                continue;
            }

            var score = distance - (path.Layer == GraphLayer.Wtt ? 0 : GraphHitTester.LayerPreferencePixels);
            if (score < bestScore)
            {
                bestScore = score;
                best = new GraphHit(path, i, distance, t <= 0.5 ? i : i + 1, v0.Time + (t * (v1.Time - v0.Time)));
            }
        }

        return best;
    }

    /// <summary>
    /// Adds every segment to the cells it passes near. Each segment is clipped to the plot (plus the tolerance) and
    /// sampled at half-cell steps; the cells within the tolerance plus half a step of each sample cover every point
    /// within the tolerance of the segment.
    /// </summary>
    private void Fill()
    {
        var v = Viewport;
        var area = new SKRect((float)_left, (float)_top, (float)(v.PlotRight + Tolerance), (float)(v.PlotBottom + Tolerance));
        var dt = Tolerance / v.TimeLength * v.TimeSpan;
        var da = Tolerance / v.AxisLength * v.AxisSpan;
        const double step = CellSize / 2;
        var reach = Tolerance + (step / 2);

        for (var p = 0; p < Scene.Paths.Count; p++)
        {
            var path = Scene.Paths[p];
            if (!v.Overlaps(path.TimeMin - dt, path.TimeMax + dt, path.AxisMin - da, path.AxisMax + da))
            {
                continue;
            }

            for (var i = 0; i + 1 < path.Vertices.Count; i++)
            {
                var v0 = path.Vertices[i];
                var v1 = path.Vertices[i + 1];
                if (SkiaGraphRenderer.ClipToRect(v.ToScreen(v0.Time, v0.Axis), v.ToScreen(v1.Time, v1.Axis), area) is not var (c, d))
                {
                    continue;
                }

                var dx = d.X - c.X;
                var dy = d.Y - c.Y;
                var samples = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((dx * dx) + (dy * dy)) / step));
                for (var k = 0; k <= samples; k++)
                {
                    var x = c.X + (dx * k / samples);
                    var y = c.Y + (dy * k / samples);
                    AddAround(x, y, reach, p, i);
                }
            }
        }
    }

    private void AddAround(double x, double y, double reach, int path, int segment)
    {
        var c0 = Column(x - reach);
        var c1 = Column(x + reach);
        var r0 = Row(y - reach);
        var r1 = Row(y + reach);
        for (var r = r0; r <= r1; r++)
        {
            for (var c = c0; c <= c1; c++)
            {
                var cell = _cells[(r * _columns) + c] ??= [];
                if (cell.Count == 0 || cell[^1] != (path, segment))
                {
                    cell.Add((path, segment));
                    Entries++;
                }
            }
        }
    }

    private int CellOf(double x, double y) => (Row(y) * _columns) + Column(x);

    private int Column(double x) => Math.Clamp((int)Math.Floor((x - _left) / CellSize), 0, _columns - 1);

    private int Row(double y) => Math.Clamp((int)Math.Floor((y - _top) / CellSize), 0, _rows - 1);
}
