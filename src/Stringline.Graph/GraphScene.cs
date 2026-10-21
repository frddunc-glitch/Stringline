using Stringline.Core;
using Stringline.Network;
using Stringline.Network.Profiles;

namespace Stringline.Graph;

/// <summary>The kind of line a path is, in drawing order (later layers draw on top). See <c>05-…md</c> §2.</summary>
public enum GraphLayer
{
    /// <summary>The train as booked in the WTT.</summary>
    Wtt,

    /// <summary>The working plan, where it differs from the WTT (S10).</summary>
    Plan,

    /// <summary>Where the train has actually been.</summary>
    Actual,

    /// <summary>Where the train is expected to go (S11).</summary>
    Forecast,
}

/// <summary>A corner of a train path in world coordinates: sim seconds and corridor axis position (0..1).</summary>
public readonly record struct GraphVertex(double Time, double Axis);

/// <summary>
/// One line on the graph: a train's run along the corridor in one layer. A train that leaves the corridor and comes
/// back has one path per visit. Vertices are in time order; a stop draws as two vertices at the same axis position.
/// </summary>
/// <param name="Key">Unique within the scene, e.g. <c>wtt:1A23#0</c> or <c>act:5102/0#0</c>.</param>
/// <param name="Train">The WTT train this path belongs to; null for an unmatched live working.</param>
/// <param name="Headcode">Label text.</param>
/// <param name="Colour">The category colour, or grey for an unmatched live working.</param>
/// <param name="IsUnmatched">An actual path of a live working not (yet) matched to the WTT.</param>
/// <param name="Direction">Down if the path runs away from the corridor's first location, Up towards it; null if neither.</param>
/// <param name="LiveUid">For an actual path: the sim uid of the live train.</param>
/// <param name="Working">For an actual path: the index of the working in <c>LiveTrain.Workings</c>.</param>
/// <param name="SegmentColours">
/// One colour per segment (vertex <c>i</c> to <c>i + 1</c>) when the line changes colour along its length, as an
/// actual path does when coloured by delay band; null when the whole line is <paramref name="Colour"/>.
/// </param>
/// <param name="Points">
/// For a WTT, plan or forecast path: the plan point index (the same as the WTT's) of each vertex, so an edit on the
/// graph knows which timing point it is at. A stop with a dwell has two vertices with the same index. Null for actuals.
/// </param>
/// <param name="IsCancelled">A cancelled train on the plan layer: drawn thin and grey with crosses at its ends (S12).</param>
/// <param name="Style">
/// The category's line style (S14a) on the WTT, plan and actual layers. The forecast layer keeps its own dash-dot, so it
/// is always <see cref="LineStyle.Solid"/> there.
/// </param>
/// <remarks>The bounds are worked out once, when the path is made; do not copy a path <c>with</c> new vertices.</remarks>
public sealed record GraphPath(
    string Key,
    GraphLayer Layer,
    TrainId? Train,
    Headcode Headcode,
    Colour Colour,
    IReadOnlyList<GraphVertex> Vertices,
    bool IsUnmatched = false,
    TrackDirection? Direction = null,
    string? LiveUid = null,
    int? Working = null,
    IReadOnlyList<Colour>? SegmentColours = null,
    IReadOnlyList<int>? Points = null,
    bool IsCancelled = false,
    LineStyle Style = LineStyle.Solid)
{
    public double TimeMin => Vertices[0].Time;

    public double TimeMax => Vertices[^1].Time;

    public double AxisMin { get; } = Vertices.Count == 0 ? 0 : Vertices.Min(v => v.Axis);

    public double AxisMax { get; } = Vertices.Count == 0 ? 0 : Vertices.Max(v => v.Axis);

    /// <summary>The plan point index of vertex <paramref name="vertex"/>, if the path has them.</summary>
    public int? PointAt(int vertex) => Points is { } points && vertex >= 0 && vertex < points.Count ? points[vertex] : null;

    /// <summary>
    /// True if vertex <paramref name="vertex"/> is the departure end of a stop: the second of two vertices at one
    /// timing point, where a drag changes the dwell.
    /// </summary>
    public bool IsDwellEnd(int vertex) =>
        vertex > 0 && PointAt(vertex) is { } index && PointAt(vertex - 1) == index && Vertices[vertex].Axis == Vertices[vertex - 1].Axis;

    /// <summary>The colour of segment <paramref name="index"/> (from vertex <c>index</c> to the next).</summary>
    public Colour ColourOf(int index) => SegmentColours is { } colours ? colours[index] : Colour;

    /// <summary>True if this path draws exactly as <paramref name="other"/> does: same key, colours and vertices.</summary>
    public bool DrawsLike(GraphPath other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return ReferenceEquals(this, other) || (Key == other.Key && Layer == other.Layer && Colour == other.Colour && IsCancelled == other.IsCancelled && Style == other.Style &&
            Vertices.SequenceEqual(other.Vertices) &&
            (SegmentColours is null ? other.SegmentColours is null : other.SegmentColours is not null && SegmentColours.SequenceEqual(other.SegmentColours)));
    }
}

/// <summary>
/// A running train's live head (S36): its actual line carried on from its last report to now, along its forecast, as
/// a short line in the actual style ending in a dot. The renderer works out where it has got to from the clock each
/// frame (<see cref="At"/>), so it moves without the scene being built again.
/// </summary>
/// <param name="Key">Unique within the scene, e.g. <c>head:1A23</c>.</param>
/// <param name="Vertices">
/// From the last report along the forecast to the next point the train will report: the last report, the end of a
/// dwell there if it is standing, then the next point. At least two, in time order; only the last one moves along
/// the axis.
/// </param>
public sealed record GraphLiveHead(string Key, TrainId Train, Headcode Headcode, Colour Colour, LineStyle Style, IReadOnlyList<GraphVertex> Vertices, string? LiveUid = null)
{
    /// <summary>
    /// The head stops this far along its last stretch (Fred, S36): it never reaches the next point without a report, so
    /// an overdue train is not drawn ahead of where it is known to be.
    /// </summary>
    public const double HoldShare = 0.95;

    /// <summary>
    /// The line to draw at sim time <paramref name="time"/>: the last report, the corners passed, and the head itself
    /// at <paramref name="time"/>. Once overdue, the head waits <see cref="HoldShare"/> of the way along, drawn flat to
    /// <paramref name="time"/>. Empty at or before the last report.
    /// </summary>
    public IReadOnlyList<GraphVertex> At(double time)
    {
        if (Vertices.Count < 2 || time <= Vertices[0].Time)
        {
            return [];
        }

        var line = new List<GraphVertex>(Vertices.Count + 1) { Vertices[0] };
        for (var i = 1; i < Vertices.Count; i++)
        {
            var (from, to) = (Vertices[i - 1], Vertices[i]);
            var last = i == Vertices.Count - 1;
            if (!last && time >= to.Time)
            {
                line.Add(to);
                continue;
            }

            var span = to.Time - from.Time;
            var share = span <= 0 ? 1 : (time - from.Time) / span;
            if (last && share > HoldShare)
            {
                var held = new GraphVertex(from.Time + (HoldShare * Math.Max(0, span)), from.Axis + (HoldShare * (to.Axis - from.Axis)));
                line.Add(held);
                line.Add(held with { Time = time });
            }
            else
            {
                line.Add(new GraphVertex(time, from.Axis + (Math.Min(1, share) * (to.Axis - from.Axis))));
            }

            break;
        }

        return line;
    }

    /// <summary>The head's place at <paramref name="time"/>, if it is drawn then.</summary>
    public GraphVertex? HeadAt(double time) => At(time) is { Count: > 0 } line ? line[^1] : null;
}

/// <summary>A location line across the graph.</summary>
/// <param name="IsStation">Trains stop here (it has platforms or booked stops); drawn heavier with a bold label.</param>
/// <param name="IsMinor">
/// A minor row (S36, <see cref="CorridorLocation.Minor"/>): a faint dotted line instead of a location line, and a small
/// label only where there is room for it (zoomed in).
/// </param>
public sealed record GraphLocation(Tiploc Tiploc, string Name, double Axis, bool IsStation, int Platforms, bool IsMinor = false);

/// <summary>
/// Everything one corridor's graph shows, in world coordinates, independent of the viewport: build it once per data
/// change and render it at any pan and zoom.
/// </summary>
/// <param name="Now">The sim time of the now line, if live. The host may copy the scene <c>with</c> a later now each
/// frame; the renderer keeps its caches as long as <see cref="Paths"/> is the same list.</param>
public sealed record GraphScene(
    string CorridorId,
    string Title,
    IReadOnlyList<GraphLocation> Locations,
    IReadOnlyList<GraphPath> Paths,
    SimTime? Now)
{
    /// <summary>Blocks and speed restrictions on this corridor (S13), drawn under the trains.</summary>
    public IReadOnlyList<GraphDisruption> Disruptions { get; init; } = [];

    /// <summary>Notes pinned to this corridor (S13), drawn over the trains.</summary>
    public IReadOnlyList<GraphNote> Notes { get; init; } = [];

    /// <summary>Conflicts on this corridor (S14): highlights under the trains, rings over them.</summary>
    public IReadOnlyList<GraphConflict> Conflicts { get; init; } = [];

    /// <summary>Running trains' live heads (S36), drawn from <see cref="Now"/> each frame.</summary>
    public IReadOnlyList<GraphLiveHead> LiveHeads { get; init; } = [];

    public IEnumerable<GraphPath> InLayer(GraphLayer layer) => Paths.Where(p => p.Layer == layer);
}
