namespace Stringline.Graph;

/// <summary>A path under the cursor.</summary>
/// <param name="Segment">Index of the vertex that starts the segment hit.</param>
/// <param name="Distance">Pixels from the cursor to the segment.</param>
/// <param name="NearestVertex">Index of the segment end nearer the cursor: the timing point the tooltip describes.</param>
/// <param name="Time">Sim seconds at the point on the segment nearest the cursor.</param>
public sealed record GraphHit(GraphPath Path, int Segment, double Distance, int NearestVertex, double Time)
{
    public GraphVertex Vertex => Path.Vertices[NearestVertex];
}

/// <summary>
/// Finds the path under a pixel (<c>05-…md</c> §7): the nearest projected segment within a tolerance, preferring the
/// actual layer over the WTT when they are about as near. A linear scan over the paths in view; S9 adds a grid.
/// </summary>
public static class GraphHitTester
{
    /// <summary>How far from a line, in pixels, still counts as on it.</summary>
    public const double DefaultTolerance = 5;

    /// <summary>An actual or plan line wins over a WTT line up to this many pixels further away.</summary>
    private const double LayerPreferencePixels = 2;

    public static GraphHit? HitTest(GraphScene scene, GraphViewport viewport, ScreenPoint point, double tolerance = DefaultTolerance)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(viewport);
        if (!viewport.IsInPlot(point))
        {
            return null;
        }

        // A world-space box around the cursor, to skip paths nowhere near it cheaply.
        var (time, axis) = viewport.ToWorld(point);
        var dt = tolerance / viewport.PlotWidth * viewport.TimeSpan;
        var da = tolerance / viewport.PlotHeight * viewport.AxisSpan;

        GraphHit? best = null;
        var bestScore = double.PositiveInfinity;
        foreach (var path in scene.Paths)
        {
            if (path.TimeMax < time - dt || path.TimeMin > time + dt)
            {
                continue;
            }

            for (var i = 0; i + 1 < path.Vertices.Count; i++)
            {
                var v0 = path.Vertices[i];
                var v1 = path.Vertices[i + 1];
                if (Math.Max(v0.Time, v1.Time) < time - dt || Math.Min(v0.Time, v1.Time) > time + dt ||
                    Math.Max(v0.Axis, v1.Axis) < axis - da || Math.Min(v0.Axis, v1.Axis) > axis + da)
                {
                    continue;
                }

                var a = viewport.ToScreen(v0.Time, v0.Axis);
                var b = viewport.ToScreen(v1.Time, v1.Axis);
                var (distance, t) = DistanceToSegment(point, a, b);
                if (distance > tolerance)
                {
                    continue;
                }

                var score = distance - (path.Layer == GraphLayer.Wtt ? 0 : LayerPreferencePixels);
                if (score < bestScore)
                {
                    bestScore = score;
                    var nearest = t <= 0.5 ? i : i + 1;
                    best = new GraphHit(path, i, distance, nearest, v0.Time + (t * (v1.Time - v0.Time)));
                }
            }
        }

        return best;
    }

    /// <summary>Distance from p to segment a–b, and how far along it (0..1) the nearest point is.</summary>
    internal static (double Distance, double T) DistanceToSegment(ScreenPoint p, ScreenPoint a, ScreenPoint b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var lengthSquared = (dx * dx) + (dy * dy);
        var t = lengthSquared == 0 ? 0 : Math.Clamp((((p.X - a.X) * dx) + ((p.Y - a.Y) * dy)) / lengthSquared, 0, 1);
        var x = a.X + (t * dx) - p.X;
        var y = a.Y + (t * dy) - p.Y;
        return (Math.Sqrt((x * x) + (y * y)), t);
    }
}
