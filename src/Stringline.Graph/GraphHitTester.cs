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

/// <summary>The kinds of thing on the graph the mouse can be over (#12).</summary>
public enum GraphTargetKind
{
    /// <summary>A conflict's red ring.</summary>
    ConflictRing,

    /// <summary>A note's marker or label.</summary>
    Note,

    /// <summary>A train line.</summary>
    Path,

    /// <summary>A conflict's red highlight over the section or platform, away from its ring.</summary>
    Conflict,

    /// <summary>A block/possession or speed restriction.</summary>
    Disruption,

    /// <summary>A location's label in the margin.</summary>
    Label,
}

/// <summary>
/// What is under the mouse (<see cref="GraphHitTester.TargetAt"/>): the property for its <see cref="Kind"/> is set. A
/// train line also gives the conflict highlight and disruption it is inside, if any.
/// </summary>
public sealed record GraphTarget(GraphTargetKind Kind)
{
    /// <summary>The train line, for <see cref="GraphTargetKind.Path"/>.</summary>
    public GraphHit? Hit { get; init; }

    /// <summary>
    /// The conflict, for <see cref="GraphTargetKind.ConflictRing"/> and <see cref="GraphTargetKind.Conflict"/>; for a
    /// line, the highlight it is inside.
    /// </summary>
    public GraphConflict? Conflict { get; init; }

    public GraphNote? Note { get; init; }

    /// <summary>The block or restriction, for <see cref="GraphTargetKind.Disruption"/>; for a line, the one it is inside.</summary>
    public GraphDisruption? Disruption { get; init; }

    /// <summary>The location, for <see cref="GraphTargetKind.Label"/>.</summary>
    public GraphLocation? Location { get; init; }
}

/// <summary>
/// Finds the path under a pixel (<c>05-…md</c> §7): the nearest projected segment within a tolerance, preferring the
/// actual layer over the WTT when they are about as near. This is a linear scan over every path, fine for a one-off
/// test; for repeated hover tests over the same view use <see cref="GraphHitIndex"/>, which gives the same answers.
/// </summary>
public static class GraphHitTester
{
    /// <summary>How far from a line, in pixels, still counts as on it.</summary>
    public const double DefaultTolerance = 5;

    /// <summary>An actual or plan line wins over a WTT line up to this many pixels further away.</summary>
    internal const double LayerPreferencePixels = 2;

    public static GraphHit? HitTest(GraphScene scene, GraphViewport viewport, ScreenPoint point, double tolerance = DefaultTolerance)
    {
        ArgumentNullException.ThrowIfNull(scene);
        return HitTest(scene.Paths, viewport, point, tolerance);
    }

    /// <summary>The nearest of <paramref name="paths"/> at a pixel, as <see cref="HitTest(GraphScene, GraphViewport, ScreenPoint, double)"/> finds it.</summary>
    public static GraphHit? HitTest(IEnumerable<GraphPath> paths, GraphViewport viewport, ScreenPoint point, double tolerance = DefaultTolerance)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(viewport);
        if (!viewport.IsInPlot(point))
        {
            return null;
        }

        // A world-space box around the cursor, to skip paths nowhere near it cheaply.
        var (time, axis) = viewport.ToWorld(point);
        var dt = tolerance / viewport.TimeLength * viewport.TimeSpan;
        var da = tolerance / viewport.AxisLength * viewport.AxisSpan;

        GraphHit? best = null;
        var bestScore = double.PositiveInfinity;
        foreach (var path in paths)
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

    /// <summary>
    /// What is under a pixel, in the order the tooltip and a click take them (#12): a conflict's ring, a note, a train
    /// line, a conflict's highlight, a block or speed restriction, then a location's label in the margin. Null if none.
    /// </summary>
    /// <param name="index">The hit grid for this scene and viewport, if the caller keeps one; else lines are scanned.</param>
    public static GraphTarget? TargetAt(GraphScene scene, GraphViewport viewport, ScreenPoint point, GraphHitIndex? index = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(viewport);
        if (GraphConflicts.ConflictAt(scene, viewport, point) is { } ring)
        {
            return new GraphTarget(GraphTargetKind.ConflictRing) { Conflict = ring };
        }

        if (GraphAnnotations.NoteAt(scene, viewport, point) is { } note)
        {
            return new GraphTarget(GraphTargetKind.Note) { Note = note };
        }

        var hit = index is not null && index.IsFor(scene, viewport) ? index.HitTest(point) : HitTest(scene, viewport, point);
        if (hit is not null)
        {
            // A held train's line runs along its conflict's band, so the line carries what it is inside too.
            return new GraphTarget(GraphTargetKind.Path)
            {
                Hit = hit,
                Conflict = GraphConflicts.HighlightAt(scene, viewport, point),
                Disruption = GraphAnnotations.DisruptionAt(scene, viewport, point),
            };
        }

        if (GraphConflicts.HighlightAt(scene, viewport, point) is { } highlight)
        {
            return new GraphTarget(GraphTargetKind.Conflict) { Conflict = highlight };
        }

        if (GraphAnnotations.DisruptionAt(scene, viewport, point) is { } disruption)
        {
            return new GraphTarget(GraphTargetKind.Disruption) { Disruption = disruption };
        }

        return GraphLabels.LocationAt(scene, viewport, point) is { } location ? new GraphTarget(GraphTargetKind.Label) { Location = location } : null;
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
